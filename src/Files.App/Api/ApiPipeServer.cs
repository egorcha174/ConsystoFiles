// Consysto fork: the control channel itself — a named pipe that only this user may open.

using Microsoft.Extensions.Logging;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Files.App.Api
{
	/// <summary>
	/// Listens for requests and answers them. One line in is one request, one line out is one answer; a caller may send several
	/// requests over the same connection or a single one and hang up.
	/// </summary>
	public sealed class ApiPipeServer : IDisposable
	{
		public const string PipeName = "ConsystoFiles";

		private readonly CancellationTokenSource stopping = new();

		public void Start()
			=> _ = Task.Run(() => ListenAsync(stopping.Token));

		private async Task ListenAsync(CancellationToken token)
		{
			while (!token.IsCancellationRequested)
			{
				NamedPipeServerStream? pipe = null;
				try
				{
					pipe = Create();
					await pipe.WaitForConnectionAsync(token);

					// Each caller is served on its own, so a caller that stops reading cannot hold up the next one
					var accepted = pipe;
					pipe = null;
					_ = Task.Run(() => ServeAsync(accepted, token), CancellationToken.None);
				}
				catch (OperationCanceledException)
				{
					break;
				}
				catch (Exception ex)
				{
					App.Logger.LogWarning(ex, "The control channel could not accept a caller");
					pipe?.Dispose();

					// A failure that repeats instantly would spin the thread; wait a moment and try again
					try
					{
						await Task.Delay(TimeSpan.FromSeconds(1), token);
					}
					catch (OperationCanceledException)
					{
						break;
					}
				}
			}
		}

		/// <summary>The pipe is opened for this user alone: nobody else signed in on the machine can reach the window.</summary>
		private static NamedPipeServerStream Create()
		{
			var security = new PipeSecurity();
			var user = WindowsIdentity.GetCurrent().User!;
			security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.FullControl, AccessControlType.Allow));

			return NamedPipeServerStreamAcl.Create(
				PipeName,
				PipeDirection.InOut,
				NamedPipeServerStream.MaxAllowedServerInstances,
				PipeTransmissionMode.Byte,
				PipeOptions.Asynchronous,
				inBufferSize: 0,
				outBufferSize: 0,
				security);
		}

		private static async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken token)
		{
			try
			{
				using (pipe)
				{
					var reader = new StreamReader(pipe, Encoding.UTF8);
					var writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true };

					while (!token.IsCancellationRequested && pipe.IsConnected)
					{
						var line = await reader.ReadLineAsync(token);
						if (line is null)
							break;

						await writer.WriteLineAsync(await AnswerAsync(line));
					}
				}
			}
			catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
			{
			}
			catch (Exception ex)
			{
				App.Logger.LogWarning(ex, "The control channel failed while serving a caller");
			}
		}

		/// <summary>Takes the text of a request and gives back the text of the answer. The web entrance uses this too.</summary>
		public static async Task<string> AnswerAsync(string line)
		{
			ApiResponse answer;
			try
			{
				var request = JsonSerializer.Deserialize(line, ApiJsonContext.Default.ApiRequest);
				answer = request is null
					? new ApiResponse(false, "the request is empty")
					: await ApiDispatcher.RunAsync(request);
			}
			catch (JsonException ex)
			{
				answer = new ApiResponse(false, $"the request is not readable: {ex.Message}");
			}

			return JsonSerializer.Serialize(answer, ApiJsonContext.Default.ApiResponse);
		}

		public void Dispose()
		{
			stopping.Cancel();
			stopping.Dispose();
		}
	}
}
