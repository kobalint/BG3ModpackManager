using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using BG3ModpackTools.Core.Models;

namespace BG3ModpackTools.Core.Lspk;

/// <summary>
/// Reads Baldur's Gate 3 LSPK (.pak) headers, file manifests, ModuleInfo metadata (meta.lsx),
/// and determines whether a package overrides base game files.
/// </summary>
public static class LspkPakReader
{
    private const uint LspkSignature = 0x4B50534C; // "LSPK"
    private const int EntrySizeV18 = 272;
    private const int EntrySizeV15 = 296;

    private static readonly HashSet<string> BaseGameModules = new(StringComparer.OrdinalIgnoreCase)
    {
        "Shared",
        "SharedDev",
        "Gustav",
        "GustavDev",
        "GustavX",
        "Engine",
        "Game",
        "MainUI",
        "Honour",
        "ModBrowser",
        "PhotoMode",
        "Crossplay",
        "DiceSet_01",
        "DiceSet_02",
        "DiceSet_03",
        "DiceSet_06"
    };

    private sealed record RawPakEntry(
        string Path,
        ulong Offset,
        uint ArchivePart,
        byte Flags,
        uint DiskSize,
        uint UncompressedSize);

    /// <summary>
    /// Reads metadata and override diagnostics from a .pak file without extracting its full payload.
    /// </summary>
    public static PakInfo ReadPakInfo(string pakPath)
    {
        var fi = new FileInfo(pakPath);
        var info = new PakInfo
        {
            FilePath = pakPath,
            FileName = fi.Name,
            FileSizeBytes = fi.Exists ? fi.Length : 0,
            LastWriteTimeUtc = fi.Exists ? fi.LastWriteTimeUtc : DateTime.MinValue
        };

        try
        {
            using var fs = new FileStream(pakPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var br = new BinaryReader(fs, Encoding.UTF8, leaveOpen: true);

            if (fs.Length < 8)
            {
                info.ParseError = "File is too small to be a valid LSPK archive.";
                return info;
            }

            uint sig = br.ReadUInt32();
            if (sig != LspkSignature)
            {
                info.ParseError = $"Invalid LSPK signature (0x{sig:X8}).";
                return info;
            }

            uint version = br.ReadUInt32();
            info.LspkVersion = version;

            List<RawPakEntry> entries = version switch
            {
                18 => ReadEntriesV18(fs, br),
                16 => ReadEntriesV16(fs, br),
                15 => ReadEntriesV15(fs, br),
                _ => throw new NotSupportedException($"Unsupported LSPK version {version}.")
            };

            info.FileCount = entries.Count;
            info.InternalFilePaths = entries.Select(e => e.Path).ToList();

            RawPakEntry? metaLsxEntry = FindPreferredMetaLsx(entries);
            info.HasMetaLsx = metaLsxEntry is not null;
            info.HasMetaLsf = entries.Any(e => IsMetaFile(e.Path, "meta.lsf"));

            if (metaLsxEntry is not null)
            {
                byte[] metaBytes = ReadEntryPayload(pakPath, fs, metaLsxEntry);
                ParseMetaLsx(metaBytes, info);
            }

            AnalyzeOverrides(entries, info);
        }
        catch (Exception ex)
        {
            info.ParseError = ex.Message;
        }

        return info;
    }

    /// <summary>
    /// Formats a BG3 64-bit packed Version64 value into major.minor.revision.build.
    /// </summary>
    public static string? FormatVersion64(string? version64Text)
    {
        if (string.IsNullOrWhiteSpace(version64Text))
        {
            return null;
        }

        if (!ulong.TryParse(version64Text.Trim(), out ulong v))
        {
            if (long.TryParse(version64Text.Trim(), out long signed))
            {
                v = unchecked((ulong)signed);
            }
            else
            {
                return null;
            }
        }

        if (v == 0)
        {
            return null;
        }

        ulong major = (v >> 55) & 0x7FUL;
        ulong minor = (v >> 47) & 0xFFUL;
        ulong revision = (v >> 31) & 0xFFFFUL;
        ulong build = v & 0x7FFFFFFFUL;
        return $"{major}.{minor}.{revision}.{build}";
    }

    private static List<RawPakEntry> ReadEntriesV18(FileStream fs, BinaryReader br)
    {
        ulong fileListOffset = br.ReadUInt64();
        _ = br.ReadUInt32(); // fileListSize
        _ = br.ReadByte();   // flags
        _ = br.ReadByte();   // priority
        _ = br.ReadBytes(16); // md5
        _ = br.ReadUInt16(); // numParts

        fs.Seek((long)fileListOffset, SeekOrigin.Begin);
        int numFiles = br.ReadInt32();
        int compressedSize = br.ReadInt32();
        ValidateEntryCount(numFiles);

        byte[] compressed = br.ReadBytes(compressedSize);
        if (compressed.Length != compressedSize)
        {
            throw new EndOfStreamException("Truncated LSPK v18 file table.");
        }

        byte[] rawTable = Lz4BlockDecoder.Decompress(compressed, numFiles * EntrySizeV18);
        var result = new List<RawPakEntry>(numFiles);

        for (int i = 0; i < numFiles; i++)
        {
            ReadOnlySpan<byte> entry = rawTable.AsSpan(i * EntrySizeV18, EntrySizeV18);
            string path = ReadNullTerminatedUtf8(entry[..256]);
            uint offLo = BinaryPrimitives.ReadUInt32LittleEndian(entry.Slice(256, 4));
            ushort offHi = BinaryPrimitives.ReadUInt16LittleEndian(entry.Slice(260, 2));
            ulong offset = offLo | ((ulong)offHi << 32);
            byte archivePart = entry[262];
            byte flags = entry[263];
            uint diskSize = BinaryPrimitives.ReadUInt32LittleEndian(entry.Slice(264, 4));
            uint uncompressedSize = BinaryPrimitives.ReadUInt32LittleEndian(entry.Slice(268, 4));

            result.Add(new RawPakEntry(path, offset, archivePart, flags, diskSize, uncompressedSize));
        }

        return result;
    }

    private static List<RawPakEntry> ReadEntriesV16(FileStream fs, BinaryReader br)
    {
        ulong fileListOffset = br.ReadUInt64();
        uint fileListSize = br.ReadUInt32();
        _ = br.ReadByte();    // flags
        _ = br.ReadByte();    // priority
        _ = br.ReadBytes(16); // md5
        _ = br.ReadUInt16();  // numParts

        return ReadEntries15Table(fs, br, fileListOffset, fileListSize);
    }

    private static List<RawPakEntry> ReadEntriesV15(FileStream fs, BinaryReader br)
    {
        ulong fileListOffset = br.ReadUInt64();
        uint fileListSize = br.ReadUInt32();
        _ = br.ReadByte();    // flags
        _ = br.ReadByte();    // priority
        _ = br.ReadBytes(16); // md5

        return ReadEntries15Table(fs, br, fileListOffset, fileListSize);
    }

    private static List<RawPakEntry> ReadEntries15Table(FileStream fs, BinaryReader br, ulong fileListOffset, uint fileListSize)
    {
        fs.Seek((long)fileListOffset, SeekOrigin.Begin);
        int numFiles = br.ReadInt32();
        ValidateEntryCount(numFiles);

        int compressedSize = br.ReadInt32();
        byte[] rawTable;

        // Some v15 files store numFiles + compressedSize (fileListSize == 8 + compressedSize);
        // others store only numFiles followed immediately by compressed data (fileListSize - 4).
        if (compressedSize <= 0 || compressedSize > fs.Length - fs.Position)
        {
            fs.Seek((long)fileListOffset + 4, SeekOrigin.Begin);
            int fallbackSize = checked((int)fileListSize - 4);
            byte[] compressed = br.ReadBytes(fallbackSize);
            rawTable = Lz4BlockDecoder.Decompress(compressed, numFiles * EntrySizeV15);
        }
        else
        {
            byte[] compressed = br.ReadBytes(compressedSize);
            if (compressed.Length != compressedSize)
            {
                throw new EndOfStreamException("Truncated LSPK v15/v16 file table.");
            }

            rawTable = Lz4BlockDecoder.Decompress(compressed, numFiles * EntrySizeV15);
        }

        var result = new List<RawPakEntry>(numFiles);
        for (int i = 0; i < numFiles; i++)
        {
            ReadOnlySpan<byte> entry = rawTable.AsSpan(i * EntrySizeV15, EntrySizeV15);
            string path = ReadNullTerminatedUtf8(entry[..256]);
            ulong offset = BinaryPrimitives.ReadUInt64LittleEndian(entry.Slice(256, 8));
            ulong diskSize = BinaryPrimitives.ReadUInt64LittleEndian(entry.Slice(264, 8));
            ulong uncompressedSize = BinaryPrimitives.ReadUInt64LittleEndian(entry.Slice(272, 8));
            uint archivePart = BinaryPrimitives.ReadUInt32LittleEndian(entry.Slice(280, 4));
            uint flags = BinaryPrimitives.ReadUInt32LittleEndian(entry.Slice(284, 4));

            result.Add(new RawPakEntry(
                path,
                offset,
                archivePart,
                (byte)(flags & 0xFF),
                checked((uint)diskSize),
                checked((uint)uncompressedSize)));
        }

        return result;
    }

    private static void ValidateEntryCount(int numFiles)
    {
        if (numFiles < 0 || numFiles > 500_000)
        {
            throw new InvalidDataException($"Invalid LSPK file entry count: {numFiles}.");
        }
    }

    private static string ReadNullTerminatedUtf8(ReadOnlySpan<byte> span)
    {
        int nul = span.IndexOf((byte)0);
        if (nul >= 0)
        {
            span = span[..nul];
        }

        return Encoding.UTF8.GetString(span).Replace('\\', '/');
    }

    private static RawPakEntry? FindPreferredMetaLsx(List<RawPakEntry> entries)
    {
        RawPakEntry? modsScoped = entries.FirstOrDefault(e =>
        {
            string norm = e.Path.Replace('\\', '/');
            return norm.StartsWith("Mods/", StringComparison.OrdinalIgnoreCase) && IsMetaFile(norm, "meta.lsx");
        });

        return modsScoped ?? entries.FirstOrDefault(e => IsMetaFile(e.Path, "meta.lsx"));
    }

    private static bool IsMetaFile(string path, string fileName)
    {
        string norm = path.Replace('\\', '/').TrimStart('/');
        return norm.Equals(fileName, StringComparison.OrdinalIgnoreCase) ||
               norm.EndsWith("/" + fileName, StringComparison.OrdinalIgnoreCase);
    }

    private static byte[] ReadEntryPayload(string mainPakPath, FileStream mainStream, RawPakEntry entry)
    {
        Stream readStream = mainStream;
        FileStream? partStream = null;

        try
        {
            if (entry.ArchivePart > 0)
            {
                string dir = Path.GetDirectoryName(mainPakPath) ?? string.Empty;
                string stem = Path.GetFileNameWithoutExtension(mainPakPath);
                string partPath = Path.Combine(dir, $"{stem}_{entry.ArchivePart}.pak");
                partStream = new FileStream(partPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                readStream = partStream;
            }

            readStream.Seek((long)entry.Offset, SeekOrigin.Begin);
            byte[] diskBytes = new byte[entry.DiskSize];
            readStream.ReadExactly(diskBytes);

            int compressionMethod = entry.Flags & 0x0F;
            return compressionMethod switch
            {
                0 => diskBytes,
                1 => DecompressZlib(diskBytes),
                2 => Lz4BlockDecoder.Decompress(diskBytes, checked((int)entry.UncompressedSize)),
                _ => throw new NotSupportedException($"Unsupported LSPK entry compression method {compressionMethod}.")
            };
        }
        finally
        {
            partStream?.Dispose();
        }
    }

    private static byte[] DecompressZlib(byte[] compressed)
    {
        using var input = new MemoryStream(compressed);
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        zlib.CopyTo(output);
        return output.ToArray();
    }

    private static void ParseMetaLsx(byte[] xmlBytes, PakInfo info)
    {
        string xml = Encoding.UTF8.GetString(xmlBytes).TrimStart('\uFEFF', '\0', ' ', '\t', '\r', '\n');
        var doc = XDocument.Parse(xml, LoadOptions.None);
        XElement? moduleInfo = doc.Descendants("node")
            .FirstOrDefault(n => string.Equals((string?)n.Attribute("id"), "ModuleInfo", StringComparison.OrdinalIgnoreCase));

        if (moduleInfo is null)
        {
            return;
        }

        foreach (XElement attr in moduleInfo.Elements("attribute"))
        {
            string? id = (string?)attr.Attribute("id");
            string? value = (string?)attr.Attribute("value");
            if (string.IsNullOrEmpty(id))
            {
                continue;
            }

            switch (id)
            {
                case "Name":
                    info.Name = value;
                    break;
                case "UUID":
                    info.Uuid = value;
                    break;
                case "Folder":
                    info.Folder = value;
                    break;
                case "Author":
                    info.Author = value;
                    break;
                case "Description":
                    info.Description = value;
                    break;
                case "Version64":
                    info.Version64 = value;
                    info.VersionString = FormatVersion64(value);
                    break;
                case "MD5":
                    info.Md5 = value;
                    break;
                case "PublishHandle":
                    info.PublishHandle = value;
                    break;
            }
        }
    }

    private static void AnalyzeOverrides(List<RawPakEntry> entries, PakInfo info)
    {
        var reasons = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        bool hasModOwnFiles = false;

        if (!info.HasMetaLsx && !info.HasMetaLsf)
        {
            reasons.Add("No meta.lsx (loose override archive)");
        }

        foreach (RawPakEntry entry in entries)
        {
            string[] segments = entry.Path
                .Replace('\\', '/')
                .Split('/', StringSplitOptions.RemoveEmptyEntries);

            if (segments.Length == 0)
            {
                continue;
            }

            string seg0 = segments[0];
            string lastSegment = segments[^1];
            bool isMetaFile = lastSegment.Equals("meta.lsx", StringComparison.OrdinalIgnoreCase) ||
                              lastSegment.Equals("meta.lsf", StringComparison.OrdinalIgnoreCase);
            bool entryIsOverride = false;

            if (seg0.Equals("Public", StringComparison.OrdinalIgnoreCase) && segments.Length >= 2)
            {
                string seg1 = segments[1];
                if (BaseGameModules.Contains(seg1))
                {
                    // Custom mod icons live under Public/Game/GUI/Assets/... and are not base-game overrides.
                    bool isCustomGuiAssets =
                        seg1.Equals("Game", StringComparison.OrdinalIgnoreCase) &&
                        segments.Length >= 4 &&
                        segments[2].Equals("GUI", StringComparison.OrdinalIgnoreCase) &&
                        segments[3].Equals("Assets", StringComparison.OrdinalIgnoreCase);

                    if (!isCustomGuiAssets)
                    {
                        reasons.Add($"Overrides Public/{seg1}/");
                        entryIsOverride = true;
                    }
                }
            }
            else if (seg0.Equals("Mods", StringComparison.OrdinalIgnoreCase) && segments.Length >= 2)
            {
                string seg1 = segments[1];
                if (BaseGameModules.Contains(seg1))
                {
                    reasons.Add($"Overrides Mods/{seg1}/");
                    entryIsOverride = true;
                }
            }
            else if (seg0.Equals("Video", StringComparison.OrdinalIgnoreCase))
            {
                reasons.Add("Overrides Video/");
                entryIsOverride = true;
            }
            else if (seg0.Equals("Generated", StringComparison.OrdinalIgnoreCase) &&
                     segments.Length >= 3 &&
                     segments[1].Equals("Public", StringComparison.OrdinalIgnoreCase) &&
                     segments[2].Equals("Shared", StringComparison.OrdinalIgnoreCase))
            {
                info.HasGeneratedSharedAssets = true;
            }

            if (!entryIsOverride && !isMetaFile &&
                (seg0.Equals("Public", StringComparison.OrdinalIgnoreCase) ||
                 seg0.Equals("Mods", StringComparison.OrdinalIgnoreCase) ||
                 seg0.Equals("Localization", StringComparison.OrdinalIgnoreCase)))
            {
                hasModOwnFiles = true;
            }
        }

        info.OverrideReasons = reasons.ToList();
        info.IsDetectedOverride = info.OverrideReasons.Count > 0;
        info.OverrideKind = !info.IsDetectedOverride
            ? OverrideKind.None
            : ((!info.HasMetaLsx && !info.HasMetaLsf) || !hasModOwnFiles
                ? OverrideKind.StandaloneOverride
                : OverrideKind.MergedOverride);
    }
}
