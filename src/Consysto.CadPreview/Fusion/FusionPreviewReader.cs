using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using ZstdSharp;

namespace Consysto.CadPreview.Fusion;

/// <summary>
/// Reads the picture Autodesk Fusion saves inside a document, so a part can be shown without Fusion installed.
///
/// A Fusion document is a zip, but not one the zip reader of .NET will open: Fusion squeezes what is inside it with
/// Zstandard, and a reader that knows only deflate refuses the entry outright. So the archive is walked here by hand —
/// the table at its end names every entry, and each one is unpacked by whichever method it declares.
///
/// The body of the part is kept as ShapeManager blocks, which are closed, but every asset folder also carries a finished
/// PNG under Previews. A document holds several assets — the design itself and derived ones such as a flat pattern — and
/// the folder of the one the document opens on is marked [Active]; that is the picture a person expects to see.
/// </summary>
public static class FusionPreviewReader
{
	private static readonly string[] SupportedExtensions = [".f3d"];

	private const string PreviewSuffix = "/Previews/small.png";
	private const string ActiveMark = "[Active]";
	private const long MaximumFileBytes = 512 * 1024 * 1024;
	private const int MaximumPreviewBytes = 32 * 1024 * 1024;

	private const ushort Stored = 0;
	private const ushort Deflate = 8;
	private const ushort Zstandard = 93;

	public static bool IsSupported(string? extension)
		=> extension is not null && SupportedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);

	/// <returns>A complete PNG file, or null when the document has no picture or cannot be read.</returns>
	public static byte[]? TryRead(string path)
	{
		try
		{
			var file = new FileInfo(path);
			if (!file.Exists || file.Length > MaximumFileBytes)
				return null;

			using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

			var entry = ChoosePreview(ZipDirectory.Read(stream));
			if (entry is null)
				return null;

			var data = ZipDirectory.Unpack(stream, entry.Value);

			return data is not null && IsPng(data) ? data : null;
		}
		catch (Exception)
		{
			// Not a zip, a document being written, a damaged file: the preview is simply absent
			return null;
		}
	}

	/// <summary>The picture of the active asset; failing that, any picture the document carries.</summary>
	private static ZipEntry? ChoosePreview(IReadOnlyList<ZipEntry> entries)
	{
		ZipEntry? fallback = null;

		foreach (var entry in entries)
		{
			if (!entry.Name.EndsWith(PreviewSuffix, StringComparison.OrdinalIgnoreCase)
				|| entry.UnpackedBytes is 0 or > MaximumPreviewBytes)
				continue;

			if (entry.Name.Contains(ActiveMark, StringComparison.OrdinalIgnoreCase))
				return entry;

			fallback ??= entry;
		}

		return fallback;
	}

	private static bool IsPng(byte[] data)
		=> data.Length > 8 && data.AsSpan(0, 8).SequenceEqual<byte>([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);

	/// <summary>One entry of the table at the end of a zip: enough to find its bytes and unpack them.</summary>
	private readonly record struct ZipEntry(string Name, ushort Method, long PackedBytes, long UnpackedBytes, long HeaderOffset);

	/// <summary>
	/// The little of the zip format we need: the table of contents at the end, and the bytes of one entry. Written here
	/// rather than taken from a library because the only thing missing from the reader of .NET is Zstandard.
	/// </summary>
	private static class ZipDirectory
	{
		private const int EndSignature = 0x06054B50;
		private const int EntrySignature = 0x02014B50;
		private const int LocalSignature = 0x04034B50;
		private const int MaximumEntries = 50_000;

		public static IReadOnlyList<ZipEntry> Read(FileStream stream)
		{
			var end = FindEnd(stream);
			if (end < 0)
				return [];

			stream.Position = end + 10;
			var count = ReadUInt16(stream);
			stream.Position = end + 16;
			long start = ReadUInt32(stream);

			if (count is 0 or > MaximumEntries || start <= 0 || start >= stream.Length)
				return [];

			var entries = new List<ZipEntry>(count);
			stream.Position = start;

			for (var index = 0; index < count; index++)
			{
				if (ReadUInt32(stream) != EntrySignature)
					break;

				stream.Position += 6;
				var method = ReadUInt16(stream);
				stream.Position += 8;
				long packed = ReadUInt32(stream);
				long unpacked = ReadUInt32(stream);
				var nameLength = ReadUInt16(stream);
				var extraLength = ReadUInt16(stream);
				var commentLength = ReadUInt16(stream);
				stream.Position += 8;
				long offset = ReadUInt32(stream);

				var name = new byte[nameLength];
				stream.ReadExactly(name);
				stream.Position += extraLength + commentLength;

				// Entries over four gigabytes carry their sizes elsewhere; a preview is never one of those
				if (packed is not uint.MaxValue && unpacked is not uint.MaxValue && offset is not uint.MaxValue)
					entries.Add(new ZipEntry(Encoding.UTF8.GetString(name), method, packed, unpacked, offset));
			}

			return entries;
		}

		public static byte[]? Unpack(FileStream stream, ZipEntry entry)
		{
			stream.Position = entry.HeaderOffset;
			if (ReadUInt32(stream) != LocalSignature)
				return null;

			stream.Position = entry.HeaderOffset + 26;
			var nameLength = ReadUInt16(stream);
			var extraLength = ReadUInt16(stream);

			var packed = new byte[entry.PackedBytes];
			stream.Position = entry.HeaderOffset + 30 + nameLength + extraLength;
			stream.ReadExactly(packed);

			switch (entry.Method)
			{
				case Stored:
					return packed;

				case Deflate:
					var unpacked = new byte[entry.UnpackedBytes];
					using (var deflate = new DeflateStream(new MemoryStream(packed), CompressionMode.Decompress))
						deflate.ReadExactly(unpacked);
					return unpacked;

				case Zstandard:
					using (var decompressor = new Decompressor())
						return decompressor.Unwrap(packed).ToArray();

				default:
					return null;
			}
		}

		/// <summary>The end of a zip is found from the back, because a comment of any length may follow it.</summary>
		private static long FindEnd(FileStream stream)
		{
			var length = (int)Math.Min(stream.Length, 64 * 1024 + 22);
			var tail = new byte[length];
			stream.Position = stream.Length - length;
			stream.ReadExactly(tail);

			for (var offset = length - 22; offset >= 0; offset--)
				if (BinaryPrimitives.ReadInt32LittleEndian(tail.AsSpan(offset)) == EndSignature)
					return stream.Length - length + offset;

			return -1;
		}

		private static ushort ReadUInt16(Stream stream)
		{
			Span<byte> bytes = stackalloc byte[2];
			stream.ReadExactly(bytes);
			return BinaryPrimitives.ReadUInt16LittleEndian(bytes);
		}

		private static uint ReadUInt32(Stream stream)
		{
			Span<byte> bytes = stackalloc byte[4];
			stream.ReadExactly(bytes);
			return BinaryPrimitives.ReadUInt32LittleEndian(bytes);
		}
	}
}
