// Consysto fork: the same commands over http, for callers that find a pipe awkward — a web dashboard, a script in any language.
//
// It listens on this machine only. A port, unlike a pipe, is open to every program running here, so a key is required: it is
// generated once and kept in the data folder of the program, where only this user can read it.

using Microsoft.Extensions.Logging;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Files.App.Api
{
	public sealed class ApiHttpServer : IDisposable
	{
		public const int DefaultPort = 3577;

		private readonly HttpListener listener = new();
		private readonly CancellationTokenSource stopping = new();
		private readonly int port;

		public ApiHttpServer(int port)
			=> this.port = port is > 0 and < 65536 ? port : DefaultPort;

		public void Start()
		{
			try
			{
				// Making the key now puts its file in place: a caller has to be able to look it up before it ever calls
				_ = ApiKey.Value;

				listener.Prefixes.Add($"http://127.0.0.1:{port}/");
				listener.Start();
			}
			catch (Exception ex)
			{
				// A taken port or a system that refuses the prefix is not a reason to fail the program: the pipe still works
				App.Logger.LogWarning(ex, "The web entrance of the control channel could not start on port {Port}", port);
				return;
			}

			_ = Task.Run(() => ListenAsync(stopping.Token));
		}

		private async Task ListenAsync(CancellationToken token)
		{
			while (!token.IsCancellationRequested && listener.IsListening)
			{
				try
				{
					var context = await listener.GetContextAsync();
					_ = Task.Run(() => ServeAsync(context), CancellationToken.None);
				}
				catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException)
				{
					break;
				}
				catch (Exception ex)
				{
					App.Logger.LogWarning(ex, "The web entrance of the control channel could not accept a caller");
				}
			}
		}

		private static async Task ServeAsync(HttpListenerContext context)
		{
			try
			{
				string answer;
				if (!IsAllowed(context.Request))
				{
					context.Response.StatusCode = 403;
					answer = "{\"ok\":false,\"error\":\"the key is missing or wrong\"}";
				}
				else
				{
					using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
					answer = await ApiPipeServer.AnswerAsync(await reader.ReadToEndAsync());
				}

				var bytes = Encoding.UTF8.GetBytes(answer);
				context.Response.ContentType = "application/json; charset=utf-8";
				context.Response.ContentLength64 = bytes.Length;
				await context.Response.OutputStream.WriteAsync(bytes);
			}
			catch (Exception ex)
			{
				App.Logger.LogWarning(ex, "The web entrance of the control channel failed while answering");
			}
			finally
			{
				try
				{
					context.Response.Close();
				}
				catch (Exception)
				{
				}
			}
		}

		/// <summary>
		/// The key travels in a header, never in the address: an address is written to logs and to the history of whatever asked.
		/// A page on a foreign site cannot add such a header without being let in, and nothing here lets it in.
		/// </summary>
		private static bool IsAllowed(HttpListenerRequest request)
		{
			var offered = request.Headers["X-Consysto-Key"];
			return !string.IsNullOrEmpty(offered)
				&& CryptographicOperations.FixedTimeEquals(
					Encoding.UTF8.GetBytes(offered),
					Encoding.UTF8.GetBytes(ApiKey.Value));
		}

		public void Dispose()
		{
			stopping.Cancel();
			try
			{
				listener.Close();
			}
			catch (Exception)
			{
			}

			stopping.Dispose();
		}
	}
}
