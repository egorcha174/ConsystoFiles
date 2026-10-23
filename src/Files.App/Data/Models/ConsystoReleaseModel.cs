// Consysto fork: a published Consysto Files release as the download catalog describes it.

namespace Files.App.Data.Models
{
	/// <summary>One file of a release: the installer or the portable zip.</summary>
	public sealed record ConsystoReleaseAsset(string Name, long Size, string Sha256, Uri DownloadUri);

	/// <summary>A release with the file that suits the running copy and the file of the other kind.</summary>
	public sealed record ConsystoReleaseModel(Version Version, ConsystoReleaseAsset? Installer, ConsystoReleaseAsset? Portable, Uri Page);

	/// <summary>What one release changed, in Markdown.</summary>
	public sealed record ConsystoReleaseNotes(Version Version, string Markdown);
}
