// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.App.UserControls.FilePreviews;
using Files.App.ViewModels.Properties;

namespace Files.App.ViewModels.Previews
{
	public sealed partial class TextPreviewViewModel : BasePreviewModel
	{
		private string? textValue;
		public string? TextValue
		{
			get => textValue;
			private set => SetProperty(ref textValue, value);
		}

		public bool HasPreview => TextValue is not null;

		public TextPreviewViewModel(ListedItem item)
			: base(item)
		{
		}

		public async override Task<List<FileProperty>> LoadPreviewAndDetailsAsync()
		{
			var details = new List<FileProperty>();

			try
			{
				var text = TextValue ?? await ReadFileAsTextAsync(PreviewFile);

				details.Add(GetFileProperty("PropertyLineCount", text.Split('\n').Length));
				details.Add(GetFileProperty("PropertyWordCount", text.Split(new[] { ' ', '\n' }, StringSplitOptions.RemoveEmptyEntries).Length));

				TextValue = text.Left(Constants.PreviewPane.TextCharacterLimit);
			}
			catch (Exception e)
			{
				Debug.WriteLine(e);
			}

			return details;
		}

		public static async Task<TextPreview?> TryLoadAsTextAsync(ListedItem item)
		{
			string? extension = item.FileExtension?.ToLowerInvariant();
			if (ExcludedExtensions(extension) || item.FileSizeBytes is 0 or > Constants.PreviewPane.TryLoadAsTextSizeLimit)
				return null;

			try
			{
				item.ItemFile = await StorageFileExtensions.DangerousGetFileFromPathAsync(item.ItemPath!);
				if (item.ItemFile is not { } itemFile)
					return null;

				// Unknown formats may fall back to readable text, never to decoded binary garbage.
				// The check works on the bytes: a valid cp1251 text is not binary just because it is not UTF-8.
				const int limit = 10 * 1024 * 1024;
				var bytes = await ReadFileBytesAsync(itemFile, limit);
				bool truncated = bytes.Length >= limit;
				if (TextFileDecoder.LooksBinary(bytes, truncated))
					return null;

				var model = new TextPreviewViewModel(item) { TextValue = TextFileDecoder.Decode(bytes, truncated) };
				await model.LoadAsync();

				return new TextPreview(model);
			}
			catch
			{
				return null;
			}
		}

		private static bool ExcludedExtensions(string? extension)
			=> extension is ".iso";
	}
}
