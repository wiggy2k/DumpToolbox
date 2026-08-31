using System.Buffers;

namespace DumpToolbox.Core;

public sealed record PregapScrambleOutcome(
    bool Attempted,
    bool Fixed,
    int ScrambledSectors,
    SearchResult? Result,
    IReadOnlyList<string> Messages);

/// <summary>
/// Repairs the mixed-mode mastering case where one or more otherwise empty
/// CD-ROM data sectors have been placed in Track 02's file-backed AUDIO pregap
/// but are present in an unscrambled representation.
///
/// Important: the CUE INDEX 00 -> INDEX 01 length describes the physical/file
/// pregap; it does NOT prove that all of those sectors belong at the beginning
/// of a per-track Redump-style Track 02 BIN. In the common mixed-mode split the
/// normal 150-sector pregap may effectively be attached to Track 01, leaving
/// only extra mastering-error sectors at the start of Track 02.
///
/// Therefore this service first corrects recognised empty data sectors in the
/// complete physical pregap window, then runs the ordinary 1-byte FindCRCs
/// rolling search across a window large enough for the Track 02 target to begin
/// anywhere within that pregap. We never blindly scramble AUDIO sectors.
/// </summary>
public sealed class CdPregapScrambleService
{
    private const int SectorSize = 2352;
    private const int IoBufferSize = 4 * 1024 * 1024;
    private readonly HashSearchEngine _searchEngine = new();
    private readonly FindEndsService _findEnds = new();

    private static readonly byte[] ScrambleMask = BuildScrambleMask();
    private static readonly byte[] Sync =
    {
        0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,
        0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00
    };

    public async Task<PregapScrambleOutcome> TryRepairTrack2Async(
        string sourceFile,
        HashTarget track2Target,
        int track2TargetIndex,
        IReadOnlyList<SearchResult> results,
        int pregapSectors,
        long? cueSuggestedOffset,
        long? symmetricAudioEdgeShiftBytes,
        IProgress<string>? activity = null,
        CancellationToken cancellationToken = default)
    {
        var messages = new List<string>();

        if (!File.Exists(sourceFile))
            throw new FileNotFoundException("FindCRCs source file not found.", sourceFile);
        if (track2TargetIndex < 0 || track2TargetIndex >= results.Count)
            throw new ArgumentOutOfRangeException(nameof(track2TargetIndex));
        if (pregapSectors <= 0)
        {
            Report(activity, messages, "PREGAP SCRAMBLE: Track 02 has no file-backed INDEX 00 pregap sectors; nothing to do.");
            return new PregapScrambleOutcome(false, false, 0, null, messages);
        }
        if (results[track2TargetIndex].Found)
        {
            Report(activity, messages, "PREGAP SCRAMBLE: Track 02 already matches; no scrambling correction is required.");
            return new PregapScrambleOutcome(false, false, 0, results[track2TargetIndex], messages);
        }
        if (track2Target.Size <= 0 || track2Target.Size % SectorSize != 0)
        {
            Report(activity, messages,
                $"PREGAP SCRAMBLE: Track 02 target size {track2Target.Size:N0} is not a whole number of 2352-byte sectors; correction skipped.");
            return new PregapScrambleOutcome(false, false, 0, null, messages);
        }

        long fileLength = new FileInfo(sourceFile).Length;
        long pregapBytes = checked((long)pregapSectors * SectorSize);
        long adjustmentAllowanceBytes = Math.Abs(symmetricAudioEdgeShiftBytes ?? 0);
        var plans = new List<SearchWindowPlan>();

        // Best anchor: Track 01 matched. The physical Track 02 pregap begins at
        // the end of that matched data track in the source image. Search an
        // extra pregap's worth of bytes so the Redump Track 02 boundary can be
        // anywhere from INDEX 00 through INDEX 01 (or even byte-shifted).
        if (track2TargetIndex > 0 && results[track2TargetIndex - 1].Found &&
            results[track2TargetIndex - 1].Offset is long previousOffset)
        {
            long previousEnd = checked(previousOffset + results[track2TargetIndex - 1].Target.Size);
            AddPlan(plans, fileLength, track2Target.Size, pregapBytes, adjustmentAllowanceBytes,
                previousEnd, previousEnd,
                "end of the preceding matched track");
        }

        // If the CUE directly addresses this same single source file, INDEX 00
        // is also a trustworthy physical pregap start.
        if (cueSuggestedOffset is long cueOffset)
        {
            AddPlan(plans, fileLength, track2Target.Size, pregapBytes, adjustmentAllowanceBytes,
                cueOffset, cueOffset,
                "CUE INDEX 00 position");
        }

        // Fallback when Track 01 is unavailable: use the following matched track
        // to bracket the likely area. We still only transform sectors that pass
        // the very strict empty-raw-data-sector test, so arbitrary AUDIO data is
        // left untouched.
        if (plans.Count == 0 &&
            track2TargetIndex + 1 < results.Count && results[track2TargetIndex + 1].Found &&
            results[track2TargetIndex + 1].Offset is long nextOffset)
        {
            long inferredStart = nextOffset - track2Target.Size;
            long searchStart = Math.Max(0, inferredStart - pregapBytes);
            AddPlan(plans, fileLength, track2Target.Size, pregapBytes, adjustmentAllowanceBytes,
                searchStart, searchStart,
                "window inferred backwards from the following matched track");
        }

        plans = plans
            .GroupBy(p => (p.SearchStart, p.ScrambleStart))
            .Select(g => g.First())
            .ToList();

        if (plans.Count == 0)
        {
            Report(activity, messages,
                "PREGAP SCRAMBLE: Track 02 is unmatched and no safe search window could be derived from adjacent matches or the CUE.");
            return new PregapScrambleOutcome(true, false, 0, null, messages);
        }

        Report(activity, messages,
            $"PREGAP SCRAMBLE: CUE describes {pregapSectors:N0} file-backed pregap sectors, but their complete length will NOT be assumed to belong to the Track 02 BIN. " +
            "Empty data sectors will be corrected first, then FindCRCs will search for the Track 02 target at every byte offset across the corrected window.");

        foreach (SearchWindowPlan plan in plans)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string tempDirectory = Path.Combine(Path.GetTempPath(), "DumpToolbox", $"track2_pregap_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDirectory);
            string tempSource = Path.Combine(tempDirectory, "corrected_search_window.bin");

            try
            {
                Report(activity, messages,
                    $"PREGAP SCRAMBLE: building corrected search window at source {plan.SearchStart:N0} (0x{plan.SearchStart:X}), " +
                    $"length {plan.SearchLength:N0} bytes, from {plan.Reason}.");

                WindowBuildResult built = await BuildCorrectedSearchWindowAsync(
                    sourceFile,
                    tempSource,
                    plan,
                    pregapSectors,
                    cancellationToken).ConfigureAwait(false);

                if (built.ScrambledSectors == 0)
                {
                    Report(activity, messages,
                        "PREGAP SCRAMBLE: no empty, unscrambled CD-ROM data sectors were detected in this pregap window.");
                    continue;
                }

                string offsets = built.RelativeSectorOffsets.Count <= 8
                    ? string.Join(", ", built.RelativeSectorOffsets.Select(x => $"+{x:N0}"))
                    : string.Join(", ", built.RelativeSectorOffsets.Take(8).Select(x => $"+{x:N0}")) + ", ...";
                Report(activity, messages,
                    $"PREGAP SCRAMBLE: corrected {built.ScrambledSectors:N0} empty data sector(s) in the physical pregap (sector offsets {offsets}). " +
                    "Running 1-byte FindCRCs across the whole corrected Track 02 search window...");

                SearchResult localResult = await SearchCorrectedWindowAsync(
                    tempSource,
                    track2Target,
                    plan,
                    adjustmentAtLocalOffset: null,
                    signedAdjustmentBytes: 0,
                    logPrefix: "PREGAP SCRAMBLE FINDCRCS",
                    activity,
                    cancellationToken).ConfigureAwait(false);

                long appliedSignedShiftBytes = 0;
                long? adjustmentAtLocalOffset = null;

                if (!IsUsableMatch(localResult))
                {
                    Report(activity, messages,
                        $"PREGAP SCRAMBLE: corrected {built.ScrambledSectors:N0} empty data sector(s), but FindCRCs found no Track 02 target anywhere in the corrected window.");

                    // If both adjacent tracks are matched and this search window starts
                    // exactly at the end of the preceding track, we know the available
                    // Track 02 prefix precisely.  A short region before Track 03 means
                    // Track 02 is missing a suffix.  Crucially, calculate/test that
                    // suffix against the already-scrambled prefix; doing edge recovery
                    // on the original bytes produces the wrong CRC whenever the pregap
                    // contains an unscrambled empty data sector.
                    if (track2TargetIndex > 0 &&
                        track2TargetIndex + 1 < results.Count &&
                        results[track2TargetIndex - 1].Found &&
                        results[track2TargetIndex - 1].Offset is long previousTrackOffset &&
                        results[track2TargetIndex + 1].Found &&
                        results[track2TargetIndex + 1].Offset is long followingTrackOffset)
                    {
                        long anchoredStart = checked(previousTrackOffset + results[track2TargetIndex - 1].Target.Size);
                        long availablePrefix = checked(followingTrackOffset - anchoredStart);
                        if (plan.SearchStart == anchoredStart &&
                            plan.ScrambleStart == anchoredStart &&
                            availablePrefix > 0 &&
                            availablePrefix < track2Target.Size &&
                            availablePrefix <= new FileInfo(tempSource).Length)
                        {
                            long missingSuffix = checked(track2Target.Size - availablePrefix);
                            string correctedPartial = Path.Combine(tempDirectory, "corrected_track2_forward_partial.bin");
                            string zeroPaddedCandidate = Path.Combine(tempDirectory, "corrected_track2_forward_zero_padded.bin");

                            await CopyPrefixAsync(
                                tempSource, correctedPartial, availablePrefix, cancellationToken).ConfigureAwait(false);

                            // Strong mixed-mode hypothesis: when the first audio track is
                            // short between two verified anchors and the final audio edge
                            // shows a positive shift of N bytes, the missing amount plus N
                            // may be an exact number of raw CD sectors.  In that case test
                            // the CUE-consistent interpretation at the pregap boundary:
                            //   scrambled data sector(s)
                            //   + generated/absent pregap silence
                            //   - the same N bytes of excess zero PCM already present
                            // rather than appending the whole shortfall at Track 02 EOF.
                            long positiveEdgeShift = symmetricAudioEdgeShiftBytes ?? 0;
                            if (positiveEdgeShift > 0 && built.RelativeSectorOffsets.Count > 0)
                            {
                                long mirroredBytes = positiveEdgeShift;
                                long virtualPregapBytes = checked(missingSuffix + mirroredBytes);
                                if (virtualPregapBytes > 0 && virtualPregapBytes % SectorSize == 0)
                                {
                                    long virtualPregapSectors = virtualPregapBytes / SectorSize;
                                    if (virtualPregapSectors <= pregapSectors)
                                    {
                                        long lastCorrectedSector = built.RelativeSectorOffsets.Max();
                                        long rebalanceAt = checked(
                                            (plan.ScrambleStart - plan.SearchStart) +
                                            ((lastCorrectedSector + 1) * SectorSize));
                                        string rebalanceCandidate = Path.Combine(tempDirectory, "corrected_track2_pregap_rebalanced.bin");
                                        bool cueExact = virtualPregapSectors + built.ScrambledSectors == pregapSectors;

                                        Report(activity, messages,
                                            $"PREGAP REBALANCE: Track 02 anchor shortfall {missingSuffix:N0} + positive audio-edge shift {mirroredBytes:N0} = " +
                                            $"{virtualPregapBytes:N0} byte(s) ({virtualPregapSectors:N0} raw 2352-byte sector(s)). " +
                                            $"Testing at the pregap/audio boundary: keep the scrambled data sector(s), remove {mirroredBytes:N0} verified zero PCM byte(s) immediately after them, " +
                                            $"and insert {virtualPregapSectors:N0} silent sector(s)." +
                                            (cueExact ? $" This exactly accounts for the CUE's {pregapSectors:N0}-sector pregap ({built.ScrambledSectors:N0} stored data + {virtualPregapSectors:N0} silent)." : string.Empty));

                                        SilenceTrimBuildResult rebalanced = await TryBuildPregapRebalancedCandidateAsync(
                                            correctedPartial,
                                            rebalanceCandidate,
                                            rebalanceAt,
                                            mirroredBytes,
                                            virtualPregapBytes,
                                            track2Target.Size,
                                            cancellationToken).ConfigureAwait(false);

                                        if (rebalanced.Created)
                                        {
                                            SearchResult rebalanceResult = await SearchExactCandidateAsync(
                                                rebalanceCandidate, track2Target, "PREGAP REBALANCE", activity, cancellationToken).ConfigureAwait(false);

                                            if (IsUsableMatch(rebalanceResult))
                                            {
                                                string rebalanceOutputPath = GetOutputPath(sourceFile, track2Target, anchoredStart);
                                                File.Move(rebalanceResult.OutputPath!, rebalanceOutputPath, true);
                                                string rebalanceStatus =
                                                    $"Track 02 fixed by scrambling {built.ScrambledSectors:N0} empty pregap data sector(s), removing {mirroredBytes:N0} excess zero PCM byte(s), " +
                                                    $"and inserting {virtualPregapSectors:N0} silent pregap sector(s); CRC32" +
                                                    (track2Target.NormalizedMd5 is null ? string.Empty : "/MD5") + " verified.";
                                                Report(activity, messages, $"PREGAP REBALANCE FIXED: {rebalanceStatus} Output: {rebalanceOutputPath}");
                                                return new PregapScrambleOutcome(
                                                    true, true, built.ScrambledSectors,
                                                    new SearchResult(track2Target, anchoredStart, true, rebalanceStatus, rebalanceResult.CrcCandidates, rebalanceOutputPath),
                                                    messages);
                                            }

                                            Report(activity, messages,
                                                "PREGAP REBALANCE: the sector-aligned pregap-boundary candidate did not match Track 02 CRC32/MD5; continuing with the ordinary end-padding/Find-Ends hypotheses.");
                                        }
                                        else
                                        {
                                            Report(activity, messages, $"PREGAP REBALANCE: {rebalanced.Reason}");
                                        }
                                    }
                                }
                            }

                            Report(activity, messages,
                                $"PREGAP + EDGE: matched adjacent tracks leave {availablePrefix:N0} corrected Track 02 byte(s) after scrambling; " +
                                $"the target is short by {missingSuffix:N0} byte(s) at the end. Testing the combined repair: scrambled pregap data sector(s) + {missingSuffix:N0} zero padding byte(s).");

                            await BuildZeroPaddedSuffixCandidateAsync(
                                correctedPartial, zeroPaddedCandidate, missingSuffix, cancellationToken).ConfigureAwait(false);

                            SearchResult paddedResult = await SearchExactCandidateAsync(
                                zeroPaddedCandidate, track2Target, "PREGAP + EDGE ZERO", activity, cancellationToken).ConfigureAwait(false);

                            if (IsUsableMatch(paddedResult))
                            {
                                string combinedOutputPath = GetOutputPath(sourceFile, track2Target, anchoredStart);
                                File.Move(paddedResult.OutputPath!, combinedOutputPath, true);
                                string combinedStatus =
                                    $"Track 02 fixed by scrambling {built.ScrambledSectors:N0} empty pregap data sector(s) and restoring " +
                                    $"{missingSuffix:N0} zero byte(s) at the end; CRC32" +
                                    (track2Target.NormalizedMd5 is null ? string.Empty : "/MD5") + " verified.";
                                Report(activity, messages, $"PREGAP + EDGE FIXED: {combinedStatus} Output: {combinedOutputPath}");
                                return new PregapScrambleOutcome(
                                    true, true, built.ScrambledSectors,
                                    new SearchResult(track2Target, anchoredStart, true, combinedStatus, paddedResult.CrcCandidates, combinedOutputPath),
                                    messages);
                            }

                            Report(activity, messages,
                                $"PREGAP + EDGE: combined scrambling + {missingSuffix:N0}-byte zero suffix did not match. " +
                                "Calculating the missing-end CRC32 from the corrected (scrambled) Track 02 prefix and searching the complete source.");

                            if (track2Target.NormalizedMd5 is string expectedMd5)
                            {
                                string recoveredOutput = GetOutputPath(sourceFile, track2Target, anchoredStart);
                                FindEndsResult recovered = await _findEnds.RunAsync(
                                    correctedPartial,
                                    track2Target.Size,
                                    track2Target.Crc32,
                                    expectedMd5,
                                    FindEndsMode.MissingEnd,
                                    sourceFile,
                                    recoveredOutput,
                                    progress: null,
                                    cancellationToken: cancellationToken).ConfigureAwait(false);

                                foreach (FindEndsAnalysis analysis in recovered.Analyses)
                                {
                                    Report(activity, messages,
                                        $"PREGAP + EDGE: corrected Track 02 missing {analysis.SideName} segment CRC32={analysis.MissingCrc32Hex}, length={analysis.MissingLength:N0}.");
                                }

                                if (recovered.Found && !string.IsNullOrWhiteSpace(recovered.OutputPath))
                                {
                                    string recoveredStatus =
                                        $"Track 02 fixed after scrambling {built.ScrambledSectors:N0} empty pregap data sector(s); " +
                                        $"missing {missingSuffix:N0}-byte end segment recovered from source offset {recovered.SourceOffset:N0}; CRC32/MD5 verified.";
                                    Report(activity, messages, $"PREGAP + EDGE FIXED: {recoveredStatus} Output: {recovered.OutputPath}");
                                    return new PregapScrambleOutcome(
                                        true, true, built.ScrambledSectors,
                                        new SearchResult(track2Target, anchoredStart, true, recoveredStatus, recovered.CrcCandidates, recovered.OutputPath),
                                        messages);
                                }

                                Report(activity, messages,
                                    "PREGAP + EDGE: the missing end segment was not recovered from the complete source after applying the pregap correction.");
                            }
                            else
                            {
                                Report(activity, messages,
                                    "PREGAP + EDGE: MD5 is required before Find-Ends can safely recover a non-zero missing suffix from the corrected Track 02 prefix.");
                            }
                        }
                    }

                    long signedShift = symmetricAudioEdgeShiftBytes ?? 0;
                    if (signedShift != 0 && built.RelativeSectorOffsets.Count > 0)
                    {
                        long magnitude = Math.Abs(signedShift);
                        long lastCorrectedSector = built.RelativeSectorOffsets.Max();
                        long adjustAt = checked(
                            (plan.ScrambleStart - plan.SearchStart) +
                            ((lastCorrectedSector + 1) * SectorSize));

                        if ((magnitude & 3) != 0)
                        {
                            Report(activity, messages,
                                $"PREGAP SYMMETRY: the mirrored edge amount is {magnitude:N0} bytes, which is not a whole 4-byte stereo PCM sample frame. " +
                                "The exact byte count will still be tested because CRC32/MD5 verification is authoritative.");
                        }

                        if (signedShift > 0)
                        {
                            string adjustedSource = Path.Combine(tempDirectory, "corrected_search_window_symmetric_remove.bin");
                            Report(activity, messages,
                                $"PREGAP SYMMETRY: positive shift +{magnitude:N0}: testing whether {magnitude:N0} byte(s) immediately after the final corrected pregap data sector are excess digital PCM silence. " +
                                "Those bytes will only be removed if they are all zero.");

                            SilenceTrimBuildResult trim = await TryBuildSilenceTrimmedWindowAsync(
                                tempSource,
                                adjustedSource,
                                adjustAt,
                                magnitude,
                                track2Target.Size,
                                cancellationToken).ConfigureAwait(false);

                            if (trim.Created)
                            {
                                Report(activity, messages,
                                    $"PREGAP SYMMETRY: removed {magnitude:N0} zero byte(s) immediately after the corrected pregap data sector(s). " +
                                    "Running 1-byte FindCRCs again across the adjusted Track 02 window...");

                                SearchResult adjustedResult = await SearchCorrectedWindowAsync(
                                    adjustedSource,
                                    track2Target,
                                    plan,
                                    adjustmentAtLocalOffset: adjustAt,
                                    signedAdjustmentBytes: magnitude,
                                    logPrefix: "PREGAP SYMMETRY + FINDCRCS",
                                    activity,
                                    cancellationToken).ConfigureAwait(false);

                                if (IsUsableMatch(adjustedResult))
                                {
                                    localResult = adjustedResult;
                                    appliedSignedShiftBytes = magnitude;
                                    adjustmentAtLocalOffset = adjustAt;
                                }
                                else
                                {
                                    Report(activity, messages,
                                        $"PREGAP SYMMETRY: removing {magnitude:N0} byte(s) of digital silence did not produce the Track 02 CRC32/MD5 target.");
                                }
                            }
                            else
                            {
                                Report(activity, messages, $"PREGAP SYMMETRY: {trim.Reason}");
                            }
                        }
                        else
                        {
                            string adjustedSource = Path.Combine(tempDirectory, "corrected_search_window_symmetric_insert.bin");
                            Report(activity, messages,
                                $"PREGAP SYMMETRY: negative shift -{magnitude:N0}: inserting {magnitude:N0} zero PCM byte(s) immediately after the final corrected pregap data sector(s), " +
                                "then running 1-byte FindCRCs. The insertion is accepted only if CRC32/MD5 verifies.");

                            await BuildSilenceInsertedWindowAsync(
                                tempSource,
                                adjustedSource,
                                adjustAt,
                                magnitude,
                                cancellationToken).ConfigureAwait(false);

                            SearchResult adjustedResult = await SearchCorrectedWindowAsync(
                                adjustedSource,
                                track2Target,
                                plan,
                                adjustmentAtLocalOffset: adjustAt,
                                signedAdjustmentBytes: -magnitude,
                                logPrefix: "PREGAP SYMMETRY - FINDCRCS",
                                activity,
                                cancellationToken).ConfigureAwait(false);

                            if (IsUsableMatch(adjustedResult))
                            {
                                localResult = adjustedResult;
                                appliedSignedShiftBytes = -magnitude;
                                adjustmentAtLocalOffset = adjustAt;
                            }
                            else
                            {
                                Report(activity, messages,
                                    $"PREGAP SYMMETRY: inserting {magnitude:N0} zero PCM byte(s) did not produce the Track 02 CRC32/MD5 target.");
                            }
                        }
                    }
                }

                if (!IsUsableMatch(localResult) || localResult.Offset is not long localOffset)
                    continue;

                long originalWindowOffset = MapAdjustedOffsetToSourceWindow(
                    localOffset,
                    adjustmentAtLocalOffset,
                    appliedSignedShiftBytes);
                long absoluteOffset = checked(plan.SearchStart + originalWindowOffset);
                string outputPath = GetOutputPath(sourceFile, track2Target, absoluteOffset);
                File.Move(localResult.OutputPath!, outputPath, true);

                long sectorShift = originalWindowOffset / SectorSize;
                long byteRemainder = originalWindowOffset % SectorSize;
                string boundary = byteRemainder == 0
                    ? $"Track 02 begins {sectorShift:N0} sector(s) into the CUE pregap search window"
                    : $"Track 02 begins {originalWindowOffset:N0} byte(s) into the CUE pregap search window ({sectorShift:N0} sectors + {byteRemainder:N0} bytes)";

                string symmetry = appliedSignedShiftBytes > 0
                    ? $"; removed {appliedSignedShiftBytes:N0} excess zero PCM byte(s) after the corrected pregap data sector(s)"
                    : appliedSignedShiftBytes < 0
                        ? $"; inserted {Math.Abs(appliedSignedShiftBytes):N0} zero PCM byte(s) after the corrected pregap data sector(s)"
                        : string.Empty;
                string status =
                    $"Track 02 fixed after scrambling {built.ScrambledSectors:N0} empty data sector(s){symmetry}; {boundary}; CRC32" +
                    (track2Target.NormalizedMd5 is null ? string.Empty : "/MD5") + " verified.";
                Report(activity, messages, $"PREGAP SCRAMBLE FIXED: {status} Output: {outputPath}");

                var result = new SearchResult(
                    track2Target,
                    absoluteOffset,
                    true,
                    status,
                    localResult.CrcCandidates,
                    outputPath);
                return new PregapScrambleOutcome(true, true, built.ScrambledSectors, result, messages);
            }
            finally
            {
                TryDeleteDirectory(tempDirectory);
            }
        }

        return new PregapScrambleOutcome(true, false, 0, null, messages);
    }

    /// <summary>
    /// Returns true only for an unscrambled raw CD-ROM sector whose user-data
    /// portion is all zero. This deliberately avoids touching arbitrary audio.
    /// </summary>
    public static bool IsEmptyUnscrambledDataSector(ReadOnlySpan<byte> sector)
    {
        if (sector.Length != SectorSize || !sector[..12].SequenceEqual(Sync))
            return false;

        byte mode = sector[15];
        if (mode == 0x01)
            return IsAllZero(sector.Slice(16, 2048));

        if (mode != 0x02)
            return false;

        // XA subheader copies must agree before we treat Mode 2 as a data sector.
        if (!sector.Slice(16, 4).SequenceEqual(sector.Slice(20, 4)))
            return false;

        bool form2 = (sector[18] & 0x20) != 0;
        return form2
            ? IsAllZero(sector.Slice(24, 2324))
            : IsAllZero(sector.Slice(24, 2048));
    }

    public static void ScrambleSectorInPlace(Span<byte> sector)
    {
        if (sector.Length != SectorSize)
            throw new ArgumentException("A raw CD sector must contain exactly 2352 bytes.", nameof(sector));

        for (int i = 0; i < ScrambleMask.Length; i++)
            sector[12 + i] ^= ScrambleMask[i];
    }

    private async Task<SearchResult> SearchCorrectedWindowAsync(
        string searchFile,
        HashTarget target,
        SearchWindowPlan plan,
        long? adjustmentAtLocalOffset,
        long signedAdjustmentBytes,
        string logPrefix,
        IProgress<string>? activity,
        CancellationToken cancellationToken)
    {
        var searchProgress = new Progress<SearchProgress>(p =>
        {
            if (p.Kind == SearchEventKind.CrcCandidate && p.Offset is long local)
            {
                long sourceWindowOffset = MapAdjustedOffsetToSourceWindow(
                    local, adjustmentAtLocalOffset, signedAdjustmentBytes);
                long absolute = plan.SearchStart + sourceWindowOffset;
                activity?.Report(
                    $"{logPrefix}: CRC candidate at adjusted window +{local:N0} bytes; source offset {absolute:N0} (0x{absolute:X}) — verifying MD5...");
            }
            else if (p.Kind == SearchEventKind.Md5Rejected && p.Offset is long rejected)
            {
                long sourceWindowOffset = MapAdjustedOffsetToSourceWindow(
                    rejected, adjustmentAtLocalOffset, signedAdjustmentBytes);
                long absolute = plan.SearchStart + sourceWindowOffset;
                activity?.Report(
                    $"{logPrefix}: MD5 rejected candidate at source offset {absolute:N0} (0x{absolute:X}).");
            }
        });

        IReadOnlyList<SearchResult> searched = await _searchEngine.SearchAsync(
            searchFile,
            new[] { target },
            alignment: 1,
            progress: searchProgress,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return searched[0];
    }

    private static bool IsUsableMatch(SearchResult result) =>
        result.Found &&
        result.Offset is not null &&
        !string.IsNullOrWhiteSpace(result.OutputPath) &&
        File.Exists(result.OutputPath);

    /// <summary>
    /// Maps an offset in an adjusted temporary window back to the corresponding
    /// position in the original source window. Positive signedAdjustmentBytes
    /// means bytes were removed; negative means zero bytes were inserted.
    /// </summary>
    private static long MapAdjustedOffsetToSourceWindow(
        long adjustedOffset,
        long? adjustmentAtLocalOffset,
        long signedAdjustmentBytes)
    {
        if (adjustmentAtLocalOffset is not long at || signedAdjustmentBytes == 0)
            return adjustedOffset;

        if (signedAdjustmentBytes > 0)
        {
            // The adjusted file is shorter: after the deletion point, add the
            // removed byte count to get back to the original source position.
            return adjustedOffset >= at
                ? checked(adjustedOffset + signedAdjustmentBytes)
                : adjustedOffset;
        }

        long inserted = -signedAdjustmentBytes;
        if (adjustedOffset < at)
            return adjustedOffset;
        if (adjustedOffset < checked(at + inserted))
            return at; // Offset lies inside synthetic inserted silence.
        return checked(adjustedOffset - inserted);
    }

    private async Task<SearchResult> SearchExactCandidateAsync(
        string candidateFile,
        HashTarget target,
        string logPrefix,
        IProgress<string>? activity,
        CancellationToken cancellationToken)
    {
        var progress = new Progress<SearchProgress>(p =>
        {
            if (p.Kind == SearchEventKind.CrcCandidate && p.Offset is long offset)
                activity?.Report($"{logPrefix}: CRC candidate at candidate offset {offset:N0}; verifying MD5...");
            else if (p.Kind == SearchEventKind.Md5Rejected && p.Offset is long rejected)
                activity?.Report($"{logPrefix}: MD5 rejected candidate at candidate offset {rejected:N0}.");
        });

        IReadOnlyList<SearchResult> results = await _searchEngine.SearchAsync(
            candidateFile, new[] { target }, alignment: 1, progress: progress, cancellationToken: cancellationToken).ConfigureAwait(false);
        return results[0];
    }

    private static async Task CopyPrefixAsync(
        string source,
        string destination,
        long length,
        CancellationToken cancellationToken)
    {
        if (length < 0)
            throw new ArgumentOutOfRangeException(nameof(length));

        byte[] buffer = ArrayPool<byte>.Shared.Rent(IoBufferSize);
        try
        {
            await using var input = new FileStream(source, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, IoBufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (length > input.Length)
                throw new EndOfStreamException("Corrected Track 02 window is shorter than the anchored prefix.");

            await using var output = new FileStream(destination, FileMode.Create, FileAccess.Write,
                FileShare.None, IoBufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await CopyExactlyAsync(input, output, length, buffer, cancellationToken).ConfigureAwait(false);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task BuildZeroPaddedSuffixCandidateAsync(
        string correctedPartial,
        string destination,
        long zeroBytes,
        CancellationToken cancellationToken)
    {
        if (zeroBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(zeroBytes));

        byte[] buffer = ArrayPool<byte>.Shared.Rent(IoBufferSize);
        byte[] zeros = new byte[Math.Min(IoBufferSize, 1024 * 1024)];
        try
        {
            await using var input = new FileStream(correctedPartial, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, IoBufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var output = new FileStream(destination, FileMode.Create, FileAccess.Write,
                FileShare.None, IoBufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            await CopyExactlyAsync(input, output, input.Length, buffer, cancellationToken).ConfigureAwait(false);

            long remaining = zeroBytes;
            while (remaining > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int count = (int)Math.Min(zeros.Length, remaining);
                await output.WriteAsync(zeros.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                remaining -= count;
            }

            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task<SilenceTrimBuildResult> TryBuildPregapRebalancedCandidateAsync(
        string source,
        string destination,
        long rebalanceAt,
        long removeZeroBytes,
        long insertZeroBytes,
        long expectedOutputSize,
        CancellationToken cancellationToken)
    {
        if (removeZeroBytes <= 0 || insertZeroBytes <= 0)
            return new SilenceTrimBuildResult(false, "The pregap rebalance requires both a positive zero-byte removal and a positive silent-sector insertion.");

        long sourceLength = new FileInfo(source).Length;
        if (rebalanceAt < 0 || rebalanceAt > sourceLength || removeZeroBytes > sourceLength - rebalanceAt)
            return new SilenceTrimBuildResult(false,
                $"the requested {removeZeroBytes:N0}-byte PCM-silence region falls outside the corrected Track 02 prefix.");

        long outputLength = checked(sourceLength - removeZeroBytes + insertZeroBytes);
        if (outputLength != expectedOutputSize)
            return new SilenceTrimBuildResult(false,
                $"the rebalance would produce {outputLength:N0} bytes, but Track 02 expects {expectedOutputSize:N0}; candidate skipped.");

        // Verify the bytes we intend to discard before creating the candidate.
        byte[] checkBuffer = ArrayPool<byte>.Shared.Rent(IoBufferSize);
        try
        {
            await using (var check = new FileStream(source, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, IoBufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                check.Position = rebalanceAt;
                long remaining = removeZeroBytes;
                while (remaining > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int want = (int)Math.Min(checkBuffer.Length, remaining);
                    int got = await check.ReadAsync(checkBuffer.AsMemory(0, want), cancellationToken).ConfigureAwait(false);
                    if (got <= 0)
                        return new SilenceTrimBuildResult(false, "unexpected EOF while checking the pregap-boundary PCM-silence bytes.");
                    if (!IsAllZero(checkBuffer.AsSpan(0, got)))
                        return new SilenceTrimBuildResult(false,
                            $"the {removeZeroBytes:N0} byte(s) immediately after the corrected pregap data sector(s) are not all zero PCM silence; rebalance skipped.");
                    remaining -= got;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(checkBuffer);
        }

        byte[] buffer = ArrayPool<byte>.Shared.Rent(IoBufferSize);
        byte[] zeros = new byte[Math.Min(IoBufferSize, 1024 * 1024)];
        try
        {
            await using var input = new FileStream(source, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, IoBufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var output = new FileStream(destination, FileMode.Create, FileAccess.Write,
                FileShare.None, IoBufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            await CopyExactlyAsync(input, output, rebalanceAt, buffer, cancellationToken).ConfigureAwait(false);

            input.Position = checked(input.Position + removeZeroBytes);

            long remainingInsert = insertZeroBytes;
            while (remainingInsert > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int count = (int)Math.Min(zeros.Length, remainingInsert);
                await output.WriteAsync(zeros.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                remainingInsert -= count;
            }

            long remainingSource = input.Length - input.Position;
            if (remainingSource > 0)
                await CopyExactlyAsync(input, output, remainingSource, buffer, cancellationToken).ConfigureAwait(false);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            return new SilenceTrimBuildResult(true, string.Empty);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task BuildSilenceInsertedWindowAsync(
        string source,
        string destination,
        long insertAt,
        long insertBytes,
        CancellationToken cancellationToken)
    {
        if (insertBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(insertBytes));

        long sourceLength = new FileInfo(source).Length;
        if (insertAt < 0 || insertAt > sourceLength)
            throw new ArgumentOutOfRangeException(nameof(insertAt));

        byte[] buffer = ArrayPool<byte>.Shared.Rent(IoBufferSize);
        byte[] zero = new byte[Math.Min(IoBufferSize, 1024 * 1024)];
        try
        {
            await using var input = new FileStream(source, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, IoBufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var output = new FileStream(destination, FileMode.Create, FileAccess.Write,
                FileShare.None, IoBufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            await CopyExactlyAsync(input, output, insertAt, buffer, cancellationToken).ConfigureAwait(false);

            long remainingInsert = insertBytes;
            while (remainingInsert > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int count = (int)Math.Min(zero.Length, remainingInsert);
                await output.WriteAsync(zero.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                remainingInsert -= count;
            }

            long remaining = input.Length - input.Position;
            if (remaining > 0)
                await CopyExactlyAsync(input, output, remaining, buffer, cancellationToken).ConfigureAwait(false);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task<SilenceTrimBuildResult> TryBuildSilenceTrimmedWindowAsync(
        string source,
        string destination,
        long trimAt,
        long trimBytes,
        long targetSize,
        CancellationToken cancellationToken)
    {
        if (trimBytes <= 0)
            return new SilenceTrimBuildResult(false, "No symmetric silence trim was requested.");

        long sourceLength = new FileInfo(source).Length;
        if (trimAt < 0 || trimAt > sourceLength || trimBytes > sourceLength - trimAt)
            return new SilenceTrimBuildResult(false,
                $"the requested {trimBytes:N0}-byte silence region falls outside the corrected search window; no bytes were removed.");
        if (sourceLength - trimBytes < targetSize)
            return new SilenceTrimBuildResult(false,
                $"removing {trimBytes:N0} bytes would leave a window smaller than the Track 02 target; no bytes were removed.");

        byte[] buffer = ArrayPool<byte>.Shared.Rent(IoBufferSize);
        try
        {
            await using var input = new FileStream(source, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, IoBufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var output = new FileStream(destination, FileMode.Create, FileAccess.Write,
                FileShare.None, IoBufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            await CopyExactlyAsync(input, output, trimAt, buffer, cancellationToken).ConfigureAwait(false);

            long remainingTrim = trimBytes;
            while (remainingTrim > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int want = (int)Math.Min(buffer.Length, remainingTrim);
                int got = await input.ReadAsync(buffer.AsMemory(0, want), cancellationToken).ConfigureAwait(false);
                if (got <= 0)
                    throw new EndOfStreamException("Unexpected end of corrected Track 02 window while checking symmetric PCM silence.");

                if (!IsAllZero(buffer.AsSpan(0, got)))
                {
                    await output.DisposeAsync().ConfigureAwait(false);
                    try { File.Delete(destination); } catch { }
                    return new SilenceTrimBuildResult(false,
                        $"the {trimBytes:N0} byte(s) immediately after the corrected pregap data sector(s) are not all zero PCM silence; no bytes were removed.");
                }

                remainingTrim -= got;
            }

            long remaining = input.Length - input.Position;
            if (remaining > 0)
                await CopyExactlyAsync(input, output, remaining, buffer, cancellationToken).ConfigureAwait(false);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            return new SilenceTrimBuildResult(true, string.Empty);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task<WindowBuildResult> BuildCorrectedSearchWindowAsync(
        string sourceFile,
        string destination,
        SearchWindowPlan plan,
        int pregapSectors,
        CancellationToken cancellationToken)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(IoBufferSize);
        byte[] sector = new byte[SectorSize];
        var correctedRelativeSectors = new List<long>();

        try
        {
            await using var input = new FileStream(sourceFile, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, IoBufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var output = new FileStream(destination, FileMode.Create, FileAccess.Write,
                FileShare.None, IoBufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            long windowEnd = checked(plan.SearchStart + plan.SearchLength);
            long pregapEnd = checked(plan.ScrambleStart + (long)pregapSectors * SectorSize);

            input.Position = plan.SearchStart;
            long current = plan.SearchStart;

            // Copy any bytes before the first complete pregap sector in this window.
            long firstSector = plan.ScrambleStart;
            if (firstSector < current)
            {
                long delta = current - firstSector;
                long sectorsToSkip = (delta + SectorSize - 1) / SectorSize;
                firstSector += sectorsToSkip * SectorSize;
            }

            if (firstSector > current)
            {
                long bytes = Math.Min(firstSector, windowEnd) - current;
                await CopyExactlyAsync(input, output, bytes, buffer, cancellationToken).ConfigureAwait(false);
                current += bytes;
            }

            for (long sectorStart = firstSector;
                 sectorStart + SectorSize <= windowEnd && sectorStart < pregapEnd;
                 sectorStart += SectorSize)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (sectorStart > current)
                {
                    long gap = sectorStart - current;
                    await CopyExactlyAsync(input, output, gap, buffer, cancellationToken).ConfigureAwait(false);
                    current += gap;
                }

                await ReadExactlyAsync(input, sector, cancellationToken).ConfigureAwait(false);
                if (IsEmptyUnscrambledDataSector(sector))
                {
                    ScrambleSectorInPlace(sector);
                    correctedRelativeSectors.Add((sectorStart - plan.ScrambleStart) / SectorSize);
                }

                await output.WriteAsync(sector, cancellationToken).ConfigureAwait(false);
                current += SectorSize;
            }

            if (current < windowEnd)
                await CopyExactlyAsync(input, output, windowEnd - current, buffer, cancellationToken).ConfigureAwait(false);

            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            return new WindowBuildResult(correctedRelativeSectors.Count, correctedRelativeSectors);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task CopyExactlyAsync(
        Stream input,
        Stream output,
        long length,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        long remaining = length;
        while (remaining > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int want = (int)Math.Min(buffer.Length, remaining);
            int got = await input.ReadAsync(buffer.AsMemory(0, want), cancellationToken).ConfigureAwait(false);
            if (got <= 0)
                throw new EndOfStreamException("Unexpected end of source while building the Track 02 pregap search window.");
            await output.WriteAsync(buffer.AsMemory(0, got), cancellationToken).ConfigureAwait(false);
            remaining -= got;
        }
    }

    private static async Task ReadExactlyAsync(Stream input, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int got = await input.ReadAsync(buffer[offset..], cancellationToken).ConfigureAwait(false);
            if (got <= 0)
                throw new EndOfStreamException();
            offset += got;
        }
    }

    private static void AddPlan(
        List<SearchWindowPlan> plans,
        long fileLength,
        long targetSize,
        long pregapBytes,
        long trimAllowanceBytes,
        long searchStart,
        long scrambleStart,
        string reason)
    {
        if (searchStart < 0 || scrambleStart < 0 || searchStart >= fileLength)
            return;

        long wanted;
        try { wanted = checked(targetSize + pregapBytes + trimAllowanceBytes); }
        catch (OverflowException) { return; }

        long available = fileLength - searchStart;
        long length = Math.Min(wanted, available);
        if (length < targetSize)
            return;

        plans.Add(new SearchWindowPlan(searchStart, length, scrambleStart, reason));
    }

    private static string GetOutputPath(string sourcePath, HashTarget target, long offset)
    {
        string directory = Path.GetDirectoryName(Path.GetFullPath(sourcePath)) ?? Directory.GetCurrentDirectory();
        string? fileName = target.OutputFileName;
        if (string.IsNullOrWhiteSpace(fileName))
        {
            string id = target.NormalizedMd5 ?? target.Crc32Hex;
            fileName = $"Match_{offset}_{id}.bin";
        }

        fileName = Path.GetFileName(fileName);
        string outputPath = Path.Combine(directory, fileName);
        if (Path.GetFullPath(outputPath).Equals(Path.GetFullPath(sourcePath), StringComparison.OrdinalIgnoreCase))
            throw new IOException("Refusing to overwrite the FindCRCs source image with the corrected Track 02 output.");
        return outputPath;
    }

    private static bool IsAllZero(ReadOnlySpan<byte> data)
    {
        foreach (byte value in data)
            if (value != 0)
                return false;
        return true;
    }

    private static byte[] BuildScrambleMask()
    {
        var mask = new byte[SectorSize - 12];
        int lfsr = 1;

        for (int i = 0; i < mask.Length; i++)
        {
            byte value = 0;
            for (int bit = 0; bit < 8; bit++)
            {
                value |= (byte)((lfsr & 1) << bit);
                int feedback = (lfsr & 1) ^ ((lfsr >> 1) & 1);
                lfsr = (lfsr >> 1) | (feedback << 14);
            }
            mask[i] = value;
        }

        // ECMA-130 Annex B's sequence starts 01 80 00 60 00 28 00 1E.
        ReadOnlySpan<byte> expected = stackalloc byte[] { 0x01, 0x80, 0x00, 0x60, 0x00, 0x28, 0x00, 0x1E };
        if (!mask.AsSpan(0, expected.Length).SequenceEqual(expected))
            throw new InvalidOperationException("Internal CD-ROM scrambler self-test failed.");

        return mask;
    }

    private static void Report(IProgress<string>? activity, List<string> messages, string message)
    {
        messages.Add(message);
        activity?.Report(message);
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Best-effort cleanup only.
        }
    }

    private sealed record SearchWindowPlan(long SearchStart, long SearchLength, long ScrambleStart, string Reason);
    private sealed record WindowBuildResult(int ScrambledSectors, IReadOnlyList<long> RelativeSectorOffsets);
    private sealed record SilenceTrimBuildResult(bool Created, string Reason);
}
