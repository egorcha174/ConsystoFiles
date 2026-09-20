// Consysto fork: switches the control channel on and off according to the settings.

namespace Files.App.Api
{
	/// <summary>
	/// The channel is off until it is switched on in the settings: a file manager that listens for commands is not what a person
	/// downloading a file manager asked for, so it is offered rather than imposed.
	/// </summary>
	public static class ApiHost
	{
		private static ApiPipeServer? pipe;
		private static ApiHttpServer? web;

		public static bool IsRunning
			=> pipe is not null;

		public static bool IsWebRunning
			=> web is not null;

		/// <summary>Reads the settings and brings the channel to the state they describe. Safe to call again at any time.</summary>
		public static void Apply()
		{
			var settings = Ioc.Default.GetRequiredService<IGeneralSettingsService>();

			if (settings.IsApiEnabled && pipe is null)
			{
				pipe = new ApiPipeServer();
				pipe.Start();
			}
			else if (!settings.IsApiEnabled && pipe is not null)
			{
				pipe.Dispose();
				pipe = null;
			}

			// The web entrance rides on the channel and never runs without it
			var webWanted = settings.IsApiEnabled && settings.IsApiWebEnabled;
			if (webWanted && web is null)
			{
				var started = new ApiHttpServer(settings.ApiWebPort);

				// A port that is taken leaves nothing running, and saying otherwise would hide it from the next attempt
				if (started.Start())
					web = started;
				else
					started.Dispose();
			}
			else if (!webWanted && web is not null)
			{
				web.Dispose();
				web = null;
			}
		}

		public static void Stop()
		{
			pipe?.Dispose();
			pipe = null;
			web?.Dispose();
			web = null;
		}
	}
}
