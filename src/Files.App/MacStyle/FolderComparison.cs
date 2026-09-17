using System.Globalization;
using System.IO;

namespace Files.App.MacStyle
{
	public enum FolderCompareState
	{
		OnlyLeft,
		OnlyRight,
		LeftNewer,
		RightNewer,
		Different,
		Identical,
		TypeMismatch,
	}

	public enum FolderSyncDirection
	{
		None,
		LeftToRight,
		RightToLeft,
	}

	/// <summary>
	/// Consysto fork: one row of the folder comparison, a file or a folder present on one side only, with the chosen copy direction.
	/// </summary>
	public sealed partial class FolderCompareEntry : ObservableObject
	{
		public FolderCompareEntry(string relativePath, FileSystemInfo? left, FileSystemInfo? right, FolderCompareState state)
		{
			RelativePath = relativePath;
			State = state;
			IsDirectory = (left ?? right) is DirectoryInfo;
			LeftSizeText = FormatSize(left);
			LeftDateText = FormatDate(left);
			RightSizeText = FormatSize(right);
			RightDateText = FormatDate(right);
			_action = state switch
			{
				FolderCompareState.OnlyLeft or FolderCompareState.LeftNewer => FolderSyncDirection.LeftToRight,
				FolderCompareState.OnlyRight or FolderCompareState.RightNewer => FolderSyncDirection.RightToLeft,
				_ => FolderSyncDirection.None,
			};
		}

		public string RelativePath { get; }

		public bool IsDirectory { get; }

		public FolderCompareState State { get; }

		public string LeftSizeText { get; }

		public string LeftDateText { get; }

		public string RightSizeText { get; }

		public string RightDateText { get; }

		public string TypeGlyph
			=> IsDirectory ? "\uE8B7" : "\uE8A5";

		public double RowOpacity
			=> State is FolderCompareState.Identical ? 0.6 : 1;

		public bool CanChangeAction
			=> State is not FolderCompareState.TypeMismatch;

		private FolderSyncDirection _action;
		public FolderSyncDirection Action
		{
			get => _action;
			set
			{
				if (SetProperty(ref _action, value))
					OnPropertyChanged(nameof(ActionText));
			}
		}

		public string ActionText
			=> Action switch
			{
				FolderSyncDirection.LeftToRight => "→",
				FolderSyncDirection.RightToLeft => "←",
				_ => State is FolderCompareState.Identical ? "=" : "≠",
			};

		// An item present on one side can only be copied from there; otherwise the click goes →, ←, skip.
		public void CycleAction()
		{
			Action = State switch
			{
				FolderCompareState.OnlyLeft => Action is FolderSyncDirection.None ? FolderSyncDirection.LeftToRight : FolderSyncDirection.None,
				FolderCompareState.OnlyRight => Action is FolderSyncDirection.None ? FolderSyncDirection.RightToLeft : FolderSyncDirection.None,
				FolderCompareState.TypeMismatch => FolderSyncDirection.None,
				_ => Action switch
				{
					FolderSyncDirection.None => FolderSyncDirection.LeftToRight,
					FolderSyncDirection.LeftToRight => FolderSyncDirection.RightToLeft,
					_ => FolderSyncDirection.None,
				},
			};
		}

		private static string FormatSize(FileSystemInfo? info)
			=> info is FileInfo file ? file.Length.ToSizeString() : string.Empty;

		private static string FormatDate(FileSystemInfo? info)
			=> info is null ? string.Empty : info.LastWriteTime.ToString("g", CultureInfo.CurrentCulture);
	}

	/// <summary>
	/// Consysto fork: compares two folders by name, size and modification time, like "Synchronize directories" in Total Commander.
	/// Folders present on both sides are descended into; a folder present on one side is reported as a single entry.
	/// </summary>
	internal static class FolderComparer
	{
		// FAT and some network shares keep modification times with a two-second step.
		private static readonly TimeSpan TimeTolerance = TimeSpan.FromSeconds(2);

		public static List<FolderCompareEntry> Compare(string leftRoot, string rightRoot, bool recursive, bool includeHidden, CancellationToken token)
		{
			var options = new EnumerationOptions
			{
				IgnoreInaccessible = true,
				AttributesToSkip = includeHidden ? FileAttributes.System : FileAttributes.System | FileAttributes.Hidden,
			};

			var entries = new List<FolderCompareEntry>();
			CompareDirectory(leftRoot, rightRoot, string.Empty, recursive, options, entries, token);
			return entries;
		}

		private static void CompareDirectory(string leftRoot, string rightRoot, string relativePath, bool recursive, EnumerationOptions options, List<FolderCompareEntry> entries, CancellationToken token)
		{
			token.ThrowIfCancellationRequested();

			var left = ReadDirectory(Path.Combine(leftRoot, relativePath), options);
			var right = ReadDirectory(Path.Combine(rightRoot, relativePath), options);

			var names = left.Keys
				.Union(right.Keys, StringComparer.OrdinalIgnoreCase)
				.OrderBy(name => (left.GetValueOrDefault(name) ?? right[name]) is DirectoryInfo ? 0 : 1)
				.ThenBy(name => name, StringComparer.CurrentCultureIgnoreCase)
				.ToList();

			foreach (var name in names)
			{
				left.TryGetValue(name, out var leftItem);
				right.TryGetValue(name, out var rightItem);
				var itemPath = Path.Combine(relativePath, name);

				if (leftItem is DirectoryInfo leftDirectory && rightItem is DirectoryInfo)
				{
					// Junctions can loop back up the tree.
					if (recursive && !leftDirectory.Attributes.HasFlag(FileAttributes.ReparsePoint))
						CompareDirectory(leftRoot, rightRoot, itemPath, recursive, options, entries, token);

					continue;
				}

				entries.Add(new FolderCompareEntry(itemPath, leftItem, rightItem, GetState(leftItem, rightItem)));
			}
		}

		private static FolderCompareState GetState(FileSystemInfo? left, FileSystemInfo? right)
		{
			if (left is null)
				return FolderCompareState.OnlyRight;
			if (right is null)
				return FolderCompareState.OnlyLeft;
			if (left is not FileInfo leftFile || right is not FileInfo rightFile)
				return FolderCompareState.TypeMismatch;

			var difference = leftFile.LastWriteTimeUtc - rightFile.LastWriteTimeUtc;
			if (difference > TimeTolerance)
				return FolderCompareState.LeftNewer;
			if (difference < -TimeTolerance)
				return FolderCompareState.RightNewer;

			return leftFile.Length == rightFile.Length ? FolderCompareState.Identical : FolderCompareState.Different;
		}

		private static Dictionary<string, FileSystemInfo> ReadDirectory(string path, EnumerationOptions options)
		{
			var items = new Dictionary<string, FileSystemInfo>(StringComparer.OrdinalIgnoreCase);
			try
			{
				foreach (var info in new DirectoryInfo(path).EnumerateFileSystemInfos("*", options))
					items[info.Name] = info;
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
			}

			return items;
		}
	}
}
