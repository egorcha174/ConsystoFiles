// Copyright (c) Files Community
// SPDX-License-Identifier: MPL-2.0

using Windows.Storage;
using Windows.System;

namespace Files.App.Actions
{
	[GeneratedRichCommand]
	internal sealed partial class OpenLogFileLocationAction : IAction
	{
		public string Label
			=> Strings.OpenLogLocation.GetLocalizedResource();

		public string Description
			=> Strings.OpenLogFileLocationDescription.GetLocalizedResource();

		public ActionCategory Category
			=> ActionCategory.Open;

		public HotKey HotKey
			=> HotKey.None; // Consysto fork: Ctrl+Shift+. toggles hidden items, as in Finder

		public async Task ExecuteAsync(object? parameter = null)
		{
			await Launcher.LaunchFolderAsync(await AppStorage.GetLocalFolderAsync()).AsTask();
		}
	}
}
