// Copyright (c) Files Community
// SPDX-License-Identifier: MPL-2.0

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Files.App.Views.Settings
{
	public sealed partial class AdvancedPage : Page
	{
		/// <summary>
		/// Consysto fork: Windows lets an app only ask to be the default; the Default apps page for Files opens and the user picks
		/// it for .torrent and magnet there.
		/// </summary>
		private async void TorrentDefault_Click(object sender, RoutedEventArgs e)
		{
			// The portable build has no manifest to declare the file types, so it declares them for the current user first
			if (AppStorage.IsPortable)
			{
				Files.App.Torrents.PortableTorrentAssociation.Register();
				await Windows.System.Launcher.LaunchUriAsync(new Uri("ms-settings:defaultapps"));
				return;
			}

			var appUserModelId = $"{AppStorage.PackageFamilyName}!App";
			await Windows.System.Launcher.LaunchUriAsync(new Uri($"ms-settings:defaultapps?registeredAUMID={Uri.EscapeDataString(appUserModelId)}"));
		}

		private void TorrentForget_Click(object sender, RoutedEventArgs e)
			=> Files.App.Torrents.PortableTorrentAssociation.Unregister();

		public AdvancedPage()
		{
			InitializeComponent();
		}

		private void OpenFilesOnWindowsStartup_Toggled(object sender, RoutedEventArgs e)
		{
			if (ViewModel.OpenFilesOnWindowsStartupCommand.CanExecute(e))
				ViewModel.OpenFilesOnWindowsStartupCommand.Execute(e);
		}

		private void SetAsDefaultExplorer_Toggled(object sender, RoutedEventArgs e)
		{
			if (ViewModel.SetAsDefaultExplorerCommand.CanExecute(e))
				ViewModel.SetAsDefaultExplorerCommand.Execute(e);
		}

		private void SetAsOpenFileDialog_Toggled(object sender, RoutedEventArgs e)
		{
			if (ViewModel.SetAsOpenFileDialogCommand.CanExecute(e))
				ViewModel.SetAsOpenFileDialogCommand.Execute(e);
		}
	}
}
