// Copyright (c) Files Community
// Licensed under the MIT License.

using Microsoft.UI.Xaml.Markup;

namespace Files.App.Helpers
{
	[MarkupExtensionReturnType(ReturnType = typeof(string))]
	public sealed partial class ResourceString : MarkupExtension
	{
		public string Name { get; set; } = string.Empty;

		// Consysto fork: the Windows App SDK resource manager (same strings, same cache as code) also works without a package
		protected override object ProvideValue() => Name.GetLocalizedResource();
	}
}
