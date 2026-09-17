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
			var appUserModelId = $"{Windows.ApplicationModel.Package.Current.Id.FamilyName}!App";
			await Windows.System.Launcher.LaunchUriAsync(new Uri($"ms-settings:defaultapps?registeredAUMID={Uri.EscapeDataString(appUserModelId)}"));
		}

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
