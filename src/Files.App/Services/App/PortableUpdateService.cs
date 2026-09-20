// Consysto fork: the portable build is updated by replacing its folder, so it only looks whether a newer release exists.

using Microsoft.Extensions.Logging;
using System.Net.Http;
using System.Text.Json;
using Windows.System;

namespace Files.App.Services
{
	/// <summary>
	/// Asks GitHub once in a while whether a newer release has been published. Nothing is downloaded or replaced on its own:
	/// the button opens the release page, and the user unpacks the new folder when it suits them.
	/// </summary>
	internal sealed partial class PortableUpdateService : ObservableObject, IUpdateService
	{
		private const string LatestReleaseAddress = "https://api.github.com/repos/egorcha174/ConsystoFiles/releases/latest";

		private const string ReleasesPage = "https://github.com/egorcha174/ConsystoFiles/releases/latest";

		private bool _isUpdateAvailable;
		public bool IsUpdateAvailable
		{
			get => _isUpdateAvailable;
			private set => SetProperty(ref _isUpdateAvailable, value);
		}

		public bool IsUpdating => false;

		public int UpdateProgress => 0;

		public bool IsAppUpdated => AppLifecycleHelper.IsAppUpdated;

		private bool _areReleaseNotesAvailable;
		public bool AreReleaseNotesAvailable
		{
			get => _areReleaseNotesAvailable;
			private set => SetProperty(ref _areReleaseNotesAvailable, value);
		}

		public async Task CheckForUpdatesAsync()
		{
			try
			{
				using var client = new HttpClient();
				client.Timeout = TimeSpan.FromSeconds(15);
				client.DefaultRequestHeaders.Add("User-Agent", "ConsystoFiles");
				client.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");

				using var document = JsonDocument.Parse(await client.GetStringAsync(LatestReleaseAddress));
				if (!document.RootElement.TryGetProperty("tag_name", out var tag) || tag.GetString() is not { } name)
					return;

				IsUpdateAvailable = IsNewer(name);
			}
			catch (Exception ex)
			{
				// No network, no releases yet, GitHub unavailable: an update check is not worth a word to the user
				App.Logger?.LogInformation(ex, "Could not ask GitHub about a new version");
			}
		}

		/// <summary>Compares the release name ("v1.26.918.1349" or "1.26.918") with the version of the running program.</summary>
		private static bool IsNewer(string tag)
		{
			var digits = new string(tag.Where(c => char.IsDigit(c) || c == '.').ToArray()).Trim('.');
			if (!Version.TryParse(digits, out var published))
				return false;

			var current = AppStorage.PackageVersion;
			return published > new Version(current.Major, current.Minor, current.Build, current.Revision);
		}

		/// <summary>Opens the release page; replacing the folder is the user's own doing.</summary>
		public async Task DownloadUpdatesAsync()
			=> await Launcher.LaunchUriAsync(new Uri(ReleasesPage));

		public Task DownloadMandatoryUpdatesAsync()
			=> Task.CompletedTask;

		public Task CheckForReleaseNotesAsync()
		{
			AreReleaseNotesAvailable = false;
			return Task.CompletedTask;
		}

		public Task CheckAndUpdateFilesLauncherAsync()
			=> Task.CompletedTask;
	}
}
