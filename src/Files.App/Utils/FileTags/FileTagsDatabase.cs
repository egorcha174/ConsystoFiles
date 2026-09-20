// Copyright (c) Files Community
// Licensed under the MIT License.

using Microsoft.Win32;
using System.Runtime.CompilerServices;
using System.Security;
using Windows.ApplicationModel;
using static Files.App.Helpers.RegistryHelpers;
using static Files.App.Utils.FileTags.TaggedFileRegistry;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace Files.App.Utils.FileTags
{
	public sealed class FileTagsDatabase
	{
		private static string? _FileTagsKey;
		private string? FileTagsKey => _FileTagsKey ??= SafetyExtensions.IgnoreExceptions(() => @$"Software\Files Community\{AppStorage.PackageName}\v1\FileTags");

		public void SetTags(string filePath, ulong? frn, string[] tags)
		{
			if (FileTagsKey is null)
				return;

			if (tags is [])
			{
				using var existingFilePathKey = AppStorage.UserRegistry.OpenSubKey(CombineKeys(FileTagsKey, filePath), writable: true);
				if (existingFilePathKey is not null)
					SaveValues(existingFilePathKey, null);

				if (frn is not null)
				{
					using var existingFrnKey = AppStorage.UserRegistry.OpenSubKey(CombineKeys(FileTagsKey, "FRN", frn.Value.ToString()), writable: true);
					if (existingFrnKey is not null)
						SaveValues(existingFrnKey, null);
				}

				return;
			}

			using var filePathKey = AppStorage.UserRegistry.CreateSubKey(CombineKeys(FileTagsKey, filePath));

			var newTag = new TaggedFile()
			{
				FilePath = filePath,
				Frn = frn,
				Tags = tags
			};
			SaveValues(filePathKey, newTag);

			if (frn is not null)
			{
				using var frnKey = AppStorage.UserRegistry.CreateSubKey(CombineKeys(FileTagsKey, "FRN", frn.Value.ToString()));
				SaveValues(frnKey, newTag);
			}
		}

		private TaggedFile? FindTag(string? filePath, ulong? frn)
		{
			if (FileTagsKey is null)
				return null;

			if (filePath is not null)
			{
				using var filePathKey = AppStorage.UserRegistry.OpenSubKey(CombineKeys(FileTagsKey, filePath), writable: true);
				if (filePathKey is not null && filePathKey.ValueCount > 0)
				{
					var tag = new TaggedFile();
					BindValues(filePathKey, tag);
					if (frn is not null)
					{
						// Keep entry updated
						tag.Frn = frn;
						var value = frn.Value;
						filePathKey.SetValue(nameof(TaggedFile.Frn), Unsafe.As<ulong, long>(ref value), RegistryValueKind.QWord);
					}
					return tag;
				}
			}

			if (frn is not null)
			{
				using var frnKey = AppStorage.UserRegistry.OpenSubKey(CombineKeys(FileTagsKey, "FRN", frn.Value.ToString()), writable: true);
				if (frnKey is not null && frnKey.ValueCount > 0)
				{
					var tag = new TaggedFile();
					BindValues(frnKey, tag);
					if (filePath is not null)
					{
						// Keep entry updated
						tag.FilePath = filePath;
						frnKey.SetValue(nameof(TaggedFile.FilePath), filePath, RegistryValueKind.String);
					}
					return tag;
				}
			}

			return null;
		}

		public void UpdateTag(string oldFilePath, ulong? frn, string? newFilePath)
		{
			if (FileTagsKey is null)
				return;

			var tag = FindTag(oldFilePath, null);
			using var filePathKey = AppStorage.UserRegistry.OpenSubKey(CombineKeys(FileTagsKey, oldFilePath), writable: true);
			if (filePathKey is not null)
				SaveValues(filePathKey, null);

			if (tag is not null)
			{
				tag.Frn = frn ?? tag.Frn;
				tag.FilePath = newFilePath ?? tag.FilePath;

				if (frn is not null)
				{
					using var newFrnKey = AppStorage.UserRegistry.CreateSubKey(CombineKeys(FileTagsKey, "FRN", frn.Value.ToString()));
					SaveValues(newFrnKey, tag);
				}

				if (newFilePath is not null)
				{
					using var newFilePathKey = AppStorage.UserRegistry.CreateSubKey(CombineKeys(FileTagsKey, newFilePath));
					SaveValues(newFilePathKey, tag);
				}
			}
		}

		public void UpdateTag(ulong oldFrn, ulong? frn, string? newFilePath)
		{
			if (FileTagsKey is null)
				return;

			var tag = FindTag(null, oldFrn);
			using var frnKey = AppStorage.UserRegistry.OpenSubKey(CombineKeys(FileTagsKey, "FRN", oldFrn.ToString()), writable: true);
			if (frnKey is not null)
				SaveValues(frnKey, null);

			if (tag is not null)
			{
				tag.Frn = frn ?? tag.Frn;
				tag.FilePath = newFilePath ?? tag.FilePath;

				if (frn is not null)
				{
					using var newFrnKey = AppStorage.UserRegistry.CreateSubKey(CombineKeys(FileTagsKey, "FRN", frn.Value.ToString()));
					SaveValues(newFrnKey, tag);
				}

				if (newFilePath is not null)
				{
					using var newFilePathKey = AppStorage.UserRegistry.CreateSubKey(CombineKeys(FileTagsKey, newFilePath));
					SaveValues(newFilePathKey, tag);
				}
			}
		}

		public string[] GetTags(string? filePath, ulong? frn)
		{
			return FindTag(filePath, frn)?.Tags ?? [];
		}

		public IEnumerable<TaggedFile> GetAll()
		{
			var list = new List<TaggedFile>();

			if (FileTagsKey is not null)
			{
				try
				{
					IterateKeys(list, FileTagsKey, 0);
				}
				catch (SecurityException)
				{
					// Handle edge case where IterateKeys results in SecurityException
				}
			}

			return list;
		}

		public IEnumerable<TaggedFile> GetAllUnderPath(string folderPath)
		{
			folderPath = folderPath.Replace('/', '\\').TrimStart('\\');
			var list = new List<TaggedFile>();

			if (FileTagsKey is not null)
			{
				try
				{
					IterateKeys(list, CombineKeys(FileTagsKey, folderPath), 0);
				}
				catch (SecurityException)
				{
					// Handle edge case where IterateKeys results in SecurityException
				}
			}

			return list;
		}

		public void Import(string json)
		{
			if (FileTagsKey is null)
				return;

			var tags = JsonSerializer.Deserialize(json, AppJsonSerializerContext.Default.TaggedFileArray);

			AppStorage.UserRegistry.DeleteSubKeyTree(FileTagsKey, false);
			if (tags is null)
			{
				return;
			}
			foreach (var tag in tags)
			{
				using var filePathKey = AppStorage.UserRegistry.CreateSubKey(CombineKeys(FileTagsKey, tag.FilePath));
				SaveValues(filePathKey, tag);
				if (tag.Frn is not null)
				{
					using var frnKey = AppStorage.UserRegistry.CreateSubKey(CombineKeys(FileTagsKey, "FRN", tag.Frn.Value.ToString()));
					SaveValues(frnKey, tag);
				}
			}
		}

		public string Export()
		{
			var list = new List<TaggedFile>();

			if (FileTagsKey is not null)
				IterateKeys(list, FileTagsKey, 0);

			return JsonSerializer.Serialize(list, AppJsonSerializerContext.Default.ListTaggedFile);
		}

		private void IterateKeys(List<TaggedFile> list, string path, int depth)
		{
			using var key = AppStorage.UserRegistry.OpenSubKey(path);
			if (key is null)
				return;

			if (key.ValueCount > 0)
			{
				var tag = new TaggedFile();
				BindValues(key, tag);
				list.Add(tag);
			}

			foreach (var subKey in key.GetSubKeyNames())
			{
				// Skip FRN key
				if (depth == 0 && subKey == "FRN")
					continue;

				IterateKeys(list, CombineKeys(path, subKey), depth + 1);
			}
		}
	}
}
