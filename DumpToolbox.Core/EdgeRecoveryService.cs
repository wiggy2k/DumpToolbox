using System.Buffers;
using System.Security.Cryptography;

namespace DumpToolbox.Core;

public sealed record EdgeRecoveryOutcome(
    IReadOnlyList<SearchResult> Results,
    IReadOnlyList<string> Messages);

/// <summary>
/// Repairs edge-length errors on first/last sequential targets when adjacent
/// successfully matched targets prove the expected boundaries. Missing edge data
/// is reconstructed with digital silence first (then FindEnds); a final-audio
/// overage is only trimmed when the excess bytes are all zero and the target hash
/// verifies.
/// </summary>
public sealed partial class EdgeRecoveryService
{
    /// <summary>
    /// Safe no-CUE inference for the common two-target case. Exactly one of the
    /// two ordinary FindCRCs results must already be verified; the other target
    /// becomes a provisional singleton edge candidate. RepairAudioEdgesAsync
    /// still validates the physical boundaries/extent before changing anything.
    /// </summary>
    public static bool TryInferTwoTargetSingletonCandidate(
        IReadOnlyList<HashTarget> targets,
        IReadOnlyList<SearchResult> results,
        out int targetIndex,
        out string description)
    {
        targetIndex = -1;
        description = string.Empty;

        if (targets.Count != 2 || results.Count != 2)
            return false;

        int foundCount = results.Count(r => r.Found);
        if (foundCount != 1)
            return false;

        int matched = results[0].Found ? 0 : 1;
        int missing = 1 - matched;
        if (results[matched].Offset is not long matchedOffset)
            return false;

        targetIndex = missing;
        description =
            $"exactly two targets were supplied and ordinary FindCRCs verified {TargetDisplayName(targets[matched], matched)} at offset {matchedOffset:N0} (0x{matchedOffset:X}), leaving only {TargetDisplayName(targets[missing], missing)} unmatched.";
        return true;
    }

    private const int BufferSize = 4 * 1024 * 1024;
    private readonly FindEndsService _findEnds = new();
    private readonly HashSearchEngine _hashSearch = new();

    public async Task<EdgeRecoveryOutcome> RepairAsync(
        string sourceFile,
        IReadOnlyList<HashTarget> targets,
        IReadOnlyList<SearchResult> results,
        string outputDirectory,
        bool attemptRepair = true,
        bool savePartialForInspection = false,
        IProgress<string>? activity = null,
        CancellationToken cancellationToken = default,
        bool enableHeadsTails = false,
        string? headsTailsSourceFile = null)
    {
        if (targets.Count != results.Count)
            throw new ArgumentException("Target/result counts do not match.", nameof(results));
        if (targets.Count < 2)
            return new EdgeRecoveryOutcome(results.ToArray(), Array.Empty<string>());
        if (!File.Exists(sourceFile))
            throw new FileNotFoundException("Edge-recovery source file not found.", sourceFile);

        string source = Path.GetFullPath(sourceFile);
        if (enableHeadsTails && (string.IsNullOrWhiteSpace(headsTailsSourceFile) || !File.Exists(headsTailsSourceFile)))
            throw new FileNotFoundException("Heads and Tails mode requires a built AudioHeadsandTails.bin corpus.", headsTailsSourceFile);
        string headsTailsSource = enableHeadsTails ? Path.GetFullPath(headsTailsSourceFile!) : source;
        string outputRoot = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(outputRoot);

        var updated = results.ToArray();
        var messages = new List<string>();
        long sourceLength = new FileInfo(source).Length;

        // First target: target 2 is the anchor. A negative expected start proves
        // that bytes are absent before offset zero rather than merely misplaced.
        if (!updated[0].Found)
        {
            if (updated[1].Found && updated[1].Offset is long nextOffset)
            {
                long expectedStart = nextOffset - targets[0].Size;
                if (attemptRepair && expectedStart < 0 && nextOffset >= 0 && nextOffset < targets[0].Size)
                {
                    updated[0] = await RepairOneAsync(
                        source, sourceLength, targets[0], targetIndex: 0,
                        partialOffset: 0, partialLength: nextOffset,
                        missingMode: FindEndsMode.MissingStart,
                        expectedStart: expectedStart,
                        outputRoot: outputRoot, savePartialOnFailure: true, activity: activity, messages: messages, cancellationToken: cancellationToken).ConfigureAwait(false);

                    if (!updated[0].Found && enableHeadsTails)
                    {
                        SearchResult hailMary = await TryHailMaryUnderdumpedAudioEdgeAsync(
                            source, headsTailsSource, targets[0], 0,
                            partialOffset: 0, partialLength: nextOffset,
                            FindEndsMode.MissingStart, outputRoot,
                            activity, messages, cancellationToken).ConfigureAwait(false);
                        if (hailMary.Found)
                            updated[0] = hailMary;
                    }
                }
                else if (attemptRepair && enableHeadsTails && expectedStart == 0 && nextOffset == targets[0].Size)
                {
                    // Last-resort exact-length edge recovery. The next verified
                    // track proves the first track's complete physical extent; if
                    // its outside edge is zero PCM silence, strip that silence and
                    // exhaustively search the combined audio for the missing prefix,
                    // progressively trading source bytes for guaranteed leading 00s.
                    updated[0] = await TryHailMaryExactSizedAudioEdgeAsync(
                        source, headsTailsSource, targets[0], 0, 0,
                        FindEndsMode.MissingStart, outputRoot,
                        activity, messages, cancellationToken).ConfigureAwait(false);
                }
                else if (attemptRepair)
                {
                    Report(activity, messages, $"EDGE: {TargetDisplayName(targets[0], 0)} is unmatched, but the next matched target does not prove a missing prefix at source offset zero or an exact-length first-track extent.");
                }

                if (!updated[0].Found && savePartialForInspection)
                {
                    long inspectionStart = Math.Max(0, expectedStart);
                    long inspectionEnd = Math.Min(sourceLength, nextOffset);
                    long inspectionLength = Math.Max(0, inspectionEnd - inspectionStart);
                    if (inspectionLength > 0)
                    {
                        updated[0] = await SaveInspectionPartialAsync(
                            source, inspectionStart, Math.Min(inspectionLength, targets[0].Size),
                            targets[0], 0, outputRoot, updated[0],
                            $"first-track extent bounded by verified target 2 start at {nextOffset:N0}",
                            activity, messages, cancellationToken).ConfigureAwait(false);
                    }
                }
            }
            else
            {
                Report(activity, messages, $"EDGE: {TargetDisplayName(targets[0], 0)} is unmatched and target 2 is not matched, so there is no reliable start-edge anchor.");
            }
        }

        // Last target: the previous target is the anchor. If its expected start is
        // inside the source but fewer than target.Size bytes remain, the tail is
        // genuinely under-dumped.
        int last = targets.Count - 1;
        if (!updated[last].Found)
        {
            if (updated[last - 1].Found && updated[last - 1].Offset is long previousOffset)
            {
                long expectedStart = previousOffset + targets[last - 1].Size;
                long available = sourceLength - expectedStart;
                if (attemptRepair && expectedStart >= 0 && expectedStart <= sourceLength && available >= 0 && available < targets[last].Size)
                {
                    updated[last] = await RepairOneAsync(
                        source, sourceLength, targets[last], targetIndex: last,
                        partialOffset: expectedStart, partialLength: available,
                        missingMode: FindEndsMode.MissingEnd,
                        expectedStart: expectedStart,
                        outputRoot: outputRoot, savePartialOnFailure: true, activity: activity, messages: messages, cancellationToken: cancellationToken).ConfigureAwait(false);

                    if (!updated[last].Found && enableHeadsTails)
                    {
                        SearchResult hailMary = await TryHailMaryUnderdumpedAudioEdgeAsync(
                            source, headsTailsSource, targets[last], last,
                            partialOffset: expectedStart, partialLength: available,
                            FindEndsMode.MissingEnd, outputRoot,
                            activity, messages, cancellationToken).ConfigureAwait(false);
                        if (hailMary.Found)
                            updated[last] = hailMary;
                    }
                }
                else if (attemptRepair && enableHeadsTails && expectedStart >= 0 && available == targets[last].Size)
                {
                    // Last-resort exact-length edge recovery. The previous verified
                    // track proves the final track start and EOF proves its end; if
                    // the outside edge is zero PCM silence, strip it and exhaustively
                    // search the combined audio for the missing suffix, progressively
                    // trading source bytes for guaranteed trailing 00s.
                    updated[last] = await TryHailMaryExactSizedAudioEdgeAsync(
                        source, headsTailsSource, targets[last], last, expectedStart,
                        FindEndsMode.MissingEnd, outputRoot,
                        activity, messages, cancellationToken).ConfigureAwait(false);
                }
                else if (attemptRepair)
                {
                    Report(activity, messages, $"EDGE: {TargetDisplayName(targets[last], last)} is unmatched, but the previous matched target does not prove a truncated suffix at EOF or an exact-length final-track extent.");
                }

                if (!updated[last].Found && savePartialForInspection && expectedStart >= 0 && expectedStart < sourceLength)
                {
                    long inspectionLength = Math.Min(Math.Max(0, available), targets[last].Size);
                    if (inspectionLength > 0)
                    {
                        updated[last] = await SaveInspectionPartialAsync(
                            source, expectedStart, inspectionLength,
                            targets[last], last, outputRoot, updated[last],
                            $"final-track extent bounded by verified previous-track end at {expectedStart:N0} and source EOF at {sourceLength:N0}",
                            activity, messages, cancellationToken).ConfigureAwait(false);
                    }
                }
            }
            else
            {
                Report(activity, messages, $"EDGE: {TargetDisplayName(targets[last], last)} is unmatched and the previous target is not matched, so there is no reliable end-edge anchor.");
            }
        }

        return new EdgeRecoveryOutcome(updated, messages);
    }


    /// <summary>
    /// CUE-aware FindCRCs audio repair. Extreme audio targets retain the
    /// first/last edge logic. Internal unmatched audio targets are also handled
    /// when both immediate neighbouring targets are hash-verified, giving a safe
    /// bounded source extent for silence shifting, zero-padding and inspection.
    /// </summary>
    public async Task<EdgeRecoveryOutcome> RepairAudioEdgesAsync(
        string sourceFile,
        IReadOnlyList<HashTarget> targets,
        IReadOnlyList<SearchResult> results,
        IReadOnlyList<int> audioTargetIndices,
        string outputDirectory,
        bool attemptRepair = true,
        bool savePartialForInspection = false,
        bool preferNextAudioAnchorForFirstAudio = false,
        IProgress<string>? activity = null,
        CancellationToken cancellationToken = default)
    {
        if (targets.Count != results.Count)
            throw new ArgumentException("Target/result counts do not match.", nameof(results));
        if (!File.Exists(sourceFile))
            throw new FileNotFoundException("Edge-recovery source file not found.", sourceFile);

        int[] audio = audioTargetIndices
            .Distinct()
            .OrderBy(i => i)
            .ToArray();
        if (audio.Length == 0)
            return new EdgeRecoveryOutcome(results.ToArray(), Array.Empty<string>());
        if (audio.Any(i => i < 0 || i >= targets.Count))
            throw new ArgumentOutOfRangeException(nameof(audioTargetIndices), "An audio target index is outside the target list.");

        string source = Path.GetFullPath(sourceFile);
        string outputRoot = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(outputRoot);

        var updated = results.ToArray();
        var messages = new List<string>();
        long sourceLength = new FileInfo(source).Length;

        if (audio.Length == 1)
        {
            int only = audio[0];
            bool hasExtent = TryGetSingleAudioExtent(
                sourceLength, targets, updated, only,
                out long extentStart, out long extentLength, out string extentDescription);

            if (!updated[only].Found && attemptRepair)
            {
                if (hasExtent && extentLength == targets[only].Size)
                {
                    Report(activity, messages,
                        $"EDGE: {TargetDisplayName(targets[only], only)} is the only mapped audio track, but {extentDescription} establishes an exact target-sized extent. Testing signed zero-silence shifts on both ends.");

                    SearchResult shifted = await TryRepairSingleAudioSilenceShiftAsync(
                        source, targets[only], only, extentStart, outputRoot,
                        activity, messages, cancellationToken).ConfigureAwait(false);
                    updated[only] = shifted;

                    if (shifted.Found)
                        return new EdgeRecoveryOutcome(updated, messages);
                }
                else if (hasExtent && extentLength > 0 && extentLength < targets[only].Size)
                {
                    long shortfall = targets[only].Size - extentLength;
                    Report(activity, messages,
                        $"EDGE: {TargetDisplayName(targets[only], only)} is the only mapped audio track; {extentDescription} establishes {extentLength:N0} available byte(s), while the target expects {targets[only].Size:N0}. The extent is SHORT by {shortfall:N0} byte(s); testing every zero-silence padding split between the start and end.");

                    SearchResult padded = await TryRepairShortSingleAudioZeroPaddingAsync(
                        source, targets[only], only, extentStart, extentLength, outputRoot,
                        activity, messages, cancellationToken).ConfigureAwait(false);
                    updated[only] = padded;

                    if (padded.Found)
                        return new EdgeRecoveryOutcome(updated, messages);
                }
                else if (hasExtent)
                {
                    Report(activity, messages,
                        $"EDGE: {TargetDisplayName(targets[only], only)} is the only mapped audio track; {extentDescription} establishes {extentLength:N0} available byte(s), while the target expects {targets[only].Size:N0}. The singleton zero-silence repair currently requires an exact-sized or short extent.");
                }
                else
                {
                    Report(activity, messages,
                        $"EDGE: {TargetDisplayName(targets[only], only)} is the only mapped audio track; there is no safe pair of physical boundaries from adjacent matched data/source edges for a signed silence-shift test.");
                }
            }

            if (!updated[only].Found && savePartialForInspection && hasExtent && extentLength > 0)
            {
                updated[only] = await SaveInspectionPartialAsync(
                    source, extentStart, Math.Min(extentLength, targets[only].Size), targets[only], only, outputRoot, updated[only],
                    $"singleton audio extent derived from {extentDescription}",
                    activity, messages, cancellationToken).ConfigureAwait(false);
            }

            if (!updated[only].Found && attemptRepair)
            {
                Report(activity, messages,
                    $"EDGE: {TargetDisplayName(targets[only], only)} remains unmatched after singleton-audio handling; without another audio-track anchor no non-zero missing audio is inferred.");
            }
            return new EdgeRecoveryOutcome(updated, messages);
        }

        // Internal AUDIO tracks can be repaired safely when both immediate
        // neighbouring targets have already been hash-verified.  Those two
        // matches give us an unambiguous physical source extent for the
        // unmatched track.  Treat that bounded extent with the same
        // silence-only recovery rules used by singleton/extreme tracks rather
        // than restricting edge recovery to the first and last AUDIO target.
        for (int audioPosition = 1; audioPosition < audio.Length - 1; audioPosition++)
        {
            int current = audio[audioPosition];
            if (updated[current].Found)
                continue;

            int previous = current - 1;
            int next = current + 1;
            if (previous < 0 || next >= targets.Count ||
                !updated[previous].Found || updated[previous].Offset is not long previousOffset ||
                !updated[next].Found || updated[next].Offset is not long nextOffset)
            {
                if (savePartialForInspection)
                {
                    Report(activity, messages,
                        $"EDGE: {TargetDisplayName(targets[current], current)} is an internal audio target, but both immediate neighbouring targets are not matched; no safe bounded partial/recovery extent can be established.");
                }
                continue;
            }

            long extentStart = checked(previousOffset + targets[previous].Size);
            long extentEnd = nextOffset;
            if (extentStart < 0 || extentEnd < extentStart || extentEnd > sourceLength)
            {
                Report(activity, messages,
                    $"EDGE: {TargetDisplayName(targets[current], current)} has matched neighbours, but their offsets do not establish a valid bounded source extent.");
                continue;
            }

            long extentLength = extentEnd - extentStart;
            string bounds =
                $"matched {TargetDisplayName(targets[previous], previous)} end at {extentStart:N0} to matched {TargetDisplayName(targets[next], next)} start at {extentEnd:N0}";

            Report(activity, messages,
                $"EDGE: internal audio target {TargetDisplayName(targets[current], current)} is bounded by {bounds}; available extent is {extentLength:N0} byte(s), target expects {targets[current].Size:N0}.");

            if (attemptRepair)
            {
                if (extentLength == targets[current].Size)
                {
                    Report(activity, messages,
                        $"EDGE: {TargetDisplayName(targets[current], current)} has an exact-sized bounded extent; testing signed zero-silence shifts on both ends.");
                    updated[current] = await TryRepairSingleAudioSilenceShiftAsync(
                        source, targets[current], current, extentStart, outputRoot,
                        activity, messages, cancellationToken).ConfigureAwait(false);
                }
                else if (extentLength > 0 && extentLength < targets[current].Size)
                {
                    long shortfall = targets[current].Size - extentLength;
                    Report(activity, messages,
                        $"EDGE: {TargetDisplayName(targets[current], current)} is SHORT by {shortfall:N0} byte(s) between its verified neighbours; testing every zero-padding split between the start and end, then safe signed silence shifts.");
                    updated[current] = await TryRepairShortSingleAudioZeroPaddingAsync(
                        source, targets[current], current, extentStart, extentLength, outputRoot,
                        activity, messages, cancellationToken).ConfigureAwait(false);
                }
                else if (extentLength > targets[current].Size)
                {
                    long overage = extentLength - targets[current].Size;
                    Report(activity, messages,
                        $"EDGE: {TargetDisplayName(targets[current], current)} has {overage:N0} extra byte(s) between its verified neighbours. Ordinary FindCRCs has already tested every target-sized source window, so no destructive trim is accepted without a hash match.");
                }
            }

            if (!updated[current].Found && savePartialForInspection && extentLength > 0)
            {
                // When the bounded region is at least target-sized, save the
                // forward interpretation.  If it is short, preserve the whole
                // bounded region and additionally save target-sized forward and
                // backward hypotheses when the surrounding source permits it.
                if (extentLength >= targets[current].Size)
                {
                    updated[current] = await SaveInspectionPartialAsync(
                        source, extentStart, targets[current].Size, targets[current], current, outputRoot, updated[current],
                        $"internal audio extent bounded by {bounds}",
                        activity, messages, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    updated[current] = await SaveInspectionPartialAsync(
                        source, extentStart, extentLength, targets[current], current, outputRoot, updated[current],
                        $"short internal audio extent bounded by {bounds}",
                        activity, messages, cancellationToken).ConfigureAwait(false);

                    bool forwardSaved = await SaveInspectionPartialVariantAsync(
                        source, extentStart, targets[current], current, outputRoot, "forward",
                        $"starts immediately after matched {TargetDisplayName(targets[previous], previous)}",
                        activity, messages, cancellationToken).ConfigureAwait(false);
                    long backwardStart = checked(extentEnd - targets[current].Size);
                    bool backwardSaved = await SaveInspectionPartialVariantAsync(
                        source, backwardStart, targets[current], current, outputRoot, "backward",
                        $"ends immediately before matched {TargetDisplayName(targets[next], next)}",
                        activity, messages, cancellationToken).ConfigureAwait(false);
                    if (forwardSaved && backwardSaved)
                    {
                        Report(activity, messages,
                            $"EDGE PARTIAL: both neighbour-anchored target-sized hypotheses were saved for {TargetDisplayName(targets[current], current)} as .forward.partial and .backward.partial.");
                    }
                }
            }
        }

        int first = audio[0];
        int nextAudio = audio[1];
        if (!updated[first].Found)
        {
            if (nextAudio != first + 1)
            {
                Report(activity, messages,
                    $"EDGE: {TargetDisplayName(targets[first], first)} is the first audio target, but the next audio track is not adjacent in the target sequence; prefix repair is skipped rather than crossing intervening data tracks.");
            }
            else if (updated[nextAudio].Found && updated[nextAudio].Offset is long nextAudioOffset)
            {
                long? partialStart = null;
                string boundaryDescription = "no earlier source boundary can be established safely";
                bool dualAnchorPartialsSaved = false;

                // The next AUDIO track is the authoritative end anchor for the
                // first AUDIO track. The preceding target does not itself have
                // to hash-match merely to establish Track 02's lower boundary.
                if (first == 0)
                {
                    partialStart = 0;
                    boundaryDescription = "source offset zero";
                }
                else if (updated[first - 1].Found && updated[first - 1].Offset is long previousOffset)
                {
                    partialStart = checked(previousOffset + targets[first - 1].Size);
                    boundaryDescription = $"end of matched {TargetDisplayName(targets[first - 1], first - 1)}";
                }
                else
                {
                    // If an earlier target is matched, project its verified
                    // location forward through the known target sizes.
                    for (int anchor = first - 2; anchor >= 0 && partialStart is null; anchor--)
                    {
                        if (!updated[anchor].Found || updated[anchor].Offset is not long anchorOffset)
                            continue;

                        long projected = anchorOffset;
                        for (int i = anchor; i < first; i++)
                            projected = checked(projected + targets[i].Size);

                        partialStart = projected;
                        boundaryDescription =
                            $"boundary projected forward from matched {TargetDisplayName(targets[anchor], anchor)} through the known intervening target sizes";
                    }

                    // Common mixed-mode layout: Track 01 is the first target and
                    // the source image begins at offset zero. Even if Track 01's
                    // contents do not hash-match, its known length still gives
                    // the physical lower boundary from which Track 02 extends
                    // backwards from the verified Track 03 start.
                    if (partialStart is null && first == 1)
                    {
                        partialStart = targets[0].Size;
                        boundaryDescription =
                            $"source offset zero plus the known size of {TargetDisplayName(targets[0], 0)} (Track 01 does not need to hash-match)";
                    }
                    else if (partialStart is null)
                    {
                        boundaryDescription = "no earlier source boundary can be established safely";
                    }
                }

                // For manual inspection, when both immediate anchors exist save
                // BOTH target-sized hypotheses before attempting repair:
                //   forward  = target.Size bytes beginning at the end of Track 01
                //   backward = target.Size bytes ending at the start of Track 03
                // These deliberately overlap the adjacent matched track when the
                // gap between anchors is shorter than the target; that is the point
                // of comparing the two possible boundary interpretations.
                if (savePartialForInspection && first > 0 &&
                    updated[first - 1].Found && updated[first - 1].Offset is long partialForwardAnchorOffset)
                {
                    long forwardInspectionStart = checked(partialForwardAnchorOffset + targets[first - 1].Size);
                    long backwardInspectionStart = checked(nextAudioOffset - targets[first].Size);

                    bool forwardSaved = await SaveInspectionPartialVariantAsync(
                        source, forwardInspectionStart, targets[first], first, outputRoot, "forward",
                        $"target-sized forward hypothesis beginning immediately after matched {TargetDisplayName(targets[first - 1], first - 1)}",
                        activity, messages, cancellationToken).ConfigureAwait(false);

                    bool backwardSaved = await SaveInspectionPartialVariantAsync(
                        source, backwardInspectionStart, targets[first], first, outputRoot, "backward",
                        $"target-sized backward hypothesis ending immediately before matched {TargetDisplayName(targets[nextAudio], nextAudio)}",
                        activity, messages, cancellationToken).ConfigureAwait(false);

                    dualAnchorPartialsSaved = forwardSaved && backwardSaved;
                    if (dualAnchorPartialsSaved)
                    {
                        Report(activity, messages,
                            $"EDGE PARTIAL: both anchor hypotheses were saved for {TargetDisplayName(targets[first], first)} as .forward.partial and .backward.partial.");
                    }
                }

                // When the immediately preceding target is matched, try the
                // forward interpretation first: the first audio track starts
                // exactly where that target ends and, if the region before the
                // next matched audio track is short, the missing bytes are at
                // the END of the first audio track.  Only if that hypothesis
                // fails verification do we fall back to the historical
                // backwards-from-next-audio interpretation (missing START).
                if (!preferNextAudioAnchorForFirstAudio && attemptRepair && first > 0 &&
                    updated[first - 1].Found && updated[first - 1].Offset is long forwardAnchorOffset)
                {
                    long forwardStart = checked(forwardAnchorOffset + targets[first - 1].Size);
                    long forwardAvailable = checked(nextAudioOffset - forwardStart);
                    if (forwardStart >= 0 && forwardStart <= nextAudioOffset &&
                        nextAudioOffset <= sourceLength &&
                        forwardAvailable >= 0 && forwardAvailable < targets[first].Size)
                    {
                        Report(activity, messages,
                            $"EDGE: first audio target {TargetDisplayName(targets[first], first)} will first be tested forwards from matched {TargetDisplayName(targets[first - 1], first - 1)} at source offset {forwardStart:N0}; matched {TargetDisplayName(targets[nextAudio], nextAudio)} leaves {forwardAvailable:N0} byte(s) available, {targets[first].Size - forwardAvailable:N0} byte(s) short of the target size.");

                        SearchResult forwardAttempt = await RepairOneAsync(
                            source, sourceLength, targets[first], targetIndex: first,
                            partialOffset: forwardStart, partialLength: forwardAvailable,
                            missingMode: FindEndsMode.MissingEnd,
                            expectedStart: forwardStart,
                            outputRoot: outputRoot, savePartialOnFailure: false,
                            activity: activity, messages: messages,
                            cancellationToken: cancellationToken).ConfigureAwait(false);

                        if (forwardAttempt.Found)
                        {
                            updated[first] = forwardAttempt;
                        }
                        else
                        {
                            Report(activity, messages,
                                $"EDGE: the forward {TargetDisplayName(targets[first - 1], first - 1)}-anchored interpretation did not verify for {TargetDisplayName(targets[first], first)}; falling back to the backwards-from-{TargetDisplayName(targets[nextAudio], nextAudio)} interpretation.");
                        }
                    }
                }

                if (!updated[first].Found && partialStart is long actualStart)
                {
                    long expectedStart = checked(nextAudioOffset - targets[first].Size);
                    long partialLength = checked(nextAudioOffset - actualStart);
                    if (expectedStart < actualStart &&
                        partialLength >= 0 && partialLength < targets[first].Size &&
                        actualStart >= 0 && actualStart <= nextAudioOffset &&
                        nextAudioOffset <= sourceLength)
                    {
                        Report(activity, messages,
                            $"EDGE: first audio target {TargetDisplayName(targets[first], first)} is worked backwards from matched {TargetDisplayName(targets[nextAudio], nextAudio)} at offset {nextAudioOffset:N0}; {boundaryDescription} establishes the available-data start.");
                        if (attemptRepair)
                        {
                            updated[first] = await RepairOneAsync(
                                source, sourceLength, targets[first], targetIndex: first,
                                partialOffset: actualStart, partialLength: partialLength,
                                missingMode: FindEndsMode.MissingStart,
                                expectedStart: expectedStart,
                                outputRoot: outputRoot, savePartialOnFailure: savePartialForInspection && !dualAnchorPartialsSaved,
                                activity: activity, messages: messages,
                                cancellationToken: cancellationToken).ConfigureAwait(false);
                        }
                        else if (savePartialForInspection && !dualAnchorPartialsSaved)
                        {
                            updated[first] = await SaveInspectionPartialAsync(
                                source, expectedStart, targets[first].Size, targets[first], first, outputRoot, updated[first],
                                $"target-sized window immediately before matched {TargetDisplayName(targets[nextAudio], nextAudio)}",
                                activity, messages, cancellationToken).ConfigureAwait(false);
                        }
                    }
                    else if (expectedStart == actualStart &&
                             partialLength == targets[first].Size)
                    {
                        Report(activity, messages,
                            $"EDGE: {TargetDisplayName(targets[first], first)} is bounded exactly by matched {TargetDisplayName(targets[nextAudio], nextAudio)} and {boundaryDescription}, but the full target-sized region does not hash-match; this is not a missing-start length error.");
                        if (savePartialForInspection && !dualAnchorPartialsSaved)
                        {
                            updated[first] = await SaveInspectionPartialAsync(
                                source, expectedStart, targets[first].Size, targets[first], first, outputRoot, updated[first],
                                $"target-sized window immediately before matched {TargetDisplayName(targets[nextAudio], nextAudio)}",
                                activity, messages, cancellationToken).ConfigureAwait(false);
                        }
                    }
                    else
                    {
                        Report(activity, messages,
                            $"EDGE: {TargetDisplayName(targets[first], first)} is worked backwards from matched {TargetDisplayName(targets[nextAudio], nextAudio)}, but the resulting geometry does not prove a truncated audio prefix.");
                        if (savePartialForInspection && !dualAnchorPartialsSaved &&
                            actualStart >= 0 && actualStart <= nextAudioOffset && nextAudioOffset <= sourceLength && partialLength > 0)
                        {
                            updated[first] = await SaveInspectionPartialAsync(
                                source, expectedStart, targets[first].Size, targets[first], first, outputRoot, updated[first],
                                $"target-sized window immediately before matched {TargetDisplayName(targets[nextAudio], nextAudio)}",
                                activity, messages, cancellationToken).ConfigureAwait(false);
                        }
                    }
                }
                else
                {
                    Report(activity, messages,
                        $"EDGE: {TargetDisplayName(targets[first], first)} can use matched {TargetDisplayName(targets[nextAudio], nextAudio)} as its end anchor, but {boundaryDescription}; a safe partial start cannot be established.");
                }

                // Mixed-mode Track 02: a verified Track 03 is the primary anchor.
                // Only if the backwards-from-Track-03 interpretation fails do
                // we try the weaker forward interpretation from Track 01.
                if (preferNextAudioAnchorForFirstAudio && !updated[first].Found && attemptRepair && first > 0 &&
                    updated[first - 1].Found && updated[first - 1].Offset is long fallbackForwardAnchorOffset)
                {
                    long forwardStart = checked(fallbackForwardAnchorOffset + targets[first - 1].Size);
                    long forwardAvailable = checked(nextAudioOffset - forwardStart);
                    if (forwardStart >= 0 && forwardStart <= nextAudioOffset &&
                        nextAudioOffset <= sourceLength &&
                        forwardAvailable >= 0 && forwardAvailable < targets[first].Size)
                    {
                        Report(activity, messages,
                            $"EDGE: the primary backwards-from-{TargetDisplayName(targets[nextAudio], nextAudio)} interpretation did not verify for {TargetDisplayName(targets[first], first)}; falling back to the forward {TargetDisplayName(targets[first - 1], first - 1)}-anchored interpretation at source offset {forwardStart:N0}.");

                        SearchResult forwardAttempt = await RepairOneAsync(
                            source, sourceLength, targets[first], targetIndex: first,
                            partialOffset: forwardStart, partialLength: forwardAvailable,
                            missingMode: FindEndsMode.MissingEnd,
                            expectedStart: forwardStart,
                            outputRoot: outputRoot, savePartialOnFailure: false,
                            activity: activity, messages: messages,
                            cancellationToken: cancellationToken).ConfigureAwait(false);

                        if (forwardAttempt.Found)
                            updated[first] = forwardAttempt;
                    }
                }
            }
            else
            {
                Report(activity, messages,
                    $"EDGE: {TargetDisplayName(targets[first], first)} is the first audio target, but the next audio target is not matched, so there is no reliable forward anchor.");
            }
        }

        int last = audio[^1];
        int previousAudio = audio[^2];
        if (!updated[last].Found)
        {
            if (previousAudio != last - 1)
            {
                Report(activity, messages,
                    $"EDGE: {TargetDisplayName(targets[last], last)} is the last audio target, but the previous audio track is not adjacent in the target sequence; suffix repair is skipped rather than crossing intervening data tracks.");
            }
            else if (updated[previousAudio].Found && updated[previousAudio].Offset is long previousAudioOffset)
            {
                long expectedStart = previousAudioOffset + targets[previousAudio].Size;
                long? actualEnd = null;
                string boundaryDescription;
                if (last == targets.Count - 1)
                {
                    actualEnd = sourceLength;
                    boundaryDescription = "source EOF";
                }
                else if (updated[last + 1].Found && updated[last + 1].Offset is long followingOffset)
                {
                    actualEnd = followingOffset;
                    boundaryDescription = $"start of {TargetDisplayName(targets[last + 1], last + 1)}";
                }
                else
                {
                    boundaryDescription = "the following target is not matched";
                }

                if (actualEnd is long end)
                {
                    long available = end - expectedStart;
                    if (expectedStart >= 0 && expectedStart <= sourceLength &&
                        end >= expectedStart && end <= sourceLength && available >= 0)
                    {
                        if (available < targets[last].Size)
                        {
                            Report(activity, messages,
                                $"EDGE: last audio target {TargetDisplayName(targets[last], last)} uses {TargetDisplayName(targets[previousAudio], previousAudio)} as its backward anchor and {boundaryDescription} as the available-data boundary.");
                            if (attemptRepair)
                            {
                                updated[last] = await RepairOneAsync(
                                    source, sourceLength, targets[last], targetIndex: last,
                                    partialOffset: expectedStart, partialLength: available,
                                    missingMode: FindEndsMode.MissingEnd,
                                    expectedStart: expectedStart,
                                    outputRoot: outputRoot, savePartialOnFailure: savePartialForInspection,
                                    activity: activity, messages: messages,
                                    cancellationToken: cancellationToken).ConfigureAwait(false);
                            }
                            else if (savePartialForInspection)
                            {
                                updated[last] = await SaveInspectionPartialAsync(
                                    source, expectedStart, targets[last].Size, targets[last], last, outputRoot, updated[last],
                                    $"bounded by matched {TargetDisplayName(targets[previousAudio], previousAudio)} and {boundaryDescription}",
                                    activity, messages, cancellationToken).ConfigureAwait(false);
                            }
                        }
                        else if (available > targets[last].Size)
                        {
                            long extra = available - targets[last].Size;
                            long extraStart = checked(expectedStart + targets[last].Size);
                            if (attemptRepair && IsRangeAllZero(source, extraStart, extra, cancellationToken))
                            {
                                Report(activity, messages,
                                    $"EDGE: last audio target {TargetDisplayName(targets[last], last)} has {extra:N0} verified trailing zero byte(s) before {boundaryDescription}; testing the opposite-polarity audio shift by removing that excess silence.");
                                updated[last] = await TryRepairTrailingSilenceOverageAsync(
                                    source, targets[last], last, expectedStart, extra,
                                    outputRoot, activity, messages, cancellationToken).ConfigureAwait(false);
                            }
                            else if (attemptRepair)
                            {
                                Report(activity, messages,
                                    $"EDGE: {TargetDisplayName(targets[last], last)} has {extra:N0} extra byte(s) before {boundaryDescription}, but they are not all zero PCM silence; no over-dump correction is attempted.");
                            }

                            if (!updated[last].Found && savePartialForInspection)
                            {
                                updated[last] = await SaveInspectionPartialAsync(
                                    source, expectedStart, targets[last].Size, targets[last], last, outputRoot, updated[last],
                                    $"bounded by matched {TargetDisplayName(targets[previousAudio], previousAudio)} and {boundaryDescription}",
                                    activity, messages, cancellationToken).ConfigureAwait(false);
                            }
                        }
                        else
                        {
                            Report(activity, messages,
                                $"EDGE: {TargetDisplayName(targets[last], last)} has exactly the target-sized amount of source data but did not hash-match; this is not an edge-length problem.");
                            if (savePartialForInspection)
                            {
                                updated[last] = await SaveInspectionPartialAsync(
                                    source, expectedStart, targets[last].Size, targets[last], last, outputRoot, updated[last],
                                    $"bounded by matched {TargetDisplayName(targets[previousAudio], previousAudio)} and {boundaryDescription}",
                                    activity, messages, cancellationToken).ConfigureAwait(false);
                            }
                        }
                    }
                    else
                    {
                        Report(activity, messages,
                            $"EDGE: {TargetDisplayName(targets[last], last)} is the last audio target, but its adjacent anchors do not establish a safe final-audio boundary.");
                    }
                }
                else
                {
                    Report(activity, messages,
                        $"EDGE: {TargetDisplayName(targets[last], last)} is the last audio target, but {boundaryDescription}; a safe partial end cannot be established.");
                }
            }
            else
            {
                Report(activity, messages,
                    $"EDGE: {TargetDisplayName(targets[last], last)} is the last audio target, but the previous audio target is not matched, so there is no reliable backward anchor.");
            }
        }

        return new EdgeRecoveryOutcome(updated, messages);
    }

}
