using System.Security.Cryptography;
using System.Text;
using BG3ModpackTools.Core.Lspk;
using BG3ModpackTools.Core.Models;

namespace BG3ModpackTools.Core.Services;

/// <summary>
/// Cached LSPK summary and SHA-256 keyed by normalized file path + size + timestamp.
/// </summary>
public sealed class CachedPakRecord
{
    public long FileSizeBytes { get; set; }
    public long LastWriteTimeUtcTicks { get; set; }
    public string Sha256 { get; set; } = string.Empty;
    public uint LspkVersion { get; set; }
    public int FileCount { get; set; }
    public bool HasMetaLsx { get; set; }
    public bool HasMetaLsf { get; set; }
    public string? Name { get; set; }
    public string? Uuid { get; set; }
    public string? Folder { get; set; }
    public string? Author { get; set; }
    public string? Description { get; set; }
    public string? Version64 { get; set; }
    public string? VersionString { get; set; }
    public string? Md5 { get; set; }
    public string? PublishHandle { get; set; }
    public bool IsDetectedOverride { get; set; }
    public OverrideKind OverrideKind { get; set; } = OverrideKind.None;
    public List<string> OverrideReasons { get; set; } = new();
    public bool HasGeneratedSharedAssets { get; set; }
    public string? ParseError { get; set; }
}

/// <summary>
/// Persisted lookup cache preventing redundant SHA-256 hashing of large unmodified .pak archives.
/// </summary>
public sealed class PakMetadataCache
{
    public int SchemaVersion { get; set; } = 2;
    public Dictionary<string, CachedPakRecord> Entries { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Computes SHA-256 hashes and provides cached PAK metadata lookup.
/// </summary>
public static class HashService
{
    private const int BufferSize = 1024 * 1024;

    public static async Task<string> ComputeFileSha256Async(string filePath, CancellationToken cancellationToken = default)
    {
        using var sha = SHA256.Create();
        await using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            BufferSize,
            useAsync: true);

        byte[] buffer = new byte[BufferSize];
        int bytesRead;
        while ((bytesRead = await stream.ReadAsync(buffer, cancellationToken)) > 0)
        {
            sha.TransformBlock(buffer, 0, bytesRead, null, 0);
        }

        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return Convert.ToHexString(sha.Hash!).ToLowerInvariant();
    }

    public static string ComputeFileSha256(string filePath)
    {
        using var sha = SHA256.Create();
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, BufferSize);
        byte[] hash = sha.ComputeHash(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Computes a deterministic composite SHA-256 across sorted (relativeKey, fileSha256) pairs for loose/hybrid mods.
    /// </summary>
    public static string ComputeCompositeHash(IEnumerable<(string Key, string Sha256)> items)
    {
        var ordered = items
            .Select(i => (Key: i.Key.Replace('\\', '/').TrimStart('/').ToLowerInvariant(), Sha: i.Sha256.ToLowerInvariant()))
            .OrderBy(i => i.Key, StringComparer.Ordinal)
            .ThenBy(i => i.Sha, StringComparer.Ordinal);

        var sb = new StringBuilder();
        foreach ((string key, string sha) in ordered)
        {
            sb.Append(key).Append('=').Append(sha).Append('\n');
        }

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Generates a unique short ID derived from a full SHA-256 content hash without colliding against existing entries.
    /// </summary>
    public static string GenerateUniqueModId(string contentHash, IEnumerable<ModEntry> existingMods)
    {
        var used = new HashSet<string>(existingMods.Select(m => m.Id), StringComparer.OrdinalIgnoreCase);
        for (int len = 16; len <= contentHash.Length; len += 4)
        {
            string candidate = contentHash[..len];
            if (!used.Contains(candidate))
            {
                return candidate;
            }
        }

        int suffix = 1;
        while (used.Contains($"{contentHash[..16]}_{suffix}"))
        {
            suffix++;
        }

        return $"{contentHash[..16]}_{suffix}";
    }

    /// <summary>
    /// Reads PAK metadata and optionally SHA-256, reusing cached results when size and write timestamp match.
    /// </summary>
    public static async Task<(PakInfo Info, string Sha256, bool CacheUpdated)> InspectPakCachedAsync(
        string pakPath,
        PakMetadataCache cache,
        bool computeHash = true,
        CancellationToken cancellationToken = default)
    {
        string fullPath = Path.GetFullPath(pakPath);
        var fi = new FileInfo(fullPath);
        if (!fi.Exists)
        {
            throw new FileNotFoundException($"PAK file not found: {fullPath}", fullPath);
        }

        long size = fi.Length;
        long ticks = fi.LastWriteTimeUtc.Ticks;

        if (cache.Entries.TryGetValue(fullPath, out CachedPakRecord? cached) &&
            cached.FileSizeBytes == size &&
            cached.LastWriteTimeUtcTicks == ticks &&
            (!computeHash || !string.IsNullOrEmpty(cached.Sha256)))
        {
            var infoFromCache = new PakInfo
            {
                FilePath = fullPath,
                FileName = fi.Name,
                FileSizeBytes = size,
                LastWriteTimeUtc = fi.LastWriteTimeUtc,
                LspkVersion = cached.LspkVersion,
                FileCount = cached.FileCount,
                HasMetaLsx = cached.HasMetaLsx,
                HasMetaLsf = cached.HasMetaLsf,
                Name = cached.Name,
                Uuid = cached.Uuid,
                Folder = cached.Folder,
                Author = cached.Author,
                Description = cached.Description,
                Version64 = cached.Version64,
                VersionString = cached.VersionString,
                Md5 = cached.Md5,
                PublishHandle = cached.PublishHandle,
                IsDetectedOverride = cached.IsDetectedOverride,
                OverrideKind = cached.OverrideKind,
                OverrideReasons = cached.OverrideReasons.ToList(),
                HasGeneratedSharedAssets = cached.HasGeneratedSharedAssets,
                ParseError = cached.ParseError
            };

            return (infoFromCache, cached.Sha256, false);
        }

        PakInfo parsed = LspkPakReader.ReadPakInfo(fullPath);
        string sha256 = computeHash
            ? await ComputeFileSha256Async(fullPath, cancellationToken)
            : (cached is not null && cached.FileSizeBytes == size && cached.LastWriteTimeUtcTicks == ticks ? cached.Sha256 : string.Empty);

        cache.Entries[fullPath] = new CachedPakRecord
        {
            FileSizeBytes = size,
            LastWriteTimeUtcTicks = ticks,
            Sha256 = sha256,
            LspkVersion = parsed.LspkVersion,
            FileCount = parsed.FileCount,
            HasMetaLsx = parsed.HasMetaLsx,
            HasMetaLsf = parsed.HasMetaLsf,
            Name = parsed.Name,
            Uuid = parsed.Uuid,
            Folder = parsed.Folder,
            Author = parsed.Author,
            Description = parsed.Description,
            Version64 = parsed.Version64,
            VersionString = parsed.VersionString,
            Md5 = parsed.Md5,
            PublishHandle = parsed.PublishHandle,
            IsDetectedOverride = parsed.IsDetectedOverride,
            OverrideKind = parsed.OverrideKind,
            OverrideReasons = parsed.OverrideReasons.ToList(),
            HasGeneratedSharedAssets = parsed.HasGeneratedSharedAssets,
            ParseError = parsed.ParseError
        };

        return (parsed, sha256, true);
    }
}
