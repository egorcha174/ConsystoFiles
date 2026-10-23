// Consysto fork: a published Consysto Files release as the download catalog describes it.

namespace Files.App.Data.Models
{
	/// <summary>One file of a release: the installer or the portable zip, with the GitHub copy when the site lists it.</summary>
	public sealed record ConsystoReleaseAsset(string Name, long Size, string Sha256, Uri DownloadUri, Uri? MirrorUri);

	/// <summary>A release with its installer and portable zip, and whether it was read from consysto.pro or from GitHub.</summary>
	public sealed record ConsystoReleaseModel(Version Version, ConsystoReleaseAsset? Installer, ConsystoReleaseAsset? Portable, Uri Page, bool FromSite);

	/// <summary>What one release changed, in Markdown.</summary>
	public sealed record ConsystoReleaseNotes(Version Version, string Markdown);
}
