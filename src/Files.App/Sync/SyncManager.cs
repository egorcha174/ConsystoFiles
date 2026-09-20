// Consysto fork: saved folder pairs for one-way backup, and the sidebar section that lists them.

using System.Collections.ObjectModel;
using System.Text.Encodings.Web;
using System.Text.Json;
using Files.App.Data.Items;
using Microsoft.Extensions.Logging;
using Windows.Storage;

namespace Files.App.Sync
{
	/// <summary>A source folder backed up into a target folder.</summary>
	public sealed class SyncPair
	{
		public required string Id { get; init; }
		public required string Name { get; set; }
		public required string Source { get; set; }
		public required string Target { get; set; }

		/// <summary>Names or masks left out of the backup; Inventor's OldVersions by default.</summary>
		public List<string> Exclusions { get; set; } = [.. SyncManager.DefaultExclusions];

		public DateTime? LastRun { get; set; }

		/// <summary>One line about the last run, shown on the page.</summary>
		public string? LastResult { get; set; }
	}

	/// <summary>A pair opens in a tab under "Sync:&lt;id&gt;"; "Sync:+" adds one.</summary>
	public static class SyncPaths
	{
		public const string Prefix = "Sync:";
		public const string AddPath = "Sync:+";
		public const string Glyph = "";

		public static bool IsSyncPath(string? path)
			=> path?.StartsWith(Prefix, StringComparison.Ordinal) == true;

		public static string ForPair(string id)
			=> Prefix + id;

		public static string? PairId(string? path)
			=> IsSyncPath(path) && path != AddPath ? path![Prefix.Length..] : null;
	}

	public sealed class SyncManager
	{
		public static readonly string[] DefaultExclusions = ["OldVersions", "~$*", "*.lck", "Thumbs.db", "desktop.ini"];

		private static string StoragePath
			=> SystemIO.Path.Combine(AppStorage.LocalFolderPath, "sync-pairs.json");

		private readonly List<SyncPair> pairs = [];
		private bool isLoaded;

		public static SyncManager Instance { get; } = new();

		/// <summary>Sidebar items: one per pair, then "Add pair…". The sidebar section shows this collection as it is.</summary>
		public ObservableCollection<LocationItem> SidebarItems { get; } = [];

		public event EventHandler? Changed;

		public IReadOnlyList<SyncPair> Pairs
		{
			get
			{
				EnsureLoaded();
				return pairs;
			}
		}

		public SyncPair? Find(string? path)
			=> SyncPaths.PairId(path) is { } id ? Pairs.FirstOrDefault(pair => pair.Id == id) : null;

		public string TitleOf(string? path)
			=> Find(path)?.Name ?? Strings.ConsystoSync.GetLocalizedResource();

		public SyncPair Add(string name, string source, string target, IEnumerable<string> exclusions)
		{
			EnsureLoaded();
			var pair = new SyncPair { Id = Guid.NewGuid().ToString("N")[..8], Name = name, Source = source, Target = target, Exclusions = [.. exclusions] };
			pairs.Add(pair);
			SaveAndNotify();
			return pair;
		}

		public void Update(SyncPair pair)
			=> SaveAndNotify();

		public void Remove(SyncPair pair)
		{
			pairs.Remove(pair);
			SaveAndNotify();
		}

		public void RecordRun(SyncPair pair, string result)
		{
			pair.LastRun = DateTime.Now;
			pair.LastResult = result;
			Save();
		}

		private void SaveAndNotify()
		{
			Save();
			RebuildSidebarItems();
			Changed?.Invoke(this, EventArgs.Empty);
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
						if (Text(element, "id") is not { } id || Text(element, "name") is not { } name
							|| Text(element, "source") is not { } source || Text(element, "target") is not { } target)
							continue;

						var pair = new SyncPair { Id = id, Name = name, Source = source, Target = target, LastResult = Text(element, "lastResult") };
						if (element.TryGetProperty("exclusions", out var exclusions) && exclusions.ValueKind == JsonValueKind.Array)
							pair.Exclusions = [.. exclusions.EnumerateArray().Select(value => value.GetString()).OfType<string>()];
						if (Text(element, "lastRun") is { } lastRun && DateTime.TryParse(lastRun, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var time))
							pair.LastRun = time;
						pairs.Add(pair);
					}
				}
			}
			catch (Exception ex)
			{
				App.Logger.LogWarning(ex, "The sync pairs could not be read");
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
					foreach (var pair in pairs)
					{
						writer.WriteStartObject();
						writer.WriteString("id", pair.Id);
						writer.WriteString("name", pair.Name);
						writer.WriteString("source", pair.Source);
						writer.WriteString("target", pair.Target);
						writer.WriteStartArray("exclusions");
						foreach (var exclusion in pair.Exclusions)
							writer.WriteStringValue(exclusion);
						writer.WriteEndArray();
						if (pair.LastRun is { } lastRun)
							writer.WriteString("lastRun", lastRun.ToString("o", System.Globalization.CultureInfo.InvariantCulture));
						if (pair.LastResult is not null)
							writer.WriteString("lastResult", pair.LastResult);
						writer.WriteEndObject();
					}

					writer.WriteEndArray();
				}

				SystemIO.File.WriteAllBytes(StoragePath, stream.ToArray());
			}
			catch (Exception ex)
			{
				App.Logger.LogWarning(ex, "The sync pairs could not be saved");
			}
		}

		private void RebuildSidebarItems()
		{
			SidebarItems.Clear();
			foreach (var pair in pairs)
				SidebarItems.Add(CreateItem(pair.Name, SyncPaths.ForPair(pair.Id), selects: true));
			SidebarItems.Add(CreateItem(Strings.ConsystoSyncAddPair.GetLocalizedResource(), SyncPaths.AddPath, selects: false));
		}

		private static LocationItem CreateItem(string text, string path, bool selects)
			=> new()
			{
				Text = text,
				Path = path,
				Section = SectionType.Sync,
				MenuOptions = new ContextMenuOptions(),
				SelectsOnInvoked = selects,
				ChildItems = null,
			};

		private static string? Text(JsonElement element, string name)
			=> element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
	}
}
