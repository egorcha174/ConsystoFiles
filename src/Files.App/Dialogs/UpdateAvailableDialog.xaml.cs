// Consysto fork: a newer release and what it changed.

using CommunityToolkit.Labs.WinUI.MarkdownTextBlock;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using WinRT;

namespace Files.App.Dialogs
{
	public sealed partial class UpdateAvailableDialog : ContentDialog, IDialog<UpdateAvailableDialogViewModel>
	{
		private FrameworkElement RootAppElement
		{
			[DynamicWindowsRuntimeCast(typeof(FrameworkElement))]
			get => (FrameworkElement)MainWindow.Instance.Content;
		}

		public UpdateAvailableDialogViewModel ViewModel
		{
			get => (UpdateAvailableDialogViewModel)DataContext;
			set => DataContext = value;
		}

		[DynamicWindowsRuntimeCast(typeof(Brush))]
		public UpdateAvailableDialog()
		{
			InitializeComponent();

			// Workaround for https://github.com/CommunityToolkit/Labs-Windows/issues/611, as in the Markdown preview
			NotesMarkdownTextBlock.Config = MarkdownConfig.Default;
			NotesMarkdownTextBlock.Config.Themes.HeadingForeground = (Brush)Application.Current.Resources["TextControlForeground"];
		}

		public new async Task<DialogResult> ShowAsync()
			=> (DialogResult)await ((ContentDialog)this).TryShowAsync();

		private async void UpdateAvailableDialog_Loaded(object sender, RoutedEventArgs e)
			=> await ViewModel.LoadNotesAsync();

		private async void NotesMarkdownTextBlock_OnLinkClicked(object sender, CommunityToolkit.Labs.WinUI.MarkdownTextBlock.LinkClickedEventArgs e)
			=> await Launcher.LaunchUriAsync(e.Uri);
	}
}
