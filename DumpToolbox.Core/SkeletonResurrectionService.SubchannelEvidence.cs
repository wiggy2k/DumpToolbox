using System.Globalization;
using System.Text;
using SharpCompress.Compressors.ZStandard;

namespace DumpToolbox.Core;

public sealed partial class SkeletonResurrectionService
{
    private const int SubchannelBytesPerSector = 96;
    private const int GapSubchannelContextSectors = 8;
    private const int MaximumReportedSubchannelFaultLbas = 16;

    private sealed record SubchannelEvidenceAnalysis(
        IReadOnlyList<GapSubchannelEvidence> Evidence,
        IReadOnlyList<string> Warnings);

    private static async Task<SubchannelEvidenceAnalysis> AnalyzeCompanionSubchannelAsync(
        string skeletonPath,
        IReadOnlyList<SkeletonContentEntry> entries,
        CancellationToken cancellationToken)
    {
        SkeletonContentEntry[] gaps = entries
            .Where(entry => entry.SpecialKind == SkeletonSpecialKind.Gap && entry.DataLength > 0)
            .ToArray();
        if (gaps.Length == 0)
            return new SubchannelEvidenceAnalysis(Array.Empty<GapSubchannelEvidence>(), Array.Empty<string>());

        string? companion = FindCompanionSubchannelPath(skeletonPath);
        if (companion is null)
            return new SubchannelEvidenceAnalysis(Array.Empty<GapSubchannelEvidence>(), Array.Empty<string>());

        try
        {
            bool compressed = companion.EndsWith(".zst", StringComparison.OrdinalIgnoreCase);
            string logicalPath = compressed ? Path.GetFileNameWithoutExtension(companion) : companion;
            string extension = Path.GetExtension(logicalPath);
            bool redumperMultiplexed = extension.Equals(".subcode", StringComparison.OrdinalIgnoreCase) ||
                                       extension.Equals(".subchannel", StringComparison.OrdinalIgnoreCase);
            string format = redumperMultiplexed
                ? "Redumper multiplexed subchannel"
                : "DIC/CloneCD de-interleaved subchannel";
            if (compressed)
                format += " (Zstandard)";

            var accumulators = gaps.Select(entry => new GapSubchannelAccumulator(entry)).ToArray();
            await using Stream input = OpenSubchannelStream(companion, compressed);
            byte[] stored = new byte[SubchannelBytesPerSector];
            byte[] row = new byte[SubchannelBytesPerSector];
            byte[] q = new byte[12];
            long sectorIndex = 0;
            long? inferredDicLba = null;

            while (await ReadSubchannelSectorAsync(input, stored, cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (redumperMultiplexed)
                    AlignSubchannelRow(row, stored);
                else
                    stored.CopyTo(row, 0);

                Buffer.BlockCopy(row, 12, q, 0, q.Length);
                long? lba;
                if (redumperMultiplexed)
                {
                    lba = checked((long)SubchannelTextService.RedumperFirstLba + sectorIndex);
                }
                else
                {
                    if (inferredDicLba is null &&
                        HasValidSubchannelQCrc(q) &&
                        TryReadQAbsoluteLba(q, out long absoluteLba))
                        inferredDicLba = absoluteLba;
                    lba = inferredDicLba;
                }

                if (lba is { } mappedLba)
                {
                    foreach (GapSubchannelAccumulator accumulator in accumulators)
                        accumulator.Observe(mappedLba, row);
                    if (!redumperMultiplexed)
                        inferredDicLba = checked(mappedLba + 1);
                }

                sectorIndex++;
            }

            GapSubchannelEvidence[] evidence = accumulators
                .Select(accumulator => accumulator.Build(companion, format))
                .ToArray();
            return new SubchannelEvidenceAnalysis(evidence, Array.Empty<string>());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new SubchannelEvidenceAnalysis(
                Array.Empty<GapSubchannelEvidence>(),
                new[]
                {
                    $"Could not analyse companion subchannel '{Path.GetFileName(companion)}': {ex.Message}"
                });
        }
    }

    private static string? FindCompanionSubchannelPath(string skeletonPath)
    {
        string fullPath = Path.GetFullPath(skeletonPath);
        string? directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            return null;

        string name = Path.GetFileName(fullPath);
        if (name.EndsWith(".zst", StringComparison.OrdinalIgnoreCase))
            name = name[..^4];
        if (name.EndsWith(".skeleton", StringComparison.OrdinalIgnoreCase))
            name = name[..^9];
        else
            name = Path.GetFileNameWithoutExtension(name);

        string[] suffixes =
        [
            ".subcode", ".subchannel", ".sub",
            ".subcode.zst", ".subchannel.zst", ".sub.zst"
        ];
        foreach (string suffix in suffixes)
        {
            string candidate = Path.Combine(directory, name + suffix);
            if (File.Exists(candidate))
                return candidate;
        }
        return null;
    }

    private static Stream OpenSubchannelStream(string path, bool compressed)
    {
        var file = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (!compressed)
            return file;

        try
        {
            return new DecompressionStream(
                file,
                1024 * 1024,
                checkEndOfStream: true,
                leaveOpen: false);
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    private static async Task<bool> ReadSubchannelSectorAsync(
        Stream input,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = await input.ReadAsync(buffer.AsMemory(offset), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                if (offset == 0)
                    return false;
                throw new InvalidDataException(
                    $"The expanded subchannel data ends with an incomplete {SubchannelBytesPerSector}-byte sector.");
            }
            offset += read;
        }
        return true;
    }

    private static void AlignSubchannelRow(Span<byte> row, ReadOnlySpan<byte> column)
    {
        row.Clear();
        for (int channel = 0; channel < 8; channel++)
        {
            int sourceMask = 0x80 >> channel;
            int rowOffset = channel * 12;
            for (int bit = 0; bit < SubchannelBytesPerSector; bit++)
            {
                if ((column[bit] & sourceMask) != 0)
                    row[rowOffset + bit / 8] |= (byte)(0x80 >> bit % 8);
            }
        }
    }

    private static bool HasValidSubchannelQCrc(ReadOnlySpan<byte> q)
    {
        ushort crc = 0;
        for (int i = 0; i < 10; i++)
        {
            crc ^= (ushort)(q[i] << 8);
            for (int bit = 0; bit < 8; bit++)
                crc = (ushort)((crc & 0x8000) != 0 ? crc << 1 ^ 0x1021 : crc << 1);
        }
        crc ^= 0xffff;
        return crc == (ushort)(q[10] << 8 | q[11]);
    }

    private static bool TryReadQAbsoluteLba(ReadOnlySpan<byte> q, out long lba)
    {
        lba = 0;
        if ((q[0] & 0x0f) != 1 ||
            !IsSubchannelBcd(q[7]) || !IsSubchannelBcd(q[8]) || !IsSubchannelBcd(q[9]))
        {
            return false;
        }

        int minute = SubchannelBcdToInt(q[7]);
        int second = SubchannelBcdToInt(q[8]);
        int frame = SubchannelBcdToInt(q[9]);
        if (second >= 60 || frame >= 75)
            return false;
        lba = checked((minute * 60L + second) * 75L + frame - 150L);
        return true;
    }

    private static bool TryReadQTrackIndex(ReadOnlySpan<byte> q, out int track, out int index, out bool leadOut)
    {
        track = 0;
        index = 0;
        leadOut = false;
        if ((q[0] & 0x0f) != 1)
            return false;
        if (q[1] == 0xaa)
        {
            track = 0xaa;
            index = IsSubchannelBcd(q[2]) ? SubchannelBcdToInt(q[2]) : q[2];
            leadOut = true;
            return true;
        }
        if (!IsSubchannelBcd(q[1]) || !IsSubchannelBcd(q[2]))
            return false;
        track = SubchannelBcdToInt(q[1]);
        index = SubchannelBcdToInt(q[2]);
        return true;
    }

    private static bool IsSubchannelBcd(byte value) =>
        (value & 0x0f) <= 9 && ((value >> 4) & 0x0f) <= 9;

    private static int SubchannelBcdToInt(byte value) =>
        ((value >> 4) & 0x0f) * 10 + (value & 0x0f);

    private sealed class GapSubchannelAccumulator
    {
        private readonly SkeletonContentEntry _entry;
        private readonly long _start;
        private readonly long _end;
        private (int Track, int Index)? _beforeStart;
        private (int Track, int Index)? _firstTrackIndex;
        private bool _trackIndexChanged;
        private readonly List<long> _invalidQCrcLbas = new();
        private readonly List<long> _qTimeDiscontinuityLbas = new();

        public GapSubchannelAccumulator(SkeletonContentEntry entry)
        {
            _entry = entry;
            _start = entry.ExtentLba;
            _end = checked(_start + entry.DataLength / CookedSectorSize - 1);
        }

        public long FramesObserved { get; private set; }
        public long PAssertedFrames { get; private set; }
        public long NonZeroRwFrames { get; private set; }
        public long InvalidQCrcCount { get; private set; }
        public long QTimeDiscontinuityCount { get; private set; }
        public long? LeadOutStartLba { get; private set; }

        public void Observe(long lba, ReadOnlySpan<byte> row)
        {
            ReadOnlySpan<byte> q = row.Slice(12, 12);
            bool hasQ = !IsAllZero(q);
            bool hasTrackIndex = TryReadQTrackIndex(q, out int track, out int index, out bool leadOut);

            if (lba == _start - 1 && hasTrackIndex && !leadOut)
                _beforeStart = (track, index);

            if (leadOut && lba >= _start && LeadOutStartLba is null)
                LeadOutStartLba = lba;

            long contextEnd = checked(_end + GapSubchannelContextSectors);
            if (lba >= _start && lba <= contextEnd && hasQ)
            {
                if (!HasValidSubchannelQCrc(q))
                {
                    InvalidQCrcCount++;
                    AddReportedLba(_invalidQCrcLbas, lba);
                }

                if (TryReadQAbsoluteLba(q, out long qLba) && qLba != lba)
                {
                    QTimeDiscontinuityCount++;
                    AddReportedLba(_qTimeDiscontinuityLbas, lba);
                }
            }

            if (lba < _start || lba > _end)
                return;

            FramesObserved++;
            if (!IsAllZero(row.Slice(0, 12)))
                PAssertedFrames++;
            if (!IsAllZero(row.Slice(24, 72)))
                NonZeroRwFrames++;

            if (hasTrackIndex)
            {
                var current = (track, index);
                if (_firstTrackIndex is null)
                    _firstTrackIndex = current;
                else if (_firstTrackIndex.Value != current)
                    _trackIndexChanged = true;
            }
        }

        public GapSubchannelEvidence Build(string sourcePath, string sourceFormat)
        {
            bool continuous = _firstTrackIndex is not null && !_trackIndexChanged;
            bool boundaryAtStart = _beforeStart is not null &&
                                   _firstTrackIndex is not null &&
                                   _beforeStart.Value != _firstTrackIndex.Value;
            string summary = BuildSummary(continuous, boundaryAtStart);
            return new GapSubchannelEvidence(
                _entry.Path,
                sourcePath,
                sourceFormat,
                _start,
                _end,
                FramesObserved,
                _firstTrackIndex?.Track,
                _firstTrackIndex?.Index,
                continuous,
                boundaryAtStart,
                PAssertedFrames,
                NonZeroRwFrames,
                InvalidQCrcCount,
                _invalidQCrcLbas.ToArray(),
                QTimeDiscontinuityCount,
                _qTimeDiscontinuityLbas.ToArray(),
                LeadOutStartLba,
                summary);
        }

        private string BuildSummary(bool continuous, bool boundaryAtStart)
        {
            if (FramesObserved == 0)
                return $"no subchannel frames cover LBA {_start:N0}-{_end:N0}";

            var parts = new List<string>();
            if (_firstTrackIndex is { } first)
            {
                string identity = $"Track {first.Track:00} / Index {first.Index:00}";
                parts.Add(continuous
                    ? identity + " throughout the GAP"
                    : identity + " changes within the GAP");
            }
            else
            {
                parts.Add("no position-Q track/index could be established");
            }

            parts.Add(boundaryAtStart
                ? $"a track/index boundary occurs at LBA {_start:N0}"
                : $"no track/index boundary occurs at LBA {_start:N0}");

            if (PAssertedFrames > 0)
                parts.Add($"P is asserted in {PAssertedFrames:N0} frame(s)");
            if (InvalidQCrcCount > 0)
                parts.Add($"{InvalidQCrcCount:N0} CRC-invalid Q frame(s) at {FormatLbas(_invalidQCrcLbas)}");
            if (QTimeDiscontinuityCount > 0)
                parts.Add($"{QTimeDiscontinuityCount:N0} Q-time discontinuity frame(s) at {FormatLbas(_qTimeDiscontinuityLbas)}");
            if (LeadOutStartLba is { } leadOut)
                parts.Add($"lead-out AA begins at LBA {leadOut:N0}");
            if (NonZeroRwFrames > 0)
                parts.Add($"raw R-W bytes are non-zero in {NonZeroRwFrames:N0} GAP frame(s)");
            else
                parts.Add("raw R-W bytes are zero throughout the GAP");
            return string.Join("; ", parts);
        }

        private static void AddReportedLba(List<long> values, long lba)
        {
            if (values.Count < MaximumReportedSubchannelFaultLbas)
                values.Add(lba);
        }

        private static string FormatLbas(IReadOnlyList<long> lbas)
        {
            if (lbas.Count == 0)
                return "unlisted LBAs";
            return string.Join(", ", lbas.Select(lba => lba.ToString("N0", CultureInfo.InvariantCulture)));
        }

        private static bool IsAllZero(ReadOnlySpan<byte> bytes)
        {
            foreach (byte value in bytes)
            {
                if (value != 0)
                    return false;
            }
            return true;
        }
    }
}
