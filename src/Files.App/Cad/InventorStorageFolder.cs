// Consysto fork: an Inventor assembly opened like a folder — inside are the documents it is built from.

using Consysto.CadPreview.Inventor;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Foundation;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Search;
using IO = System.IO;

namespace Files.App.Cad
{
	/// <summary>Paths of assemblies browsed as folders: the assembly file itself is the folder, it has no inner paths.</summary>
	public static class InventorAssemblyPaths
	{
		public static bool IsAssemblyPath(string? path)
			=> path is not null
				&& path.EndsWith(".iam", StringComparison.OrdinalIgnoreCase)
				&& IO.File.Exists(path);

		/// <summary>True while the page lists the parts of an assembly: they are real files elsewhere, so nothing here may change them.</summary>
		public static bool IsShowingAssembly(IShellPage? page)
			=> IsAssemblyPath(page?.ShellViewModel?.WorkingDirectory);
	}

	/// <summary>
	/// The parts of an assembly as its contents. They are ordinary files elsewhere on the disk, so everything that changes
	/// them — creating, renaming, deleting — is refused here and done in their own folder.
	/// </summary>
	public sealed partial class InventorStorageFolder : BaseStorageFolder
	{
		public override string Path { get; }
		public override string Name { get; }
		public override string DisplayName => Name;
		public override string DisplayType => Strings.Folder.GetLocalizedResource();
		public override string FolderRelativeId => $"0\\{Name}";

		public override DateTimeOffset DateCreated { get; }
		public override Windows.Storage.FileAttributes Attributes => Windows.Storage.FileAttributes.Directory;
		public override IStorageItemExtraProperties Properties => new BaseBasicStorageItemExtraProperties(this);

		public InventorStorageFolder(string path)
		{
			Path = path;
			Name = IO.Path.GetFileName(path);
			DateCreated = SafetyExtensions.IgnoreExceptions(() => new DateTimeOffset(IO.File.GetCreationTime(path)));
		}

		public static IAsyncOperation<BaseStorageFolder?> FromPathAsync(string path)
			=> Task.FromResult<BaseStorageFolder?>(InventorAssemblyPaths.IsAssemblyPath(path) ? new InventorStorageFolder(path) : null)
				.AsAsyncOperation();

		public override IAsyncOperation<StorageFolder> ToStorageFolderAsync() => throw new NotSupportedException();

		public override bool IsEqual(IStorageItem item) => item?.Path == Path;
		public override bool IsOfType(StorageItemTypes type) => type == StorageItemTypes.Folder;

		public override IAsyncOperation<IndexedState> GetIndexedStateAsync()
			=> Task.FromResult(IndexedState.NotIndexed).AsAsyncOperation();

		public override IAsyncOperation<BaseStorageFolder?> GetParentAsync()
			=> AsyncInfo.Run<BaseStorageFolder?>(async (cancellationToken)
				=> IO.Path.GetDirectoryName(Path) is { } parent ? await SystemStorageFolder.FromPathAsync(parent) : null);

		/// <summary>The dates and size of the assembly file itself: it is what the user sees in the properties.</summary>
		public override IAsyncOperation<BaseBasicProperties> GetBasicPropertiesAsync()
			=> AsyncInfo.Run(async (cancellationToken) =>
			{
				var file = await SystemStorageFile.FromPathAsync(Path);
				return file is null ? new BaseBasicProperties() : await file.GetBasicPropertiesAsync();
			});

		public override IAsyncOperation<IReadOnlyList<IStorageItem>?> GetItemsAsync()
			=> AsyncInfo.Run<IReadOnlyList<IStorageItem>?>(async (cancellationToken) =>
			{
				var references = await Task.Run(() => InventorReferenceReader.Read(Path), cancellationToken);
				var items = new List<IStorageItem>();
				foreach (var reference in references)
				{
					// A part that has moved away cannot be listed: there is no file to show
					if (reference.ResolvedPath is { } resolved && await SystemStorageFile.FromPathAsync(resolved) is { } file)
						items.Add(file);
				}

				return items;
			});

		public override IAsyncOperation<IReadOnlyList<IStorageItem>?> GetItemsAsync(uint startIndex, uint maxItemsToRetrieve)
			=> AsyncInfo.Run<IReadOnlyList<IStorageItem>?>(async (cancellationToken) =>
			{
				var items = await GetItemsAsync();
				return items?.Skip((int)startIndex).Take((int)maxItemsToRetrieve).ToList();
			});

		public override IAsyncOperation<IStorageItem?> GetItemAsync(string name)
			=> AsyncInfo.Run<IStorageItem?>(async (cancellationToken) =>
			{
				var items = await GetItemsAsync();
				return items?.FirstOrDefault(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
			});

		public override IAsyncOperation<IStorageItem?> TryGetItemAsync(string name) => GetItemAsync(name);

		public override IAsyncOperation<BaseStorageFile?> GetFileAsync(string name)
			=> AsyncInfo.Run<BaseStorageFile?>(async (cancellationToken) => await GetItemAsync(name) as BaseStorageFile);

		public override IAsyncOperation<IReadOnlyList<BaseStorageFile>?> GetFilesAsync()
			=> AsyncInfo.Run<IReadOnlyList<BaseStorageFile>?>(async (cancellationToken) =>
			{
				var items = await GetItemsAsync();
				return items?.OfType<BaseStorageFile>().ToList();
			});

		public override IAsyncOperation<IReadOnlyList<BaseStorageFile>?> GetFilesAsync(CommonFileQuery query)
			=> GetFilesAsync();

		public override IAsyncOperation<IReadOnlyList<BaseStorageFile>?> GetFilesAsync(CommonFileQuery query, uint startIndex, uint maxItemsToRetrieve)
			=> AsyncInfo.Run<IReadOnlyList<BaseStorageFile>?>(async (cancellationToken) =>
			{
				var files = await GetFilesAsync();
				return files?.Skip((int)startIndex).Take((int)maxItemsToRetrieve).ToList();
			});

		// An assembly has no folders inside it, only documents
		public override IAsyncOperation<BaseStorageFolder?> GetFolderAsync(string name) => throw new NotSupportedException();
		public override IAsyncOperation<IReadOnlyList<BaseStorageFolder>?> GetFoldersAsync()
			=> Task.FromResult<IReadOnlyList<BaseStorageFolder>?>([]).AsAsyncOperation();
		public override IAsyncOperation<IReadOnlyList<BaseStorageFolder>?> GetFoldersAsync(CommonFolderQuery query) => GetFoldersAsync();
		public override IAsyncOperation<IReadOnlyList<BaseStorageFolder>?> GetFoldersAsync(CommonFolderQuery query, uint startIndex, uint maxItemsToRetrieve)
			=> GetFoldersAsync();

		// The contents of an assembly are decided in Inventor, not in the file manager
		public override IAsyncOperation<BaseStorageFile?> CreateFileAsync(string desiredName) => throw new NotSupportedException();
		public override IAsyncOperation<BaseStorageFile?> CreateFileAsync(string desiredName, CreationCollisionOption options) => throw new NotSupportedException();
		public override IAsyncOperation<BaseStorageFolder?> CreateFolderAsync(string desiredName) => throw new NotSupportedException();
		public override IAsyncOperation<BaseStorageFolder?> CreateFolderAsync(string desiredName, CreationCollisionOption options) => throw new NotSupportedException();
		public override IAsyncOperation<BaseStorageFolder?> MoveAsync(IStorageFolder destinationFolder) => throw new NotSupportedException();
		public override IAsyncOperation<BaseStorageFolder?> MoveAsync(IStorageFolder destinationFolder, NameCollisionOption option) => throw new NotSupportedException();
		public override IAsyncAction RenameAsync(string desiredName) => throw new NotSupportedException();
		public override IAsyncAction RenameAsync(string desiredName, NameCollisionOption option) => throw new NotSupportedException();
		public override IAsyncAction DeleteAsync() => throw new NotSupportedException();
		public override IAsyncAction DeleteAsync(StorageDeleteOption option) => throw new NotSupportedException();

		public override IAsyncOperation<StorageItemThumbnail?> GetThumbnailAsync(ThumbnailMode mode)
			=> Task.FromResult<StorageItemThumbnail?>(null).AsAsyncOperation();
		public override IAsyncOperation<StorageItemThumbnail?> GetThumbnailAsync(ThumbnailMode mode, uint requestedSize)
			=> Task.FromResult<StorageItemThumbnail?>(null).AsAsyncOperation();
		public override IAsyncOperation<StorageItemThumbnail?> GetThumbnailAsync(ThumbnailMode mode, uint requestedSize, ThumbnailOptions options)
			=> Task.FromResult<StorageItemThumbnail?>(null).AsAsyncOperation();

		public override bool AreQueryOptionsSupported(QueryOptions queryOptions) => false;
		public override bool IsCommonFileQuerySupported(CommonFileQuery query) => false;
		public override bool IsCommonFolderQuerySupported(CommonFolderQuery query) => false;

		public override StorageItemQueryResult CreateItemQuery() => throw new NotSupportedException();
		public override BaseStorageItemQueryResult CreateItemQueryWithOptions(QueryOptions queryOptions) => new(this, queryOptions);
		public override StorageFileQueryResult CreateFileQuery() => throw new NotSupportedException();
		public override StorageFileQueryResult CreateFileQuery(CommonFileQuery query) => throw new NotSupportedException();
		public override BaseStorageFileQueryResult CreateFileQueryWithOptions(QueryOptions queryOptions) => new(this, queryOptions);
		public override StorageFolderQueryResult CreateFolderQuery() => throw new NotSupportedException();
		public override StorageFolderQueryResult CreateFolderQuery(CommonFolderQuery query) => throw new NotSupportedException();
		public override BaseStorageFolderQueryResult CreateFolderQueryWithOptions(QueryOptions queryOptions) => new(this, queryOptions);
	}
}
