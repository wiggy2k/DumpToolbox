using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using System.Text;

namespace DumpToolbox.Core;

public sealed partial class SkeletoolCatalogueService
{
    private static readonly HashSet<string> ArchiveExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".zipx", ".7z", ".rar", ".arj", ".ace", ".arc", ".zst", ".gz", ".bz2",
        ".xz", ".lz", ".z", ".tar", ".tgz", ".tbz", ".tbz2", ".txz", ".tzst"
    };

    private static void EnsureTemporaryCache()
    {
        if (Interlocked.Exchange(ref _cacheInitialized, 1) != 0) return;

        // v0.8.81 and earlier kept materialized images/files beside the EXE forever.
        // They are reproducible working files, so remove that legacy cache on upgrade.
        TryDeleteDirectory(LegacyCacheDirectory);

        try
        {
            Directory.CreateDirectory(TempCacheRoot);
            foreach (string directory in Directory.EnumerateDirectories(TempCacheRoot))
            {
                if (Path.GetFullPath(directory).Equals(Path.GetFullPath(SessionCacheDirectory), StringComparison.OrdinalIgnoreCase))
                    continue;

                string name = Path.GetFileName(directory);
                int underscore = name.IndexOf('_');
                if (underscore <= 0 || !int.TryParse(name[..underscore], out int pid) || !IsProcessAlive(pid))
                    TryDeleteDirectory(directory);
            }

            Directory.CreateDirectory(SessionCacheDirectory);
            AppDomain.CurrentDomain.ProcessExit += static (_, _) => TryDeleteDirectory(SessionCacheDirectory);
        }
        catch
        {
            // Materialization methods will surface a useful I/O error if the OS temp
            // directory is genuinely unavailable. Cache cleanup itself must not stop
            // DumpToolbox from starting.
        }
    }

    private static bool IsProcessAlive(int pid)
    {
        try
        {
            using System.Diagnostics.Process process = System.Diagnostics.Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch { return false; }
    }

    private static byte[] Sha1Bytes(string value)
    {
        if (!IsSha1(value))
            throw new InvalidDataException($"Invalid SHA-1 value in catalogue: {value}");
        return Convert.FromHexString(value);
    }

    private static string Sha1Hex(byte[] value)
    {
        if (value.Length != 20)
            throw new InvalidDataException($"Invalid binary SHA-1 length in catalogue: {value.Length}");
        return Convert.ToHexString(value).ToLowerInvariant();
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken ct)
    {
        await using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using SHA1 sha = SHA1.Create();
        byte[] hash = await sha.ComputeHashAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string HashText(string value) =>
        Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static bool IsSha1(string? value) =>
        value is { Length: 40 } && value.All(Uri.IsHexDigit);

    private static bool IsDirectImage(string path) =>
        Path.GetExtension(path).Equals(".iso", StringComparison.OrdinalIgnoreCase) ||
        Path.GetExtension(path).Equals(".bin", StringComparison.OrdinalIgnoreCase);

    private static bool IsArchive(string path) => ArchiveExtensions.Contains(Path.GetExtension(path));

    private static int CueSectorSize(string type) => type.EndsWith("/2048", StringComparison.OrdinalIgnoreCase) ? 2048 : type.EndsWith("/2336", StringComparison.OrdinalIgnoreCase) ? 2336 : 2352;
    private static string Norm(string path) => path.Replace('\\', '/');
    private static StringComparer PathComparer() => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private static string Now() => DateTimeOffset.UtcNow.ToString("O");

    private static DateTimeOffset ParseDate(string value) =>
        DateTimeOffset.Parse(
            value,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind);

    private static DateTimeOffset? ReadDate(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : ParseDate(reader.GetString(ordinal));

    private static string MakeTempDirectory(string prefix)
    {
        string path = Path.Combine(Path.GetTempPath(), $"DumpToolbox_{prefix}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch { }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, true);
        }
        catch { }
    }

    private static string SafeName(string value)
    {
        foreach (char invalidCharacter in Path.GetInvalidFileNameChars())
            value = value.Replace(invalidCharacter, '_');
        return string.IsNullOrWhiteSpace(value) ? "image.bin" : value;
    }

    private static bool IsUnderPath(string path, string parent)
    {
        string candidate = Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string root = Path.GetFullPath(parent)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return candidate.StartsWith(
            root,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    private static async Task CopyRangeAsync(
        string source,
        string destination,
        long offset,
        long length,
        CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        await using FileStream input = new(
            source, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using FileStream output = new(
            destination, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        input.Position = offset;

        byte[] buffer = new byte[1024 * 1024];
        long remaining = length;
        while (remaining > 0)
        {
            int read = await input.ReadAsync(
                buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), ct).ConfigureAwait(false);
            if (read <= 0)
                throw new EndOfStreamException(source);
            await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            remaining -= read;
        }
    }

    private static SkeletonContentEntry ResolveEntryGeometry(SkeletonContentEntry entry, long length)
    {
        if (entry.DataLength == length || entry.AlternateIsoRecords is null)
            return entry;

        SkeletonAlternateIsoRecord[] candidates = entry.AlternateIsoRecords
            .Where(candidate => candidate.DataLength == length)
            .ToArray();
        return candidates.Length == 1
            ? entry with { ExtentLba = candidates[0].ExtentLba, DataLength = candidates[0].DataLength }
            : entry;
    }

    private sealed record ExistingUnit(long Id, string Sha1);
    private sealed record UnitScanResult(long UnitId, bool Skipped, int ImagesScanned, int FilesHashed, int Errors);
    private sealed record ScanOneImageResult(int Files);
    private sealed record DirectUnitPlan(string SourcePath, IReadOnlyList<ImagePlan> Images, string LayoutHash);
    private sealed record CatalogueWorkItem(bool IsArchive, string Path, DirectUnitPlan? DirectPlan);
    private sealed record ImagePlan(string SourcePath, string SourceEntryPath, string DisplayName, long SourceOffset, long SourceLength);
}
