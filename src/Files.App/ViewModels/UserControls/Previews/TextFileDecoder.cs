// Copyright (c) Files Community
// Licensed under the MIT License.

using System.Globalization;
using System.Text;
using UtfUnknown;

namespace Files.App.ViewModels.Previews
{
	/// <summary>
	/// Consysto fork: decodes a text file in its own encoding and tells text from binary data.
	/// Order: byte order mark, UTF-16 without a mark, strict UTF-8 (plain ASCII included), the charset detector,
	/// the system ANSI code page.
	/// </summary>
	public static class TextFileDecoder
	{
		// The detector only needs a sample; feeding it a 10 MB log costs time and gives nothing more
		private const int DetectionSample = 64 * 1024;
		private const float MinimumConfidence = 0.5f;
		private const int MinimumNonAsciiForDetector = 8;

		private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

		/// <param name="truncated">The bytes are the head of a longer file (the read limit was hit).</param>
		public static string Decode(byte[] bytes, bool truncated = false)
			=> DetectEncoding(bytes, truncated, out int preamble).GetString(bytes, preamble, bytes.Length - preamble);

		public static Encoding DetectEncoding(byte[] bytes, bool truncated, out int preamble)
		{
			preamble = 0;
			if (StartsWith(bytes, 0xEF, 0xBB, 0xBF)) { preamble = 3; return new UTF8Encoding(false); }
			if (StartsWith(bytes, 0xFF, 0xFE, 0x00, 0x00)) { preamble = 4; return new UTF32Encoding(false, false); }
			if (StartsWith(bytes, 0x00, 0x00, 0xFE, 0xFF)) { preamble = 4; return new UTF32Encoding(true, false); }
			if (StartsWith(bytes, 0xFF, 0xFE)) { preamble = 2; return new UnicodeEncoding(false, false); }
			if (StartsWith(bytes, 0xFE, 0xFF)) { preamble = 2; return new UnicodeEncoding(true, false); }

			if (GuessUtf16WithoutMark(bytes) is { } utf16)
				return utf16;

			if (IsValidUtf8(bytes, truncated))
				return new UTF8Encoding(false);

			try
			{
				var sample = bytes.Length > DetectionSample ? bytes[..DetectionSample] : bytes;
				// A handful of non-ASCII bytes cannot tell cp1251 from cp1252 ("File: а" reads as "File: à"):
				// then the system code page is the better guess. The detector decides when there is enough to go on.
				int nonAscii = 0;
				foreach (byte b in sample)
					if (b >= 0x80 && ++nonAscii >= MinimumNonAsciiForDetector)
						break;
				if (nonAscii >= MinimumNonAsciiForDetector)
				{
					var detected = CharsetDetector.DetectFromBytes(sample).Detected;
					if (detected?.Encoding is { } encoding && detected.Confidence >= MinimumConfidence)
						return encoding;
				}
			}
			catch (Exception)
			{
				// An unknown code page name from the detector: fall through to the system one
			}

			return SystemAnsiEncoding();
		}

		/// <summary>
		/// Binary data rather than text: a zero byte outside UTF-16/32, or more than 1 % control characters
		/// (tab, line breaks, form feed and escape sequences of colored logs are normal text).
		/// </summary>
		public static bool LooksBinary(byte[] bytes, bool truncated = false)
		{
			if (bytes.Length == 0)
				return false;
			var encoding = DetectEncoding(bytes, truncated, out int preamble);
			if (encoding is not UnicodeEncoding and not UTF32Encoding && Array.IndexOf(bytes, (byte)0) >= 0)
				return true;

			string text = encoding.GetString(bytes, preamble, bytes.Length - preamble);
			long control = 0;
			foreach (char c in text)
				if (char.IsControl(c) && c is not '\t' and not '\r' and not '\n' and not '\f' and not '\v' and not '\u001b')
					control++;
			return control * 100 > text.Length;
		}

		// UTF-16 is also written without a mark. Then the high byte of every character takes one or two values:
		// zero for Latin, spaces and digits, 0x04 for Cyrillic. Only that clear pattern counts, so a binary file
		// with scattered zeros is not taken for text.
		private static Encoding? GuessUtf16WithoutMark(byte[] bytes)
		{
			int length = Math.Min(bytes.Length, DetectionSample) & ~1;
			if (length < 4 || Array.IndexOf(bytes, (byte)0, 0, length) < 0)
				return null;
			if (HighBytesLookLikeUtf16(bytes, length, highOffset: 1))
				return new UnicodeEncoding(bigEndian: false, byteOrderMark: false);
			if (HighBytesLookLikeUtf16(bytes, length, highOffset: 0))
				return new UnicodeEncoding(bigEndian: true, byteOrderMark: false);
			return null;
		}

		private static bool HighBytesLookLikeUtf16(byte[] bytes, int length, int highOffset)
		{
			Span<int> high = stackalloc int[256];
			int lowZeros = 0, pairs = length / 2;
			for (int i = 0; i < length; i += 2)
			{
				high[bytes[i + highOffset]]++;
				if (bytes[i + 1 - highOffset] == 0)
					lowZeros++;
			}
			int topNonZero = 0;
			for (int v = 1; v < 256; v++)
				topNonZero = Math.Max(topNonZero, high[v]);
			return high[0] * 20 >= pairs                        // some ASCII: spaces, digits, line breaks
				&& (high[0] + topNonZero) * 10 >= pairs * 9     // high bytes take one or two values
				&& lowZeros * 50 <= pairs;                      // a zero character is rare in text
		}

		private static bool IsValidUtf8(byte[] bytes, bool truncated)
		{
			try
			{
				// Only a file cut by the read limit may end in the middle of a multi-byte character; a complete
				// cp1251 file ending in a Cyrillic letter must not pass for UTF-8
				StrictUtf8.GetDecoder().GetCharCount(bytes, 0, bytes.Length, flush: !truncated);
				return true;
			}
			catch (DecoderFallbackException)
			{
				return false;
			}
		}

		private static Encoding SystemAnsiEncoding()
		{
			try
			{
				return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.ANSICodePage);
			}
			catch (Exception)
			{
				return Encoding.Latin1;
			}
		}

		private static bool StartsWith(byte[] bytes, params byte[] prefix)
			=> bytes.AsSpan().StartsWith(prefix);
	}
}
