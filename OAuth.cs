using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Rennorb.Logging;

namespace Rennorb.Updater;

internal static class OAuth
{
	static Logger _logger = new Logger("OAuth");
	public static AuthResponse2? DoOAuth(HttpClient httpClient)
	{
		AuthResponse1 response1;
		using(var httpResponse1 = httpClient.PostAsync("https://github.com/login/device/code", new FormUrlEncodedContent(new KeyValuePair<string, string>[]{
			new("client_id", Config.ClientId ),
			new("scope"    , "repo"),
		})).Result)
		{
			try { response1 = JsonSerializer.Deserialize<AuthResponse1>(httpResponse1.Content.ReadAsStream(), Updater.SerializerOptions); }
			catch(Exception ex)
			{
				_logger.WriteLine(LogLevel.ERROR, $"Authentication failed at stage1:\n{ex}");
				return null;
			}
		}

		Console.WriteLine($"[AUTH REQUIRED] This application needs github authentification. Go to {response1.VerificationURI} and enter the following code:");
		Console.WriteLine($"+---------+\n" +
						  $"|{response1.UserCode:9}|\n" +
						  $"+---------+");

		var stage2Content = new FormUrlEncodedContent(new KeyValuePair<string, string>[] {
			new("client_id"  , Config.ClientId ),
			new("device_code", response1.DeviceCode),
			new("grant_type" , "urn:ietf:params:oauth:grant-type:device_code"),
		});
		var verificationFailsIn = DateTime.Now + TimeSpan.FromSeconds(response1.ExpiresIn);
		while(DateTime.Now < verificationFailsIn)
		{
			using var httpResponse2 = httpClient.PostAsync("https://github.com/login/oauth/access_token", stage2Content).Result;

			var jdoc = JsonDocument.Parse(httpResponse2.Content.ReadAsStream());
			if(!jdoc.RootElement.TryGetProperty("error", out var node))
			{
				var auth = JsonSerializer.Deserialize<AuthResponse2>(jdoc, Updater.SerializerOptions);
				httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(auth.TokenType, auth.AccessToken);
				return auth;
			}

			//error handling
			switch(node.GetString())
			{
				case "authorization_pending": break; //all good
				case "slow_down": Thread.Sleep(response1.Interval * 1000); break; //slow down some more
				case "access_denied":
					_logger.WriteLine(LogLevel.INFO, "Authentication failed: user denied authentication");
					return null;
				default:
					_logger.WriteLine(LogLevel.ERROR, $"Authentication failed: {node.GetString()}");
					return null;
			}

			Thread.Sleep(response1.Interval * 1000);
		}

		_logger.WriteLine(LogLevel.ERROR, "Authentication failed: took too long");
		return null;
	}

	struct AuthResponse1
	{
		[JsonPropertyName("device_code")]
		public string DeviceCode;
		[JsonPropertyName("user_code")]
		public string UserCode;
		[JsonPropertyName("verification_uri")]
		public string VerificationURI;
		[JsonPropertyName("expires_in")]
		public int ExpiresIn;
		[JsonPropertyName("interval")]
		public int Interval;

		public AuthResponse1(string deviceCode, string userCode, string verificationURI, int expiresIn, int interval)
		{
			DeviceCode = deviceCode;
			UserCode = userCode;
			VerificationURI = verificationURI;
			ExpiresIn = expiresIn;
			Interval = interval;
		}
	}

	public struct AuthResponse2
	{
		[JsonPropertyName("access_token")]
		public string AccessToken;
		[JsonPropertyName("token_type")]
		public string TokenType;
		[JsonPropertyName("scope")]
		public string Scope;

		public AuthResponse2(string accessToken, string tokenType, string scope)
		{
			AccessToken = accessToken;
			TokenType = tokenType;
			Scope = scope;
		}
	}
}
