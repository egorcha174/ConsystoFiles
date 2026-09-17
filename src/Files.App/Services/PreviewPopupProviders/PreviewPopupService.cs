// Copyright (c) Files Community
// SPDX-License-Identifier: MPL-2.0

namespace Files.App.Services.PreviewPopupProviders
{
	/// <inheritdoc cref="IPreviewPopupService"/>
	internal sealed partial class PreviewPopupService : ObservableObject, IPreviewPopupService
	{
		public async Task<IPreviewPopupProvider?> GetProviderAsync()
		{
			// Consysto fork: the built-in preview goes first, it draws the CAD formats the external viewers can't
			if (await Files.App.MacStyle.QuickPreviewProvider.Instance.DetectAvailability())
				return Files.App.MacStyle.QuickPreviewProvider.Instance;
			if (await QuickLookProvider.Instance.DetectAvailability())
				return await Task.FromResult<IPreviewPopupProvider>(QuickLookProvider.Instance);
			if (await SeerProProvider.Instance.DetectAvailability())
				return await Task.FromResult<IPreviewPopupProvider>(SeerProProvider.Instance);
			if (await PowerToysPeekProvider.Instance.DetectAvailability())
				return await Task.FromResult<IPreviewPopupProvider>(PowerToysPeekProvider.Instance);
			else
				return null;
		}
	}
}
