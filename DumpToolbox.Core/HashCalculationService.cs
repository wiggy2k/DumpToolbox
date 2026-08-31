using System.Buffers;
using System.Security.Cryptography;

namespace DumpToolbox.Core;

public sealed record HashCalculationOptions(
    bool Crc32 = true,
    bool Md5 = true,
    bool Sha1 = true,
    bool Sha256 = false,
    bool Sha384 = false,
    bool Sha512 = false)
{
    public bool AnySelected => Crc32 || Md5 || Sha1 || Sha256 || Sha384 || Sha512;
}

public sealed record HashCalculationProgress(long BytesRead, long TotalBytes)
{
    public double Fraction => TotalBytes <= 0 ? 1 : Math.Clamp(BytesRead / (double)TotalBytes, 0, 1);
}

public sealed record HashCalculationResult(
    string FilePath,
    long FileLength,
    IReadOnlyDictionary<string, string> Hashes);

public sealed class HashCalculationService
{
    private const int BufferSize = 4 * 1024 * 1024;

    public Task<HashCalculationResult> CalculateAsync(
        string filePath,
        HashCalculationOptions options,
        IProgress<HashCalculationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentException("A file is required.", nameof(filePath));
        if (!File.Exists(filePath))
            throw new FileNotFoundException("Input file not found.", filePath);
        if (!options.AnySelected)
            throw new InvalidOperationException("Select at least one hash algorithm.");

        return Task.Run(() => CalculateCore(Path.GetFullPath(filePath), options, progress, cancellationToken), cancellationToken);
    }

    private static HashCalculationResult CalculateCore(
        string filePath,
        HashCalculationOptions options,
        IProgress<HashCalculationProgress>? progress,
        CancellationToken cancellationToken)
    {
        long totalBytes = new FileInfo(filePath).Length;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        uint crc32 = 0;
        var hashers = new List<(string Name, IncrementalHash Hash)>();

        try
        {
            if (options.Md5)
                hashers.Add(("MD5", IncrementalHash.CreateHash(HashAlgorithmName.MD5)));
            if (options.Sha1)
                hashers.Add(("SHA-1", IncrementalHash.CreateHash(HashAlgorithmName.SHA1)));
            if (options.Sha256)
                hashers.Add(("SHA-256", IncrementalHash.CreateHash(HashAlgorithmName.SHA256)));
            if (options.Sha384)
                hashers.Add(("SHA-384", IncrementalHash.CreateHash(HashAlgorithmName.SHA384)));
            if (options.Sha512)
                hashers.Add(("SHA-512", IncrementalHash.CreateHash(HashAlgorithmName.SHA512)));

            using var input = new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                BufferSize,
                FileOptions.SequentialScan);

            long bytesRead = 0;
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ReadOnlySpan<byte> chunk = buffer.AsSpan(0, read);
                if (options.Crc32)
                    crc32 = Crc32.Compute(chunk, crc32);
                foreach (var (_, hasher) in hashers)
                    hasher.AppendData(chunk);

                bytesRead += read;
                progress?.Report(new HashCalculationProgress(bytesRead, totalBytes));
            }

            var results = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (options.Crc32)
                results["CRC32"] = crc32.ToString("x8");
            foreach ((string name, IncrementalHash hasher) in hashers)
                results[name] = Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();

            progress?.Report(new HashCalculationProgress(totalBytes, totalBytes));
            return new HashCalculationResult(filePath, totalBytes, results);
        }
        finally
        {
            foreach (var (_, hasher) in hashers)
                hasher.Dispose();
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
