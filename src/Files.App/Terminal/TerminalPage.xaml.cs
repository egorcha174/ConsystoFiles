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
		}

		protected override async void OnNavigatedTo(NavigationEventArgs e)
		{
			base.OnNavigatedTo(e);
			if (e.Parameter is not NavigationArguments arguments)
				return;

			appInstance = arguments.AssociatedTabInstance;
			terminalPath = arguments.NavPathParam ?? string.Empty;
			await UpdateShellAsync();

			try
			{
				await View.EnsureCoreWebView2Async();
				var core = View.CoreWebView2!;
				core.Settings.AreDevToolsEnabled = false;
				core.Settings.AreDefaultContextMenusEnabled = false;
				core.Settings.IsStatusBarEnabled = false;
				core.SetVirtualHostNameToFolderMapping(
					HostName,
					Path.Combine(AppContext.BaseDirectory, "Assets", "Consysto", "Terminal"),
					CoreWebView2HostResourceAccessKind.DenyCors);
				core.WebMessageReceived += Core_WebMessageReceived;

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
			}
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
