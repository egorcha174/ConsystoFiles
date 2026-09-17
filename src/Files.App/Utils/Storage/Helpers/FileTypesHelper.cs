// Copyright (c) Files Community
// SPDX-License-Identifier: MPL-2.0

using System.Collections.Concurrent;
using Windows.Win32;
using Windows.Win32.Storage.FileSystem;
using Windows.Win32.UI.Shell;

namespace Files.App.Utils.Storage
{
	/// <summary>
	/// Resolves the localized shell type name for a file extension (for example ".txt" produces "Text Document").
	/// </summary>
	public static class FileTypesHelper
	{
		// The type name is identical for every file of an extension, so cache it by extension.
		private static readonly ConcurrentDictionary<string, string> typeNameCache = new(StringComparer.OrdinalIgnoreCase);

		/// <summary>
		/// Consysto fork: CAD exchange formats get their own names. Whatever program registered them last names them otherwise,
		/// e.g. a laser cutter's software writes "AutoCAD图形交换(DXF)格式" for DXF.
		/// </summary>
		public static string? CadTypeName(string? extension)
			=> extension?.ToLowerInvariant() switch
			{
				".dxf" => Strings.ConsystoFileTypeDxf.GetLocalizedResource(),
				".dwg" => Strings.ConsystoFileTypeDwg.GetLocalizedResource(),
				".step" or ".stp" => Strings.ConsystoFileTypeStep.GetLocalizedResource(),
				".iges" or ".igs" => Strings.ConsystoFileTypeIges.GetLocalizedResource(),
				".stl" => Strings.ConsystoFileTypeStl.GetLocalizedResource(),
				".3mf" => Strings.ConsystoFileType3mf.GetLocalizedResource(),
				_ => null,
			};

		public static unsafe string GetLocalizedTypeName(string? extension)
		{
			if (string.IsNullOrEmpty(extension))
				return string.Empty;

			if (typeNameCache.TryGetValue(extension, out var cached))
				return cached;

			if (CadTypeName(extension) is { } cadTypeName)
				return typeNameCache[extension] = cadTypeName;

			var typeName = string.Empty;
			SHFILEINFOW shfi = default;

			fixed (char* pExtension = extension)
			{
				// SHGFI_USEFILEATTRIBUTES resolves from the extension alone, so this never touches disk
				var result = PInvoke.SHGetFileInfo(
					pExtension,
					FILE_FLAGS_AND_ATTRIBUTES.FILE_ATTRIBUTE_NORMAL,
					&shfi,
					(uint)sizeof(SHFILEINFOW),
					SHGFI_FLAGS.SHGFI_TYPENAME | SHGFI_FLAGS.SHGFI_USEFILEATTRIBUTES);

				if (result != 0 && shfi.szTypeName.Value[0] != '\0')
					typeName = shfi.szTypeName.ToString();
			}

			typeNameCache[extension] = typeName;
			return typeName;
		}
	}
}
