using System.Buffers;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;

namespace DumpToolbox.Core;

/// <summary>
/// Source-file hash cache and expected-hash bookkeeping used by skeleton source matching.
/// </summary>
public sealed partial class SkeletonResurrectionService
{
    private static StringComparer GetPathComparer()
        => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static string NormalizeCacheRelativePath(string path)
        => path.Replace('\\', '/');

    private static bool IsSha1(string? value)
        => value is { Length: 40 } && value.All(Uri.IsHexDigit);

    private static async Task<Dictionary<string, HashCacheEntry>> LoadHashCacheAsync(
        string cachePath,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, HashCacheEntry>(GetPathComparer());
        if (!File.Exists(cachePath))
            return result;

        try
        {
            await using FileStream stream = OpenRead(
                cachePath,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            HashCacheDocument? document = await JsonSerializer.DeserializeAsync<HashCacheDocument>(
                stream,
                cancellationToken: cancellationToken);

            if (document?.Version != HashCacheVersion || document.Files is null)
                return result;

            foreach (HashCacheEntry entry in document.Files)
            {
                if (string.IsNullOrWhiteSpace(entry.RelativePath) || !IsSha1(entry.Sha1))
                    continue;
                string key = NormalizeCacheRelativePath(entry.RelativePath);
                entry.RelativePath = key;
                result[key] = entry;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // A damaged/old cache is only an optimisation failure. Ignore it and
            // rebuild hashes from the source files.
            result.Clear();
        }

        return result;
    }

    private static void PruneMissingCacheEntries(
        string rootDirectory,
        ConcurrentDictionary<string, HashCacheEntry> cache)
    {
        foreach (string key in cache.Keys)
        {
            try
            {
                string localPath = key.Replace('/', Path.DirectorySeparatorChar);
                string fullPath = Path.GetFullPath(Path.Combine(rootDirectory, localPath));
                if (!File.Exists(fullPath))
                    cache.TryRemove(key, out _);
            }
            catch
            {
                cache.TryRemove(key, out _);
            }
        }
    }

    private static async Task TrySaveHashCacheAsync(
        string cachePath,
        IEnumerable<HashCacheEntry> entries,
        CancellationToken cancellationToken)
    {
        string tempPath = cachePath + ".tmp";
        try
        {
            var document = new HashCacheDocument
            {
                Version = HashCacheVersion,
                Files = entries
                    .Where(e => !string.IsNullOrWhiteSpace(e.RelativePath) && IsSha1(e.Sha1))
                    .OrderBy(e => e.RelativePath, GetPathComparer())
                    .ToList()
            };

            await using (var stream = new FileStream(
                tempPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    document,
                    new JsonSerializerOptions { WriteIndented = true },
                    cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            File.Move(tempPath, cachePath, true);
        }
        catch
        {
            try
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
            catch
            {
                // Best-effort cleanup only.
            }
        }
    }

    private static async Task<string> CalculateSha1Async(string path, CancellationToken cancellationToken)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        await using var stream = OpenRead(path, HashBufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(HashBufferSize);
        try
        {
            while (true)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
                if (read == 0)
                    break;
                hash.AppendData(buffer, 0, read);
            }
            return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static void AddExpected(
        Dictionary<string, List<(SkeletonContentEntry Entry, bool Xa)>> expected,
        string hash,
        SkeletonContentEntry entry,
        bool xa)
    {
        if (!expected.TryGetValue(hash, out List<(SkeletonContentEntry Entry, bool Xa)>? list))
        {
            list = new List<(SkeletonContentEntry Entry, bool Xa)>();
            expected[hash] = list;
        }
        list.Add((entry, xa));
    }

}
