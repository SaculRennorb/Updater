using System.IO.Compression;
using System.Net.Http.Headers;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Rennorb.Logging;

namespace Rennorb.Updater;

internal static class Config
{
	[JsonIgnore]
	public const string FILE_SOURCE = "config/updater.json";

	public static string               ProgramDllPath;
	///<summary> shape: HardstuckGuild/HsDiscordBot</summary>
	public static string               GithubRepoId;
	public static string               ClientId;
	[JsonConverter(typeof(HashAlgorithmNameConverter))]
	public static HashAlgorithmName    SignatureHashAlgorithm;
	[JsonConverter(typeof(RSASignaturePaddingConverter))]
	public static RSASignaturePadding  SignaturePadding;
	public static string               PublicKeyPath;

	public static OAuth.AuthResponse2? CurrentAuth;

#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
	static Config()
#pragma warning restore CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
	{
		StaticConfig.LoadFromDisk(typeof(Config), FILE_SOURCE);
	}

	public static void SetDefaults()
	{
		ProgramDllPath         = "program.dll";
		GithubRepoId           = "owner/repo";
		ClientId               = "xxxxxxxxxxxxxxxx";
		SignatureHashAlgorithm = HashAlgorithmName.SHA512;
		SignaturePadding       = RSASignaturePadding.Pkcs1;
		PublicKeyPath          = "res/public.pem";
	}

	public static void SaveToDisk() => StaticConfig.SaveToDisk(typeof(Config), FILE_SOURCE);
}

public class HashAlgorithmNameConverter : JsonConverter<HashAlgorithmName>
{
	public override HashAlgorithmName Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => new(reader.GetString());
	public override void Write(Utf8JsonWriter writer, HashAlgorithmName value, JsonSerializerOptions options) => writer.WriteStringValue(value.Name);
}

public class RSASignaturePaddingConverter : JsonConverter<RSASignaturePadding>
{
	public override RSASignaturePadding? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
	{
		var str = reader.GetString();
		switch(str)
		{
			case "Pkcs1": return RSASignaturePadding.Pkcs1;
			case "Pss": return RSASignaturePadding.Pss;
			default: throw new InvalidDataException($"unknown padding {str}");
		}
	}

	public override void Write(Utf8JsonWriter writer, RSASignaturePadding value, JsonSerializerOptions options)
	{
		if(value == RSASignaturePadding.Pkcs1)
			writer.WriteStringValue("Pkcs1");
		else if(value == RSASignaturePadding.Pss)
			writer.WriteStringValue("Pss");
		else
			throw new ArgumentException($"unknown padding {value}");
	}
}

internal class ProgramMethods
{
	public AssemblyLoadContext AssemblyContext;
	public object              Instance;

	/// <summary> sig: int Main(string[] args); </summary>
	public Func<string[], int> Main;
	/// <summary> sig: void ConsoleCommandReceived(string command); </summary>
	public Action<string>      ConsoleCommandReceived;
	/// <summary> sig: void Stop(); </summary>
	public Action              Stop;

	public ProgramMethods(AssemblyLoadContext assemblyContext, object instance, Func<string[], int> main, Action<string> consoleCommandReceived, Action stop)
	{
		AssemblyContext        = assemblyContext;
		Instance               = instance;
		Main                   = main;
		ConsoleCommandReceived = consoleCommandReceived;
		Stop                   = stop;
	}
}

class Updater
{
	static Logger _logger = new Logger("Updater");

	public static readonly JsonSerializerOptions SerializerOptions = new JsonSerializerOptions {
		AllowTrailingCommas = true,
		IncludeFields       = true,
		WriteIndented       = true,
	};

	readonly HttpClient           _httpClient;
	readonly string               _githubUrl;
	readonly RSAParameters        _rsaPublicParams;

	public Updater()
	{
		_httpClient = new HttpClient() {
			DefaultRequestHeaders = {
				UserAgent = { new("R-Updater", "0.0.1") },
				Accept    = { new("application/json") },
			},
		};
		if(Config.CurrentAuth.HasValue)
		{
			_httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(Config.CurrentAuth.Value.TokenType, Config.CurrentAuth.Value.AccessToken);
		}

		_githubUrl = $"https://api.github.com/repos/{Config.GithubRepoId}/releases/latest";

		using(var rsa = RSA.Create())
		{
			string pem;
			using(var reader = new StreamReader(Config.PublicKeyPath, System.Text.Encoding.UTF8, false))
				pem = reader.ReadToEnd();
			rsa.ImportFromPem(pem);
			_rsaPublicParams = rsa.ExportParameters(false);
		}
	}

	public OAuth.AuthResponse2? DoOAuth() => OAuth.DoOAuth(_httpClient);

	AutoResetEvent? _updateTrigger;
	Thread?   _wantsUpdateWatcher;

	string[]? _currentArgs;
	Thread?   _programThread;
	public void BeginKeepMainAlive(string[] args)
	{
		_logger.WriteLine(LogLevel.VERBOSE, "BeginKeepMainAlive");
		if(_programThread != null && !_programThread.ThreadState.HasFlag(ThreadState.Stopped))
		{
			_logger.WriteLine("main loop already running");
			return;
		}

		_currentArgs = args;

		if(_wantsUpdateWatcher == null)
		{
			_wantsUpdateWatcher = new Thread(this.UpdateWatcherLopp) { IsBackground = true };
			_wantsUpdateWatcher.Start();
		}

		_programThread = new Thread(this.KeepMainAliveThread!) { IsBackground = true };
		_programThread.Start(new MainParams() {
			Args        = args,
			WantsUpdate = () => { this._updateTrigger?.Set(); },
		});
	}

	void UpdateWatcherLopp()
	{
		this._updateTrigger = new(false);

		while(true)
		{
			this._updateTrigger.WaitOne();
			try { this.CurrentInstance?.Stop(); }
			catch(Exception ex) { _logger.WriteLine(LogLevel.ERROR, $"Exception while trying to stop child:\n{ex}"); }
			this.MaybeUpdateFiles();
			this.BeginKeepMainAlive(_currentArgs!);
		}
	}

	public ProgramMethods? CurrentInstance;
	public bool            IsMainRunning;

	public struct MainParams
	{
		public string[] Args;
		public Action   WantsUpdate;
	}

	//TODO(Rennorb): @hammer use the return code 
	//TODO(Rennorb): @hammer stop when return code is a loader error or at least fall into update cycle for auto recovery on new update
	void KeepMainAliveThread(object obj) {
		var returnCode = KeepMainAlive((MainParams)obj);
		if(returnCode == 0)
			_logger.WriteLine("Process exited normally");
	}
	int KeepMainAlive(MainParams @params)
	{
		while(true)
		{
			try
			{
				_logger.WriteLine("loading assembly");
				//NOTE(Rennorb): need to actually destroy the context after loop so unload works.
				var assemblyContext = new AssemblyLoadContext("Program Context", true);
				var assembly = assemblyContext.LoadFromAssemblyPath(Path.GetFullPath(Config.ProgramDllPath));

				var programType = assembly.ExportedTypes.FirstOrDefault(type => type.Name == "Program");
				if(programType == null)
				{
					_logger.WriteLine(LogLevel.ERROR, $"the assembly {Config.ProgramDllPath} does not contain a public 'Program' type");
					return -1;
				}

				var constructor = programType.GetConstructor(new[]{ typeof(Action) });
				if(constructor == null)
				{
					_logger.WriteLine(LogLevel.ERROR, $"the '{programType.FullName}' type in the target assembly '{Config.ProgramDllPath}' does not contain a vaild Constructor (missing 'public Program(Action wantsUpdate)')");
					return -2;
				}

				var mainMethod = programType.GetMethod(nameof(ProgramMethods.Main), new[]{ typeof(string[]) });
				if(mainMethod == null)
				{
					_logger.WriteLine(LogLevel.ERROR, $"the '{programType.FullName}' type in the target assembly '{Config.ProgramDllPath}' does not contain a vaild Main method (missing 'public int Main(string[] args)')");
					return -3;
				}
				var commandReceivedMethod = programType.GetMethod(nameof(ProgramMethods.ConsoleCommandReceived), new[]{ typeof(string) });
				if(commandReceivedMethod == null)
				{
					_logger.WriteLine(LogLevel.ERROR, $"the '{programType.FullName}' type in the target assembly '{Config.ProgramDllPath}' does not contain a vaild ConsoleCommandReceived method (missing 'public void  ConsoleCommandReceived(string command)')");
					return -4;
				}

				var stopMethod = programType.GetMethod(nameof(ProgramMethods.Stop), Type.EmptyTypes);
				if(stopMethod == null)
				{
					_logger.WriteLine(LogLevel.ERROR, $"the '{programType.FullName}' type in the target assembly '{Config.ProgramDllPath}' does not contain a vaild Stop method (missing 'public void Stop()')");
					return -5;
				}

				var instance = constructor.Invoke(new object[]{ @params.WantsUpdate });
				CurrentInstance = new(
					assemblyContext,
					instance,
					mainMethod.CreateDelegate<Func<string[], int>>(instance),
					commandReceivedMethod.CreateDelegate<Action<string>>(instance),
					stopMethod.CreateDelegate<Action>(instance)
				);

				IsMainRunning = true;
				var returnCode = CurrentInstance.Main(@params.Args);
				IsMainRunning = false;
				_logger.WriteLine("unloading assembly");
				assemblyContext.Unload();
				if(returnCode == 0)
					return 0;
				else
					_logger.WriteLine($"Main crashed with {returnCode}");
			}
			catch(Exception ex)
			{
				IsMainRunning = false;
				_logger.WriteLine(LogLevel.ERROR, $"exception on main thread:\n{ex}");
			}
		}
	}

	/// <summary> assumes the program is not currently running </summary>
	public void MaybeUpdateFiles()
	{
		_logger.WriteLine("checking for new update");
		JsonDocument jdoc;
		using(var response = _httpClient.GetAsync(_githubUrl).Result)
		using(var stream = response.Content.ReadAsStream())
		{
			if(!response.IsSuccessStatusCode)
			{
				if(response.StatusCode == System.Net.HttpStatusCode.NotFound)
					_logger.WriteLine(LogLevel.ERROR, $"Update request was not found, check that you actually have a release on {Config.GithubRepoId}");
				using(var reader = new StreamReader(stream))
					_logger.WriteLine(LogLevel.ERROR, $"Issue with update request:\n{reader.ReadToEnd()}");
				return;
			}

			jdoc = JsonDocument.Parse(stream);
		}
		var assetsNode = jdoc.RootElement.GetProperty("assets");


		static HttpRequestMessage GetDownloadRequest(string url) => new(HttpMethod.Get, url) { 
			Headers = { Accept = { new("application/octet-stream") } }
		};

		var remoteVersion        = new Version(99, 99, 99);
		byte[]? signature        = null;
		string? binariesDownloadUrl = null;
		var tempStoragePath = "updates";
		int found = 0;
		
		foreach(var assetNode in assetsNode.EnumerateArray())
		{
			switch(assetNode.GetProperty("name").GetString())
			{
				case "version.txt":
				{
					var downloadUrl = assetNode.GetProperty("url").GetString()!;
					using(var response = _httpClient.Send(GetDownloadRequest(downloadUrl)))
						remoteVersion = Version.Parse(response.Content.ReadAsStringAsync().Result);
					found++;
				}
				break;

				case "signature.bin":
				{
					var downloadUrl = assetNode.GetProperty("url").GetString()!;
					using(var response = _httpClient.Send(GetDownloadRequest(downloadUrl)))
						signature = response.Content.ReadAsByteArrayAsync().Result;
					found++;
				}
				break;

				case "binaries.zip":
				{
					binariesDownloadUrl = assetNode.GetProperty("url").GetString();
					found++;
				}
				break;
			}
			if(found == 3) break;
		}

		var localVersionString = System.Diagnostics.FileVersionInfo.GetVersionInfo(Config.ProgramDllPath).FileVersion;
		if(!Version.TryParse(localVersionString, out var localVersion)) localVersion = new(0, 0, 0);

		if(remoteVersion <= localVersion)
		{
			_logger.WriteLine(LogLevel.INFO, "no newer version on remote");
			return;
		}
		if(binariesDownloadUrl == null)
		{
			_logger.WriteLine(LogLevel.INFO, "current release does not contain a binaries.zip");
			return;
		}
		if(signature == null)
		{
			_logger.WriteLine(LogLevel.WARN, "current release does not contain a signature");
			return;
		}

		Directory.CreateDirectory(tempStoragePath);
		var latestUpdatePath = Path.Combine(tempStoragePath, "latest.zip");

		using(var response = _httpClient.Send(GetDownloadRequest(binariesDownloadUrl)))
		using(var downloadStream = response.Content.ReadAsStream())
		using(var bufferStream = new MemoryStream(downloadStream.Length <= int.MaxValue ? (int)downloadStream.Length : int.MaxValue))
		{
			downloadStream.CopyTo(bufferStream);

			bufferStream.Seek(0, SeekOrigin.Begin);
			bool valid;
			using(var rsa = RSA.Create(_rsaPublicParams))
				valid = rsa.VerifyData(bufferStream, signature, Config.SignatureHashAlgorithm, Config.SignaturePadding);

			if(!valid)
			{
				_logger.WriteLine(LogLevel.ERROR, "could not verify signature");
				return;
			}

			bufferStream.Seek(0, SeekOrigin.Begin);
			using(var fileStream = File.Open(latestUpdatePath, FileMode.Create, FileAccess.Write, FileShare.None))
			{
				bufferStream.CopyTo(fileStream);
			}
		}

		_logger.WriteLine("update downloaded, extracting...");
		foreach(var entry in ZipFile.OpenRead(latestUpdatePath).Entries)
		{
			try { entry.ExtractToFile(entry.FullName, true); }
			catch(IOException ex)
			{
				if(ex.Message.Contains("cannot access"))
					_logger.WriteLine(LogLevel.WARN, $"cannot access {entry.FullName}");
				else
					throw;
			}
		}

		_logger.WriteLine("done extracting, rebooting program...");
	}

	public void SignBinaries()
	{
		Directory.CreateDirectory("sign");
		foreach(var file in new[] { "secret.pem", "sign/binaries.zip", "sign/version.txt" })
			if(!File.Exists(file))
			{
				_logger.WriteLine(LogLevel.ERROR, $"missing {file}");
				return;
			}

		if(!Version.TryParse(File.ReadAllText("sign/version.txt"), out var textVersion))
		{
			_logger.WriteLine(LogLevel.ERROR, $"could not find a vaild version text in sign/version.txt");
			return;
		}

		using(var rsa = RSA.Create())
		{
			string pem;
			using(var reader = new StreamReader("secret.pem", System.Text.Encoding.UTF8, false))
				pem = reader.ReadToEnd();
			rsa.ImportFromPem(pem);

			var sigFile = "sign/signature.bin";
			if(File.Exists(sigFile)) File.Delete(sigFile);

			using(var fileStream = File.OpenRead("sign/binaries.zip"))
			using(var signatureFileStream = File.Open(sigFile, FileMode.CreateNew, FileAccess.Write, FileShare.None))
			{
				var signature = rsa.SignData(fileStream, Config.SignatureHashAlgorithm, Config.SignaturePadding);
				signatureFileStream.Write(signature);
			}
		}

		_logger.WriteLine($"successfully signed binaries with version {textVersion}, the version of your main binary should match this.");
	}
}
