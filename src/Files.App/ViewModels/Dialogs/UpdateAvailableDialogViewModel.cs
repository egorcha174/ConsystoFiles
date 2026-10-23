// Consysto fork: the offer of a newer release with what changed since the running version.

using System.Text;

namespace Files.App.ViewModels.Dialogs
{
	public sealed partial class UpdateAvailableDialogViewModel : ObservableObject
	{
		private readonly Func<Task<IReadOnlyList<ConsystoReleaseNotes>>> _loadNotes;

		public ConsystoReleaseModel Release { get; }

		public string Title
			=> string.Format(Strings.ConsystoUpdateDialogTitle.GetLocalizedResource(), Release.Version);

		public string InstalledVersion { get; }

		public string Hint { get; }

		/// <summary>"Update" when the installed copy can replace itself, "Download" otherwise.</summary>
		public string PrimaryText { get; }

		private string _notes = Strings.ConsystoUpdateNotesLoading.GetLocalizedResource();
		public string Notes
		{
			get => _notes;
			private set => SetProperty(ref _notes, value);
		}

		public UpdateAvailableDialogViewModel(ConsystoReleaseModel release, Version current, bool isInstalledCopy, bool canInstall, Func<Task<IReadOnlyList<ConsystoReleaseNotes>>> loadNotes)
		{
			Release = release;
			_loadNotes = loadNotes;
			InstalledVersion = string.Format(Strings.ConsystoUpdateDialogInstalled.GetLocalizedResource(), current);
			Hint = (canInstall ? Strings.ConsystoUpdateInstallHint : isInstalledCopy ? Strings.ConsystoUpdateInstallerHint : Strings.ConsystoUpdatePortableHint).GetLocalizedResource();
			PrimaryText = (canInstall ? Strings.ConsystoUpdateInstall : Strings.ConsystoUpdateDownload).GetLocalizedResource();
		}

		public async Task LoadNotesAsync()
		{
			var notes = await _loadNotes();
			if (notes.Count == 0)
			{
				Notes = string.Format(Strings.ConsystoUpdateNotesUnavailable.GetLocalizedResource(), Release.Page);
				return;
			}

			var text = new StringBuilder();
			foreach (var item in notes)
				text.Append("### ").Append(item.Version).Append("\n\n").Append(item.Markdown).Append("\n\n");

			Notes = text.ToString();
		}
	}
}
