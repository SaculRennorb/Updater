using System.Diagnostics;
using Rennorb.Logging;

namespace Rennorb.Updater;

class Program
{
	static Logger _logger = new Logger("Updater");

	static void Main(string[] args)
	{
		var updater = new Updater();

		var index = Array.IndexOf(args, "--sign");
		if(index > -1)
		{
			_logger.WriteLine("sign mode");
			updater.SignBinaries();
			return;
		}

		if(!Config.CurrentAuth.HasValue)
		{
			var auth = updater.DoOAuth();
			if(!auth.HasValue)
			{
				_logger.WriteLine(LogLevel.ERROR, "Did not authenticate");
				return;
			}

			Config.CurrentAuth = auth;
			Config.SaveToDisk();
		}

		if(Config.GithubRepoId == "owner/repo")
		{
			_logger.WriteLine(LogLevel.ERROR, $"please set the correct settings in {Config.FILE_SOURCE}");
			return;
		}

		updater.MaybeUpdateFiles();
		updater.BeginKeepMainAlive(args);

		EnterLocalProcessingLoop(updater, args);
	}

	static void EnterLocalProcessingLoop(Updater updater, string[] args)
	{
		string? line;
		Console.WriteLine("enter 'stop' to exit");
		while((line = Console.ReadLine()) != null)
		{
			switch(line)
			{
				case "stop":
				case "exit":
				case "shutdown":
					return;

				case "update":
					updater.CurrentInstance?.Stop();
					updater.MaybeUpdateFiles();
					updater.BeginKeepMainAlive(args);
					break;

				case "help":
					Console.WriteLine("updater commands:\n" +
									  " - stop|exit|shutdown: stop the wrapper\n" +
									  " - update: look for new update");
					updater.CurrentInstance?.ConsoleCommandReceived(line);
					return;

				default:
					updater.CurrentInstance?.ConsoleCommandReceived(line);
					break;
			}
		}

		Process.GetCurrentProcess().WaitForExit(); //NOTE(Rennorb): in contexts without input the above loop will fall through
	}
}
