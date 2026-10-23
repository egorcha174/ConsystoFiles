// Consysto fork: the release builds have no package to update, so the app reads the consysto.pro download catalog itself.

using Microsoft.Extensions.Logging;
using System.Net.Http;
using System.Text;
using Windows.System;

namespace Files.App.Services
{
	/// <summary>
	/// Learns about a newer release from the consysto.pro catalog, or from GitHub when the site is unreachable, and offers it
	/// together with what changed since the running version. The installed copy updates itself when the user agrees;
	/// the portable one opens the new zip in the browser.
	/// </summary>
	internal sealed partial class PortableUpdateService : ObservableObject, IUpdateService
	{
		private const string SiteAddress = "https://consysto.pro";

		private const string CatalogPath = "/downloads/releases/catalog.json";

		private const string SiteFilesPrefix = "/downloads/releases/files/";

		private const string Repository = "egorcha174/ConsystoFiles";

		private const string GitHubLatestAddress = "https://api.github.com/repos/" + Repository + "/releases/latest";

		private const string GitHubReleasesAddress = "https://api.github.com/repos/" + Repository + "/releases?per_page=30";

		private const string GitHubDownloadPrefix = "https://github.com/" + Repository + "/releases/download/";

		private const string GitHubReleasesPage = "https://github.com/" + Repository + "/releases";

		private static readonly HttpClient Client = CreateClient();

		/// <summary>
		/// For trying an update before a release: CONSYSTO_UPDATE_TEST_SITE=http://localhost:8911 serves its own
		/// downloads/releases/catalog.json and files. Only this computer can be named, and GitHub is then not asked.
		/// </summary>
		private static readonly string? TestSite = Environment.GetEnvironmentVariable("CONSYSTO_UPDATE_TEST_SITE") is { } site &&
			Uri.TryCreate(site, UriKind.Absolute, out var uri) && uri.IsLoopback && uri.Scheme == Uri.UriSchemeHttp
				? uri.GetLeftPart(UriPartial.Authority)
				: null;

		private static string Site
			=> TestSite ?? SiteAddress;

		private readonly IApplicationSettingsService _settings;

		private readonly IDialogService _dialogs;

		private IReadOnlyList<ConsystoReleaseNotes>? _notes;

		public PortableUpdateService(IApplicationSettingsService settings, IDialogService dialogs)
		{
			_settings = settings;
			_dialogs = dialogs;
		}

		/// <summary>The NSIS setup leaves its uninstaller next to Files.exe; the portable zip has none.</summary>
		public static bool IsInstalledCopy { get; } = SystemIO.File.Exists(SystemIO.Path.Combine(AppContext.BaseDirectory, "Uninstall.exe"));

		public static Version CurrentVersion
		{
			get
			{
				var version = AppStorage.PackageVersion;
				return new Version(version.Major, version.Minor, version.Build, version.Revision);
			}
		}

		private ConsystoReleaseModel? _latestRelease;
		/// <summary>The newest release found by the last successful check.</summary>
		public ConsystoReleaseModel? LatestRelease
		{
			get => _latestRelease;
			private set => SetProperty(ref _latestRelease, value);
		}

		private bool _isUpdateAvailable;
		public bool IsUpdateAvailable
		{
			get => _isUpdateAvailable;
			private set => SetProperty(ref _isUpdateAvailable, value);
		}

		private bool _isUpdating;
		public bool IsUpdating
		{
			get => _isUpdating;
			private set => SetProperty(ref _isUpdating, value);
		}

		private int _updateProgress;
		public int UpdateProgress
		{
			get => _updateProgress;
			private set => SetProperty(ref _updateProgress, value);
		}

		public bool IsAppUpdated => AppLifecycleHelper.IsAppUpdated;

		private bool _areReleaseNotesAvailable;
		public bool AreReleaseNotesAvailable
		{
			get => _areReleaseNotesAvailable;
			private set => SetProperty(ref _areReleaseNotesAvailable, value);
		}

		/// <summary>The scheduled check: respects the switch in settings, a skipped version, and offers each version once.</summary>
		public async Task CheckForUpdatesAsync()
		{
			if (!IsUpdating)
				RemoveDownloadedSetup();

			if (!_settings.CheckForUpdatesAutomatically)
				return;

			var release = await FindLatestReleaseAsync();
			if (release is null || release.Version <= CurrentVersion)
				return;

			var version = release.Version.ToString();
			IsUpdateAvailable = version != _settings.SkippedUpdateVersion;
			if (!IsUpdateAvailable || version == _settings.LastOfferedUpdateVersion)
				return;

			_settings.LastOfferedUpdateVersion = version;

			// Wait for the restored tabs so the dialog doesn't cover the window while it is still opening
			await MainPageViewModel.StartupTabsLoadedTask;
			await MainWindow.Instance.DispatcherQueue.EnqueueOrInvokeAsync(OfferUpdateAsync);
		}

		/// <summary>The "Check now" button: ignores the switch and a skipped version, and shows the dialog when there is something new.</summary>
		public async Task<bool?> CheckNowAsync()
		{
			var release = await FindLatestReleaseAsync();
			if (release is null)
				return null;

			IsUpdateAvailable = release.Version > CurrentVersion;
			if (IsUpdateAvailable)
				await OfferUpdateAsync();

			return IsUpdateAvailable;
		}

		/// <summary>The toolbar button.</summary>
		public Task DownloadUpdatesAsync()
			=> OfferUpdateAsync();

		private async Task OfferUpdateAsync()
		{
			if (LatestRelease is not { } release || IsUpdating)
				return;

			var canInstall = IsInstalledCopy && release.Installer is not null;
			var asset = IsInstalledCopy ? release.Installer ?? release.Portable : release.Portable ?? release.Installer;
			var viewModel = new UpdateAvailableDialogViewModel(release, CurrentVersion, IsInstalledCopy, canInstall, GetReleaseNotesAsync);

			switch (await _dialogs.ShowDialogAsync(viewModel))
			{
				case DialogResult.Primary when canInstall:
					await InstallAsync(release, release.Installer!);
					break;
				case DialogResult.Primary:
					await Launcher.LaunchUriAsync(asset?.DownloadUri ?? release.Page);
					break;
				case DialogResult.Secondary:
					_settings.SkippedUpdateVersion = release.Version.ToString();
					IsUpdateAvailable = false;
					break;
			}
		}

		public Task DownloadMandatoryUpdatesAsync()
			=> Task.CompletedTask;

		public Task CheckForReleaseNotesAsync()
		{
			AreReleaseNotesAvailable = false;
			return Task.CompletedTask;
		}

		public Task CheckAndUpdateFilesLauncherAsync()
			=> Task.CompletedTask;

		private async Task<ConsystoReleaseModel?> FindLatestReleaseAsync()
		{
			var release = await TryAsync(ReadSiteCatalogAsync, "consysto.pro");
			if (release is null && TestSite is null)
				release = await TryAsync(ReadGitHubLatestAsync, "GitHub");
			if (release is not null)
			{
				if (release.Version != LatestRelease?.Version)
					_notes = null;

				LatestRelease = release;
			}

			return release;
		}

		private static async Task<T?> TryAsync<T>(Func<Task<T?>> read, string source) where T : class
		{
			try
			{
				return await read();
			}
			catch (Exception ex)
			{
				// No network or the source is down: an update check is not worth a word to the user
				App.Logger?.LogInformation(ex, "Could not ask {Source} about a new version", source);
				return null;
			}
		}

		private static async Task<ConsystoReleaseModel?> ReadSiteCatalogAsync()
		{
			using var document = JsonDocument.Parse(await Client.GetStringAsync(Site + CatalogPath));
			var root = document.RootElement;
			if (!root.TryGetProperty("schema", out var schema) || schema.GetInt32() != 1 ||
				!root.TryGetProperty("products", out var products) || !products.TryGetProperty("files", out var files) ||
				!TryGetVersion(files, "version", out var version) || !files.TryGetProperty("assets", out var assets))
				return null;

			ConsystoReleaseAsset? installer = null, portable = null;
			foreach (var item in assets.EnumerateArray())
			{
				var name = GetString(item, "name");
				var sha256 = GetString(item, "sha256");
				var siteUrl = GetString(item, "siteUrl");
				if (name is null || sha256 is null || siteUrl is null ||
					!siteUrl.StartsWith(SiteFilesPrefix, StringComparison.Ordinal) ||
					!item.TryGetProperty("size", out var size) || !size.TryGetInt64(out var length))
					continue;

				var mirror = GetString(item, "githubUrl") is { } githubUrl && githubUrl.StartsWith(GitHubDownloadPrefix, StringComparison.Ordinal)
					? new Uri(githubUrl)
					: null;
				var asset = new ConsystoReleaseAsset(name, length, sha256, new Uri(Site + siteUrl), mirror);
				switch (GetString(item, "role"))
				{
					case "installer":
						installer = asset;
						break;
					case "portable":
						portable = asset;
						break;
				}
			}

			return new(version, installer, portable, new Uri(Site + "/downloads.html?product=files"), FromSite: true);
		}

		private static Task<ConsystoReleaseModel?> ReadGitHubLatestAsync()
			=> ReadGitHubReleaseAsync(GitHubLatestAddress);

		private static async Task<ConsystoReleaseModel?> ReadGitHubReleaseAsync(string address)
		{
			using var document = JsonDocument.Parse(await Client.GetStringAsync(address));
			var root = document.RootElement;
			if (!TryGetVersion(root, "tag_name", out var version) || !root.TryGetProperty("assets", out var assets))
				return null;

			ConsystoReleaseAsset? installer = null, portable = null;
			foreach (var item in assets.EnumerateArray())
			{
				var name = GetString(item, "name");
				var digest = GetString(item, "digest");
				var url = GetString(item, "browser_download_url");
				if (name is null || digest is null || url is null ||
					!digest.StartsWith("sha256:", StringComparison.Ordinal) ||
					!url.StartsWith(GitHubDownloadPrefix, StringComparison.Ordinal) ||
					!item.TryGetProperty("size", out var size) || !size.TryGetInt64(out var length))
					continue;

				var asset = new ConsystoReleaseAsset(name, length, digest["sha256:".Length..], new Uri(url), null);
				if (name.StartsWith("ConsystoFiles-setup_", StringComparison.OrdinalIgnoreCase))
					installer = asset;
				else if (name.StartsWith("ConsystoFiles-portable_", StringComparison.OrdinalIgnoreCase))
					portable = asset;
			}

			var page = GetString(root, "html_url") is { } address && address.StartsWith("https://github.com/" + Repository + "/", StringComparison.Ordinal)
				? new Uri(address)
				: new Uri(GitHubReleasesPage);

			return new(version, installer, portable, page, FromSite: false);
		}

		/// <summary>
		/// The "What's new" sections of every release newer than the running one, newest first. Only GitHub keeps the release
		/// texts; when it is unreachable the dialog points to the release page instead.
		/// </summary>
		private async Task<IReadOnlyList<ConsystoReleaseNotes>> GetReleaseNotesAsync()
		{
			if (_notes is not null)
				return _notes;

			var latest = LatestRelease?.Version;
			var current = CurrentVersion;
			var russian = Strings.ConsystoUpdateNotesLanguage.GetLocalizedResource() == "ru";
			var notes = new List<ConsystoReleaseNotes>();

			var loaded = await TryAsync(async () =>
			{
				using var document = JsonDocument.Parse(await Client.GetStringAsync(GitHubReleasesAddress));
				foreach (var item in document.RootElement.EnumerateArray())
				{
					if ((item.TryGetProperty("draft", out var draft) && draft.GetBoolean()) ||
						(item.TryGetProperty("prerelease", out var prerelease) && prerelease.GetBoolean()) ||
						!TryGetVersion(item, "tag_name", out var version) ||
						version <= current || (latest is not null && version > latest))
						continue;

					if (GetString(item, "body") is { } body && ExtractWhatsNew(body, russian) is { } text)
						notes.Add(new(version, text));
				}

				return notes;
			}, "GitHub");

			if (loaded is null)
				return [];

			_notes = notes.OrderByDescending(x => x.Version).ToList();
			return _notes;
		}

		/// <summary>
		/// The "## Что нового · What's new" section of a release text: Russian first, then English after a "---" line.
		/// </summary>
		internal static string? ExtractWhatsNew(string body, bool russian)
		{
			var lines = body.Replace("\r\n", "\n").Split('\n');
			var start = Array.FindIndex(lines, x => x.StartsWith("## ", StringComparison.Ordinal) &&
				(x.Contains("Что нового", StringComparison.OrdinalIgnoreCase) || x.Contains("What's new", StringComparison.OrdinalIgnoreCase)));
			if (start < 0)
				return null;

			var parts = new List<StringBuilder> { new() };
			for (var i = start + 1; i < lines.Length && !lines[i].StartsWith("## ", StringComparison.Ordinal); i++)
			{
				if (lines[i].Trim() == "---")
					parts.Add(new());
				else
					parts[^1].AppendLine(lines[i]);
			}

			var texts = parts.Select(x => x.ToString().Trim()).Where(x => x.Length > 0).ToList();
			if (texts.Count == 0)
				return null;

			return russian || texts.Count == 1 ? texts[0] : texts[1];
		}

		/// <summary>Reads "v1.26.1023.946" or "1.26.1023.946".</summary>
		private static bool TryGetVersion(JsonElement element, string property, out Version version)
		{
			version = new();
			if (GetString(element, property) is not { } text || !Version.TryParse(text.TrimStart('v', 'V'), out var parsed))
				return false;

			version = parsed;
			return true;
		}

		private static string? GetString(JsonElement element, string property)
			=> element.TryGetProperty(property, out var value) && value.ValueKind is JsonValueKind.String ? value.GetString() : null;

		private static HttpClient CreateClient()
		{
			var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
			client.DefaultRequestHeaders.Add("User-Agent", "ConsystoFiles/" + CurrentVersion);
			client.DefaultRequestHeaders.Add("Accept", "application/json, application/vnd.github+json");
			return client;
		}
	}
}
