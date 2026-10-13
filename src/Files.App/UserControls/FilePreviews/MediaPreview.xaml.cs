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
		private MediaPlayer? subscribedPlayer;

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
			if (unloaded || PlayerContext.MediaPlayer is not { } player || ReferenceEquals(player, subscribedPlayer))
				return;

			DetachPlayer();
			subscribedPlayer = player;
			player.Volume = UserSettingsService.InfoPaneSettingsService.MediaVolume;
			player.MediaFailed += MediaPlayer_MediaFailed;
			player.VolumeChanged += MediaPlayer_VolumeChanged;
			ViewModel.TogglePlaybackRequested += TogglePlaybackRequestInvoked;
		}

		private void DetachPlayer()
		{
			if (subscribedPlayer is { } player)
			{
				player.MediaFailed -= MediaPlayer_MediaFailed;
				player.VolumeChanged -= MediaPlayer_VolumeChanged;
				subscribedPlayer = null;
			}
			ViewModel.TogglePlaybackRequested -= TogglePlaybackRequestInvoked;
		}

		private void MediaPreview_Unloaded(object sender, RoutedEventArgs e)
		{
			if (unloaded)
				return;

			unloaded = true;
			// Detach from the original player before clearing Source can release it.
			DetachPlayer();
			// The MediaPlayerElement isn't properly disposed by Windows so we set the source to null
			// to avoid issues the next time the control is used.
			PlayerContext.Source = null;
			ViewModel.PreviewControlBase_Unloaded(sender, e);

			PlayerContext.Loaded -= PlayerContext_Loaded;
			Unloaded -= MediaPreview_Unloaded;
		}

		private void MediaPlayer_MediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
		{
			App.Logger.LogWarning($"MediaPreview: {args.Error}, 0x{args.ExtendedErrorCode?.HResult:X8}, {args.ErrorMessage}");
			DispatcherQueue.TryEnqueue(() =>
			{
				if (unloaded || !ReferenceEquals(sender, subscribedPlayer))
					return;
				DetachPlayer();
				PlayerContext.Source = null;
				Content = new UnsupportedPreview();
			});
		}

		private void MediaPlayer_VolumeChanged(MediaPlayer sender, object args)
		{
			DispatcherQueue.TryEnqueue(() =>
			{
				if (unloaded || !ReferenceEquals(sender, subscribedPlayer))
					return;

				var volume = sender.Volume;
				if (volume != UserSettingsService.InfoPaneSettingsService.MediaVolume)
					UserSettingsService.InfoPaneSettingsService.MediaVolume = volume;
			});
		}

		private void TogglePlaybackRequestInvoked(object? sender, EventArgs e)
		{
			if (unloaded || subscribedPlayer is not { } player)
				return;

			if (player.PlaybackSession.PlaybackState is not MediaPlaybackState.Playing)
			{
				player.Play();
			}
			else
			{
				player.Pause();
			}
		}

		private void TogglePlaybackAcceleratorInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
		{
			TogglePlaybackRequestInvoked(sender, EventArgs.Empty);
		}
	}
}
