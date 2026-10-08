using Files.App.ViewModels.Previews;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Media.Playback;

// The User Control item template is documented at https://go.microsoft.com/fwlink/?LinkId=234236

namespace Files.App.UserControls.FilePreviews
{
	public sealed partial class MediaPreview : UserControl
	{
		private IUserSettingsService UserSettingsService { get; } = Ioc.Default.GetRequiredService<IUserSettingsService>();
		private bool unloaded;

		public MediaPreview(MediaPreviewViewModel model)
		{
			ViewModel = model;
			InitializeComponent();
			PlayerContext.Loaded += PlayerContext_Loaded;
			Unloaded += MediaPreview_Unloaded;
		}

		public MediaPreviewViewModel ViewModel { get; set; }

		private void PlayerContext_Loaded(object sender, RoutedEventArgs e)
		{
			// Consysto fork: the element creates its MediaPlayer only once it has a source, so in the constructor it is
			// still null. Subscribing there threw, and every video, playable or not, ended up «not supported» (08.10.2026)
			PlayerContext.MediaPlayer.MediaFailed += MediaPlayer_MediaFailed;
			PlayerContext.MediaPlayer.Volume = UserSettingsService.InfoPaneSettingsService.MediaVolume;
			PlayerContext.MediaPlayer.VolumeChanged += MediaPlayer_VolumeChanged;
			ViewModel.TogglePlaybackRequested += TogglePlaybackRequestInvoked;
		}

		private void MediaPreview_Unloaded(object sender, RoutedEventArgs e)
		{
			unloaded = true;
			if (PlayerContext.MediaPlayer is { } player)
				player.MediaFailed -= MediaPlayer_MediaFailed;
			// The MediaPlayerElement isn't properly disposed by Windows so we set the source to null
			// to avoid issues the next time the control is used.
			PlayerContext.Source = null;
			ViewModel.PreviewControlBase_Unloaded(sender, e);

			PlayerContext.Loaded -= PlayerContext_Loaded;
			Unloaded -= MediaPreview_Unloaded;

			PlayerContext.MediaPlayer.VolumeChanged -= MediaPlayer_VolumeChanged;
			ViewModel.TogglePlaybackRequested -= TogglePlaybackRequestInvoked;
		}

		private void MediaPlayer_MediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
		{
			App.Logger.LogWarning($"MediaPreview: {args.Error}, 0x{args.ExtendedErrorCode?.HResult:X8}, {args.ErrorMessage}");
			DispatcherQueue.TryEnqueue(() =>
			{
				if (unloaded)
					return;
				PlayerContext.Source = null;
				Content = new UnsupportedPreview();
			});
		}

		private void MediaPlayer_VolumeChanged(MediaPlayer sender, object args)
		{
			if (sender.Volume != UserSettingsService.InfoPaneSettingsService.MediaVolume)
			{
				UserSettingsService.InfoPaneSettingsService.MediaVolume = sender.Volume;
			}
		}

		private void TogglePlaybackRequestInvoked(object? sender, EventArgs e)
		{
			if (PlayerContext.MediaPlayer.PlaybackSession.PlaybackState is not MediaPlaybackState.Playing)
			{
				PlayerContext.MediaPlayer.Play();
			}
			else
			{
				PlayerContext.MediaPlayer.Pause();
			}
		}

		private void TogglePlaybackAcceleratorInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
		{
			TogglePlaybackRequestInvoked(sender, EventArgs.Empty);
		}
	}
}
