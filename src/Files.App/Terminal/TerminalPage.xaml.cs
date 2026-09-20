// Consysto fork: a terminal in a tab. The shell lives here; the web view only draws it and returns keys.

using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.Web.WebView2.Core;
using System.IO;
using System.Text.Json;

namespace Files.App.Terminal
{
	public sealed partial class TerminalPage : Page
	{
		private const string HostName = "terminal.consysto";

		private IShellPage? appInstance;
		private string terminalPath = string.Empty;
		private PseudoConsoleSession? session;

		public TerminalPage()
		{
			InitializeComponent();
			Unloaded += Page_Unloaded;

			// Copying and pasting are caught by the window: the keys do not reach the web view, and a Russian layout turns
			// Ctrl+C and Ctrl+V into other letters anyway. Here they are the keys themselves, whatever the layout.
			KeyboardAccelerators.Add(NewAccelerator(Windows.System.VirtualKey.V, async () => await PasteFromClipboardAsync()));
			KeyboardAccelerators.Add(NewAccelerator(Windows.System.VirtualKey.C, () => Post("copy", string.Empty)));
		}

		private static Microsoft.UI.Xaml.Input.KeyboardAccelerator NewAccelerator(Windows.System.VirtualKey key, Action invoke)
		{
			var accelerator = new Microsoft.UI.Xaml.Input.KeyboardAccelerator { Key = key, Modifiers = Windows.System.VirtualKeyModifiers.Control };
			accelerator.Invoked += (_, args) =>
			{
				args.Handled = true;
				invoke();
			};

			return accelerator;
		}

		protected override async void OnNavigatedTo(NavigationEventArgs e)
		{
			base.OnNavigatedTo(e);
			if (e.Parameter is not NavigationArguments arguments)
				return;

			appInstance = arguments.AssociatedTabInstance;
			terminalPath = arguments.NavPathParam ?? string.Empty;
			await UpdateShellAsync();

			// The terminal draws in a web view, which Windows 11 always has and Windows 10 may not: say so plainly instead of
			// showing an empty tab with a puzzling error from deep inside the component.
			if (!IsWebViewInstalled())
			{
				ShowError(Strings.ConsystoTerminalNeedsWebView.GetLocalizedResource());
				return;
			}

			try
			{
				await View.EnsureCoreWebView2Async();
				var core = View.CoreWebView2!;
				core.Settings.AreDevToolsEnabled = false;
				core.Settings.AreDefaultContextMenusEnabled = false;
				core.Settings.IsStatusBarEnabled = false;

				// Ctrl+C, Ctrl+V, Ctrl+F and the rest belong to the terminal, not to the browser engine that draws it:
				// otherwise the engine takes them first and the terminal never sees them
				core.Settings.AreBrowserAcceleratorKeysEnabled = false;
				core.SetVirtualHostNameToFolderMapping(
					HostName,
					Path.Combine(AppContext.BaseDirectory, "Assets", "Consysto", "Terminal"),
					CoreWebView2HostResourceAccessKind.DenyCors);
				core.WebMessageReceived += Core_WebMessageReceived;

				// Opening a terminal means wanting to type in it: the keyboard goes there at once, without a click first
				core.NavigationCompleted += (_, _) => FocusTerminal();

				var theme = ActualTheme == ElementTheme.Light ? "light" : "dark";
				core.Navigate($"https://{HostName}/terminal.html?theme={theme}");
			}
			catch (Exception ex)
			{
				App.Logger.LogWarning(ex, "The terminal view could not start");
				ShowError(ex.Message);
			}
		}

		/// <summary>Navigating away or closing the tab ends the shell: the page is not kept, so nothing would show it again.</summary>
		protected override void OnNavigatedFrom(NavigationEventArgs e)
		{
			base.OnNavigatedFrom(e);
			Close();
		}

		private void Page_Unloaded(object sender, RoutedEventArgs e)
			=> Close();

		private void Close()
		{
			session?.Dispose();
			session = null;
		}

		private void Core_WebMessageReceived(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
		{
			using var message = JsonDocument.Parse(args.TryGetWebMessageAsString());
			var root = message.RootElement;
			switch (root.GetProperty("type").GetString())
			{
				case "ready":
					StartShell(ReadSize(root, "columns"), ReadSize(root, "rows"));
					break;
				case "input":
					session?.Write(root.GetProperty("data").GetString() ?? string.Empty);
					break;
				case "resize":
					session?.Resize(ReadSize(root, "columns"), ReadSize(root, "rows"));
					break;
				// The clipboard is handled here: the browser engine of the view is not allowed to read it on its own
				case "selection":
					var selected = root.GetProperty("data").GetString() ?? string.Empty;
					if (selected.Length > 0)
						DispatcherQueue.TryEnqueue(() => CopyToClipboard(selected));
					break;
				case "pasteRequest":
					DispatcherQueue.TryEnqueue(async () => await PasteFromClipboardAsync());
					break;
				case "menu":
					// The web view has no menu of its own here, so the tab shows one with the few things a terminal needs
					var hasSelection = root.TryGetProperty("hasSelection", out var selection) && selection.GetBoolean();
					DispatcherQueue.TryEnqueue(() => ShowMenu(hasSelection));
					break;
			}
		}

		private void ShowMenu(bool hasSelection)
		{
			var menu = new MenuFlyout();
			menu.Items.Add(NewItem(Strings.Copy.GetLocalizedResource(), "copy", hasSelection));
			menu.Items.Add(NewItem(Strings.Paste.GetLocalizedResource(), "paste", true));
			menu.Items.Add(NewItem(Strings.SelectAll.GetLocalizedResource(), "selectAll", true));
			menu.Items.Add(new MenuFlyoutSeparator());
			menu.Items.Add(NewItem(Strings.ConsystoTerminalClear.GetLocalizedResource(), "clear", true));
			menu.ShowAt(View, new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions
			{
				Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.Auto,
			});
		}

		private MenuFlyoutItem NewItem(string text, string command, bool enabled)
		{
			var item = new MenuFlyoutItem { Text = text, IsEnabled = enabled };
			item.Click += (_, _) => Post(command, string.Empty);
			return item;
		}

		private static short ReadSize(JsonElement root, string name)
			=> (short)Math.Clamp(root.GetProperty(name).GetInt32(), 1, short.MaxValue);

		private void StartShell(short columns, short rows)
		{
			if (session is not null)
				return;

			var folder = TerminalPaths.FolderOf(terminalPath);
			if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
				folder = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

			try
			{
				session = PseudoConsoleSession.Start(ShellCommandLine(), folder, columns, rows);
				session.OutputReceived += text => Post("output", text);
				session.Exited += () => Post("exited", Strings.ConsystoTerminalExited.GetLocalizedResource());
			}
			catch (Exception ex)
			{
				App.Logger.LogWarning(ex, "The terminal shell could not start");
				ShowError(ex.Message);
			}
		}

		/// <summary>PowerShell 7 when it is installed, otherwise the Windows PowerShell every system has.</summary>
		private static string ShellCommandLine()
		{
			foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries))
			{
				var candidate = Path.Combine(folder.Trim(), "pwsh.exe");
				if (File.Exists(candidate))
					return $"\"{candidate}\" -NoLogo";
			}

			return "powershell.exe -NoLogo";
		}

		// Output comes from the reader thread in bursts; the web view takes messages only on the UI thread.
		private void Post(string type, string data)
		{
			var json = JsonSerializer.Serialize(new TerminalMessage(type, data), TerminalJsonContext.Default.TerminalMessage);
			DispatcherQueue.TryEnqueue(() =>
			{
				try
				{
					View.CoreWebView2?.PostWebMessageAsJson(json);
				}
				catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException)
				{
				}
			});
		}

		private static void CopyToClipboard(string text)
		{
			var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
			package.SetText(text);
			Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
		}

		/// <summary>What is on the clipboard goes straight into the shell, as if it had been typed.</summary>
		private async Task PasteFromClipboardAsync()
		{
			try
			{
				var content = Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();
				if (!content.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text))
					return;

				var text = await content.GetTextAsync();
				if (!string.IsNullOrEmpty(text))
					session?.Write(text.ReplaceLineEndings("\r"));
			}
			catch (Exception ex)
			{
				App.Logger.LogWarning(ex, "The clipboard could not be read for the terminal");
			}
		}

		/// <summary>Gives the keyboard to the terminal: the view takes the focus, the page puts the cursor into the shell.</summary>
		private void FocusTerminal()
			=> DispatcherQueue.TryEnqueue(() =>
			{
				View.Focus(FocusState.Programmatic);
				Post("focus", string.Empty);
			});

		private static bool IsWebViewInstalled()
		{
			try
			{
				return !string.IsNullOrEmpty(CoreWebView2Environment.GetAvailableBrowserVersionString());
			}
			catch (Exception)
			{
				return false;
			}
		}

		private void ShowError(string text)
		{
			ErrorBar.Title = Strings.ConsystoTerminalFailed.GetLocalizedResource();
			ErrorBar.Message = text;
			ErrorBar.IsOpen = true;
		}

		private async Task UpdateShellAsync()
		{
			if (appInstance is not { } shell)
				return;

			// Like Home, the page has no files: the toolbar hides its folder commands and the preview pane stays closed.
			shell.InstanceViewModel.IsPageTypeNotHome = false;
			shell.InstanceViewModel.IsPageTypeSearchResults = false;
			shell.InstanceViewModel.IsPageTypeMtpDevice = false;
			shell.InstanceViewModel.IsPageTypeRecycleBin = false;
			shell.InstanceViewModel.IsPageTypeCloudDrive = false;
			shell.InstanceViewModel.IsPageTypeFtp = false;
			shell.InstanceViewModel.IsPageTypeZipFolder = false;
			shell.InstanceViewModel.IsPageTypeLibrary = false;
			shell.InstanceViewModel.GitRepositoryPath = null;
			shell.InstanceViewModel.IsGitRepository = false;
			shell.InstanceViewModel.IsPageTypeReleaseNotes = false;
			shell.InstanceViewModel.IsPageTypeSettings = false;
			shell.ToolbarViewModel.CanRefresh = false;
			shell.ToolbarViewModel.CanGoBack = shell.CanNavigateBackward;
			shell.ToolbarViewModel.CanGoForward = shell.CanNavigateForward;
			shell.ToolbarViewModel.CanNavigateToParent = false;

			var shellViewModel = shell.GetRequiredShellViewModel();
			await shellViewModel.SetWorkingDirectoryAsync(terminalPath);
			shell.SlimContentPage?.StatusBarViewModel.UpdateGitInfo(false, string.Empty, null);

			var title = TerminalPaths.TitleOf(terminalPath);
			shell.ToolbarViewModel.PathComponents.Clear();
			shell.ToolbarViewModel.PathComponents.Add(new PathBoxItem()
			{
				Title = title,
				Path = terminalPath,
				ChevronToolTip = string.Format(Strings.BreadcrumbBarChevronButtonToolTip.GetLocalizedResource(), title),
			});
		}
	}

	internal sealed record TerminalMessage(string type, string data);

	[System.Text.Json.Serialization.JsonSerializable(typeof(TerminalMessage))]
	internal sealed partial class TerminalJsonContext : System.Text.Json.Serialization.JsonSerializerContext
	{
	}
}
