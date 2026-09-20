// Consysto fork: runs the commands that arrive through the control channel.
//
// Everything here touches the window, so everything here runs on the thread that owns it: the channels themselves live on
// background threads and hand the work over.

using Files.App.Views;
using System.Threading.Tasks;

namespace Files.App.Api
{
	public static class ApiDispatcher
	{
		/// <summary>Runs one request and returns what to answer. Never throws: a failure comes back as an answer with a reason.</summary>
		public static Task<ApiResponse> RunAsync(ApiRequest request)
		{
			var answer = new TaskCompletionSource<ApiResponse>();

			var queue = MainWindow.Instance?.DispatcherQueue;
			if (queue is null || !queue.TryEnqueue(async () =>
			{
				try
				{
					answer.TrySetResult(await ExecuteAsync(request));
				}
				catch (Exception ex)
				{
					answer.TrySetResult(new ApiResponse(false, ex.Message));
				}
			}))
			{
				answer.TrySetResult(new ApiResponse(false, "the window is not ready"));
			}

			return answer.Task;
		}

		private static async Task<ApiResponse> ExecuteAsync(ApiRequest request)
		{
			switch (request.command?.ToLowerInvariant())
			{
				case "ping":
					return new ApiResponse(true);

				case "state":
					return new ApiResponse(true, window: ReadWindow());

				case "open":
					return await OpenAsync(request);

				case "splitpanes":
					if (Panes is not { } toSplit)
						return NoWindow;
					if (!toSplit.IsMultiPaneActive)
						toSplit.OpenSecondaryPane(request.path ?? string.Empty);
					else if (!string.IsNullOrEmpty(request.path))
						toSplit.OpenInOtherPane(request.path);
					return new ApiResponse(true, window: ReadWindow());

				case "closepane":
					if (Panes is not { IsMultiPaneActive: true } toClose)
						return new ApiResponse(false, "the window shows a single pane");
					toClose.CloseOtherPane();
					return new ApiResponse(true, window: ReadWindow());

				case "focuspane":
					if (Panes is not { } toFocus)
						return NoWindow;
					if (request.index == 1 && toFocus.IsLeftPaneActive || request.index == 0 && toFocus.IsRightPaneActive)
						toFocus.FocusOtherPane();
					else
						toFocus.FocusActivePane();
					return new ApiResponse(true, window: ReadWindow());

				case "selecttab":
					var tabs = MainPageViewModel.AppInstances;
					if (request.index is not { } wanted || wanted < 0 || wanted >= tabs.Count)
						return new ApiResponse(false, "there is no tab with that number");
					Ioc.Default.GetRequiredService<MainPageViewModel>().SelectedTabItem = tabs[wanted];
					return new ApiResponse(true, window: ReadWindow());

				case "closetab":
					return CloseTab(request.index);

				case "addtorrent":
					if (string.IsNullOrEmpty(request.path))
						return new ApiResponse(false, "the torrent file or magnet link is missing");
					// The folder is the caller's to choose; without one the download goes where downloads normally go
					var into = request.name ?? SystemIO.Path.Combine(
						Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
					if (!SystemIO.Directory.Exists(into))
						return new ApiResponse(false, $"there is no folder {into}");
					await Files.App.Torrents.TorrentHost.AddToFolderAsync(request.path, into);
					return new ApiResponse(true, window: ReadWindow());

				case "createbackup":
					if (string.IsNullOrWhiteSpace(request.name) || string.IsNullOrEmpty(request.path) || request.folders is not { Length: > 0 })
						return new ApiResponse(false, "a backup needs a name, a folder to copy and a folder to copy into");
					var pair = Files.App.Sync.SyncManager.Instance.Add(
						request.name, request.path, request.folders[0], Files.App.Sync.SyncManager.DefaultExclusions);
					await OpenAsync(request with { command = "open", path = Files.App.Sync.SyncPaths.ForPair(pair.Id), where = "tab" });
					return new ApiResponse(true, window: ReadWindow());

				case "createcollection":
					return await CreateCollectionAsync(request);

				case "run":
					if (string.IsNullOrEmpty(request.name))
						return new ApiResponse(false, "the name of the command is missing");
					var command = Ioc.Default.GetRequiredService<ICommandManager>()[request.name];
					if (command.Code is CommandCodes.None)
						return new ApiResponse(false, $"there is no command named {request.name}");
					if (!command.IsExecutable)
						return new ApiResponse(false, $"the command {request.name} cannot run right now");
					await command.ExecuteAsync();
					return new ApiResponse(true, window: ReadWindow());

				case null or "":
					return new ApiResponse(false, "the command is missing");

				default:
					return new ApiResponse(false, $"unknown command: {request.command}");
			}
		}

		/// <summary>Makes a collection over the given folders, the way the dialog in the sidebar does.</summary>
		private static async Task<ApiResponse> CreateCollectionAsync(ApiRequest request)
		{
			if (string.IsNullOrWhiteSpace(request.name))
				return new ApiResponse(false, "the name of the collection is missing");

			var folders = request.folders ?? (string.IsNullOrEmpty(request.path) ? [] : new[] { request.path });
			if (folders.Length == 0)
				return new ApiResponse(false, "the collection has no folders");

			var manager = Files.App.Books.Library.CollectionManager.Instance;
			var made = await manager.CreateLibraryAsync(request.name, request.where, folders);
			if (made is null)
				return new ApiResponse(false, "the collection could not be made; is there one by that name already?");

			return new ApiResponse(true, window: ReadWindow());
		}

		private static async Task<ApiResponse> OpenAsync(ApiRequest request)
		{
			if (string.IsNullOrEmpty(request.path))
				return new ApiResponse(false, "the path is missing");

			switch (request.where?.ToLowerInvariant())
			{
				// A new tab is what a person means by "open" most of the time, so it is also what an omitted place means
				case null or "" or "tab":
					await NavigationHelpers.AddNewTabByPathAsync(typeof(ShellPanesPage), request.path, true);
					break;

				case "current":
					if (Active is not { } active)
						return NoWindow;
					NavigateTo(active, request.path);
					break;

				case "other":
					if (Panes is not { } panes)
						return NoWindow;
					if (!panes.IsMultiPaneActive)
						panes.OpenSecondaryPane(request.path);
					else
						panes.OpenInOtherPane(request.path);
					break;

				default:
					return new ApiResponse(false, $"unknown place: {request.where}");
			}

			return new ApiResponse(true, window: ReadWindow());
		}

		/// <summary>The pages of this fork are not folders, and the shell reaches them by another door.</summary>
		private static void NavigateTo(IShellPage pane, string path)
		{
			if (Files.App.Books.Library.ConsystoPages.IsPagePath(path))
				pane.NavigateToConsystoPage(path);
			else
				pane.NavigateToPath(path);
		}

		private static ApiResponse CloseTab(int? index)
		{
			var tabs = MainPageViewModel.AppInstances;
			var viewModel = Ioc.Default.GetRequiredService<MainPageViewModel>();
			var number = index ?? tabs.IndexOf(viewModel.SelectedTabItem!);
			if (number < 0 || number >= tabs.Count)
				return new ApiResponse(false, "there is no tab with that number");

			// Closing the last tab would close the window, which is not what a command about a tab should do
			if (tabs.Count == 1)
				return new ApiResponse(false, "the only tab cannot be closed");

			viewModel.MultitaskingControl?.CloseTab(tabs[number]);
			return new ApiResponse(true, window: ReadWindow());
		}

		private static ApiWindow ReadWindow()
		{
			var tabs = MainPageViewModel.AppInstances;
			var selected = Ioc.Default.GetService<MainPageViewModel>()?.SelectedTabItem;

			var list = new List<ApiTab>(tabs.Count);
			for (var i = 0; i < tabs.Count; i++)
				list.Add(new ApiTab(i, tabs[i].Header, tabs[i].NavigationParameter?.NavigationParameter as string));

			var panes = Panes?.GetPanes().Select(pane => pane.ShellViewModel?.WorkingDirectory ?? string.Empty).ToArray() ?? [];
			var activePane = Panes is { IsRightPaneActive: true } ? 1 : 0;

			return new ApiWindow(selected is null ? -1 : tabs.IndexOf(selected), [.. list], panes, activePane);
		}

		private static IShellPage? Active
			=> Ioc.Default.GetService<IContentPageContext>()?.ShellPage;

		private static IShellPanesPage? Panes
			=> Active?.PaneHolder;

		private static ApiResponse NoWindow
			=> new(false, "no folder is open in the window");
	}
}
