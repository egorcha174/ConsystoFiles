// Copyright (c) Files Community
// Licensed under the MIT License.

namespace Files.App.Data.Contracts
{
	public interface IApplicationSettingsService : IBaseSettingsService
	{
		/// <summary>
		/// Gets or sets a value indicating whether or not the user clicked the 'review' prompt.
		/// </summary>
		bool HasClickedReviewPrompt { get; set; }

		/// <summary>
		/// Gets or sets a value indicating whether or not the user clicked the 'sponsor' prompt.
		/// </summary>
		bool HasClickedSponsorPrompt { get; set; }

		/// <summary>
		/// Gets or sets a value indicating whether or not to display a prompt when running the app as administrator.
		/// </summary>
		bool ShowRunningAsAdminPrompt { get; set; }

		/// <summary>
		/// Gets or sets a value indicating whether or not to display a prompt when creating an alternate data stream.
		/// </summary>
		bool ShowDataStreamsAreHiddenPrompt { get; set; }

		/// <summary>
		/// Consysto fork: gets or sets whether the app looks for a newer release on its own.
		/// </summary>
		bool CheckForUpdatesAutomatically { get; set; }

		/// <summary>
		/// Consysto fork: gets or sets the release the user chose to skip.
		/// </summary>
		string SkippedUpdateVersion { get; set; }

		/// <summary>
		/// Consysto fork: gets or sets the release whose dialog was already shown on its own, so it is offered only once.
		/// </summary>
		string LastOfferedUpdateVersion { get; set; }

	}
}
