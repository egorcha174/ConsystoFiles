// Consysto fork: the user's OPDS catalogs — the stored list, their passwords and their sidebar items.

using System.Collections.Specialized;
using System.Net;
using System.Text.Encodings.Web;
using System.Text.Json;
using Consysto.BookPreview.Opds;
using Consysto.CadPreview.WinUI;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Security.Credentials;
using Windows.Storage;

namespace Files.App.Books.Opds
{
	public sealed class OpdsCatalog
	{
		public required string Id { get; init; }

		public required string Title { get; set; }

		public required string Address { get; set; }

		public string? UserName { get; set; }
	}

	/// <summary>
	/// Catalog list in LocalState\opds-catalogs.json; passwords in the Windows Credential Manager, never in the file.
	/// Also keeps one HTTP client per catalog and a short-lived cache of pages, so going back in a tab is instant.
	/// </summary>
	public sealed class OpdsCatalogManager
	{
		private const string CredentialResourcePrefix = "Consysto.Files.Opds.";
		private const int FeedCacheSize = 40;
		private static readonly TimeSpan FeedCacheLifetime = TimeSpan.FromMinutes(10);

		public static OpdsCatalogManager Instance { get; } = new();

		private readonly List<OpdsCatalog> catalogs = [];
		private readonly List<INavigationControlItem> sidebarItems = [];
		private readonly Dictionary<string, OpdsClient> clients = [];
		private readonly Dictionary<string, string> searchTemplates = [];
		private readonly Dictionary<string, (OpdsFeed Feed, DateTime Loaded)> feeds = [];
		private BitmapImage? catalogIcon;
		private BitmapImage? addIcon;
		private bool isLoaded;

		private static string StoragePath
			=> SystemIO.Path.Combine(ApplicationData.Current.LocalFolder.Path, "opds-catalogs.json");

		/// <summary>Raised with the sidebar section as the sender, the way the other sidebar managers do.</summary>
		public event EventHandler<NotifyCollectionChangedEventArgs>? DataChanged;

		public IReadOnlyList<OpdsCatalog> Catalogs
		{
			get
			{
				EnsureLoaded();
				return catalogs;
			}
		}

		public IReadOnlyList<INavigationControlItem> SidebarItems
		{
			get
			{
				EnsureLoaded();
				return sidebarItems;
			}
		}

		public OpdsCatalog? Find(string? path)
			=> OpdsPaths.CatalogId(path) is { } id ? Catalogs.FirstOrDefault(catalog => catalog.Id == id) : null;

		/// <summary>Tab and path bar title of a catalog page, or the section name for a catalog that is gone.</summary>
		public string TitleOf(string? path)
			=> Find(path)?.Title ?? Strings.ConsystoOpdsCatalogs.GetLocalizedResource();

		public OpdsCatalog Add(string title, string address, string? userName, string? password)
		{
			EnsureLoaded();
			var catalog = new OpdsCatalog
			{
				Id = Guid.NewGuid().ToString("N")[..8],
				Title = title,
				Address = address,
				UserName = NullIfEmpty(userName),
			};

			catalogs.Add(catalog);
			StorePassword(catalog, password);
			OnChanged();
			return catalog;
		}

		/// <summary>A null password keeps the saved one.</summary>
		public void Update(OpdsCatalog catalog, string title, string address, string? userName, string? password)
		{
			var previousUserName = catalog.UserName;
			catalog.Title = title;
			catalog.Address = address;
			catalog.UserName = NullIfEmpty(userName);

			if (previousUserName is not null && previousUserName != catalog.UserName)
				RemovePassword(catalog.Id, previousUserName);

			StorePassword(catalog, password);
			ForgetSession(catalog.Id);
			OnChanged();
		}

		public void Remove(OpdsCatalog catalog)
		{
			catalogs.Remove(catalog);
			if (catalog.UserName is not null)
				RemovePassword(catalog.Id, catalog.UserName);

			ForgetSession(catalog.Id);
			OnChanged();
		}

		public async Task<OpdsFeed> GetFeedAsync(OpdsCatalog catalog, Uri address, bool refresh, CancellationToken cancellationToken)
		{
			var key = address.AbsoluteUri;
			if (!refresh && feeds.TryGetValue(key, out var cached) && DateTime.UtcNow - cached.Loaded < FeedCacheLifetime)
				return cached.Feed;

			var feed = await GetClient(catalog).GetFeedAsync(address, cancellationToken);
			if (feeds.Count >= FeedCacheSize)
				feeds.Remove(feeds.MinBy(pair => pair.Value.Loaded).Key);

			feeds[key] = (feed, DateTime.UtcNow);
			if (feed.SearchTemplate is not null)
				searchTemplates[catalog.Id] = feed.SearchTemplate;

			return feed;
		}

		/// <summary>Catalogs link their search from the start page only; deeper pages reuse what was found there.</summary>
		public async Task<string?> GetSearchTemplateAsync(OpdsCatalog catalog, OpdsFeed? feed, CancellationToken cancellationToken)
		{
			if (feed is not null && (feed.SearchTemplate is not null || feed.SearchDescription is not null)
				&& await GetClient(catalog).GetSearchTemplateAsync(feed, cancellationToken) is { } template)
			{
				searchTemplates[catalog.Id] = template;
				return template;
			}

			return searchTemplates.GetValueOrDefault(catalog.Id);
		}

		public bool HasSearch(OpdsCatalog catalog, OpdsFeed feed)
			=> feed.SearchTemplate is not null || feed.SearchDescription is not null || searchTemplates.ContainsKey(catalog.Id);

		public Task<string> DownloadAsync(OpdsCatalog catalog, OpdsAcquisition acquisition, string directory, string baseName, IProgress<double> progress, CancellationToken cancellationToken)
			=> GetClient(catalog).DownloadAsync(acquisition, directory, baseName, progress, cancellationToken);

		public void SignIn(OpdsCatalog catalog, string userName, string password)
			=> Update(catalog, catalog.Title, catalog.Address, userName, password);

		public void ForgetPage(Uri address)
			=> feeds.Remove(address.AbsoluteUri);

		private OpdsClient GetClient(OpdsCatalog catalog)
		{
			if (!clients.TryGetValue(catalog.Id, out var client))
			{
				client = new OpdsClient();
				clients[catalog.Id] = client;
			}

			client.Credentials = catalog.UserName is { } userName && ReadPassword(catalog) is { } password
				? new NetworkCredential(userName, password)
				: null;
			return client;
		}

		private void ForgetSession(string id)
		{
			if (clients.Remove(id, out var client))
				client.Dispose();

			searchTemplates.Remove(id);
			feeds.Clear();
		}

		private void OnChanged()
		{
			Save();
			RebuildSidebarItems();
			DataChanged?.Invoke(SectionType.OpdsCatalogs, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
		}

		private void EnsureLoaded()
		{
			if (isLoaded)
				return;

			isLoaded = true;
			try
			{
				if (SystemIO.File.Exists(StoragePath))
				{
					using var document = JsonDocument.Parse(SystemIO.File.ReadAllBytes(StoragePath));
					foreach (var element in document.RootElement.EnumerateArray())
					{
						var id = Property(element, "id");
						var title = Property(element, "title");
						var address = Property(element, "address");
						if (id is null || title is null || address is null)
							continue;

						catalogs.Add(new OpdsCatalog { Id = id, Title = title, Address = address, UserName = Property(element, "userName") });
					}
				}
			}
			catch (Exception ex)
			{
				App.Logger.LogWarning(ex, "The OPDS catalog list could not be read");
			}

			RebuildSidebarItems();
		}

		private void Save()
		{
			try
			{
				using var stream = new SystemIO.MemoryStream();
				using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
				{
					writer.WriteStartArray();
					foreach (var catalog in catalogs)
					{
						writer.WriteStartObject();
						writer.WriteString("id", catalog.Id);
						writer.WriteString("title", catalog.Title);
						writer.WriteString("address", catalog.Address);
						if (catalog.UserName is not null)
							writer.WriteString("userName", catalog.UserName);

						writer.WriteEndObject();
					}

					writer.WriteEndArray();
				}

				SystemIO.File.WriteAllBytes(StoragePath, stream.ToArray());
			}
			catch (Exception ex)
			{
				App.Logger.LogWarning(ex, "The OPDS catalog list could not be saved");
			}
		}

		private void RebuildSidebarItems()
		{
			sidebarItems.Clear();
			foreach (var catalog in catalogs)
				sidebarItems.Add(CreateSidebarItem(catalog.Title, OpdsPaths.ForCatalog(catalog.Id)));

			sidebarItems.Add(CreateSidebarItem(Strings.ConsystoOpdsAddCatalog.GetLocalizedResource(), OpdsPaths.AddCatalogPath));
			_ = ApplySidebarIconsAsync();
		}

		private static LocationItem CreateSidebarItem(string text, string path)
			=> new()
			{
				Text = text,
				Path = path,
				Section = SectionType.OpdsCatalogs,
				MenuOptions = new ContextMenuOptions(),
				SelectsOnInvoked = !OpdsPaths.IsActionPath(path),
				ChildItems = null,
			};

		private async Task ApplySidebarIconsAsync()
		{
			try
			{
				var gray = Windows.UI.Color.FromArgb(255, 0x8A, 0x8A, 0x8A);
				catalogIcon ??= await (await GlyphRenderer.RenderAsync("\uE82D", 32, gray)).ToBitmapAsync();
				addIcon ??= await (await GlyphRenderer.RenderAsync("\uE710", 32, gray)).ToBitmapAsync();

				foreach (var item in sidebarItems.OfType<LocationItem>())
				{
					item.Icon = item.Path switch
					{
						OpdsPaths.AddCatalogPath => addIcon,
						_ => catalogIcon,
					};
				}
			}
			catch (Exception ex)
			{
				App.Logger.LogWarning(ex, "The OPDS sidebar icons could not be drawn");
			}
		}

		private static void StorePassword(OpdsCatalog catalog, string? password)
		{
			if (catalog.UserName is null || string.IsNullOrEmpty(password))
				return;

			try
			{
				new PasswordVault().Add(new PasswordCredential(CredentialResourcePrefix + catalog.Id, catalog.UserName, password));
			}
			catch (Exception ex)
			{
				App.Logger.LogWarning(ex, "The OPDS catalog password could not be stored");
			}
		}

		private static string? ReadPassword(OpdsCatalog catalog)
		{
			try
			{
				var credential = new PasswordVault().Retrieve(CredentialResourcePrefix + catalog.Id, catalog.UserName);
				credential.RetrievePassword();
				return credential.Password;
			}
			catch (Exception)
			{
				// Retrieve throws when nothing is stored
				return null;
			}
		}

		private static void RemovePassword(string id, string userName)
		{
			try
			{
				var vault = new PasswordVault();
				vault.Remove(vault.Retrieve(CredentialResourcePrefix + id, userName));
			}
			catch (Exception)
			{
				// Nothing stored
			}
		}

		private static string? Property(JsonElement element, string name)
			=> element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

		private static string? NullIfEmpty(string? value)
			=> string.IsNullOrWhiteSpace(value) ? null : value.Trim();
	}
}
