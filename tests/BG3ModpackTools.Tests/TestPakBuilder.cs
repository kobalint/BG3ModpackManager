using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace BG3ModpackTools.Tests;

/// <summary>
/// Builds minimal valid synthetic LSPK v18 and v16 .pak archives and BG3 meta.lsx payloads for unit tests.
/// </summary>
internal static class TestPakBuilder
{
    public enum PayloadCompression
    {
        None = 0,
        Zlib = 1,
        Lz4 = 2
    }

    public static string BuildMetaLsx(
        string name,
        string uuid,
        string folder,
        string version64 = "36028797018963968",
        string author = "Tester")
    {
        return $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <save>
              <version major="4" minor="8" revision="0" build="100" />
              <region id="Config">
                <node id="root">
                  <children>
                    <node id="ModuleInfo">
                      <attribute id="Author" type="LSString" value="{System.Security.SecurityElement.Escape(author)}" />
                      <attribute id="Folder" type="LSString" value="{System.Security.SecurityElement.Escape(folder)}" />
                      <attribute id="MD5" type="LSString" value="" />
                      <attribute id="Name" type="LSString" value="{System.Security.SecurityElement.Escape(name)}" />
                      <attribute id="PublishHandle" type="uint64" value="0" />
                      <attribute id="UUID" type="FixedString" value="{System.Security.SecurityElement.Escape(uuid)}" />
                      <attribute id="Version64" type="int64" value="{version64}" />
                    </node>
                  </children>
                </node>
              </region>
            </save>
            """;
    }

    /// <summary>
    /// Encodes raw bytes as a valid literal-only LZ4 raw block so Lz4BlockDecoder can decompress it cleanly.
    /// </summary>
    public static byte[] EncodeLz4LiteralBlock(ReadOnlySpan<byte> input)
    {
        using var ms = new MemoryStream();
        int len = input.Length;
        if (len < 15)
        {
            ms.WriteByte((byte)(len << 4));
        }
        else
        {
            ms.WriteByte(0xF0);
            int rem = len - 15;
            while (rem >= 255)
            {
                ms.WriteByte(255);
                rem -= 255;
            }

            ms.WriteByte((byte)rem);
        }

        ms.Write(input);
        return ms.ToArray();
    }

    public static void CreatePakV18(
        string outputPath,
        IEnumerable<(string InternalPath, byte[] Content, PayloadCompression Compression)> files)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        var entries = files.ToList();
        const int headerSize = 40; // 4 sig + 4 ver + 8 off + 4 sz + 1 flags + 1 prio + 16 md5 + 2 parts

        using var fs = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
        fs.Write(new byte[headerSize]);

        byte[] rawTable = new byte[entries.Count * 272];
        for (int i = 0; i < entries.Count; i++)
        {
            (string internalPath, byte[] content, PayloadCompression comp) = entries[i];
            ulong offset = (ulong)fs.Position;
            byte[] stored = CompressPayload(content, comp);
            fs.Write(stored);

            Span<byte> row = rawTable.AsSpan(i * 272, 272);
            byte[] pathBytes = Encoding.UTF8.GetBytes(internalPath.Replace('\\', '/'));
            if (pathBytes.Length >= 256)
            {
                throw new InvalidOperationException("Path too long for test LSPK entry.");
            }

            pathBytes.CopyTo(row);
            BinaryPrimitives.WriteUInt32LittleEndian(row.Slice(256, 4), (uint)(offset & 0xFFFFFFFFUL));
            BinaryPrimitives.WriteUInt16LittleEndian(row.Slice(260, 2), (ushort)((offset >> 32) & 0xFFFFUL));
            row[262] = 0; // part
            row[263] = (byte)comp;
            BinaryPrimitives.WriteUInt32LittleEndian(row.Slice(264, 4), (uint)stored.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(row.Slice(268, 4), (uint)content.Length);
        }

        ulong fileListOffset = (ulong)fs.Position;
        byte[] compressedTable = EncodeLz4LiteralBlock(rawTable);
        Span<byte> counts = stackalloc byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(counts[..4], entries.Count);
        BinaryPrimitives.WriteInt32LittleEndian(counts[4..], compressedTable.Length);
        fs.Write(counts);
        fs.Write(compressedTable);

        uint fileListSize = (uint)(8 + compressedTable.Length);
        fs.Seek(0, SeekOrigin.Begin);
        Span<byte> hdr = stackalloc byte[headerSize];
        hdr.Clear();
        Encoding.ASCII.GetBytes("LSPK").CopyTo(hdr[..4]);
        BinaryPrimitives.WriteUInt32LittleEndian(hdr.Slice(4, 4), 18);
        BinaryPrimitives.WriteUInt64LittleEndian(hdr.Slice(8, 8), fileListOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(hdr.Slice(16, 4), fileListSize);
        hdr[20] = 0;
        hdr[21] = 0;
        MD5.HashData(compressedTable).CopyTo(hdr.Slice(22, 16));
        BinaryPrimitives.WriteUInt16LittleEndian(hdr.Slice(38, 2), 1);
        fs.Write(hdr);
    }

    public static void CreatePakV16(
        string outputPath,
        IEnumerable<(string InternalPath, byte[] Content, PayloadCompression Compression)> files)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        var entries = files.ToList();
        const int headerSize = 40;

        using var fs = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
        fs.Write(new byte[headerSize]);

        byte[] rawTable = new byte[entries.Count * 296];
        for (int i = 0; i < entries.Count; i++)
        {
            (string internalPath, byte[] content, PayloadCompression comp) = entries[i];
            ulong offset = (ulong)fs.Position;
            byte[] stored = CompressPayload(content, comp);
            fs.Write(stored);

            Span<byte> row = rawTable.AsSpan(i * 296, 296);
            byte[] pathBytes = Encoding.UTF8.GetBytes(internalPath.Replace('\\', '/'));
            pathBytes.CopyTo(row);
            BinaryPrimitives.WriteUInt64LittleEndian(row.Slice(256, 8), offset);
            BinaryPrimitives.WriteUInt64LittleEndian(row.Slice(264, 8), (ulong)stored.Length);
            BinaryPrimitives.WriteUInt64LittleEndian(row.Slice(272, 8), (ulong)content.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(row.Slice(280, 4), 0); // part
            BinaryPrimitives.WriteUInt32LittleEndian(row.Slice(284, 4), (uint)comp);
        }

        ulong fileListOffset = (ulong)fs.Position;
        byte[] compressedTable = EncodeLz4LiteralBlock(rawTable);
        Span<byte> counts = stackalloc byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(counts[..4], entries.Count);
        BinaryPrimitives.WriteInt32LittleEndian(counts[4..], compressedTable.Length);
        fs.Write(counts);
        fs.Write(compressedTable);

        uint fileListSize = (uint)(8 + compressedTable.Length);
        fs.Seek(0, SeekOrigin.Begin);
        Span<byte> hdr = stackalloc byte[headerSize];
        hdr.Clear();
        Encoding.ASCII.GetBytes("LSPK").CopyTo(hdr[..4]);
        BinaryPrimitives.WriteUInt32LittleEndian(hdr.Slice(4, 4), 16);
        BinaryPrimitives.WriteUInt64LittleEndian(hdr.Slice(8, 8), fileListOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(hdr.Slice(16, 4), fileListSize);
        BinaryPrimitives.WriteUInt16LittleEndian(hdr.Slice(38, 2), 1);
        fs.Write(hdr);
    }

    private static byte[] CompressPayload(byte[] data, PayloadCompression comp)
    {
        return comp switch
        {
            PayloadCompression.None => data,
            PayloadCompression.Zlib => CompressZlib(data),
            PayloadCompression.Lz4 => EncodeLz4LiteralBlock(data),
            _ => throw new ArgumentOutOfRangeException(nameof(comp))
        };
    }

    private static byte[] CompressZlib(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var zlib = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(data, 0, data.Length);
        }

        return ms.ToArray();
    }
}
