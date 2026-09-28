using System.IO.Compression;
using System.Text;

namespace Consysto.CadPreview.Print;

/// <summary>
/// The binary G-code of PrusaSlicer (libbgcode): "GCDE", a version, a checksum type, then blocks. A block is its type,
/// compression, unpacked and packed sizes, parameters, data and a CRC32. Compression is none, deflate or heatshrink
/// (a window of 2^11 or 2^12 bytes, a lookahead of 2^4); the G-code itself is also packed with MeatPack.
/// </summary>
internal static class BinaryGcode
{
    public const uint Magic = 0x45444347;

    public enum BlockType : ushort { FileMetadata = 0, Gcode = 1, SlicerMetadata = 2, PrinterMetadata = 3, PrintMetadata = 4, Thumbnail = 5 }

    private const ushort None = 0;
    private const ushort Deflate = 1;
    private const ushort Heatshrink11 = 2;
    private const ushort Heatshrink12 = 3;

    /// <summary>G-code block encodings: plain text, MeatPack, and MeatPack with the comments dropped.</summary>
    private const ushort MeatPackEncoding = 1;
    private const ushort MeatPackCommentsEncoding = 2;

    /// <summary>A G-code block holds 64 KiB of text; anything much larger is a broken file.</summary>
    private const uint MaxGcodeBlockBytes = 16 * 1024 * 1024;

    public sealed class Block
    {
        public BlockType Type { get; init; }

        public ushort Compression { get; init; }

        /// <summary>The size unpacked.</summary>
        public uint Size { get; init; }

        /// <summary>The encoding of text blocks, the image format of a thumbnail.</summary>
        public ushort Parameter { get; init; }

        public int Width { get; init; }

        public int Height { get; init; }

        /// <summary>As stored; null when the block was skipped.</summary>
        public byte[]? Data { get; init; }
    }

    /// <summary>The blocks in file order; the data only of those <paramref name="wanted"/> takes, the rest is sought past.</summary>
    public static IEnumerable<Block> ReadBlocks(Stream stream, Func<BlockType, uint, bool> wanted)
    {
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        if (stream.Length < 10 || reader.ReadUInt32() != Magic)
            yield break;

        reader.ReadUInt32();
        var checksumSize = reader.ReadUInt16() == 1 ? 4 : 0;
        while (stream.Position + 8 <= stream.Length)
        {
            var type = (BlockType)reader.ReadUInt16();
            var compression = reader.ReadUInt16();
            var size = reader.ReadUInt32();
            var stored = compression == None ? size : reader.ReadUInt32();

            ushort parameter;
            int width = 0, height = 0;
            if (type == BlockType.Thumbnail)
            {
                parameter = reader.ReadUInt16();
                width = reader.ReadUInt16();
                height = reader.ReadUInt16();
            }
            else
            {
                parameter = reader.ReadUInt16();
            }

            if (stream.Position + stored > stream.Length)
                yield break;

            byte[]? data = null;
            if (wanted(type, size))
                data = reader.ReadBytes((int)stored);
            else
                stream.Seek(stored, SeekOrigin.Current);

            stream.Seek(checksumSize, SeekOrigin.Current);
            yield return new Block { Type = type, Compression = compression, Size = size, Parameter = parameter, Width = width, Height = height, Data = data };
        }
    }

    /// <summary>The data unpacked, no longer than the size the block declares; null for an unknown compression.</summary>
    public static byte[]? Unpack(Block block)
    {
        if (block.Data is not { } data)
            return null;

        var size = (int)block.Size;
        switch (block.Compression)
        {
            case None:
                return data;
            case Deflate:
                using (var input = new ZLibStream(new MemoryStream(data), CompressionMode.Decompress))
                {
                    var output = new byte[size];
                    var read = input.ReadAtLeast(output, size, throwOnEndOfStream: false);
                    return read == size ? output : output[..read];
                }
            case Heatshrink11:
                return Heatshrink(data, 11, 4, size);
            case Heatshrink12:
                return Heatshrink(data, 12, 4, size);
            default:
                return null;
        }
    }

    /// <summary>
    /// LZSS as heatshrink writes it, bits most significant first: 1 and a byte is that byte; 0, an index and a count copy
    /// count + 1 bytes from index + 1 bytes back. The window starts as zeros. The last byte is padded with zero bits.
    /// </summary>
    internal static byte[] Heatshrink(byte[] input, int windowBits, int lookaheadBits, int size)
    {
        var output = new byte[size];
        var written = 0;
        var position = 0;
        ulong bits = 0;
        var bitCount = 0;

        bool Take(int count, out int value)
        {
            while (bitCount < count)
            {
                if (position >= input.Length)
                {
                    value = 0;
                    return false;
                }

                bits = (bits << 8) | input[position++];
                bitCount += 8;
            }

            bitCount -= count;
            value = (int)((bits >> bitCount) & ((1UL << count) - 1));
            return true;
        }

        while (written < size && Take(1, out var tag))
        {
            if (tag == 1)
            {
                if (!Take(8, out var literal))
                    break;

                output[written++] = (byte)literal;
                continue;
            }

            if (!Take(windowBits, out var index) || !Take(lookaheadBits, out var count))
                break;

            var distance = index + 1;
            for (var i = 0; i <= count && written < size; i++, written++)
                output[written] = written >= distance ? output[written - distance] : (byte)0;
        }

        return written == size ? output : output[..written];
    }

    /// <summary>The G-code of a binary file as text, one block at a time, for the drawing of its moves.</summary>
    public static TextReader OpenGcode(string path)
        => new BlockReader(GcodeText(path));

    private static IEnumerable<string> GcodeText(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
        var meatPack = new MeatPackDecoder();
        foreach (var block in ReadBlocks(stream, (type, size) => type == BlockType.Gcode && size <= MaxGcodeBlockBytes))
        {
            if (Unpack(block) is not { } data)
                continue;

            yield return block.Parameter is MeatPackEncoding or MeatPackCommentsEncoding
                ? meatPack.Decode(data)
                : Encoding.UTF8.GetString(data);
        }
    }

    /// <summary>
    /// MeatPack puts two G-code characters in a byte, four bits each, low half first: digits, '.', ' ' (or 'E' when spaces
    /// are dropped), newline, 'G' and 'X'. A half of 0b1111 means that character comes whole in a following byte.
    /// 0xFF 0xFF and a command byte switch packing and the dropping of spaces on and off.
    /// </summary>
    internal sealed class MeatPackDecoder
    {
        private const byte Signal = 0xFF;
        private const byte EnablePacking = 0xFB;
        private const byte DisablePacking = 0xFA;
        private const byte ResetAll = 0xF9;
        private const byte EnableNoSpaces = 0xF7;
        private const byte DisableNoSpaces = 0xF6;
        private const string Table = "0123456789. \nGX";

        private bool packing;
        private bool noSpaces;
        private bool commandNext;
        private bool inComment;
        // Carries across blocks: a line can be cut between two of them
        private char previous = '\n';
        private int signals;
        private int wholeChars;
        private char pending;
        private readonly StringBuilder text = new();

        /// <summary>The state carries from one block to the next, as the stream it was cut from.</summary>
        public string Decode(ReadOnlySpan<byte> data)
        {
            text.Clear();
            foreach (var value in data)
            {
                if (value == Signal)
                {
                    if (signals > 0)
                    {
                        commandNext = true;
                        signals = 0;
                    }
                    else
                    {
                        signals++;
                    }

                    continue;
                }

                if (commandNext)
                {
                    Command(value);
                    commandNext = false;
                    continue;
                }

                if (signals > 0)
                {
                    Receive(Signal);
                    signals = 0;
                }

                Receive(value);
            }

            return text.ToString();
        }

        private void Command(byte command)
        {
            switch (command)
            {
                case EnablePacking: packing = true; break;
                case DisablePacking: packing = false; break;
                case EnableNoSpaces: noSpaces = true; break;
                case DisableNoSpaces: noSpaces = false; break;
                case ResetAll: packing = false; noSpaces = false; break;
            }
        }

        private void Receive(byte value)
        {
            if (!packing)
            {
                Output((char)value);
                return;
            }

            if (wholeChars > 0)
            {
                Output((char)value);
                if (pending != '\0')
                {
                    Output(pending);
                    pending = '\0';
                }

                wholeChars--;
                return;
            }

            var low = value & 0xF;
            var high = value >> 4;
            if (low == 0xF)
            {
                wholeChars++;
                if (high == 0xF)
                    wholeChars++;
                else
                    pending = Unpacked(high);
                return;
            }

            var first = Unpacked(low);
            Output(first);
            // A line that ends in the low half leaves the high one unused
            if (first == '\n')
                return;

            if (high == 0xF)
                wholeChars++;
            else
                Output(Unpacked(high));
        }

        private char Unpacked(int code)
            => code == 11 && noSpaces ? 'E' : Table[code];

        /// <summary>
        /// Spaces dropped by the packer come back before each word, as the G-code reader splits words by them: a letter
        /// right after a number starts a word ("G1X10" is "G1 X10"). Comments are left as they came.
        /// </summary>
        private void Output(char value)
        {
            if (value == ';')
                inComment = true;
            else if (value == '\n')
                inComment = false;
            else if (noSpaces && !inComment && char.IsAsciiLetter(value) && (char.IsAsciiDigit(previous) || previous is '.' or '-'))
                text.Append(' ');

            text.Append(value);
            previous = value;
        }
    }

    /// <summary>Lines across the text of consecutive blocks; a line may start in one block and end in the next.</summary>
    private sealed class BlockReader(IEnumerable<string> blocks) : TextReader
    {
        private readonly IEnumerator<string> source = blocks.GetEnumerator();
        private string current = string.Empty;
        private int offset;
        private bool ended;

        private bool Fill()
        {
            while (offset >= current.Length)
            {
                if (ended || !source.MoveNext())
                {
                    ended = true;
                    return false;
                }

                current = source.Current;
                offset = 0;
            }

            return true;
        }

        public override int Peek()
            => Fill() ? current[offset] : -1;

        public override int Read()
            => Fill() ? current[offset++] : -1;

        public override string? ReadLine()
        {
            if (!Fill())
                return null;

            StringBuilder? line = null;
            while (Fill())
            {
                var end = current.IndexOf('\n', offset);
                if (end >= 0)
                {
                    var part = current.AsSpan(offset, end - offset).TrimEnd('\r');
                    offset = end + 1;
                    return line is null ? part.ToString() : line.Append(part).ToString();
                }

                (line ??= new StringBuilder()).Append(current.AsSpan(offset));
                offset = current.Length;
            }

            return line?.ToString();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                source.Dispose();
            base.Dispose(disposing);
        }
    }
}
