// Consysto fork: one-way backup of a folder — what would be copied, and copying it.

using System.IO;
using System.IO.Enumeration;

namespace Files.App.Sync
{
	public enum BackupItemKind
	{
		/// <summary>The file is not in the target yet.</summary>
		New,

		/// <summary>The source file is newer or differs in size.</summary>
		Changed,

		/// <summary>The target file is newer than the source; a backup does not overwrite it unless asked.</summary>
		TargetNewer,
	}

	public sealed record BackupItem(string RelativePath, BackupItemKind Kind, long Size, DateTime SourceModified, DateTime? TargetModified);

	public sealed record BackupPlan(string Source, string Target, IReadOnlyList<BackupItem> Items, int FilesChecked, int FoldersSkipped)
	{
		public IEnumerable<BackupItem> ToCopy(bool includeTargetNewer)
			=> Items.Where(item => includeTargetNewer || item.Kind is not BackupItemKind.TargetNewer);
	}

	/// <summary>Where a scan or a copy is: files looked at or copied, bytes copied, the file at hand.</summary>
	public sealed record BackupProgress(int Done, int Total, long Bytes, long TotalBytes, string? Path);

	/// <summary>
	/// Compares a source folder with its backup by size and modification time and copies what is new or changed. Nothing in the
	/// target is ever deleted. Copies keep the modification time, so the next scan sees them as identical.
	/// </summary>
	public static class BackupPlanner
	{
		// FAT, exFAT and some network shares keep modification times with a two-second step.
		private static readonly TimeSpan TimeTolerance = TimeSpan.FromSeconds(2);
		private const int CopyBufferBytes = 1024 * 1024;

		public static BackupPlan Scan(string source, string target, IReadOnlyList<string> exclusions, IProgress<BackupProgress>? progress, CancellationToken token)
		{
			var items = new List<BackupItem>();
			var checkedCount = 0;
			var skipped = 0;
			var options = new EnumerationOptions
			{
				IgnoreInaccessible = true,
				AttributesToSkip = FileAttributes.System | FileAttributes.Hidden | FileAttributes.ReparsePoint,
			};

			var folders = new Stack<string>();
			folders.Push(string.Empty);
			while (folders.Count > 0)
			{
				token.ThrowIfCancellationRequested();
				var relative = folders.Pop();
				var sourceFolder = Path.Combine(source, relative);
				var targetFolder = Path.Combine(target, relative);

				Dictionary<string, FileInfo> targetFiles;
				try
				{
					targetFiles = Directory.Exists(targetFolder)
						? new DirectoryInfo(targetFolder).EnumerateFiles("*", options).ToDictionary(file => file.Name, StringComparer.OrdinalIgnoreCase)
						: new Dictionary<string, FileInfo>(StringComparer.OrdinalIgnoreCase);
				}
				catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
				{
					targetFiles = new Dictionary<string, FileInfo>(StringComparer.OrdinalIgnoreCase);
				}

				DirectoryInfo directory;
				try
				{
					directory = new DirectoryInfo(sourceFolder);
					foreach (var child in directory.EnumerateDirectories("*", options))
					{
						if (IsExcluded(child.Name, exclusions))
							skipped++;
						else
							folders.Push(Path.Combine(relative, child.Name));
					}
				}
				catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
				{
					skipped++;
					continue;
				}

				IEnumerable<FileInfo> files;
				try
				{
					files = directory.EnumerateFiles("*", options).ToList();
				}
				catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
				{
					continue;
				}

				foreach (var file in files)
				{
					if (IsExcluded(file.Name, exclusions))
						continue;

					checkedCount++;
					if (checkedCount % 200 == 0)
						progress?.Report(new BackupProgress(checkedCount, 0, 0, 0, file.FullName));

					var path = Path.Combine(relative, file.Name);
					if (!targetFiles.TryGetValue(file.Name, out var copy))
					{
						items.Add(new BackupItem(path, BackupItemKind.New, file.Length, file.LastWriteTime, null));
						continue;
					}

					var difference = file.LastWriteTimeUtc - copy.LastWriteTimeUtc;
					if (difference < -TimeTolerance)
						items.Add(new BackupItem(path, BackupItemKind.TargetNewer, file.Length, file.LastWriteTime, copy.LastWriteTime));
					else if (difference > TimeTolerance || file.Length != copy.Length)
						items.Add(new BackupItem(path, BackupItemKind.Changed, file.Length, file.LastWriteTime, copy.LastWriteTime));
				}
			}

			items.Sort((a, b) => string.Compare(a.RelativePath, b.RelativePath, StringComparison.CurrentCultureIgnoreCase));
			return new BackupPlan(source, target, items, checkedCount, skipped);
		}

		/// <returns>Files copied and the files that failed, with the reason.</returns>
		public static (int Copied, IReadOnlyList<(string Path, string Error)> Failed) Execute(
			BackupPlan plan, bool includeTargetNewer, IProgress<BackupProgress>? progress, CancellationToken token)
		{
			var items = plan.ToCopy(includeTargetNewer).ToList();
			var totalBytes = items.Sum(item => item.Size);
			var failed = new List<(string, string)>();
			var copied = 0;
			long bytes = 0;
			var buffer = new byte[CopyBufferBytes];

			for (var index = 0; index < items.Count; index++)
			{
				token.ThrowIfCancellationRequested();
				var item = items[index];
				var from = Path.Combine(plan.Source, item.RelativePath);
				var to = Path.Combine(plan.Target, item.RelativePath);
				progress?.Report(new BackupProgress(index, items.Count, bytes, totalBytes, from));

				try
				{
					CopyFile(from, to, buffer, read =>
					{
						bytes += read;
						progress?.Report(new BackupProgress(index, items.Count, bytes, totalBytes, from));
					}, token);
					copied++;
				}
				catch (OperationCanceledException)
				{
					throw;
				}
				catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
				{
					failed.Add((item.RelativePath, ex.Message));
				}
			}

			progress?.Report(new BackupProgress(items.Count, items.Count, bytes, totalBytes, null));
			return (copied, failed);
		}

		/// <summary>
		/// Copies into a temporary file next to the target and swaps it in, so an interrupted copy never leaves a half-written
		/// file in the backup; the source's modification time is kept.
		/// </summary>
		private static void CopyFile(string from, string to, byte[] buffer, Action<int> onRead, CancellationToken token)
		{
			Directory.CreateDirectory(Path.GetDirectoryName(to)!);
			var temporary = to + ".consysto-copy";
			try
			{
				using (var input = new FileStream(from, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1))
				using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 1))
				{
					int read;
					while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
					{
						token.ThrowIfCancellationRequested();
						output.Write(buffer, 0, read);
						onRead(read);
					}
				}

				File.SetLastWriteTimeUtc(temporary, File.GetLastWriteTimeUtc(from));
				File.Move(temporary, to, overwrite: true);
			}
			finally
			{
				if (File.Exists(temporary))
					File.Delete(temporary);
			}
		}

		/// <summary>Exact names ("OldVersions") or simple masks ("~$*", "*.bak").</summary>
		private static bool IsExcluded(string name, IReadOnlyList<string> exclusions)
			=> exclusions.Any(pattern => FileSystemName.MatchesSimpleExpression(pattern, name, ignoreCase: true));
	}
}
