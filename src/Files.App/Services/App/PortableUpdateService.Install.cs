// Consysto fork: the installed copy updates itself through its own NSIS setup.

using Microsoft.Extensions.Logging;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using Windows.System;

namespace Files.App.Services
{
	internal sealed partial class PortableUpdateService
	{
		private static readonly string DownloadFolder = Path.Combine(Path.GetTempPath(), "ConsystoFilesUpdate");

		// A 100 MB setup over a slow line takes longer than the 15 seconds an update check may
		private static readonly HttpClient DownloadClient = CreateDownloadClient();

		/// <summary>
		/// Downloads the setup, checks it against both consysto.pro and GitHub, and hands over to it: the setup waits for the
		/// app to close, replaces the program files keeping "data", and starts the app again with the same tabs.
		/// </summary>
		private async Task InstallAsync(ConsystoReleaseModel release, ConsystoReleaseAsset setup)
		{
			var title = string.Format(Strings.ConsystoUpdateDialogTitle.GetLocalizedResource(), release.Version);
			if (Ioc.Default.GetRequiredService<StatusCenterViewModel>().HasAnyItemInProgress)
			{
				await DialogDisplayHelper.ShowDialogAsync(title, Strings.ConsystoUpdateWaitForOperations.GetLocalizedResource());
				return;
			}

			IsUpdating = true;
			UpdateProgress = 0;

			string? path = null;
			try
			{
				if (await IsConfirmedByOtherSourceAsync(release, setup))
					path = await DownloadAsync(setup);
				else
					App.Logger?.LogWarning("The setup {Name} is not confirmed by the second source", setup.Name);
			}
			catch (Exception ex)
			{
				App.Logger?.LogWarning(ex, "Could not download the setup {Name}", setup.Name);
			}

			if (path is null)
			{
				IsUpdating = false;
				UpdateProgress = 0;
				await DialogDisplayHelper.ShowDialogAsync(title, Strings.ConsystoUpdateDownloadFailed.GetLocalizedResource());
				await Launcher.LaunchUriAsync(release.Page);
				return;
			}

			// The same steps as the restart from settings: the next start reopens these tabs
			Ioc.Default.GetRequiredService<IUserSettingsService>().AppSettingsService.RestoreTabsOnStartup = true;
			AppLifecycleHelper.SaveSessionTabs();

			try
			{
				Process.Start(new ProcessStartInfo(path, "/S /UPDATE") { UseShellExecute = false, WorkingDirectory = DownloadFolder });
			}
			catch (Exception ex)
			{
				App.Logger?.LogWarning(ex, "Could not start the setup {Path}", path);
				IsUpdating = false;
				UpdateProgress = 0;
				await DialogDisplayHelper.ShowDialogAsync(title, Strings.ConsystoUpdateDownloadFailed.GetLocalizedResource());
				return;
			}

			App.AppModel.ForceProcessTermination = true;
			Environment.Exit(0);
		}

		/// <summary>
		/// The checksum must be the same on consysto.pro and on GitHub, so a file swapped on one of them is never run.
		/// When the second source is unreachable, the update is left to the browser.
		/// </summary>
		private static async Task<bool> IsConfirmedByOtherSourceAsync(ConsystoReleaseModel release, ConsystoReleaseAsset setup)
		{
			if (TestSite is not null)
				return true;

			var other = release.FromSite
				? await TryAsync(() => ReadGitHubReleaseAsync($"https://api.github.com/repos/{Repository}/releases/tags/v{release.Version}"), "GitHub")
				: await TryAsync(ReadSiteCatalogAsync, "consysto.pro");

			return other is not null && other.Version == release.Version && other.Installer is { } confirmed &&
				confirmed.Name == setup.Name && confirmed.Size == setup.Size &&
				string.Equals(confirmed.Sha256, setup.Sha256, StringComparison.OrdinalIgnoreCase);
		}

		/// <summary>Downloads from the site, then from GitHub; returns the path of a file whose size and SHA-256 match.</summary>
		private async Task<string?> DownloadAsync(ConsystoReleaseAsset setup)
		{
			// The name comes from the catalog and becomes a file name and a program to run
			if (Path.GetFileName(setup.Name) != setup.Name || !setup.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
				return null;

			Directory.CreateDirectory(DownloadFolder);
			var path = Path.Combine(DownloadFolder, setup.Name);

			foreach (var address in new[] { setup.DownloadUri, setup.MirrorUri })
			{
				if (address is null)
					continue;

				try
				{
					if (await DownloadFileAsync(address, path, setup))
						return path;
				}
				catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
				{
					App.Logger?.LogInformation(ex, "Could not download {Address}", address);
				}

				SafetyExtensions.IgnoreExceptions(() => File.Delete(path));
			}

			return null;
		}

		private async Task<bool> DownloadFileAsync(Uri address, string path, ConsystoReleaseAsset setup)
		{
			UpdateProgress = 0;

			using var response = await DownloadClient.GetAsync(address, HttpCompletionOption.ResponseHeadersRead);
			response.EnsureSuccessStatusCode();

			using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
			await using (var source = await response.Content.ReadAsStreamAsync())
			await using (var target = File.Create(path))
			{
				var buffer = new byte[81920];
				long total = 0;
				int read;
				while ((read = await source.ReadAsync(buffer)) > 0)
				{
					total += read;
					if (total > setup.Size)
						return false;

					hash.AppendData(buffer, 0, read);
					await target.WriteAsync(buffer.AsMemory(0, read));
					UpdateProgress = (int)(total * 100 / setup.Size);
				}

				if (total != setup.Size)
					return false;
			}

			return string.Equals(Convert.ToHexString(hash.GetHashAndReset()), setup.Sha256, StringComparison.OrdinalIgnoreCase);
		}

		/// <summary>The setup of the previous update cannot remove itself while it runs, so the next start does.</summary>
		private static void RemoveDownloadedSetup()
			=> SafetyExtensions.IgnoreExceptions(() =>
			{
				if (Directory.Exists(DownloadFolder))
					Directory.Delete(DownloadFolder, true);
			});

		private static HttpClient CreateDownloadClient()
		{
			var client = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
			client.DefaultRequestHeaders.Add("User-Agent", "ConsystoFiles/" + CurrentVersion);
			return client;
		}
	}
}
