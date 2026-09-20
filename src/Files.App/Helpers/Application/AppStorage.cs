// Consysto fork: where the app keeps its data — the package's folders when installed, a "data" folder next to the program
// in the portable build.

using System.Text;
using System.Text.Json;
using Windows.Storage;

namespace Files.App.Helpers
{
	/// <summary>
	/// The app's folders and small settings store. An installed package has them from Windows (ApplicationData); a portable
	/// build has no package identity, so everything lives in a "data" folder beside Files.exe and moves with it.
	/// </summary>
	public static partial class AppStorage
	{
#if CONSYSTO_PORTABLE
		public const bool IsPortable = true;
#else
		public const bool IsPortable = false;
#endif

		/// <summary>The portable data folder, next to the program.</summary>
		public static string PortableDataPath
			=> SystemIO.Path.Combine(AppContext.BaseDirectory, "data");

		public static string LocalFolderPath
			=> IsPortable ? Ensure("Local") : ApplicationData.Current.LocalFolder.Path;

		public static string LocalCacheFolderPath
			=> IsPortable ? Ensure("Cache") : ApplicationData.Current.LocalCacheFolder.Path;

		public static string TemporaryFolderPath
			=> IsPortable ? Ensure("Temp") : ApplicationData.Current.TemporaryFolder.Path;

		public static string RoamingFolderPath
			=> IsPortable ? Ensure("Roaming") : ApplicationData.Current.RoamingFolder.Path;

		public static async Task<StorageFolder> GetLocalFolderAsync()
			=> IsPortable ? await StorageFolder.GetFolderFromPathAsync(LocalFolderPath) : ApplicationData.Current.LocalFolder;

		public static async Task<StorageFolder> GetLocalCacheFolderAsync()
			=> IsPortable ? await StorageFolder.GetFolderFromPathAsync(LocalCacheFolderPath) : ApplicationData.Current.LocalCacheFolder;

		public static async Task<StorageFolder> GetTemporaryFolderAsync()
			=> IsPortable ? await StorageFolder.GetFolderFromPathAsync(TemporaryFolderPath) : ApplicationData.Current.TemporaryFolder;

		public static async Task<StorageFolder> GetRoamingFolderAsync()
			=> IsPortable ? await StorageFolder.GetFolderFromPathAsync(RoamingFolderPath) : ApplicationData.Current.RoamingFolder;

		/// <summary>The package name, or a fixed name for the portable build (used in registry keys and drag and drop).</summary>
		public static string PackageName
			=> IsPortable ? "ConsystoFiles.Portable" : Windows.ApplicationModel.Package.Current.Id.Name;

		public static string PackageFamilyName
			=> IsPortable ? "ConsystoFiles.Portable" : Windows.ApplicationModel.Package.Current.Id.FamilyName;

		public static string DisplayName
			=> IsPortable ? "Consysto Files" : Windows.ApplicationModel.Package.Current.DisplayName;

		/// <summary>The version of the package, or of Files.exe in the portable build.</summary>
		public static Windows.ApplicationModel.PackageVersion PackageVersion
		{
			get
			{
				if (!IsPortable)
					return Windows.ApplicationModel.Package.Current.Id.Version;

				var version = typeof(AppStorage).Assembly.GetName().Version ?? new Version(1, 0, 0, 0);
				return new Windows.ApplicationModel.PackageVersion
				{
					Major = (ushort)version.Major,
					Minor = (ushort)version.Minor,
					Build = (ushort)Math.Max(0, version.Build),
					Revision = (ushort)Math.Max(0, version.Revision),
				};
			}
		}

		/// <summary>The folder the program runs from.</summary>
		public static string InstalledPath
			=> IsPortable ? AppContext.BaseDirectory.TrimEnd('\\') : Windows.ApplicationModel.Package.Current.InstalledLocation.Path;

		/// <summary>The folder the running program's files are in; for a package, where its files really are.</summary>
		public static string EffectivePath
			=> IsPortable ? AppContext.BaseDirectory.TrimEnd('\\') : Windows.ApplicationModel.Package.Current.EffectivePath;

		/// <summary>Small values shared by all running instances (which window is active, one-off flags).</summary>
		public static IDictionary<string, object> LocalSettings
			=> IsPortable ? new PortableSettings(null) : ApplicationData.Current.LocalSettings.Values;

		/// <summary>A named group of values, like a container of the package's local settings.</summary>
		public static IDictionary<string, object> SettingsContainer(string name)
			=> IsPortable
				? new PortableSettings(name)
				: ApplicationData.Current.LocalSettings.CreateContainer(name, ApplicationDataCreateDisposition.Always).Values;

		/// <summary>
		/// Where the app keeps its own registry values (folder view preferences, file tags, launch count): the user's registry
		/// when installed, a private hive file "data\registry.dat" in the portable build, so nothing is left behind in Windows.
		/// </summary>
		public static Microsoft.Win32.RegistryKey UserRegistry
			=> IsPortable ? PortableHive.Value : Microsoft.Win32.Registry.CurrentUser;

		// Loaded once and kept open: Windows keeps the hive loaded while a handle to it is open
		private static readonly Lazy<Microsoft.Win32.RegistryKey> PortableHive = new(OpenPortableHive);

		/// <summary>
		/// Opens the hive file, and never lets the program fail over it: these values are view preferences and file tags, worth
		/// far less than a start. Windows opens a hive file for one process at a time, so a second copy of the program — and a
		/// folder that cannot be written to — fall back to a temporary hive of their own, which is dropped when they close.
		/// </summary>
		private static Microsoft.Win32.RegistryKey OpenPortableHive()
		{
			var path = SystemIO.Path.Combine(PortableDataPath, "registry.dat");
			if (TryLoadHive(path, out var hive))
				return hive;

			// A damaged file is set aside once, under a name that says what it is, and started over
			if (SystemIO.File.Exists(path) && !IsInUse(path))
			{
				SafetyExtensions.IgnoreExceptions(() => SystemIO.File.Move(path, path + ".damaged", overwrite: true));
				if (TryLoadHive(path, out hive))
					return hive;
			}

			var temporary = SystemIO.Path.Combine(SystemIO.Path.GetTempPath(), $"ConsystoFiles-{Environment.ProcessId}.dat");
			SafetyExtensions.IgnoreExceptions(() => SystemIO.File.Copy(path, temporary, overwrite: true));
			AppDomain.CurrentDomain.ProcessExit += (_, _) => SafetyExtensions.IgnoreExceptions(() => SystemIO.File.Delete(temporary));
			if (TryLoadHive(temporary, out hive))
				return hive;

			var fresh = SystemIO.Path.Combine(SystemIO.Path.GetTempPath(), $"ConsystoFiles-{Guid.NewGuid():N}.dat");
			AppDomain.CurrentDomain.ProcessExit += (_, _) => SafetyExtensions.IgnoreExceptions(() => SystemIO.File.Delete(fresh));
			if (TryLoadHive(fresh, out hive))
				return hive;

			// Hive files are refused altogether (a policy, an unusual file system). Better a key in the user's registry than no
			// program at all; it is the one case where the portable build leaves something behind, and it says so in the log.
			return Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\ConsystoFiles.Portable");
		}

		private static bool TryLoadHive(string path, out Microsoft.Win32.RegistryKey hive)
		{
			hive = null!;
			try
			{
				SystemIO.Directory.CreateDirectory(SystemIO.Path.GetDirectoryName(path)!);
				if (RegLoadAppKey(path, out var handle, KeyAllAccess, 0, 0) != 0)
					return false;

				hive = Microsoft.Win32.RegistryKey.FromHandle(handle);
				return true;
			}
			catch (Exception ex) when (ex is SystemIO.IOException or UnauthorizedAccessException or ArgumentException)
			{
				return false;
			}
		}

		private static bool IsInUse(string path)
		{
			try
			{
				using var stream = new SystemIO.FileStream(path, SystemIO.FileMode.Open, SystemIO.FileAccess.ReadWrite, SystemIO.FileShare.None);
				return false;
			}
			catch (Exception)
			{
				return true;
			}
		}

		private const int KeyAllAccess = 0xF003F;

		[System.Runtime.InteropServices.LibraryImport("advapi32.dll", EntryPoint = "RegLoadAppKeyW", StringMarshalling = System.Runtime.InteropServices.StringMarshalling.Utf16)]
		private static partial int RegLoadAppKey(string file, out Microsoft.Win32.SafeHandles.SafeRegistryHandle key, int desired, int options, int reserved);

		/// <summary>
		/// A file that ships with the app ("ms-appx:///...") or lies in its data folder ("ms-appdata:///local/..."). Windows
		/// resolves such addresses only for an installed package, so the portable build turns them into ordinary paths.
		/// </summary>
		public static async Task<StorageFile> GetAppFileAsync(Uri uri)
		{
			if (!IsPortable)
				return await StorageFile.GetFileFromApplicationUriAsync(uri);

			var relative = uri.AbsolutePath.TrimStart('/').Replace('/', '\\');
			var root = AppContext.BaseDirectory;
			if (uri.Scheme is "ms-appdata")
			{
				var parts = relative.Split('\\', 2);
				root = parts[0].ToLowerInvariant() switch
				{
					"local" => LocalFolderPath,
					"temp" => TemporaryFolderPath,
					"roaming" => RoamingFolderPath,
					_ => PortableDataPath,
				};
				relative = parts.Length > 1 ? parts[1] : string.Empty;
			}

			return await StorageFile.GetFileFromPathAsync(SystemIO.Path.Combine(root, relative));
		}

		/// <summary>
		/// The program's own captions. Left to itself, the resource manager looks for "resources.pri", which only a package
		/// has; the build names the file after the assembly. Without this the portable build comes up with every caption empty
		/// on a computer where no build of Files is installed — and looks fine where one is, because it borrows its resources.
		/// </summary>
		public static Microsoft.Windows.ApplicationModel.Resources.ResourceManager CreateResourceManager()
		{
			if (IsPortable)
			{
				var path = SystemIO.Path.Combine(AppContext.BaseDirectory, "Files.pri");
				if (SystemIO.File.Exists(path))
					return new Microsoft.Windows.ApplicationModel.Resources.ResourceManager(path);
			}

			return new Microsoft.Windows.ApplicationModel.Resources.ResourceManager();
		}

		public const string PortableLanguageKey = "ConsystoLanguageOverride";

		/// <summary>
		/// Applies the portable build's chosen language before any window opens; the Windows App SDK override works without a
		/// package. Kept here, away from AppLanguageHelper, whose setup needs services that do not exist that early.
		/// </summary>
		public static void ApplyPortableLanguage()
		{
			if (!IsPortable)
				return;

			// An empty override is refused rather than meaning "system language"; the system language is simply not overriding
			if (LocalSettings.TryGetValue(PortableLanguageKey, out var value) && value is string { Length: > 0 } code)
				Microsoft.Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride = code;
		}

		/// <summary>
		/// A folder of the portable build's data folder. When that folder cannot be written to — the program was unpacked into
		/// Program Files, started from a disc or from a write-protected flash drive — the data goes to the usual place for
		/// application data instead, so the program still runs.
		/// </summary>
		private static string Ensure(string name)
		{
			var path = SystemIO.Path.Combine(PortableFallbackPath ?? PortableDataPath, name);
			try
			{
				SystemIO.Directory.CreateDirectory(path);
				return path;
			}
			catch (Exception ex) when (ex is SystemIO.IOException or UnauthorizedAccessException)
			{
				PortableFallbackPath ??= SystemIO.Path.Combine(
					Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ConsystoFiles.Portable");

				path = SystemIO.Path.Combine(PortableFallbackPath, name);
				SystemIO.Directory.CreateDirectory(path);
				return path;
			}
		}

		private static string? PortableFallbackPath;
	}

	/// <summary>
	/// The portable build's settings store: one JSON file read and written on every access, under a machine-wide mutex, so two
	/// running copies see each other's values the way package settings do. Only the value types the app stores are kept:
	/// strings, booleans, 32- and 64-bit integers and doubles.
	/// </summary>
	internal sealed class PortableSettings(string? container) : IDictionary<string, object>
	{
		private static string FilePath
			=> SystemIO.Path.Combine(AppStorage.PortableDataPath, "settings.json");

		// One lock per data folder: two portable copies in different folders do not share settings
		private static readonly Mutex FileLock = new(false, "ConsystoFilesPortable-" + Convert.ToHexString(
			System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(AppStorage.PortableDataPath.ToUpperInvariant())))[..16]);

		private string ContainerKey
			=> container ?? string.Empty;

		public object this[string key]
		{
			get => TryGetValue(key, out var value) ? value : throw new KeyNotFoundException(key);
			set => Change(values => values[key] = value);
		}

		public ICollection<string> Keys => Read().Keys;

		public ICollection<object> Values => Read().Values;

		public int Count => Read().Count;

		public bool IsReadOnly => false;

		public void Add(string key, object value)
			=> Change(values => values.Add(key, value));

		public void Add(KeyValuePair<string, object> item)
			=> Add(item.Key, item.Value);

		public void Clear()
			=> Change(values => values.Clear());

		public bool Contains(KeyValuePair<string, object> item)
			=> TryGetValue(item.Key, out var value) && Equals(value, item.Value);

		public bool ContainsKey(string key)
			=> Read().ContainsKey(key);

		public void CopyTo(KeyValuePair<string, object>[] array, int arrayIndex)
			=> ((ICollection<KeyValuePair<string, object>>)Read()).CopyTo(array, arrayIndex);

		public IEnumerator<KeyValuePair<string, object>> GetEnumerator()
			=> Read().GetEnumerator();

		System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()
			=> GetEnumerator();

		public bool Remove(string key)
		{
			var removed = false;
			Change(values => removed = values.Remove(key));
			return removed;
		}

		public bool Remove(KeyValuePair<string, object> item)
			=> Contains(item) && Remove(item.Key);

		public bool TryGetValue(string key, [System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out object value)
			=> Read().TryGetValue(key, out value);

		private Dictionary<string, object> Read()
		{
			Lock();
			try
			{
				return (Load(out _) ?? []).GetValueOrDefault(ContainerKey) ?? [];
			}
			finally
			{
				FileLock.ReleaseMutex();
			}
		}

		private void Change(Action<Dictionary<string, object>> change)
		{
			Lock();
			try
			{
				var all = Load(out var readable);

				// The file is there but could not be read this time (antivirus, a network hiccup). Writing now would replace
				// everything with the one value being changed, so the change is dropped instead — the next one will do it.
				if (all is null || !readable)
					return;

				if (!all.TryGetValue(ContainerKey, out var values))
					all[ContainerKey] = values = [];
				change(values);
				Save(all);
			}
			finally
			{
				FileLock.ReleaseMutex();
			}
		}

		private static void Lock()
		{
			try
			{
				FileLock.WaitOne();
			}
			catch (AbandonedMutexException)
			{
				// A copy that crashed while holding the lock leaves the file as it was: whole, since writes are atomic
			}
		}

		/// <param name="readable">
		/// False when the file exists but could not be read; the caller must not write over it then. A file that is not there
		/// yet, or one whose contents make no sense any more, counts as readable: starting over is the right answer for both.
		/// </param>
		private static Dictionary<string, Dictionary<string, object>> Load(out bool readable)
		{
			readable = true;
			var all = new Dictionary<string, Dictionary<string, object>>(StringComparer.Ordinal);
			try
			{
				if (!SystemIO.File.Exists(FilePath))
					return all;

				using var document = JsonDocument.Parse(ReadAllBytesWithRetries(FilePath));
				foreach (var group in document.RootElement.EnumerateObject())
				{
					var values = new Dictionary<string, object>(StringComparer.Ordinal);
					foreach (var entry in group.Value.EnumerateObject())
					{
						if (ReadValue(entry.Value) is { } value)
							values[entry.Name] = value;
					}
					all[group.Name] = values;
				}
			}
			catch (JsonException)
			{
				// A damaged file starts the settings over; the app's main settings live in their own files
				all.Clear();
			}
			catch (Exception ex) when (ex is SystemIO.IOException or UnauthorizedAccessException)
			{
				readable = false;
				all.Clear();
			}

			return all;
		}

		private static byte[] ReadAllBytesWithRetries(string path)
		{
			for (var attempt = 1; ; attempt++)
			{
				try
				{
					return SystemIO.File.ReadAllBytes(path);
				}
				catch (SystemIO.IOException) when (attempt < 3)
				{
					System.Threading.Thread.Sleep(50 * attempt);
				}
			}
		}

		private static object? ReadValue(JsonElement element)
		{
			if (!element.TryGetProperty("t", out var type) || !element.TryGetProperty("v", out var value))
				return null;

			return type.GetString() switch
			{
				"s" => value.GetString(),
				"b" => value.GetBoolean(),
				"i" => value.GetInt32(),
				"l" => value.GetInt64(),
				"d" => value.GetDouble(),
				_ => null,
			};
		}

		private static void Save(Dictionary<string, Dictionary<string, object>> all)
		{
			using var stream = new SystemIO.MemoryStream();
			using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
			{
				writer.WriteStartObject();
				foreach (var (group, values) in all)
				{
					writer.WriteStartObject(group);
					foreach (var (key, value) in values)
					{
						writer.WriteStartObject(key);
						switch (value)
						{
							case string text: writer.WriteString("t", "s"); writer.WriteString("v", text); break;
							case bool flag: writer.WriteString("t", "b"); writer.WriteBoolean("v", flag); break;
							case int number: writer.WriteString("t", "i"); writer.WriteNumber("v", number); break;
							case long number: writer.WriteString("t", "l"); writer.WriteNumber("v", number); break;
							case double number: writer.WriteString("t", "d"); writer.WriteNumber("v", number); break;
							default: writer.WriteString("t", "s"); writer.WriteString("v", Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)); break;
						}
						writer.WriteEndObject();
					}
					writer.WriteEndObject();
				}
				writer.WriteEndObject();
			}

			// A folder that cannot be written to (a disc, a flash drive with the switch on, Program Files) must not stop the
			// program: the settings of this run are simply not kept.
			SafetyExtensions.IgnoreExceptions(() =>
			{
				SystemIO.Directory.CreateDirectory(AppStorage.PortableDataPath);
				var temporary = FilePath + $".{Environment.ProcessId}.tmp";
				SystemIO.File.WriteAllBytes(temporary, stream.ToArray());
				SystemIO.File.Move(temporary, FilePath, overwrite: true);
			});
		}
	}
}
