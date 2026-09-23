using System.Buffers.Binary;
using System.IO.Compression;

namespace Consysto.CadPreview.Artwork;

/// <summary>
/// Shows documents of the drawing programs a workshop meets on the way to the cutter: Adobe Illustrator and CorelDRAW.
/// Neither program is started and nothing inside the file is executed — only the picture already saved in it is read.
///
/// The two formats give it up differently:
///
/// * An Illustrator document saved the usual way <em>is</em> a PDF — it begins with the PDF signature and the artwork is
///   its first page. Such a file is handed on to be drawn as a page; the host has a PDF engine for that.
/// * A CorelDRAW document carries a finished picture of itself. Newer ones are zips with it among the entries; older
///   ones are RIFF containers with it in a DISP chunk.
/// </summary>
public static class ArtworkPreviewReader
{
	private static readonly string[] SupportedExtensions = [".ai", ".cdr"];

	private const long MaximumFileBytes = 128 * 1024 * 1024;
	private const int MaximumPreviewBytes = 32 * 1024 * 1024;

	/// <summary>A zip entry claiming to unpack to more than this many times its stored size is not opened.</summary>
	private const int MaximumCompressionRatio = 200;

	public static bool IsSupported(string? extension)
		=> extension is not null && SupportedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);

	/// <summary>An Illustrator document that is a PDF inside, and therefore can be drawn page by page.</summary>
	public static bool IsPdfInside(string path)
	{
		try
		{
			var file = new FileInfo(path);
			if (!file.Exists || file.Length is 0 or > MaximumFileBytes)
				return false;

			using var stream = File.OpenRead(path);
			Span<byte> head = stackalloc byte[5];
			return stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false) == head.Length
				&& head.SequenceEqual("%PDF-"u8);
		}
		catch (Exception)
		{
			return false;
		}
	}

	/// <returns>A complete PNG, JPEG or BMP file, or null when the document carries no picture or cannot be read.</returns>
	public static byte[]? TryRead(string path)
	{
		try
		{
			var file = new FileInfo(path);
			if (!file.Exists || file.Length is 0 or > MaximumFileBytes)
				return null;

			using var stream = File.OpenRead(path);
			Span<byte> signature = stackalloc byte[4];
			if (stream.ReadAtLeast(signature, signature.Length, throwOnEndOfStream: false) != signature.Length)
				return null;

			stream.Position = 0;

			// Written out as numbers: the two bytes after "PK" are control characters,
			// and inside a string literal they are invisible and easy to lose in an edit
			ReadOnlySpan<byte> zip = [(byte)'P', (byte)'K', 0x03, 0x04];

			return signature.SequenceEqual(zip) ? FromZip(stream)
				: signature.SequenceEqual("RIFF"u8) ? FromRiff(stream)
				: null;
		}
		catch (Exception)
		{
			// A damaged file, a file being written, or one built to trip the reader up: the picture is simply absent
			return null;
		}
	}

	/// <summary>
	/// Newer CorelDRAW documents are zips with the picture among the entries. Two layouts are in the wild and the name
	/// differs between them: newer documents keep it at previews/thumbnail.png, older ones at
	/// metadata/thumbnails/thumbnail.bmp. Looking for one word only leaves half the documents blank.
	///
	/// The largest match wins, which picks the picture of the whole document over the one of its first page.
	/// </summary>
	private static byte[]? FromZip(Stream stream)
	{
		using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);

		var candidates = archive.Entries
			.Where(entry => HasPreviewName(entry.FullName)
				&& entry.Length is > 0 and <= MaximumPreviewBytes
				&& HasPictureExtension(entry.FullName))
			.OrderByDescending(entry => entry.Length);

		foreach (var entry in candidates)
		{
			// An entry that claims to swell enormously is left alone: unpacking it is what a zip bomb wants
			if (entry.CompressedLength > 0 && entry.Length / entry.CompressedLength > MaximumCompressionRatio)
				continue;

			var picture = new byte[entry.Length];
			using (var content = entry.Open())
				content.ReadExactly(picture);

			if (IsPicture(picture))
				return picture;
		}

		return null;
	}

	/// <summary>
	/// Older CorelDRAW documents are RIFF containers. Between the RIFF header and the first chunk sits a four-byte form
	/// type — for these documents usually "CARA" — and a reader that walks straight past it lands mid-chunk and comes
	/// back empty-handed. That is the whole reason such files showed nothing.
	/// </summary>
	private static byte[]? FromRiff(Stream stream)
	{
		Span<byte> header = stackalloc byte[12];
		if (stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) != header.Length)
			return null;

		var declared = BinaryPrimitives.ReadUInt32LittleEndian(header[4..8]);
		var end = Math.Min(stream.Length, 8L + declared);

		Span<byte> entry = stackalloc byte[8];
		while (stream.Position + 8 <= end)
		{
			if (stream.ReadAtLeast(entry, entry.Length, throwOnEndOfStream: false) != entry.Length)
				return null;

			var size = BinaryPrimitives.ReadUInt32LittleEndian(entry[4..8]);
			if (size > end - stream.Position)
				return null;

			if (entry[..4].SequenceEqual("DISP"u8))
			{
				if (size > MaximumPreviewBytes)
					return null;

				var payload = new byte[size];
				stream.ReadExactly(payload);
				return BitmapFromClipboard(payload);
			}

			// Chunks are padded to an even length
			stream.Position += size + (size & 1);
		}

		return null;
	}

	/// <summary>
	/// The DISP chunk holds what would be put on the clipboard: a one-word kind, then the picture without the fourteen
	/// bytes of file header. Those bytes are put back here.
	/// </summary>
	private static byte[]? BitmapFromClipboard(byte[] payload)
	{
		const int MarkerBytes = 4;
		const int FileHeaderBytes = 14;
		const int MinimumInfoHeaderBytes = 40;

		if (payload.Length < MarkerBytes + MinimumInfoHeaderBytes)
			return null;

		var info = payload.AsSpan(MarkerBytes);
		var infoHeaderBytes = BinaryPrimitives.ReadUInt32LittleEndian(info);
		var bitsPerPixel = BinaryPrimitives.ReadUInt16LittleEndian(info[14..]);
		var coloursUsed = BinaryPrimitives.ReadUInt32LittleEndian(info[32..]);

		if (infoHeaderBytes < MinimumInfoHeaderBytes || infoHeaderBytes > info.Length || bitsPerPixel is 0 or > 32)
			return null;

		// Where the pixels start is worked out from the header itself. Taking it from the stored picture size instead
		// looks tempting, but that field is allowed to be zero for uncompressed pictures, and then the picture is lost.
		var paletteEntries = coloursUsed != 0 ? coloursUsed : (bitsPerPixel <= 8 ? 1u << bitsPerPixel : 0u);
		var pixelsStart = FileHeaderBytes + infoHeaderBytes + paletteEntries * 4;

		var total = FileHeaderBytes + (long)info.Length;
		if (pixelsStart > total)
			return null;

		var bitmap = new byte[total];
		bitmap[0] = (byte)'B';
		bitmap[1] = (byte)'M';
		BinaryPrimitives.WriteUInt32LittleEndian(bitmap.AsSpan(2), (uint)total);
		BinaryPrimitives.WriteUInt32LittleEndian(bitmap.AsSpan(10), pixelsStart);
		info.CopyTo(bitmap.AsSpan(FileHeaderBytes));

		return bitmap;
	}

	private static bool HasPreviewName(string name)
		=> name.Contains("preview", StringComparison.OrdinalIgnoreCase)
			|| name.Contains("thumbnail", StringComparison.OrdinalIgnoreCase);

	private static bool HasPictureExtension(string name)
		=> name.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
			|| name.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
			|| name.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)
			|| name.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase);

	/// <summary>The name inside the archive is a hint, not a promise: what was unpacked has to look like a picture.</summary>
	private static bool IsPicture(ReadOnlySpan<byte> picture)
	{
		ReadOnlySpan<byte> png = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];
		ReadOnlySpan<byte> jpeg = [0xFF, 0xD8, 0xFF];

		return picture.StartsWith(png) || picture.StartsWith(jpeg) || picture.StartsWith("BM"u8);
	}
}
