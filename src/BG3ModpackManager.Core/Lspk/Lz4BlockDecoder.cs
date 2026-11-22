namespace BG3ModpackManager.Core.Lspk;

/// <summary>
/// Pure-C# LZ4 raw block decompressor used by BG3 LSPK (.pak) file tables and compressed entries.
/// </summary>
public static class Lz4BlockDecoder
{
    /// <summary>
    /// Decompresses an LZ4 raw block payload into a buffer of exact <paramref name="uncompressedSize"/> bytes.
    /// </summary>
    public static byte[] Decompress(ReadOnlySpan<byte> src, int uncompressedSize)
    {
        if (uncompressedSize < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(uncompressedSize));
        }

        if (uncompressedSize == 0)
        {
            return Array.Empty<byte>();
        }

        var output = new byte[uncompressedSize];
        int srcPos = 0;
        int dstPos = 0;

        while (srcPos < src.Length)
        {
            byte token = src[srcPos++];
            int literalLength = token >> 4;
            if (literalLength == 15)
            {
                byte extra;
                do
                {
                    if (srcPos >= src.Length)
                    {
                        throw new InvalidDataException("Unexpected end of LZ4 block while reading literal length.");
                    }

                    extra = src[srcPos++];
                    literalLength += extra;
                }
                while (extra == 255);
            }

            if (srcPos + literalLength > src.Length)
            {
                throw new InvalidDataException("LZ4 literal length exceeds compressed input buffer.");
            }

            if (dstPos + literalLength > output.Length)
            {
                throw new InvalidDataException("LZ4 literal length exceeds expected uncompressed size.");
            }

            src.Slice(srcPos, literalLength).CopyTo(output.AsSpan(dstPos, literalLength));
            srcPos += literalLength;
            dstPos += literalLength;

            if (srcPos >= src.Length)
            {
                break;
            }

            if (srcPos + 2 > src.Length)
            {
                throw new InvalidDataException("Unexpected end of LZ4 block while reading match offset.");
            }

            int offset = src[srcPos] | (src[srcPos + 1] << 8);
            srcPos += 2;
            if (offset <= 0 || offset > dstPos)
            {
                throw new InvalidDataException($"Invalid LZ4 match offset {offset} at output position {dstPos}.");
            }

            int matchLength = (token & 0x0F) + 4;
            if (matchLength == 19)
            {
                byte extra;
                do
                {
                    if (srcPos >= src.Length)
                    {
                        throw new InvalidDataException("Unexpected end of LZ4 block while reading match length.");
                    }

                    extra = src[srcPos++];
                    matchLength += extra;
                }
                while (extra == 255);
            }

            if (dstPos + matchLength > output.Length)
            {
                throw new InvalidDataException("LZ4 match length exceeds expected uncompressed size.");
            }

            int matchPos = dstPos - offset;
            // Byte-by-byte copy preserves overlapping run-length matches (offset < matchLength).
            for (int i = 0; i < matchLength; i++)
            {
                output[dstPos++] = output[matchPos++];
            }
        }

        if (dstPos != uncompressedSize)
        {
            throw new InvalidDataException($"LZ4 block produced {dstPos} bytes, expected {uncompressedSize}.");
        }

        return output;
    }
}
