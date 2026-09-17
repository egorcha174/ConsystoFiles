using Files.Shared.Helpers;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml.Controls;
using System.IO;
using System.Windows.Input;

namespace Files.App.ViewModels.Properties
{
	/// <summary>Consysto fork: a set of icons offered in the dialog, a DLL or ICO file.</summary>
	public sealed record IconSetOption(string Name, string Path);

	public sealed partial class CustomizationViewModel : ObservableObject
	{
		private ICommonDialogService CommonDialogService { get; } = Ioc.Default.GetRequiredService<ICommonDialogService>();

		private static string DefaultIconDllFilePath
			=> Path.Combine(Constants.UserEnvironmentPaths.SystemRootPath, "System32", "SHELL32.dll");

		// Consysto fork: own folders and Fluent Emoji objects first, then the icons of Windows
		public IReadOnlyList<IconSetOption> IconSets { get; } = CreateIconSets();

		private IconSetOption? _SelectedIconSet;
		public IconSetOption? SelectedIconSet
		{
			get => _SelectedIconSet;
			set
			{
				if (SetProperty(ref _SelectedIconSet, value) && value is not null)
					IconResourceItemPath = value.Path;
			}
		}

		private static List<IconSetOption> CreateIconSets()
		{
			var sets = new List<IconSetOption>();
			if (PrepareConsystoIconFile("ConsystoFolders.dll") is { } folders)
				sets.Add(new(Strings.ConsystoIconSetFolders.GetLocalizedResource(), folders));
			if (PrepareConsystoIconFile("ConsystoObjects.dll") is { } objects)
				sets.Add(new(Strings.ConsystoIconSetObjects.GetLocalizedResource(), objects));

			var system32 = Path.Combine(Constants.UserEnvironmentPaths.SystemRootPath, "System32");
			sets.Add(new(Strings.ConsystoIconSetWindows.GetLocalizedResource(), DefaultIconDllFilePath));
			sets.Add(new(Strings.ConsystoIconSetWindowsModern.GetLocalizedResource(), Path.Combine(system32, "imageres.dll")));
			return sets;
		}

		/// <summary>
		/// desktop.ini is read by Explorer, which sees neither the app package nor the app's virtualized AppData,
		/// so the icon file is copied next to the user profile and referenced from there.
		/// </summary>
		private static string? PrepareConsystoIconFile(string fileName)
		{
			var source = Path.Combine(AppContext.BaseDirectory, "Assets", "Consysto", "Icons", fileName);
			if (!File.Exists(source))
				return null;

			var target = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".consysto", "icons", fileName);
			try
			{
				var sourceInfo = new FileInfo(source);
				var targetInfo = new FileInfo(target);
				if (!targetInfo.Exists || targetInfo.Length != sourceInfo.Length || targetInfo.LastWriteTimeUtc < sourceInfo.LastWriteTimeUtc)
				{
					Directory.CreateDirectory(Path.GetDirectoryName(target)!);
					File.Copy(source, target, overwrite: true);
				}
			}
			catch (Exception ex)
			{
				// Explorer may hold the old copy open; it still works, icons are only appended between versions
				App.Logger.LogWarning(ex, "The Consysto icon set {File} could not be copied", fileName);
			}

			return File.Exists(target) ? target : null;
		}

		private readonly AppWindow? _appWindow;

		private readonly IShellPage? _appInstance;

		private readonly string? _selectedItemPath;

		private bool _isIconChanged;

		public readonly bool IsShortcut;

		public ObservableCollection<IconFileInfo> DllIcons { get; } = [];

		private string? _IconResourceItemPath;
		public string? IconResourceItemPath
		{
			get => _IconResourceItemPath;
			set
			{
				if (SetProperty(ref _IconResourceItemPath, value))
				{
					// A file picked by hand is not one of the sets
					var set = IconSets?.FirstOrDefault(candidate => string.Equals(candidate.Path, value, StringComparison.OrdinalIgnoreCase));
					if (!ReferenceEquals(set, _SelectedIconSet))
					{
						_SelectedIconSet = set;
						OnPropertyChanged(nameof(SelectedIconSet));
					}

					DllIcons.Clear();

					if (IsConvertibleImagePath(_IconResourceItemPath))
					{
						ConvertImageInfoBarSeverity = InfoBarSeverity.Informational;
						ConvertImageInfoBarMessage = Strings.ConvertToIconRequiredMessage.GetLocalizedResource();
						IsConvertImageInfoBarOpen = true;
						return;
					}

					IsConvertImageInfoBarOpen = false;

					if (Path.Exists(_IconResourceItemPath))
					{
						var icons = Win32Helper.ExtractIconsFromDLL(_IconResourceItemPath);
						if (icons?.Count is null or 0)
							return;

						foreach (var item in icons)
							DllIcons.Add(item);
					}
				}
			}
		}

		private IconFileInfo? _SelectedDllIcon;
		public IconFileInfo? SelectedDllIcon
		{
			get => _SelectedDllIcon;
			set
			{
				if (SetProperty(ref _SelectedDllIcon, value))
					_isIconChanged = true;
			}
		}

		[ObservableProperty] public partial bool IsConvertImageInfoBarOpen { get; set; }
		[ObservableProperty] public partial InfoBarSeverity ConvertImageInfoBarSeverity { get; set; }
		[ObservableProperty] public partial string? ConvertImageInfoBarMessage { get; set; }

		public ICommand? RestoreDefaultIconCommand { get; private set; }
		public ICommand? OpenFilePickerCommand { get; private set; }
		public ICommand? ConvertImageToIconCommand { get; private set; }

		public CustomizationViewModel(IShellPage appInstance, BaseProperties baseProperties, AppWindow appWindow)
		{
			ListedItem? item;
			if (baseProperties is FileProperties fileProperties)
				item = fileProperties.Item;
			else if (baseProperties is FolderProperties folderProperties)
				item = folderProperties.Item;
			else
				return;

			_appInstance = appInstance;
			_appWindow = appWindow;
			IconResourceItemPath = IconSets.FirstOrDefault()?.Path ?? DefaultIconDllFilePath;
			IsShortcut = item.IsShortcut;
			_selectedItemPath = item.ItemPath;


			RestoreDefaultIconCommand = new RelayCommand(ExecuteRestoreDefaultIconCommand);
			OpenFilePickerCommand = new RelayCommand(ExecuteOpenFilePickerCommand);
			ConvertImageToIconCommand = new AsyncRelayCommand(ExecuteConvertImageToIconCommandAsync);
		}

		private static bool IsConvertibleImagePath(string? path)
		{
			return FileExtensionHelpers.IsConvertibleToIcoFile(path) && File.Exists(path);
		}

		private void ExecuteRestoreDefaultIconCommand()
		{
			SelectedDllIcon = null;
			_isIconChanged = true;
		}

		private void ExecuteOpenFilePickerCommand()
		{
			var parentWindowId = (_appWindow
				?? throw new InvalidOperationException("The customization window has not been initialized.")).Id;
			var hWnd = Microsoft.UI.Win32Interop.GetWindowFromWindowId(parentWindowId);

			string[] extensions =
			[
				Strings.AllSupportedFiles.GetLocalizedResource(), "*.dll;*.exe;*.ico;*.icl;*.png;*.bmp;*.jpg;*.jpeg;*.jfif",
				Strings.IconFiles.GetLocalizedResource(), "*.dll;*.exe;*.ico;*.icl",
				Strings.ApplicationExtension.GetLocalizedResource(), "*.dll",
				Strings.Application.GetLocalizedResource(), "*.exe",
				Strings.IcoFileCapitalized.GetLocalizedResource(), "*.ico",
				Strings.IclFileCapitalized.GetLocalizedResource(), "*.icl ",
				Strings.ImageFiles.GetLocalizedResource(), "*.png;*.bmp;*.jpg;*.jpeg;*.jfif",
			];

			var result = CommonDialogService.Open_FileOpenDialog(hWnd, false, extensions, Environment.SpecialFolder.MyComputer, out var filePath);
			if (result)
				IconResourceItemPath = filePath;
		}

		private async Task ExecuteConvertImageToIconCommandAsync()
		{
			var imagePath = IconResourceItemPath;
			if (imagePath is null || !IsConvertibleImagePath(imagePath))
				return;

			var appWindow = _appWindow
				?? throw new InvalidOperationException("The customization window has not been initialized.");
			var hWnd = Microsoft.UI.Win32Interop.GetWindowFromWindowId(appWindow.Id);
			if (!CommonDialogService.Open_FileSaveDialog(hWnd, false, [Strings.IcoFileCapitalized.GetLocalizedResource(), "*.ico"], Environment.SpecialFolder.MyPictures, out var icoFilePath))
				return;

			// The save dialog doesn't enforce an extension, and the shell only accepts real .ico files here
			if (!Path.GetExtension(icoFilePath).Equals(".ico", StringComparison.OrdinalIgnoreCase))
				icoFilePath += ".ico";

			var converted = await Task.Run(() => Win32Helper.ConvertImageToIcoFile(imagePath, icoFilePath));
			if (converted)
			{
				IconResourceItemPath = icoFilePath;
				SelectedDllIcon = DllIcons.FirstOrDefault();
			}
			else
			{
				ConvertImageInfoBarSeverity = InfoBarSeverity.Error;
				ConvertImageInfoBarMessage = Strings.ConvertToIconError.GetLocalizedResource();
				IsConvertImageInfoBarOpen = true;
			}
		}

		public async Task<bool> UpdateIcon()
		{
			if (!_isIconChanged)
				return false;

			var selectedItemPath = _selectedItemPath
				?? throw new InvalidOperationException("The selected item path has not been initialized.");
			bool result = false;

			if (SelectedDllIcon is null)
			{
				result = IsShortcut
					? Win32Helper.SetCustomFileIcon(selectedItemPath, null)
					: Win32Helper.SetCustomDirectoryIcon(selectedItemPath, null);
			}
			else
			{
				result = IsShortcut
					? Win32Helper.SetCustomFileIcon(selectedItemPath, IconResourceItemPath, SelectedDllIcon.Index)
					: Win32Helper.SetCustomDirectoryIcon(selectedItemPath, IconResourceItemPath, SelectedDllIcon.Index);
			}

			if (!result)
				return false;

			await MainWindow.Instance.DispatcherQueue.EnqueueOrInvokeAsync(() =>
			{
				_appInstance?.ShellViewModel?.RefreshItems(null);
			});

			return true;
		}
	}
}
