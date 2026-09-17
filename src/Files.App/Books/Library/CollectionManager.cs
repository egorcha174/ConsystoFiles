// Consysto fork: libraries of books, photos and music — Windows libraries given a kind, their indexes, and sharing books with a phone.

using System.Collections.Specialized;
using System.Net.Sockets;
using System.Text.Encodings.Web;
using System.Text.Json;
using Consysto.BookPreview.Server;
using Consysto.CadPreview.WinUI;
using Consysto.Collections;
using Microsoft.Extensions.Logging;
using Windows.Storage;

namespace Files.App.Books.Library
{
	/// <summary>A Windows library shown as a collection: folders and name come from the library, the kind from the user.</summary>
	public sealed class CollectionSettings
	{
		/// <summary>The library file name without its extension ("Pictures", "Книги").</summary>
		public required string Id { get; init; }

		public required string LibraryPath { get; init; }

		public required string KindId { get; set; }

		public required string Title { get; set; }

		public IReadOnlyList<string> Folders { get; set; } = [];
	}

	/// <summary>
	/// The model: the Libraries section is the one place for catalogued files. A Windows library opens as a plain folder, or as
	/// a collection page when it has a kind — Pictures and Music get theirs by default, any library can be given one.
	/// Kinds are kept in LocalState\collections.json, one index per library in LocalCache\collections. One books library at a time
	/// can be shared with a phone as an OPDS catalog: no password, the local network only, and only while Files runs.
	/// </summary>
	public sealed class CollectionManager
	{
		public const int DefaultPort = 8765;
		private const string FolderKind = "folder";
		private const uint SharedThumbnailSize = 256;
		private const uint SharedPdfCoverSize = 1024;

		public static CollectionManager Instance { get; } = new();

		private readonly Lock gate = new();
		private readonly Dictionary<string, string> kinds = new(StringComparer.OrdinalIgnoreCase);
		private readonly List<CollectionSettings> collections = [];
		private readonly Dictionary<string, CollectionIndex> indexes = [];
		private List<(string Id, string KindId, string[] Folders)> legacyCollections = [];
		private string? legacySharedId;
		private LocationItem? createLibraryItem;
		private OpdsServer? server;
		private bool isLoaded;
		private bool isStarted;
		private bool isSubscribed;

		/// <summary>The libraries that open as collections.</summary>
		public IReadOnlyList<CollectionSettings> Collections
		{
			get
			{
				lock (gate)
					return collections.ToArray();
			}
		}

		/// <summary>The books library shared with a phone; null when sharing is off.</summary>
		public string? SharedCollectionId { get; private set; }

		public int Port { get; private set; } = DefaultPort;

		/// <summary>Why sharing is on but not answering (the port is taken).</summary>
		public string? SharingError { get; private set; }

		/// <summary>Catalog addresses to type into the reader.</summary>
		public IReadOnlyList<string> Addresses
			=> LocalNetwork.Addresses().Select(address => $"http://{address}:{Port}/opds").ToArray();

		/// <summary>Raised on any thread: kinds or folders changed, a scan started or ended.</summary>
		public event EventHandler? StateChanged;

		/// <summary>Raised on a worker thread, a few times a second, while a collection is being scanned.</summary>
		public event EventHandler? ScanProgressChanged;

		/// <summary>"Create library…" at the end of the Libraries section. Read on the UI thread: the item carries an icon.</summary>
		public LocationItem CreateLibraryItem
		{
			get
			{
				if (createLibraryItem is not null)
					return createLibraryItem;

				createLibraryItem = new LocationItem
				{
					Text = Strings.ConsystoLibraryCreate.GetLocalizedResource(),
					Path = CollectionPaths.AddCollectionPath,
					Section = SectionType.Library,
					MenuOptions = new ContextMenuOptions(),
					SelectsOnInvoked = false,
					ChildItems = null,
				};
				_ = ApplyCreateIconAsync(createLibraryItem);
				return createLibraryItem;
			}
		}

		private static string SettingsPath
			=> SystemIO.Path.Combine(ApplicationData.Current.LocalFolder.Path, "collections.json");

		private static string IndexPathOf(string id)
			=> SystemIO.Path.Combine(ApplicationData.Current.LocalCacheFolder.Path, "collections", id + ".json");

		public CollectionSettings? Find(string? path)
			=> CollectionPaths.CollectionId(path) is { } id ? FindById(id) : null;

		public CollectionSettings? FindById(string id)
			=> Collections.FirstOrDefault(collection => string.Equals(collection.Id, id, StringComparison.OrdinalIgnoreCase));

		public CollectionSettings? FindByLibrary(string? libraryPath)
			=> Collections.FirstOrDefault(collection => string.Equals(collection.LibraryPath, libraryPath, StringComparison.OrdinalIgnoreCase));

		public string TitleOf(string? path)
			=> Find(path)?.Title ?? Strings.SidebarLibraries.GetLocalizedResource();

		public string GlyphOf(string? path)
			=> CollectionKinds.Find(Find(path)?.KindId)?.Glyph ?? FluentGlyphs.Library;

		/// <summary>The kind a library opens as; null for plain folders.</summary>
		public string? KindOfLibrary(string? libraryPath)
		{
			if (libraryPath is null)
				return null;

			lock (gate)
			{
				EnsureLoaded();
				return KindOfLibraryLocked(libraryPath);
			}
		}

		public CollectionSnapshot SnapshotOf(string id)
		{
			lock (gate)
				return indexes.GetValueOrDefault(id)?.Snapshot ?? CollectionSnapshot.Empty;
		}

		/// <summary>What the scan of a collection is doing right now; null when it is not scanning.</summary>
		public CollectionScanProgress? ScanProgressOf(string id)
		{
			lock (gate)
				return indexes.GetValueOrDefault(id)?.Progress;
		}

		/// <summary>Also true before the collections have started.</summary>
		public bool IsScanning(string id)
		{
			lock (gate)
				return !isStarted || indexes.GetValueOrDefault(id)?.IsScanning == true;
		}

		/// <summary>Called once at startup, off the UI thread; the libraries arrive later through the library manager.</summary>
		public void Initialize()
		{
			lock (gate)
			{
				EnsureLoaded();
				isStarted = true;
			}

			if (!isSubscribed)
			{
				isSubscribed = true;
				App.LibraryManager.DataChanged += LibraryManager_DataChanged;
			}

			SyncLibraries();
		}

		/// <summary>Makes a library open as a collection of <paramref name="kindId"/>, or as plain folders when it is null.</summary>
		public void SetKind(string libraryPath, string? kindId)
		{
			lock (gate)
			{
				EnsureLoaded();
				kinds[libraryPath] = kindId ?? FolderKind;
				Save();
			}

			SyncLibraries();
		}

		/// <summary>Creates a Windows library with one folder, pinned to the navigation pane, and gives it a kind.</summary>
		public async Task<CollectionSettings?> CreateLibraryAsync(string name, string? kindId, string folder)
		{
			if (!await App.LibraryManager.CreateNewLibrary(name))
				return null;

			// A new library starts with Documents in it; the chosen folder takes its place
			var path = SystemIO.Path.Combine(ShellLibraryItem.LibrariesPath, name + ShellLibraryItem.EXTENSION);
			await App.LibraryManager.UpdateLibrary(path, defaultSaveFolder: folder, folders: [folder], isPinned: true);
			SetKind(path, kindId);
			return FindByLibrary(path);
		}

		/// <summary>Changes the folders of the library itself, so Windows and Files see the same set.</summary>
		public async Task SetFoldersAsync(string id, IReadOnlyList<string> folders)
		{
			if (FindById(id) is not { } collection || folders.Count == 0)
				return;

			var library = App.LibraryManager.Libraries.FirstOrDefault(item => string.Equals(item.Path, collection.LibraryPath, StringComparison.OrdinalIgnoreCase));
			var keepsSaveFolder = library?.DefaultSaveFolder is { } saveFolder && folders.Contains(saveFolder, StringComparer.OrdinalIgnoreCase);
			await App.LibraryManager.UpdateLibrary(collection.LibraryPath, defaultSaveFolder: keepsSaveFolder ? null : folders[0], folders: [.. folders]);
		}

		/// <summary>Shares one books library, or none when <paramref name="collectionId"/> is null.</summary>
		public void SetSharing(string? collectionId, int port)
		{
			lock (gate)
			{
				EnsureLoaded();
				if (collectionId == SharedCollectionId && port == Port)
					return;

				SharedCollectionId = collectionId;
				Port = port;
				Save();

				StopServer();
				if (collectionId is not null && isStarted)
					StartServer();
			}

			OnStateChanged();
		}

		public Task RescanAsync(string id)
		{
			CollectionIndex? index;
			lock (gate)
				index = indexes.GetValueOrDefault(id);

			return index?.ScanAsync() ?? Task.CompletedTask;
		}

		private void LibraryManager_DataChanged(object? sender, NotifyCollectionChangedEventArgs e)
			=> SyncLibraries();

		private void SyncLibraries()
		{
			var libraries = App.LibraryManager.Libraries;
			lock (gate)
			{
				EnsureLoaded();
				MigrateLegacyCollections(libraries);

				var current = new List<CollectionSettings>();
				foreach (var library in libraries)
				{
					if (library.Path is not { } path || KindOfLibraryLocked(path) is not { } kindId || CollectionKinds.Find(kindId) is null)
						continue;

					var collection = collections.FirstOrDefault(item => string.Equals(item.LibraryPath, path, StringComparison.OrdinalIgnoreCase))
						?? new CollectionSettings { Id = SystemIO.Path.GetFileNameWithoutExtension(path), LibraryPath = path, KindId = kindId, Title = library.Text };

					if (indexes.TryGetValue(collection.Id, out var existing) && existing.Kind.Id != kindId)
					{
						existing.Dispose();
						indexes.Remove(collection.Id);
						if (SharedCollectionId == collection.Id)
							StopServer();
					}

					var folders = library.Folders.ToArray();
					var foldersChanged = !folders.SequenceEqual(collection.Folders, StringComparer.OrdinalIgnoreCase);
					collection.KindId = kindId;
					collection.Title = library.Text;
					collection.Folders = folders;
					current.Add(collection);

					if (!isStarted)
						continue;

					if (!indexes.ContainsKey(collection.Id))
						StartIndex(collection);
					else if (foldersChanged)
						indexes[collection.Id].SetRoots(folders);
				}

				foreach (var gone in collections.Where(item => !current.Contains(item)))
				{
					if (indexes.Remove(gone.Id, out var index))
						index.Dispose();
				}

				collections.Clear();
				collections.AddRange(current);

				var shared = SharedCollectionId is { } sharedId ? collections.FirstOrDefault(item => item.Id == sharedId) : null;
				if (shared is null || shared.KindId != CollectionKinds.BooksId)
					StopServer();
				else if (isStarted && server is null && SharingError is null)
					StartServer();
			}

			OnStateChanged();
		}

		private string? KindOfLibraryLocked(string libraryPath)
		{
			if (kinds.TryGetValue(libraryPath, out var kind))
				return kind == FolderKind ? null : kind;

			// The standard libraries say what they hold
			return SystemIO.Path.GetFileNameWithoutExtension(libraryPath) switch
			{
				"Pictures" => CollectionKinds.PhotosId,
				"Music" => CollectionKinds.MusicId,
				_ => null,
			};
		}

		private void StartIndex(CollectionSettings collection)
		{
			if (CollectionKinds.Find(collection.KindId) is not { } kind)
				return;

			var index = new CollectionIndex(kind.Create(), IndexPathOf(collection.Id));
			index.Changed += (_, _) => OnStateChanged();
			index.ProgressChanged += (_, _) => ScanProgressChanged?.Invoke(this, EventArgs.Empty);
			indexes[collection.Id] = index;
			index.SetRoots(collection.Folders);
		}

		private void StartServer()
		{
			SharingError = null;
			if (SharedCollectionId is not { } id || !indexes.TryGetValue(id, out var index) || index.Kind is not Consysto.BookPreview.Library.BookCollection)
				return;

			var candidate = new OpdsServer(index, new OpdsServerOptions
			{
				Port = Port,
				Title = string.Format(Strings.ConsystoBookServerCatalogTitle.GetLocalizedResource(), Environment.MachineName),
				CoverProvider = GetSharedCoverAsync,
				Log = message => App.Logger.LogInformation("Book server: {Message}", message),
			});

			try
			{
				candidate.Start();
				server = candidate;
				App.Logger.LogInformation("Book server is listening on port {Port}", Port);
			}
			catch (SocketException ex)
			{
				candidate.Dispose();
				SharingError = ex.SocketErrorCode is SocketError.AddressAlreadyInUse or SocketError.AccessDenied
					? string.Format(Strings.ConsystoBookServerPortBusy.GetLocalizedResource(), Port)
					: string.Format(Strings.ConsystoBookServerError.GetLocalizedResource(), ex.Message);
				App.Logger.LogWarning(ex, "Book server could not start on port {Port}", Port);
			}
		}

		private void StopServer()
		{
			server?.Dispose();
			server = null;
			SharingError = null;
		}

		/// <summary>
		/// Full-size covers of EPUB, FictionBook and Kindle books go out as stored in the book (the server reads them itself);
		/// thumbnails and PDF pages are drawn and cached on disk by the thumbnail service.
		/// </summary>
		private static async Task<byte[]?> GetSharedCoverAsync(CollectionItem book, bool thumbnail, CancellationToken cancellationToken)
		{
			if (!thumbnail && Consysto.BookPreview.BookReader.GetFormat(book.Path) != Consysto.BookPreview.BookFormat.Pdf || !BookThumbnailService.IsSupported(book.Path))
				return null;

			return await BookThumbnailService.GetThumbnailAsync(book.Path, thumbnail ? SharedThumbnailSize : SharedPdfCoverSize);
		}

		private void OnStateChanged()
			=> StateChanged?.Invoke(this, EventArgs.Empty);

		private static async Task ApplyCreateIconAsync(LocationItem item)
		{
			try
			{
				item.Icon = await (await GlyphRenderer.RenderAsync(FluentGlyphs.Add, 32, Windows.UI.Color.FromArgb(255, 0x8A, 0x8A, 0x8A))).ToBitmapAsync();
			}
			catch (Exception ex)
			{
				App.Logger.LogWarning(ex, "The create library icon could not be drawn");
			}
		}

		private void EnsureLoaded()
		{
			if (isLoaded)
				return;

			isLoaded = true;
			try
			{
				if (!SystemIO.File.Exists(SettingsPath))
					return;

				using var document = JsonDocument.Parse(SystemIO.File.ReadAllBytes(SettingsPath));
				var root = document.RootElement;
				if (root.TryGetProperty("libraries", out var stored) && stored.ValueKind == JsonValueKind.Object)
				{
					foreach (var library in stored.EnumerateObject())
					{
						if (library.Value.ValueKind == JsonValueKind.String)
							kinds[library.Name] = library.Value.GetString()!;
					}
				}

				// Collections of the build before libraries: each finds its library by the same folders
				if (root.TryGetProperty("collections", out var legacy) && legacy.ValueKind == JsonValueKind.Array)
				{
					legacyCollections = legacy.EnumerateArray()
						.Where(element => Text(element, "id") is not null && Text(element, "kind") is not null)
						.Select(element => (Text(element, "id")!, Text(element, "kind")!, Texts(element, "folders")))
						.ToList();
				}

				if (root.TryGetProperty("sharing", out var sharing) && sharing.ValueKind == JsonValueKind.Object)
				{
					SharedCollectionId = Text(sharing, "collection");
					if (legacyCollections.Count > 0)
						legacySharedId = SharedCollectionId;

					if (sharing.TryGetProperty("port", out var port) && port.TryGetInt32(out var number) && number is >= 1024 and <= 65535)
						Port = number;
				}
			}
			catch (Exception ex) when (ex is SystemIO.IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
			{
				App.Logger.LogWarning(ex, "Collection settings could not be read");
			}
		}

		private void MigrateLegacyCollections(IReadOnlyList<LibraryLocationItem> libraries)
		{
			if (legacyCollections.Count == 0 || libraries.Count == 0)
				return;

			foreach (var (id, kindId, folders) in legacyCollections)
			{
				var library = libraries.FirstOrDefault(item =>
					item.Path is not null && item.Folders.Count == folders.Length && item.Folders.All(folder => folders.Contains(folder, StringComparer.OrdinalIgnoreCase)));
				if (library?.Path is not { } path)
				{
					App.Logger.LogInformation("Collection {Collection} has no library with the same folders and was not carried over", id);
					continue;
				}

				kinds.TryAdd(path, kindId);
				if (legacySharedId == id)
					SharedCollectionId = SystemIO.Path.GetFileNameWithoutExtension(path);
			}

			legacyCollections = [];
			legacySharedId = null;
			Save();
		}

		private void Save()
		{
			try
			{
				using var stream = new SystemIO.MemoryStream();
				using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
				{
					writer.WriteStartObject();
					writer.WriteStartObject("libraries");
					foreach (var (path, kind) in kinds)
						writer.WriteString(path, kind);

					writer.WriteEndObject();
					writer.WriteStartObject("sharing");
					if (SharedCollectionId is null)
						writer.WriteNull("collection");
					else
						writer.WriteString("collection", SharedCollectionId);

					writer.WriteNumber("port", Port);
					writer.WriteEndObject();
					writer.WriteEndObject();
				}

				SystemIO.File.WriteAllBytes(SettingsPath, stream.ToArray());
			}
			catch (Exception ex) when (ex is SystemIO.IOException or UnauthorizedAccessException)
			{
				App.Logger.LogWarning(ex, "Collection settings could not be saved");
			}
		}

		private static string? Text(JsonElement element, string name)
			=> element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

		private static string[] Texts(JsonElement element, string name)
			=> element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
				? value.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!).ToArray()
				: [];
	}
}
