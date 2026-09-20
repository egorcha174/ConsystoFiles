using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace Files.App.Helpers.Application
{
	internal static class AppToastNotificationHelper
	{
		// Consysto fork: the notification centre only accepts messages from an installed app, so the portable build
		// says nothing. Errors still go to data\Local\debug.log.
		private static bool Silent
			=> AppStorage.IsPortable;

		public static void ShowUnhandledExceptionToast()
		{
			if (Silent)
				return;

			var toastContent = new AppNotificationBuilder()
					.AddText(Strings.ExceptionNotificationHeader.GetLocalizedResource())
					.AddText(Strings.ExceptionNotificationBody.GetLocalizedResource())
					.SetAppLogoOverride(new Uri("ms-appx:///Assets/error.png"))
					.AddButton(new AppNotificationButton(Strings.ExceptionNotificationReportButton.GetLocalizedResource())
						.SetInvokeUri(new Uri(Constants.ExternalUrl.BugReportUrl)))
					.BuildNotification();
			AppNotificationManager.Default.Show(toastContent);
		}

		public static void ShowBackgroundRunningToast()
		{
			if (Silent)
				return;

			var toastContent = new AppNotificationBuilder()
				.AddText(Strings.BackgroundRunningNotificationHeader.GetLocalizedResource())
				.AddText(Strings.BackgroundRunningNotificationBody.GetLocalizedResource())
				.BuildNotification();
			AppNotificationManager.Default.Show(toastContent);
		}

		public static void ShowDriveEjectToast()
		{
			if (Silent)
				return;

			var toastContent = new AppNotificationBuilder()
				.AddText(Strings.EjectNotificationHeader.GetLocalizedResource())
				.AddText(Strings.EjectNotificationBody.GetLocalizedResource())
				.SetAttributionText("SettingsAboutAppName".GetLocalizedResource())
				.BuildNotification();
			AppNotificationManager.Default.Show(toastContent);
		}
	}
}
