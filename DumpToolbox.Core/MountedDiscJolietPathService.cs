namespace DumpToolbox.Core;

internal static class MountedDiscJolietPathService
{
    private const int LogicalSectorSize = 2048;
    internal const string MatchMethod =
        "Mounted source disc Joliet pathname -> primary ISO9660 record + verified source match";

    public static IReadOnlyDictionary<string, string> TryRead(string sourceDirectory, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(sourceDirectory))
            return EmptyMap();

        try
        {
            string directory = Path.GetFullPath(sourceDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string? root = Path.GetPathRoot(directory);
            if (string.IsNullOrWhiteSpace(root))
                return EmptyMap();

            string normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!directory.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase) ||
                new DriveInfo(root).DriveType != DriveType.CDRom)
            {
                return EmptyMap();
            }

            string devicePath = @"\\.\" + normalizedRoot;
            using var volume = new FileStream(
                devicePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite,
                LogicalSectorSize,
                FileOptions.RandomAccess);

            byte[] ReadSector(long lba, CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                var result = new byte[LogicalSectorSize];
                long offset = checked(lba * LogicalSectorSize);
                int read = 0;
                while (read < result.Length)
                {
                    int count = RandomAccess.Read(volume.SafeFileHandle, result.AsSpan(read), offset + read);
                    if (count == 0)
                        throw new EndOfStreamException($"The source disc ended before logical sector {lba:N0}.");
                    read += count;
                }
                return result;
            }

            byte[] ReadBytes(uint lba, uint length, CancellationToken token)
            {
                if (length > int.MaxValue)
                    throw new InvalidDataException("A source-disc directory is too large to inspect safely.");

                var result = new byte[(int)length];
                int copied = 0;
                while (copied < result.Length)
                {
                    byte[] sector = ReadSector(lba + copied / LogicalSectorSize, token);
                    int count = Math.Min(sector.Length, result.Length - copied);
                    sector.AsSpan(0, count).CopyTo(result.AsSpan(copied));
                    copied += count;
                }
                return result;
            }

            return ReadAsync(
                    (lba, token) => Task.FromResult(ReadSector(lba, token)),
                    (lba, length, token) => Task.FromResult(ReadBytes(lba, length, token)),
                    cancellationToken)
                .GetAwaiter()
                .GetResult();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Optical-drive access is supplementary evidence. A locked drive, a data
            // track that Windows cannot expose directly, or a non-ISO disc must not
            // prevent the existing conservative source matcher from running.
            return EmptyMap();
        }
    }

    internal static async Task<IReadOnlyDictionary<string, string>> ReadAsync(
        Func<long, CancellationToken, Task<byte[]>> readSector,
        Func<uint, uint, CancellationToken, Task<byte[]>> readBytes,
        CancellationToken cancellationToken)
    {
        List<DiscVolumeDescriptorEvidence> descriptors =
            await DiscMasteringOrderingExtractor.ReadDescriptorsAsync(readSector, cancellationToken).ConfigureAwait(false);
        DiscVolumeDescriptorEvidence? primary = descriptors.FirstOrDefault(item => item.Namespace == "ISO9660");
        DiscVolumeDescriptorEvidence? joliet = descriptors.FirstOrDefault(item => item.Namespace == "JOLIET");
        if (primary is null || joliet is null)
            return EmptyMap();

        List<DiscFilesystemRecordEvidence> primaryRecords =
            await DiscMasteringOrderingExtractor.ReadTreeAsync(readBytes, primary, cancellationToken).ConfigureAwait(false);
        List<DiscFilesystemRecordEvidence> jolietRecords =
            await DiscMasteringOrderingExtractor.ReadTreeAsync(readBytes, joliet, cancellationToken).ConfigureAwait(false);

        var primaryGroups = primaryRecords.GroupBy(GeometryKey).ToDictionary(group => group.Key, group => group.ToArray());
        var jolietGroups = jolietRecords.GroupBy(GeometryKey).ToDictionary(group => group.Key, group => group.ToArray());
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach ((RecordGeometry geometry, DiscFilesystemRecordEvidence[] primaryGroup) in primaryGroups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (primaryGroup.Length != 1 ||
                !jolietGroups.TryGetValue(geometry, out DiscFilesystemRecordEvidence[]? jolietGroup) ||
                jolietGroup.Length != 1)
            {
                continue;
            }

            result[Normalize(primaryGroup[0].Path)] = Normalize(jolietGroup[0].Path);
        }

        return result;
    }

    internal static int EnrichMatches(
        IDictionary<string, SkeletonSourceMatch> matches,
        IReadOnlyDictionary<string, string> jolietByPrimaryPath)
    {
        int enriched = 0;
        foreach (string key in matches.Keys.ToArray())
        {
            SkeletonSourceMatch match = matches[key];
            if (string.IsNullOrWhiteSpace(match.SourceRelativePath) ||
                !jolietByPrimaryPath.TryGetValue(Normalize(match.SourceRelativePath), out string? jolietPath))
            {
                continue;
            }

            matches[key] = match with
            {
                SourceRelativePath = jolietPath,
                MatchMethod = MatchMethod
            };
            enriched++;
        }

        return enriched;
    }

    internal static bool TryResolveJolietPath(
        string sourceRelativePath,
        IReadOnlyDictionary<string, string> jolietByPrimaryPath,
        out string jolietPath)
    {
        string normalized = Normalize(sourceRelativePath);
        if (jolietByPrimaryPath.TryGetValue(normalized, out string? primaryMappedPath))
        {
            jolietPath = primaryMappedPath;
            return true;
        }

        string[] visibleMatches = jolietByPrimaryPath.Values
            .Where(path => Normalize(path).Equals(normalized, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.Ordinal)
            .Take(2)
            .ToArray();
        if (visibleMatches.Length == 1)
        {
            jolietPath = visibleMatches[0];
            return true;
        }

        jolietPath = string.Empty;
        return false;
    }

    private static RecordGeometry GeometryKey(DiscFilesystemRecordEvidence record) =>
        new(record.Extent, record.Length, record.IsDirectory, (byte)(record.Flags & 0x04));

    private static string Normalize(string path) => path.Replace('\\', '/').Trim('/');

    private static IReadOnlyDictionary<string, string> EmptyMap() =>
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    private readonly record struct RecordGeometry(uint Extent, uint Length, bool IsDirectory, byte AssociatedFlag);
}

