using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DumpToolbox.Core;

public sealed partial class SkeletonResurrectionService
{
    public Task<SkeletonResurrectionResult> ResurrectAsync(
        SkeletonInspectionResult inspection,
        IReadOnlyDictionary<string, SkeletonSourceMatch> matches,
        string outputPath,
        bool allowMissing,
        IProgress<SkeletonResurrectionProgress>? progress = null,
        IProgress<string>? activity = null,
        CancellationToken cancellationToken = default,
        Func<EofSlackAmbiguityRequest, CancellationToken, Task<EofSlackAmbiguityDecision>>? eofSlackAmbiguityResolver = null)
    {
        // Resurrection is deliberately performed on a worker thread.  The hot path uses
        // large synchronous sequential reads/writes and in-memory sector patching; doing
        // tiny awaited 2 KiB operations per sector is dramatically slower on both Windows
        // and Linux.  Progress<T> created by the UI still marshals reports back to the UI.
        return Task.Run(
            () => ResurrectSequential(
                inspection,
                matches,
                outputPath,
                allowMissing,
                progress,
                activity,
                cancellationToken,
                eofSlackAmbiguityResolver),
            cancellationToken);
    }

    private static SkeletonResurrectionResult ResurrectSequential(
        SkeletonInspectionResult inspection,
        IReadOnlyDictionary<string, SkeletonSourceMatch> matches,
        string outputPath,
        bool allowMissing,
        IProgress<SkeletonResurrectionProgress>? progress,
        IProgress<string>? activity,
        CancellationToken cancellationToken,
        Func<EofSlackAmbiguityRequest, CancellationToken, Task<EofSlackAmbiguityDecision>>? eofSlackAmbiguityResolver)
    {
        if (string.IsNullOrWhiteSpace(outputPath))
            throw new ArgumentException("Choose an output filename.", nameof(outputPath));

        string output = Path.GetFullPath(outputPath);
        string skeleton = Path.GetFullPath(inspection.SkeletonPath);
        EnsureDifferentPaths(skeleton, output);
        string partial = output + ".partial";
        EnsureDifferentPaths(skeleton, partial);

        string? destinationDirectory = Path.GetDirectoryName(output);
        if (!string.IsNullOrWhiteSpace(destinationDirectory))
            Directory.CreateDirectory(destinationDirectory);

        int missing = CountMissingRequired(inspection, matches);
        if (missing > 0 && !allowMissing)
            throw new InvalidOperationException($"{missing:N0} required hash entr{(missing == 1 ? "y is" : "ies are")} still missing. Enable partial resurrection to leave missing areas zeroed.");

        TryDelete(partial);
        long skeletonLength = new FileInfo(skeleton).Length;

        RedumperDatTarget? expectedDatTarget = null;
        if (inspection.SourceKind == SkeletonSourceKind.Redumper)
        {
            expectedDatTarget = TryResolveExpectedRedumperDatTarget(skeleton, skeletonLength, activity, cancellationToken);
        }

        activity?.Report(
            inspection.ImageKind == SkeletonImageKind.Raw2352
                ? "Using fast one-pass raw resurrection: skeleton is read sequentially, payloads are inserted in memory, and each output block is written once."
                : "Using fast one-pass cooked resurrection: matching source extents are streamed directly into the output while the remaining skeleton is copied sequentially.");

        try
        {
            int restored = inspection.ImageKind == SkeletonImageKind.Raw2352
                ? ResurrectRawSequential(
                    inspection,
                    matches,
                    skeleton,
                    partial,
                    skeletonLength,
                    progress,
                    activity,
                    cancellationToken)
                : ResurrectCookedSequential(
                    inspection,
                    matches,
                    skeleton,
                    partial,
                    skeletonLength,
                    progress,
                    activity,
                    cancellationToken);

            // Reproduce deterministic post-EOF residue using external mastering rules
            // in the DICSimulator oracle corpus for narrowly identified Easy CD Creator /
            // Roxio mastering environments. This is intentionally a post-pass: every
            // ordinary source payload has already been restored, so LBA-delta donors are
            // read from the reconstructed image itself. Both SkeleTool and the built-in
            // DIC path pass through this shared resurrection service.
            // Restore ISOCD/Pantaray trademark sectors declared by the PVD FS/TM
            // Application Use record. These bytes are outside ordinary filesystem
            // files, so both DIC and SkeleTool need this shared post-pass.
            ApplyIsoCdTrademarkPayload(inspection, partial, activity, cancellationToken);

            ApplyMasteringEofResidue(
                inspection,
                matches,
                partial,
                activity,
                cancellationToken,
                expectedDatTarget,
                eofSlackAmbiguityResolver);

            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(output))
                File.Delete(output);
            File.Move(partial, output);

            if (expectedDatTarget is not null)
                VerifyRedumperDatTarget(output, expectedDatTarget, activity, cancellationToken);

            progress?.Report(new SkeletonResurrectionProgress(
                SkeletonResurrectionEventKind.Complete,
                skeletonLength,
                skeletonLength,
                "Complete"));

            return new SkeletonResurrectionResult(output, restored, missing, new FileInfo(output).Length);
        }
        catch
        {
            TryDelete(partial);
            throw;
        }
    }

    private static RedumperDatTarget? TryResolveExpectedRedumperDatTarget(
        string skeletonPath,
        long skeletonLength,
        IProgress<string>? activity,
        CancellationToken cancellationToken)
    {
        string? directory = Path.GetDirectoryName(skeletonPath);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            return null;

        string skeletonStem = Path.GetFileNameWithoutExtension(skeletonPath);
        string exactLog = Path.Combine(directory, skeletonStem + ".log");
        var logs = new List<string>();
        if (File.Exists(exactLog))
            logs.Add(exactLog);

        foreach (string candidate in Directory.EnumerateFiles(directory, "*.log", SearchOption.TopDirectoryOnly)
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!logs.Any(existing => string.Equals(existing, candidate, StringComparison.OrdinalIgnoreCase)))
                logs.Add(candidate);
        }

        if (logs.Count == 0)
        {
            activity?.Report("Redumper DAT verification: no .log file found beside the skeleton; final image hashes will not be checked against the dump DAT.");
            return null;
        }

        var allTargets = new List<RedumperDatTarget>();
        foreach (string log in logs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string text;
            try
            {
                text = File.ReadAllText(log);
            }
            catch (Exception ex)
            {
                activity?.Report($"Redumper DAT verification: could not read '{Path.GetFileName(log)}': {ex.Message}");
                continue;
            }

            foreach (Match match in RedumperDatRomRegex.Matches(text))
            {
                if (!long.TryParse(match.Groups["size"].Value, out long size) || size < 0)
                    continue;

                allTargets.Add(new RedumperDatTarget(
                    log,
                    match.Groups["name"].Value,
                    size,
                    match.Groups["crc"].Value.ToLowerInvariant(),
                    match.Groups["md5"].Value.ToLowerInvariant(),
                    match.Groups["sha1"].Value.ToLowerInvariant()));
            }
        }

        if (allTargets.Count == 0)
        {
            activity?.Report("Redumper DAT verification: .log file(s) were found beside the skeleton, but no DAT <rom> hash entries were found.");
            return null;
        }

        RedumperDatTarget[] sameSize = allTargets
            .Where(target => target.Size == skeletonLength)
            .ToArray();

        if (sameSize.Length == 0)
        {
            string sizes = string.Join(", ", allTargets.Select(t => t.Size).Distinct().OrderBy(v => v).Take(8).Select(v => v.ToString("N0")));
            activity?.Report(
                $"Redumper DAT verification: {allTargets.Count:N0} DAT entr{(allTargets.Count == 1 ? "y was" : "ies were")} found, " +
                $"but none has the skeleton size {skeletonLength:N0} bytes" +
                (string.IsNullOrWhiteSpace(sizes) ? "." : $" (available size(s): {sizes})."));
            return null;
        }

        RedumperDatTarget? selected = null;
        if (sameSize.Length == 1)
        {
            selected = sameSize[0];
        }
        else
        {
            string normalizedSkeleton = NormalizeDatBasename(skeletonStem);
            RedumperDatTarget[] nameMatches = sameSize
                .Where(target => string.Equals(NormalizeDatBasename(Path.GetFileNameWithoutExtension(target.Name)), normalizedSkeleton, StringComparison.OrdinalIgnoreCase))
                .ToArray();

            if (nameMatches.Length == 1)
                selected = nameMatches[0];
            else
            {
                // Track skeletons are commonly named "Disc (Track N).skeleton" while the
                // DAT entry is "Disc (Track N).bin". If exact normalization did not settle
                // it, accept a unique mutual-prefix filename match, but never guess.
                RedumperDatTarget[] prefixMatches = sameSize
                    .Where(target =>
                    {
                        string targetStem = NormalizeDatBasename(Path.GetFileNameWithoutExtension(target.Name));
                        return targetStem.StartsWith(normalizedSkeleton, StringComparison.OrdinalIgnoreCase) ||
                               normalizedSkeleton.StartsWith(targetStem, StringComparison.OrdinalIgnoreCase);
                    })
                    .ToArray();
                if (prefixMatches.Length == 1)
                    selected = prefixMatches[0];
            }
        }

        if (selected is null)
        {
            string examples = string.Join("; ", sameSize.Take(6).Select(t => $"'{t.Name}' from {Path.GetFileName(t.LogPath)}"));
            activity?.Report(
                $"Redumper DAT verification: {sameSize.Length:N0} DAT entries share the skeleton size {skeletonLength:N0} bytes and the filename did not uniquely disambiguate them; verification skipped. Candidates: {examples}");
            return null;
        }

        activity?.Report(
            $"Redumper DAT verification: expected image resolved from '{Path.GetFileName(selected.LogPath)}' by size {selected.Size:N0}: " +
            $"'{selected.Name}', CRC32 {selected.Crc32}, MD5 {selected.Md5}, SHA-1 {selected.Sha1}.");
        return selected;
    }

    private static string NormalizeDatBasename(string value)
    {
        string result = value.Trim();
        if (result.EndsWith(".skeleton", StringComparison.OrdinalIgnoreCase))
            result = result[..^9];
        return result.Trim();
    }

    private static void VerifyRedumperDatTarget(
        string outputPath,
        RedumperDatTarget expected,
        IProgress<string>? activity,
        CancellationToken cancellationToken)
    {
        activity?.Report($"Redumper DAT verification: hashing rebuilt image '{Path.GetFileName(outputPath)}'...");

        HashCalculationResult actual = new HashCalculationService()
            .CalculateAsync(
                outputPath,
                new HashCalculationOptions(Crc32: true, Md5: true, Sha1: true),
                cancellationToken: cancellationToken)
            .GetAwaiter()
            .GetResult();

        string crc = actual.Hashes["CRC32"];
        string md5 = actual.Hashes["MD5"];
        string sha1 = actual.Hashes["SHA-1"];
        bool sizeMatch = actual.FileLength == expected.Size;
        bool crcMatch = string.Equals(crc, expected.Crc32, StringComparison.OrdinalIgnoreCase);
        bool md5Match = string.Equals(md5, expected.Md5, StringComparison.OrdinalIgnoreCase);
        bool sha1Match = string.Equals(sha1, expected.Sha1, StringComparison.OrdinalIgnoreCase);
        bool allMatch = sizeMatch && crcMatch && md5Match && sha1Match;

        activity?.Report($"Redumper DAT verification: {(allMatch ? "MATCH" : "MISMATCH")}");
        ReportDatValue(activity, "Size", expected.Size.ToString("N0"), actual.FileLength.ToString("N0"), sizeMatch);
        ReportDatValue(activity, "CRC32", expected.Crc32, crc, crcMatch);
        ReportDatValue(activity, "MD5", expected.Md5, md5, md5Match);
        ReportDatValue(activity, "SHA-1", expected.Sha1, sha1, sha1Match);
    }

    private static bool HasExpectedImageHashes(SkeletonInspectionResult inspection) =>
        !string.IsNullOrWhiteSpace(inspection.ExpectedImageCrc32) ||
        !string.IsNullOrWhiteSpace(inspection.ExpectedImageMd5) ||
        !string.IsNullOrWhiteSpace(inspection.ExpectedImageSha1);


    private static bool ExpectedInspectionHashesMatch(SkeletonInspectionResult inspection, string crc, string md5, string sha1)
    {
        bool any = false;
        bool ok = true;
        if (!string.IsNullOrWhiteSpace(inspection.ExpectedImageCrc32))
        {
            any = true;
            ok &= string.Equals(crc, inspection.ExpectedImageCrc32, StringComparison.OrdinalIgnoreCase);
        }
        if (!string.IsNullOrWhiteSpace(inspection.ExpectedImageMd5))
        {
            any = true;
            ok &= string.Equals(md5, inspection.ExpectedImageMd5, StringComparison.OrdinalIgnoreCase);
        }
        if (!string.IsNullOrWhiteSpace(inspection.ExpectedImageSha1))
        {
            any = true;
            ok &= string.Equals(sha1, inspection.ExpectedImageSha1, StringComparison.OrdinalIgnoreCase);
        }
        return any && ok;
    }

    private static EofSlackRule? TryEofSlackRulesAgainstExpectedHashes(
        SkeletonInspectionResult inspection,
        IReadOnlyDictionary<string, SkeletonSourceMatch> matches,
        string imagePath,
        IReadOnlyList<EofSlackRule> rules,
        RedumperDatTarget? expectedDatTarget,
        IProgress<string>? activity,
        CancellationToken cancellationToken)
    {
        string backup = imagePath + ".eofbaseline";
        File.Copy(imagePath, backup, overwrite: true);
        try
        {
            foreach (EofSlackRule candidate in rules)
            {
                cancellationToken.ThrowIfCancellationRequested();
                File.Copy(backup, imagePath, overwrite: true);
                ApplyMasteringEofResidueRule(inspection, matches, imagePath, candidate, activity: null, cancellationToken);

                HashCalculationResult actual = new HashCalculationService().CalculateAsync(
                    imagePath,
                    new HashCalculationOptions(Crc32: true, Md5: true, Sha1: true),
                    cancellationToken: cancellationToken).GetAwaiter().GetResult();

                string crc = actual.Hashes["CRC32"];
                string md5 = actual.Hashes["MD5"];
                string sha1 = actual.Hashes["SHA-1"];
                bool matchesExpected = expectedDatTarget is not null
                    ? actual.FileLength == expectedDatTarget.Size &&
                      string.Equals(crc, expectedDatTarget.Crc32, StringComparison.OrdinalIgnoreCase) &&
                      string.Equals(md5, expectedDatTarget.Md5, StringComparison.OrdinalIgnoreCase) &&
                      string.Equals(sha1, expectedDatTarget.Sha1, StringComparison.OrdinalIgnoreCase)
                    : ExpectedInspectionHashesMatch(inspection, crc, md5, sha1);

                activity?.Report($"EOF slack trial [{candidate.Section}] {candidate.Name}, delta {candidate.DeltaSectors:N0}: {(matchesExpected ? "DESTINATION HASH MATCH" : "no match")}");
                if (matchesExpected)
                    return candidate;
            }

            File.Copy(backup, imagePath, overwrite: true);
            return null;
        }
        finally
        {
            TryDelete(backup);
        }
    }

    private static void ReportDatValue(
        IProgress<string>? activity,
        string metric,
        string expected,
        string actual,
        bool matches)
    {
        // Keep the metric, Expected:/Actual: labels and values in true fixed columns.
        // Include the colon in the padded label so we never produce visually awkward
        // strings such as "Actual  :". The SkeleTool GUI uses a monospaced font.
        activity?.Report($"  {metric,-7} {"Expected:",-10} {expected}");
        activity?.Report($"  {"",-7} {"Actual:",-10} {actual} {(matches ? "MATCH" : "MISMATCH")}");
    }

    private static void ApplyMasteringEofResidue(
        SkeletonInspectionResult inspection,
        IReadOnlyDictionary<string, SkeletonSourceMatch> matches,
        string imagePath,
        IProgress<string>? activity,
        CancellationToken cancellationToken,
        RedumperDatTarget? expectedDatTarget,
        Func<EofSlackAmbiguityRequest, CancellationToken, Task<EofSlackAmbiguityDecision>>? eofSlackAmbiguityResolver)
    {
        if (!File.Exists(imagePath) || inspection.SectorCount <= 16)
            return;

        if (!TryReadPrimaryVolumeDescriptorIdentity(
                inspection,
                imagePath,
                out string systemId,
                out string applicationId,
                out string dataPreparerId,
                cancellationToken))
            return;

        string sourceLabel = inspection.SourceKind == SkeletonSourceKind.DiscImageCreator ? "DIC" : "SKELETOOL";
        EofSlackRuleSet ruleSet = EofSlackRuleService.Load();
        foreach (string warning in ruleSet.Warnings)
            activity?.Report($"{sourceLabel}: EOFSlackRules.ini warning: {warning}");

        if (!ruleSet.Enabled)
        {
            activity?.Report($"{sourceLabel}: EOF slack rules are disabled in '{ruleSet.FilePath}'.");
            return;
        }

        IReadOnlyList<EofSlackRule> matchingRules = EofSlackRuleService.FindMatches(ruleSet, systemId, applicationId, dataPreparerId);
        if (matchingRules.Count == 0)
            return;

        EofSlackRule rule;
        if (matchingRules.Count > 1)
        {
            string names = string.Join(", ", matchingRules.Select(r => $"[{r.Section}] {r.Name} ({r.DeltaSectors:N0} sectors)"));
            bool canTryAll = HasExpectedImageHashes(inspection) || expectedDatTarget is not null;
            activity?.Report(
                $"{sourceLabel}: EOF slack ambiguity — {matchingRules.Count:N0} enabled observations match this mastering signature. " +
                $"Both/all have been observed on comparable discs. Matches: {names}");

            EofSlackAmbiguityDecision decision = eofSlackAmbiguityResolver is null
                ? new EofSlackAmbiguityDecision()
                : eofSlackAmbiguityResolver(
                    new EofSlackAmbiguityRequest(systemId, applicationId, dataPreparerId, matchingRules, canTryAll),
                    cancellationToken).GetAwaiter().GetResult();

            if (decision.TryAllAndVerify && canTryAll)
            {
                EofSlackRule? verified = TryEofSlackRulesAgainstExpectedHashes(
                    inspection, matches, imagePath, matchingRules, expectedDatTarget, activity, cancellationToken);
                if (verified is null)
                {
                    activity?.Report($"{sourceLabel}: none of the matching EOF slack observations produced the expected destination hashes; leaving default zero-filled EOF slack unchanged.");
                    return;
                }
                activity?.Report($"{sourceLabel}: EOF slack observation [{verified.Section}] '{verified.Name}' selected and retained because it reproduced the expected destination hash(es).");
                return;
            }
            else
            {
                EofSlackRule? selectedRule = matchingRules.FirstOrDefault(r => string.Equals(r.Section, decision.RuleSection, StringComparison.OrdinalIgnoreCase));
                if (selectedRule is null)
                {
                    activity?.Report($"{sourceLabel}: no EOF slack observation selected; leaving default zero-filled EOF slack unchanged.");
                    return;
                }
                rule = selectedRule;
            }
        }
        else
        {
            rule = matchingRules[0];
        }

        ApplyMasteringEofResidueRule(inspection, matches, imagePath, rule, activity, cancellationToken);
    }

    private static void ApplyMasteringEofResidueRule(
        SkeletonInspectionResult inspection,
        IReadOnlyDictionary<string, SkeletonSourceMatch> matches,
        string imagePath,
        EofSlackRule rule,
        IProgress<string>? activity,
        CancellationToken cancellationToken)
    {
        string sourceLabel = inspection.SourceKind == SkeletonSourceKind.DiscImageCreator ? "DIC" : "SKELETOOL";
        activity?.Report(
            $"{sourceLabel}: applying EOF slack observation [{rule.Section}] '{rule.Name}', delta={rule.DeltaSectors:N0} sector(s) " +
            $"({rule.DeltaSectors * CookedSectorSize:N0} bytes)" +
            (string.IsNullOrWhiteSpace(rule.Confidence) ? "." : $"; confidence={rule.Confidence}."));

        var candidates = new Dictionary<long, (SkeletonContentEntry Entry, long EofOffset, long SourceLba)>();
        foreach (SkeletonContentEntry originalEntry in inspection.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (originalEntry.IsSpecial || originalEntry.DataLength <= 0 || originalEntry.ContainsMode2Form2)
                continue;

            // Only synthesize residue for a file whose payload is already present in the
            // cumulative image: either it did not require a source in this pass or the
            // current resurrection supplied a source match.
            bool payloadAvailable = !originalEntry.RequiresSource || matches.ContainsKey(originalEntry.Path);
            if (!payloadAvailable)
                continue;

            SkeletonContentEntry entry = matches.TryGetValue(originalEntry.Path, out SkeletonSourceMatch? match)
                ? match.Entry
                : originalEntry;

            if (!TryGetSimpleFinalLogicalSector(entry, out long finalLba, out int eofOffset))
                continue;
            if (eofOffset <= 0 || eofOffset >= CookedSectorSize)
                continue;

            long sourceLba = finalLba - rule.DeltaSectors;
            if (sourceLba < inspection.BaseLba || sourceLba >= finalLba || finalLba >= inspection.BaseLba + inspection.SectorCount)
                continue;

            candidates.TryAdd(finalLba, (entry, eofOffset, sourceLba));
        }

        if (candidates.Count == 0)
        {
            activity?.Report($"{sourceLabel}: external EOF slack rule matched, but no eligible restored partial EOF sectors were found.");
            return;
        }

        int patchedSectors = 0;
        long patchedBytes = 0;
        int skippedUnsupported = 0;
        using var stream = new FileStream(
            imagePath,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.Read,
            1024 * 1024,
            FileOptions.RandomAccess);

        byte[] sourceSector = inspection.ImageKind == SkeletonImageKind.Raw2352 ? new byte[RawSectorSize] : new byte[CookedSectorSize];
        byte[] targetSector = inspection.ImageKind == SkeletonImageKind.Raw2352 ? new byte[RawSectorSize] : new byte[CookedSectorSize];

        foreach (KeyValuePair<long, (SkeletonContentEntry Entry, long EofOffset, long SourceLba)> pair in candidates.OrderBy(pair => pair.Key))
        {
            cancellationToken.ThrowIfCancellationRequested();
            long finalLba = pair.Key;
            SkeletonContentEntry entry = pair.Value.Entry;
            long sourceLba = pair.Value.SourceLba;
            int eofOffset = checked((int)pair.Value.EofOffset);
            int tailLength = CookedSectorSize - eofOffset;

            if (inspection.ImageKind == SkeletonImageKind.Cooked2048)
            {
                long sourceOffset = checked((sourceLba - inspection.BaseLba) * CookedSectorSize);
                long targetOffset = checked((finalLba - inspection.BaseLba) * CookedSectorSize);
                stream.Position = sourceOffset;
                ReadExactlySync(stream, sourceSector, 0, CookedSectorSize, cancellationToken);
                stream.Position = targetOffset;
                ReadExactlySync(stream, targetSector, 0, CookedSectorSize, cancellationToken);
                Buffer.BlockCopy(sourceSector, eofOffset, targetSector, eofOffset, tailLength);
                stream.Position = targetOffset;
                stream.Write(targetSector, 0, CookedSectorSize);
            }
            else
            {
                long sourceOffset = checked((sourceLba - inspection.BaseLba) * RawSectorSize);
                long targetOffset = checked((finalLba - inspection.BaseLba) * RawSectorSize);
                stream.Position = sourceOffset;
                ReadExactlySync(stream, sourceSector, 0, RawSectorSize, cancellationToken);
                stream.Position = targetOffset;
                ReadExactlySync(stream, targetSector, 0, RawSectorSize, cancellationToken);

                RawSectorPayloadKind sourceKind = GetRawPayloadKind(sourceSector);
                RawSectorPayloadKind targetKind = GetRawPayloadKind(targetSector);
                if (sourceKind is not (RawSectorPayloadKind.Mode1 or RawSectorPayloadKind.Mode2Form1) ||
                    targetKind is not (RawSectorPayloadKind.Mode1 or RawSectorPayloadKind.Mode2Form1))
                {
                    skippedUnsupported++;
                    continue;
                }

                int sourceUserOffset = sourceKind == RawSectorPayloadKind.Mode1 ? 16 : 24;
                int targetUserOffset = targetKind == RawSectorPayloadKind.Mode1 ? 16 : 24;
                Buffer.BlockCopy(sourceSector, sourceUserOffset + eofOffset, targetSector, targetUserOffset + eofOffset, tailLength);

                RebuildErrorFields(
                    targetSector,
                    targetKind,
                    IsMode2Form2NoEdc(inspection, finalLba),
                    IsDicLoggedMode2Form1EccError(inspection, finalLba));

                // Exact DIC recipes remain stronger evidence than the external mastering
                // inference. If a recipe owns this sector it is reasserted here.
                ApplyDicFinalSectorRecipes(inspection, finalLba, targetSector.AsSpan(0, RawSectorSize));

                stream.Position = targetOffset;
                stream.Write(targetSector, 0, RawSectorSize);
            }

            patchedSectors++;
            patchedBytes += tailLength;
            activity?.Report(
                $"{sourceLabel}: EOF slack [{rule.Section}] LBA {finalLba:N0} '{entry.Path}' — " +
                $"copied {tailLength:N0} byte(s) after EOF offset {eofOffset:N0} from LBA {sourceLba:N0} " +
                $"(delta {rule.DeltaSectors:N0}).");
        }

        stream.Flush();
        activity?.Report(
            $"{sourceLabel}: external EOF slack pass complete using [{rule.Section}] — {patchedSectors:N0} sector(s), " +
            $"{patchedBytes:N0} tail byte(s) synthesized" +
            (skippedUnsupported > 0 ? $"; {skippedUnsupported:N0} unsupported/non-Form1 sector(s) skipped." : "."));
    }


    private static bool TryGetSimpleFinalLogicalSector(
        SkeletonContentEntry entry,
        out long finalLba,
        out int eofOffset)
    {
        finalLba = 0;
        eofOffset = 0;

        IReadOnlyList<SkeletonExtentSegment> extents = entry.Extents is { Count: > 0 }
            ? entry.Extents
            : new[]
            {
                new SkeletonExtentSegment(
                    entry.ExtentLba,
                    entry.DataLength,
                    entry.PhysicalSectorCount > 0 ? entry.PhysicalSectorCount : DivideRoundUp(entry.DataLength, CookedSectorSize),
                    entry.ContainsMode2Form2)
            };

        if (extents.Count == 0)
            return false;

        // The oracle signatures were discovered on ordinary 2048-byte logical file
        // extents. Do not infer through XA/Form2 or interleaved/expanded physical maps.
        foreach (SkeletonExtentSegment extent in extents)
        {
            if (extent.ContainsMode2Form2 || extent.DataLength <= 0)
                return false;
            long expectedSectors = DivideRoundUp(extent.DataLength, CookedSectorSize);
            if (extent.PhysicalSectorCount > 0 && extent.PhysicalSectorCount != expectedSectors)
                return false;
        }

        SkeletonExtentSegment last = extents[^1];
        eofOffset = checked((int)(last.DataLength % CookedSectorSize));
        if (eofOffset == 0)
            return false;
        finalLba = checked((long)last.ExtentLba + (last.DataLength - 1) / CookedSectorSize);
        return true;
    }

    private static bool TryReadPrimaryVolumeDescriptorIdentity(
        SkeletonInspectionResult inspection,
        string imagePath,
        out string systemId,
        out string applicationId,
        out string dataPreparerId,
        CancellationToken cancellationToken)
    {
        systemId = string.Empty;
        applicationId = string.Empty;
        dataPreparerId = string.Empty;
        long index = 16L - inspection.BaseLba;
        if (index < 0 || index >= inspection.SectorCount)
            return false;

        using var stream = new FileStream(
            imagePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            64 * 1024,
            FileOptions.RandomAccess);

        byte[] payload = new byte[CookedSectorSize];
        if (inspection.ImageKind == SkeletonImageKind.Cooked2048)
        {
            stream.Position = checked(index * CookedSectorSize);
            ReadExactlySync(stream, payload, 0, payload.Length, cancellationToken);
        }
        else
        {
            byte[] raw = new byte[RawSectorSize];
            stream.Position = checked(index * RawSectorSize);
            ReadExactlySync(stream, raw, 0, raw.Length, cancellationToken);
            RawSectorPayloadKind kind = GetRawPayloadKind(raw);
            if (kind is not (RawSectorPayloadKind.Mode1 or RawSectorPayloadKind.Mode2Form1))
                return false;
            int userOffset = kind == RawSectorPayloadKind.Mode1 ? 16 : 24;
            Buffer.BlockCopy(raw, userOffset, payload, 0, CookedSectorSize);
        }

        if (payload[0] != 1 || payload[1] != (byte)'C' || payload[2] != (byte)'D' ||
            payload[3] != (byte)'0' || payload[4] != (byte)'0' || payload[5] != (byte)'1')
            return false;

        systemId = ReadIsoAsciiField(payload, 8, 32);
        dataPreparerId = ReadIsoAsciiField(payload, 446, 128);
        applicationId = ReadIsoAsciiField(payload, 574, 128);
        return true;
    }

    private static string ReadIsoAsciiField(byte[] payload, int offset, int length)
    {
        if (offset < 0 || length <= 0 || offset + length > payload.Length)
            return string.Empty;
        return Encoding.ASCII.GetString(payload, offset, length).TrimEnd('\0', ' ');
    }

    private static SkeletonContentEntry ResolveRedumperEntryGeometryForSourceLength(
        SkeletonContentEntry entry,
        long sourceLength)
    {
        if (entry.DataLength == sourceLength)
            return entry;

        SkeletonAlternateIsoRecord[] candidates = (entry.AlternateIsoRecords ?? Array.Empty<SkeletonAlternateIsoRecord>())
            .Append(new SkeletonAlternateIsoRecord(entry.ExtentLba, entry.DataLength))
            .Where(record => record.DataLength == sourceLength)
            .GroupBy(record => (record.ExtentLba, record.DataLength))
            .Select(group => group.First())
            .ToArray();

        if (candidates.Length != 1)
            return entry;

        SkeletonAlternateIsoRecord selected = candidates[0];
        return entry with
        {
            ExtentLba = selected.ExtentLba,
            DataLength = selected.DataLength,
            AlternateIsoRecords = entry.AlternateIsoRecords
        };
    }

    private static long GetMatchSourceLength(SkeletonSourceMatch match)
        => match.GeneratedPayload?.LongLength ?? match.SourceLength ?? new FileInfo(match.SourcePath).Length;

    private static Stream OpenMatchSourceStream(SkeletonSourceMatch match)
    {
        if (match.GeneratedPayload is not null)
            return new MemoryStream(match.GeneratedPayload, writable: false);

        if (match.SourceImageLba is null)
            return OpenRead(match.SourcePath, CopyBufferSize, FileOptions.SequentialScan);

        long length = match.SourceLength
            ?? throw new InvalidOperationException("Image-backed Skeletool source is missing its byte length.");
        if (length > int.MaxValue)
            throw new InvalidOperationException("Direct ISO/BIN source entries larger than 2 GiB are not yet supported without streaming image extents.");

        IReadOnlyList<SkeletonSourceImageExtent> extents = match.SourceImageExtents is { Count: > 0 }
            ? match.SourceImageExtents
            : new[] { new SkeletonSourceImageExtent(match.SourceImageLba.Value, length) };

        using var image = OpenRead(match.SourcePath, CopyBufferSize, FileOptions.RandomAccess);
        long imageLength = image.Length;
        bool raw = imageLength % RawSectorSize == 0;
        if (raw)
        {
            Span<byte> sync = stackalloc byte[SyncPattern.Length];
            image.Position = 0;
            int got = image.Read(sync);
            raw = got == sync.Length && sync.SequenceEqual(SyncPattern);
        }

        byte[] payload = new byte[checked((int)length)];
        int written = 0;
        foreach (SkeletonSourceImageExtent extent in extents)
        {
            long extentRemaining = extent.Length;
            long lba = extent.Lba;
            if (!raw)
            {
                image.Position = checked(lba * CookedSectorSize);
                while (extentRemaining > 0)
                {
                    int want = (int)Math.Min((long)(payload.Length - written), extentRemaining);
                    int n = image.Read(payload, written, want);
                    if (n <= 0) throw new EndOfStreamException($"Unexpected end of source image: {match.SourcePath}");
                    written += n;
                    extentRemaining -= n;
                }
            }
            else
            {
                byte[] sector = new byte[RawSectorSize];
                while (extentRemaining > 0)
                {
                    image.Position = checked(lba * RawSectorSize);
                    int read = 0;
                    while (read < sector.Length)
                    {
                        int n = image.Read(sector, read, sector.Length - read);
                        if (n <= 0) throw new EndOfStreamException($"Unexpected end of source image: {match.SourcePath}");
                        read += n;
                    }
                    int userOffset = sector[15] switch
                    {
                        1 => 16,
                        2 when (sector[18] & XaForm2Bit) == 0 => 24,
                        _ => throw new InvalidOperationException($"Source image file extent at LBA {lba:N0} is not Mode 1 / Mode 2 Form 1.")
                    };
                    int copy = (int)Math.Min(CookedSectorSize, extentRemaining);
                    Buffer.BlockCopy(sector, userOffset, payload, written, copy);
                    written += copy;
                    extentRemaining -= copy;
                    lba++;
                }
            }
        }

        if (written != payload.Length)
            throw new InvalidOperationException($"Logical image-backed source '{match.SourceRelativePath ?? match.Entry.Path}' produced {written:N0} byte(s), expected {payload.Length:N0}.");

        return new MemoryStream(payload, writable: false);
    }

    private static int ResurrectCookedSequential(
        SkeletonInspectionResult inspection,
        IReadOnlyDictionary<string, SkeletonSourceMatch> matches,
        string skeletonPath,
        string partialPath,
        long skeletonLength,
        IProgress<SkeletonResurrectionProgress>? progress,
        IProgress<string>? activity,
        CancellationToken cancellationToken)
    {
        var plans = new List<CookedSequentialPlan>();
        int restored = ReportImmediatelySatisfiedEntries(
            inspection,
            matches,
            rawSystemAreaNeedsRebuild: false,
            progress,
            activity);

        foreach (SkeletonContentEntry entry in inspection.Entries)
        {
            if (!entry.CanRestore || entry.IsEmpty)
                continue;

            if (entry.SpecialKind == SkeletonSpecialKind.SystemArea &&
                string.Equals(entry.Sha1, ZeroSystemAreaSha1, StringComparison.OrdinalIgnoreCase) &&
                !matches.ContainsKey(entry.Path))
            {
                continue;
            }

            if (!matches.TryGetValue(entry.Path, out SkeletonSourceMatch? match))
                continue;
            if (match.IsXa)
                throw new NotSupportedException("XA/Form2 restoration requires a raw 2352-byte skeleton.");

            SkeletonContentEntry effectiveEntry = match.Entry;
            long sourceLength = GetMatchSourceLength(match);
            long expected = effectiveEntry.SpecialKind == SkeletonSpecialKind.Gap || effectiveEntry.DataLength == 0
                ? sourceLength
                : effectiveEntry.DataLength;
            if (sourceLength != expected)
            {
                throw new InvalidOperationException(
                    $"Matched source length for '{entry.Path}' is {sourceLength:N0} bytes; expected {expected:N0} bytes.");
            }

            IReadOnlyList<SkeletonExtentSegment> segments = effectiveEntry.Extents is { Count: > 0 }
                ? effectiveEntry.Extents
                : new[]
                {
                    new SkeletonExtentSegment(
                        effectiveEntry.ExtentLba,
                        effectiveEntry.DataLength == 0 ? sourceLength : effectiveEntry.DataLength,
                        effectiveEntry.PhysicalSectorCount,
                        effectiveEntry.ContainsMode2Form2)
                };

            long sourceOffset = 0;
            for (int segmentIndex = 0; segmentIndex < segments.Count; segmentIndex++)
            {
                SkeletonExtentSegment segment = segments[segmentIndex];
                long byteOffset = checked(((long)segment.ExtentLba - inspection.BaseLba) * CookedSectorSize);
                long length = segment.DataLength;
                if (byteOffset < 0 || byteOffset + length > skeletonLength)
                    throw new InvalidOperationException($"Extent for '{entry.Path}' is outside the skeleton image.");

                plans.Add(new CookedSequentialPlan(
                    effectiveEntry,
                    match,
                    byteOffset,
                    length,
                    sourceOffset,
                    segmentIndex == segments.Count - 1));
                sourceOffset = checked(sourceOffset + length);
            }

            if (sourceOffset != sourceLength)
            {
                throw new InvalidOperationException(
                    $"Extent map for '{entry.Path}' accounts for {sourceOffset:N0} bytes; source contains {sourceLength:N0} bytes.");
            }
        }

        plans.Sort((a, b) => a.StartOffset.CompareTo(b.StartOffset));
        for (int i = 1; i < plans.Count; i++)
        {
            CookedSequentialPlan previous = plans[i - 1];
            CookedSequentialPlan current = plans[i];
            if (current.StartOffset < previous.StartOffset + previous.Length)
            {
                throw new InvalidOperationException(
                    $"Overlapping recoverable ISO extents were found ('{previous.Entry.Path}' and '{current.Entry.Path}'). " +
                    "The fast sequential resurrection path cannot safely resolve overlapping file extents.");
            }
        }

        using var skeleton = OpenRead(skeletonPath, CopyBufferSize, FileOptions.SequentialScan);
        using var output = new FileStream(
            partialPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            CopyBufferSize,
            FileOptions.SequentialScan);

        byte[] buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
        try
        {
            long position = 0;
            foreach (CookedSequentialPlan plan in plans)
            {
                cancellationToken.ThrowIfCancellationRequested();

                long untouched = plan.StartOffset - position;
                if (untouched > 0)
                {
                    CopyExactSync(
                        skeleton,
                        output,
                        untouched,
                        buffer,
                        cancellationToken,
                        bytesWritten => ReportSequentialProgress(progress, bytesWritten, skeletonLength, "Building output"));
                    position += untouched;
                }

                skeleton.Position = checked(skeleton.Position + plan.Length);
                using (Stream source = OpenMatchSourceStream(plan.Match))
                {
                    if (plan.SourceOffset > 0)
                        source.Seek(plan.SourceOffset, SeekOrigin.Begin);
                    CopyExactSync(
                        source,
                        output,
                        plan.Length,
                        buffer,
                        cancellationToken,
                        bytesWritten => ReportSequentialProgress(progress, bytesWritten, skeletonLength, $"Restoring {plan.Entry.Path}"));
                }

                position += plan.Length;
                if (plan.CompletesEntry)
                {
                    restored++;
                    progress?.Report(new SkeletonResurrectionProgress(
                        SkeletonResurrectionEventKind.EntryRestored,
                        output.Position,
                        skeletonLength,
                        $"Restored {plan.Entry.Path}",
                        plan.Entry.Path));
                }
            }

            long remainder = skeletonLength - position;
            if (remainder > 0)
            {
                CopyExactSync(
                    skeleton,
                    output,
                    remainder,
                    buffer,
                    cancellationToken,
                    bytesWritten => ReportSequentialProgress(progress, bytesWritten, skeletonLength, "Building output"));
            }

            output.Flush();
            if (output.Length != skeletonLength)
                throw new InvalidOperationException($"Resurrected output is {output.Length:N0} bytes; expected {skeletonLength:N0} bytes.");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return restored;
    }

    private static int ResurrectRawSequential(
        SkeletonInspectionResult inspection,
        IReadOnlyDictionary<string, SkeletonSourceMatch> matches,
        string skeletonPath,
        string partialPath,
        long skeletonLength,
        IProgress<SkeletonResurrectionProgress>? progress,
        IProgress<string>? activity,
        CancellationToken cancellationToken)
    {
        const int sectorsPerBlock = 2048; // 4,816,896 bytes per raw block.
        int blockBytes = sectorsPerBlock * RawSectorSize;

        var plans = BuildRawSequentialPlans(inspection, matches);
        ValidateRawSequentialOverlaps(inspection, plans, skeletonPath, activity, cancellationToken);
        int restored = ReportImmediatelySatisfiedEntries(
            inspection,
            matches,
            rawSystemAreaNeedsRebuild: true,
            progress,
            activity);

        using var skeleton = OpenRead(skeletonPath, CopyBufferSize, FileOptions.SequentialScan);
        using var output = new FileStream(
            partialPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            CopyBufferSize,
            FileOptions.SequentialScan);

        byte[] block = ArrayPool<byte>.Shared.Rent(blockBytes);
        RawSectorPayloadKind[] rebuildKinds = ArrayPool<RawSectorPayloadKind>.Shared.Rent(sectorsPerBlock);
        var activePlans = new List<RawSequentialPlan>();
        int nextPlan = 0;
        long sectorIndex = 0;

        try
        {
            while (sectorIndex < inspection.SectorCount)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int sectorsThisBlock = (int)Math.Min((long)sectorsPerBlock, inspection.SectorCount - sectorIndex);
                int bytesThisBlock = checked(sectorsThisBlock * RawSectorSize);
                ReadExactlySync(skeleton, block, 0, bytesThisBlock, cancellationToken);
                Array.Clear(rebuildKinds, 0, sectorsThisBlock);

                var completedThisBlock = new List<RawSequentialPlan>();

                for (int localSector = 0; localSector < sectorsThisBlock; localSector++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    long currentSector = sectorIndex + localSector;

                    for (int activeIndex = activePlans.Count - 1; activeIndex >= 0; activeIndex--)
                    {
                        RawSequentialPlan plan = activePlans[activeIndex];
                        if (currentSector < plan.MaxEndSectorIndex)
                            continue;

                        if (!plan.RegenerateOnly && plan.Consumed != plan.SourceLength)
                        {
                            throw new InvalidOperationException(
                                $"Could only place {plan.Consumed:N0} of {plan.SourceLength:N0} source bytes for '{plan.Entry.Path}'. " +
                                "The skeleton extent/sector forms do not match the hash source.");
                        }

                        FinishRawPlan(plan, completedThisBlock);
                        activePlans.RemoveAt(activeIndex);
                    }

                    while (nextPlan < plans.Count && plans[nextPlan].StartSectorIndex <= currentSector)
                    {
                        RawSequentialPlan candidate = plans[nextPlan];
                        if (candidate.StartSectorIndex < currentSector)
                        {
                            throw new InvalidOperationException(
                                $"Could not enter restore extent '{candidate.Entry.Path}' at its expected LBA {inspection.BaseLba + candidate.StartSectorIndex:N0}.");
                        }

                        activePlans.Add(candidate);
                        nextPlan++;
                        StartRawPlan(candidate, skeletonLength, output.Position + (long)localSector * RawSectorSize, progress, activity);
                    }

                    if (activePlans.Count == 0)
                        continue;

                    int sectorOffset = localSector * RawSectorSize;
                    RawSectorPayloadKind rebuiltKind = RawSectorPayloadKind.Unsupported;
                    for (int activeIndex = activePlans.Count - 1; activeIndex >= 0; activeIndex--)
                    {
                        RawSequentialPlan plan = activePlans[activeIndex];
                        RawSectorPayloadKind kind = ProcessRawPlanSector(
                            plan,
                            block,
                            sectorOffset,
                            inspection.BaseLba + currentSector,
                            cancellationToken);

                        if (kind != RawSectorPayloadKind.Unsupported)
                            rebuiltKind = kind;

                        bool complete = plan.RegenerateOnly
                            ? currentSector + 1 >= plan.MaxEndSectorIndex
                            : plan.Consumed >= plan.SourceLength;

                        if (!complete)
                            continue;

                        FinishRawPlan(plan, completedThisBlock);
                        activePlans.RemoveAt(activeIndex);
                    }

                    rebuildKinds[localSector] = rebuiltKind;
                }

                // EDC/ECC regeneration is pure per-sector work, so do it in parallel
                // after all source payload bytes for this block have been inserted.
                Parallel.For(
                    0,
                    sectorsThisBlock,
                    new ParallelOptions
                    {
                        CancellationToken = cancellationToken,
                        // Do not saturate every logical processor. Leaving one worker's
                        // worth of CPU headroom keeps Avalonia and the OS responsive
                        // while EDC/ECC is being regenerated.
                        MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1)
                    },
                    localSector =>
                    {
                        RawSectorPayloadKind kind = rebuildKinds[localSector];
                        long lba = inspection.BaseLba + sectorIndex + localSector;
                        Span<byte> sector = block.AsSpan(localSector * RawSectorSize, RawSectorSize);

                        // Rebuild EDC/ECC only for sectors whose logical payload was
                        // modified in this pass. Final DIC recipes, however, must be
                        // asserted for every physical sector in the block. This keeps
                        // exact raw replacements / all-zero sectors / 0x55 protection
                        // bodies stronger than donor bytes and stronger than any prior
                        // regeneration, even when a sector was not part of an active
                        // source-file restore plan.
                        if (kind != RawSectorPayloadKind.Unsupported)
                        {
                            RebuildErrorFields(
                                sector,
                                kind,
                                IsMode2Form2NoEdc(inspection, lba),
                                IsDicLoggedMode2Form1EccError(inspection, lba));
                        }

                        ApplyDicFinalSectorRecipes(inspection, lba, sector);
                    });

                output.Write(block, 0, bytesThisBlock);
                sectorIndex += sectorsThisBlock;

                long outputBytes = checked(sectorIndex * RawSectorSize);
                ReportSequentialProgress(progress, outputBytes, skeletonLength, "Building output");

                foreach (RawSequentialPlan completedPlan in completedThisBlock)
                {
                    restored++;
                    progress?.Report(new SkeletonResurrectionProgress(
                        SkeletonResurrectionEventKind.EntryRestored,
                        outputBytes,
                        skeletonLength,
                        completedPlan.RegenerateOnly
                            ? "Zero system area EDC/ECC regenerated"
                            : $"Restored {completedPlan.Entry.Path}",
                        completedPlan.Entry.Path));
                }
            }

            foreach (RawSequentialPlan plan in activePlans)
            {
                if (!plan.RegenerateOnly && plan.Consumed != plan.SourceLength)
                {
                    throw new InvalidOperationException(
                        $"Could only place {plan.Consumed:N0} of {plan.SourceLength:N0} source bytes for '{plan.Entry.Path}'. " +
                        "The skeleton ended before the hash source could be restored.");
                }
                plan.DisposeSource();
            }

            if (nextPlan != plans.Count)
                throw new InvalidOperationException($"{plans.Count - nextPlan:N0} recoverable extent(s) lie beyond the end of the skeleton image.");

            output.Flush();
            if (output.Length != skeletonLength)
                throw new InvalidOperationException($"Resurrected output is {output.Length:N0} bytes; expected {skeletonLength:N0} bytes.");
        }
        finally
        {
            foreach (RawSequentialPlan plan in activePlans)
                plan.DisposeSource();
            foreach (RawSequentialPlan plan in plans)
                plan.DisposeSource();
            ArrayPool<RawSectorPayloadKind>.Shared.Return(rebuildKinds, clearArray: true);
            ArrayPool<byte>.Shared.Return(block);
        }

        return restored;
    }

    private static List<RawSequentialPlan> BuildRawSequentialPlans(
        SkeletonInspectionResult inspection,
        IReadOnlyDictionary<string, SkeletonSourceMatch> matches)
    {
        var plans = new List<RawSequentialPlan>();

        foreach (SkeletonContentEntry entry in inspection.Entries)
        {
            if (!entry.CanRestore || entry.IsEmpty)
                continue;

            bool zeroSystemArea = entry.SpecialKind == SkeletonSpecialKind.SystemArea &&
                                  string.Equals(entry.Sha1, ZeroSystemAreaSha1, StringComparison.OrdinalIgnoreCase) &&
                                  !matches.ContainsKey(entry.Path);

            SkeletonSourceMatch? match = null;
            if (!zeroSystemArea && !matches.TryGetValue(entry.Path, out match))
                continue;

            if (zeroSystemArea)
            {
                long startSectorIndex = (long)entry.ExtentLba - inspection.BaseLba;
                if (startSectorIndex < 0 || startSectorIndex >= inspection.SectorCount)
                    throw new InvalidOperationException($"Extent for '{entry.Path}' is outside the raw skeleton image.");
                long maxEnd = checked(startSectorIndex + SystemAreaSectors);
                if (maxEnd > inspection.SectorCount)
                    throw new InvalidOperationException("SYSTEM_AREA extends beyond the raw skeleton image.");
                plans.Add(new RawSequentialPlan(entry, null, startSectorIndex, maxEnd, 0, 0, regenerateOnly: true, usePhysicalPayloadMap: false, completesEntry: true));
                continue;
            }

            SkeletonSourceMatch resolvedMatch = match
                ?? throw new InvalidOperationException($"No source match is available for '{entry.Path}'.");
            SkeletonContentEntry effectiveEntry = resolvedMatch.Entry;
            long sourceFileLength = GetMatchSourceLength(resolvedMatch);
            bool usePhysicalPayloadMap = inspection.SourceKind == SkeletonSourceKind.DiscImageCreator;

            if (usePhysicalPayloadMap && sourceFileLength != effectiveEntry.DataLength)
            {
                throw new InvalidOperationException(
                    $"Matched DIC source length for '{entry.Path}' is {sourceFileLength:N0} bytes; expected {effectiveEntry.DataLength:N0} bytes.");
            }

            IReadOnlyList<SkeletonExtentSegment> segments = effectiveEntry.Extents is { Count: > 0 }
                ? effectiveEntry.Extents
                : new[]
                {
                    new SkeletonExtentSegment(
                        effectiveEntry.ExtentLba,
                        effectiveEntry.DataLength,
                        effectiveEntry.PhysicalSectorCount > 0 ? effectiveEntry.PhysicalSectorCount : DivideRoundUp(effectiveEntry.DataLength, CookedSectorSize),
                        effectiveEntry.ContainsMode2Form2)
                };

            long sourceOffset = 0;
            for (int segmentIndex = 0; segmentIndex < segments.Count; segmentIndex++)
            {
                SkeletonExtentSegment segment = segments[segmentIndex];
                long startSectorIndex = (long)segment.ExtentLba - inspection.BaseLba;
                if (startSectorIndex < 0 || startSectorIndex >= inspection.SectorCount)
                    throw new InvalidOperationException($"Extent for '{entry.Path}' is outside the raw skeleton image.");

                long scanSectorCount;
                if (usePhysicalPayloadMap)
                {
                    scanSectorCount = segment.PhysicalSectorCount > 0
                        ? segment.PhysicalSectorCount
                        : DivideRoundUp(segment.DataLength, CookedSectorSize);
                }
                else if (entry.SpecialKind == SkeletonSpecialKind.Gap && entry.DataLength == 0)
                {
                    long payloadSize = resolvedMatch.IsXa ? 2324 : CookedSectorSize;
                    if (sourceFileLength % payloadSize != 0)
                    {
                        throw new InvalidOperationException(
                            $"GAP source '{resolvedMatch.SourcePath}' is {sourceFileLength:N0} bytes; expected a multiple of {payloadSize:N0} bytes.");
                    }

                    long payloadSectors = sourceFileLength / payloadSize;
                    long availableSectors = inspection.SectorCount - startSectorIndex;
                    scanSectorCount = Math.Min(availableSectors, Math.Max(payloadSectors * 4, payloadSectors + 1024));
                }
                else
                {
                    long extentBytes = entry.SpecialKind == SkeletonSpecialKind.SystemArea
                        ? SystemAreaSectors * CookedSectorSize
                        : segment.DataLength;
                    scanSectorCount = DivideRoundUp(extentBytes, CookedSectorSize);
                }

                if (startSectorIndex + scanSectorCount > inspection.SectorCount)
                    throw new InvalidOperationException($"Extent for '{entry.Path}' extends beyond the raw skeleton image.");

                plans.Add(new RawSequentialPlan(
                    effectiveEntry,
                    resolvedMatch,
                    startSectorIndex,
                    checked(startSectorIndex + scanSectorCount),
                    sourceOffset,
                    segment.DataLength,
                    regenerateOnly: false,
                    usePhysicalPayloadMap,
                    completesEntry: segmentIndex == segments.Count - 1));

                sourceOffset = checked(sourceOffset + segment.DataLength);
            }

            if (sourceOffset != effectiveEntry.DataLength)
                throw new InvalidOperationException($"Multi-extent map for '{entry.Path}' accounts for {sourceOffset:N0} bytes; expected {effectiveEntry.DataLength:N0} bytes.");
        }

        plans.Sort((a, b) =>
        {
            int cmp = a.StartSectorIndex.CompareTo(b.StartSectorIndex);
            return cmp != 0 ? cmp : string.Compare(a.Entry.Path, b.Entry.Path, StringComparison.OrdinalIgnoreCase);
        });

        return plans;
    }

    private static void ValidateRawSequentialOverlaps(
        SkeletonInspectionResult inspection,
        IReadOnlyList<RawSequentialPlan> plans,
        string skeletonPath,
        IProgress<string>? activity,
        CancellationToken cancellationToken)
    {
        // Map every physical sector to the number of logical source bytes consumed by
        // DIC recovery.  The prefix coordinate gives each source byte a stable global
        // payload position regardless of Mode 1 / Mode 2 Form 1 / Form 2 capacity.
        int[] payloadCapacity = BuildRawPayloadCapacityMap(inspection, skeletonPath, cancellationToken);
        long[] payloadPrefix = new long[payloadCapacity.Length + 1];
        for (int i = 0; i < payloadCapacity.Length; i++)
            payloadPrefix[i + 1] = checked(payloadPrefix[i] + payloadCapacity[i]);

        // Keep a non-overlapping set of already-proven byte ranges.  Every new plan is
        // compared only against the canonical coverage it intersects.  Once A==B and
        // A==C over a range, B-vs-C is redundant by transitivity; the old pairwise
        // validator nevertheless reread those same bytes again.  This is the dominant
        // cost on heavily overlapping discs such as Rebellion.
        var coverage = new List<ValidatedPayloadCoverage>();

        foreach (RawSequentialPlan plan in plans)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (plan.RegenerateOnly || plan.Match is null || !plan.UsePhysicalPayloadMap)
                continue;

            long planGlobalStart = payloadPrefix[checked((int)plan.StartSectorIndex)];
            long physicalCapacity = checked(payloadPrefix[checked((int)plan.MaxEndSectorIndex)] - planGlobalStart);
            if (plan.SourceLength > physicalCapacity)
            {
                throw new InvalidOperationException(
                    $"Cannot validate overlapping extent '{plan.Entry.Path}': its {plan.SourceLength:N0} source byte(s) exceed the " +
                    $"{physicalCapacity:N0} byte(s) addressable by the mapped raw-sector payload range.");
            }

            long planGlobalEnd = checked(planGlobalStart + plan.SourceLength);
            if (planGlobalEnd <= planGlobalStart)
                continue;

            // Compare only the portions already constrained by earlier plans.
            foreach (ValidatedPayloadCoverage prior in coverage)
            {
                if (prior.End <= planGlobalStart)
                    continue;
                if (prior.Start >= planGlobalEnd)
                    break;

                long overlapStart = Math.Max(planGlobalStart, prior.Start);
                long overlapEnd = Math.Min(planGlobalEnd, prior.End);
                if (overlapStart >= overlapEnd)
                    continue;

                long overlapStartSector = FindSectorForPayloadCoordinate(payloadPrefix, overlapStart);
                long overlapEndSector = FindSectorForPayloadCoordinate(payloadPrefix, overlapEnd - 1);
                activity?.Report(
                    $"WARNING: DIC recoverable extents overlap at LBA {inspection.BaseLba + overlapStartSector:N0}-{inspection.BaseLba + overlapEndSector:N0}: " +
                    $"'{prior.Plan.Entry.Path}' and '{plan.Entry.Path}'. Validating canonical overlapping source bytes before resurrection.");

                ValidateDicPayloadRangeEqual(
                    inspection,
                    prior.Plan,
                    plan,
                    overlapStart,
                    overlapEnd,
                    payloadPrefix,
                    cancellationToken);

                activity?.Report(
                    $"DIC OVERLAP: VERIFIED — shared bytes are identical for '{prior.Plan.Entry.Path}' and '{plan.Entry.Path}' " +
                    $"at LBA {inspection.BaseLba + overlapStartSector:N0}-{inspection.BaseLba + overlapEndSector:N0}.");
            }

            // Add only portions not already represented by canonical coverage.
            long cursor = planGlobalStart;
            var additions = new List<ValidatedPayloadCoverage>();
            foreach (ValidatedPayloadCoverage prior in coverage)
            {
                if (prior.End <= cursor)
                    continue;
                if (prior.Start >= planGlobalEnd)
                    break;
                if (prior.Start > cursor)
                    additions.Add(new ValidatedPayloadCoverage(cursor, Math.Min(prior.Start, planGlobalEnd), plan));
                cursor = Math.Max(cursor, prior.End);
                if (cursor >= planGlobalEnd)
                    break;
            }
            if (cursor < planGlobalEnd)
                additions.Add(new ValidatedPayloadCoverage(cursor, planGlobalEnd, plan));

            if (additions.Count > 0)
            {
                coverage.AddRange(additions);
                coverage.Sort((a, b) => a.Start.CompareTo(b.Start));
            }
        }
    }

    private static int[] BuildRawPayloadCapacityMap(
        SkeletonInspectionResult inspection,
        string skeletonPath,
        CancellationToken cancellationToken)
    {
        if (inspection.SectorCount > int.MaxValue)
            throw new InvalidOperationException("Raw overlap validation cannot index an image with more than Int32.MaxValue sectors.");

        int[] capacities = new int[checked((int)inspection.SectorCount)];
        byte[] rawSector = new byte[RawSectorSize];
        using FileStream skeleton = OpenRead(skeletonPath, 1024 * 1024, FileOptions.SequentialScan);
        for (int sectorIndex = 0; sectorIndex < capacities.Length; sectorIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadExactlySync(skeleton, rawSector.AsSpan(0, RawSectorSize), cancellationToken);
            RawSectorPayloadKind kind = GetRawPayloadKind(rawSector);
            capacities[sectorIndex] = kind switch
            {
                RawSectorPayloadKind.Mode1 => CookedSectorSize,
                RawSectorPayloadKind.Mode2Form1 => CookedSectorSize,
                RawSectorPayloadKind.Mode2Form2 => 2324,
                _ => 0
            };
        }
        return capacities;
    }

    private static void ValidateDicPayloadRangeEqual(
        SkeletonInspectionResult inspection,
        RawSequentialPlan left,
        RawSequentialPlan right,
        long globalStart,
        long globalEnd,
        IReadOnlyList<long> payloadPrefix,
        CancellationToken cancellationToken)
    {
        long leftGlobalStart = payloadPrefix[checked((int)left.StartSectorIndex)];
        long rightGlobalStart = payloadPrefix[checked((int)right.StartSectorIndex)];
        long leftSourceOffset = checked(left.SourceOffset + (globalStart - leftGlobalStart));
        long rightSourceOffset = checked(right.SourceOffset + (globalStart - rightGlobalStart));
        long compareLength = checked(globalEnd - globalStart);

        using Stream leftSource = OpenMatchSourceStream(left.Match!);
        using Stream rightSource = OpenMatchSourceStream(right.Match!);
        leftSource.Position = leftSourceOffset;
        rightSource.Position = rightSourceOffset;

        byte[] leftBytes = new byte[HashBufferSize];
        byte[] rightBytes = new byte[HashBufferSize];
        long compared = 0;
        while (compared < compareLength)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int count = checked((int)Math.Min((long)HashBufferSize, compareLength - compared));
            ReadExactlySync(leftSource, leftBytes.AsSpan(0, count), cancellationToken);
            ReadExactlySync(rightSource, rightBytes.AsSpan(0, count), cancellationToken);
            ReadOnlySpan<byte> a = leftBytes.AsSpan(0, count);
            ReadOnlySpan<byte> b = rightBytes.AsSpan(0, count);
            if (!a.SequenceEqual(b))
            {
                int firstDifference = 0;
                while (firstDifference < count && a[firstDifference] == b[firstDifference])
                    firstDifference++;

                long differingGlobal = checked(globalStart + compared + firstDifference);
                long sectorIndex = FindSectorForPayloadCoordinate(payloadPrefix, differingGlobal);
                long userByte = differingGlobal - payloadPrefix[checked((int)sectorIndex)];
                throw new InvalidOperationException(
                    $"DIC recoverable extent conflict at LBA {inspection.BaseLba + sectorIndex:N0}, user-data byte {userByte:N0}: " +
                    $"'{left.Entry.Path}' and '{right.Entry.Path}' require different bytes in the same physical image location.");
            }
            compared += count;
        }
    }

    private static long FindSectorForPayloadCoordinate(IReadOnlyList<long> payloadPrefix, long coordinate)
    {
        int lo = 0;
        int hi = payloadPrefix.Count - 1;
        while (lo + 1 < hi)
        {
            int mid = lo + ((hi - lo) / 2);
            if (payloadPrefix[mid] <= coordinate)
                lo = mid;
            else
                hi = mid;
        }
        return lo;
    }

    private sealed record ValidatedPayloadCoverage(long Start, long End, RawSequentialPlan Plan);

    private static int ReportImmediatelySatisfiedEntries(
        SkeletonInspectionResult inspection,
        IReadOnlyDictionary<string, SkeletonSourceMatch> matches,
        bool rawSystemAreaNeedsRebuild,
        IProgress<SkeletonResurrectionProgress>? progress,
        IProgress<string>? activity)
    {
        int restored = 0;
        foreach (SkeletonContentEntry entry in inspection.Entries)
        {
            if (entry.IsEmpty)
            {
                activity?.Report($"Empty file requires no data: {entry.Path}");
                progress?.Report(new SkeletonResurrectionProgress(
                    SkeletonResurrectionEventKind.EntryRestored,
                    0,
                    1,
                    "Empty file",
                    entry.Path));
                restored++;
                continue;
            }

            if (!rawSystemAreaNeedsRebuild &&
                entry.SpecialKind == SkeletonSpecialKind.SystemArea &&
                string.Equals(entry.Sha1, ZeroSystemAreaSha1, StringComparison.OrdinalIgnoreCase) &&
                !matches.ContainsKey(entry.Path))
            {
                activity?.Report("SYSTEM_AREA hash is the standard all-zero cooked system area; no payload data needs to be supplied.");
                progress?.Report(new SkeletonResurrectionProgress(
                    SkeletonResurrectionEventKind.EntryRestored,
                    0,
                    1,
                    "Zero system area already satisfied",
                    entry.Path));
                restored++;
            }
        }
        return restored;
    }

    private static void StartRawPlan(
        RawSequentialPlan plan,
        long totalBytes,
        long processedBytes,
        IProgress<SkeletonResurrectionProgress>? progress,
        IProgress<string>? activity)
    {
        // Avoid posting a start/log event for every individual file. On discs with
        // thousands of files those callbacks can overwhelm the UI dispatcher. Completion
        // events still update each tree node and block progress keeps the title/bar live.
        if (plan.RegenerateOnly)
        {
            activity?.Report("SYSTEM_AREA payload is all zero; regenerating raw-sector EDC/ECC during the sequential pass.");
            return;
        }

        SkeletonSourceMatch match = plan.Match
            ?? throw new InvalidOperationException($"No source match is available for '{plan.Entry.Path}'.");
        // Use the logical match stream rather than opening SourcePath directly.
        // For ordinary files this is simply the file itself; for image-backed matches
        // it exposes only the matched file payload beginning at SourceImageLba.
        plan.SourceStream = OpenMatchSourceStream(match);
        if (plan.SourceOffset > 0)
            plan.SourceStream.Seek(plan.SourceOffset, SeekOrigin.Begin);
    }

    private static RawSectorPayloadKind ProcessRawPlanSector(
        RawSequentialPlan plan,
        byte[] block,
        int sectorOffset,
        long lba,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var sector = block.AsSpan(sectorOffset, RawSectorSize);
        if (!sector.Slice(0, SyncPattern.Length).SequenceEqual(SyncPattern))
            throw new InvalidOperationException($"Raw sector sync is invalid at LBA {lba:N0}.");

        RawSectorPayloadKind kind = GetRawPayloadKind(sector);
        if (kind is not (RawSectorPayloadKind.Mode1 or RawSectorPayloadKind.Mode2Form1 or RawSectorPayloadKind.Mode2Form2))
            throw new InvalidOperationException($"Unsupported sector mode while restoring '{plan.Entry.Path}' at LBA {lba:N0}.");

        if (plan.RegenerateOnly)
            return kind;

        if (plan.UsePhysicalPayloadMap)
        {
            int physicalUserOffset = kind == RawSectorPayloadKind.Mode1 ? 16 : 24;
            int capacity = kind == RawSectorPayloadKind.Mode2Form2 ? 2324 : CookedSectorSize;
            int toRead = (int)Math.Min((long)capacity, plan.SourceLength - plan.Consumed);
            if (toRead > 0)
            {
                ReadSourceExactlyForPlan(plan, sector.Slice(physicalUserOffset, toRead), lba, cancellationToken);
                plan.Consumed += toRead;
            }
            return kind;
        }

        if (plan.Match!.IsXa)
        {
            if (kind != RawSectorPayloadKind.Mode2Form2)
                return RawSectorPayloadKind.Unsupported;

            int toRead = (int)Math.Min(2324L, plan.SourceLength - plan.Consumed);
            ReadSourceExactlyForPlan(plan, sector.Slice(24, toRead), lba, cancellationToken);
            plan.Consumed += toRead;
            return kind;
        }

        if (kind == RawSectorPayloadKind.Mode2Form2)
            return RawSectorPayloadKind.Unsupported;

        int userOffset = kind == RawSectorPayloadKind.Mode1 ? 16 : 24;
        int normalRead = (int)Math.Min((long)CookedSectorSize, plan.SourceLength - plan.Consumed);
        ReadSourceExactlyForPlan(plan, sector.Slice(userOffset, normalRead), lba, cancellationToken);
        plan.Consumed += normalRead;
        return kind;
    }


    private static void ReadSourceExactlyForPlan(
        RawSequentialPlan plan,
        Span<byte> destination,
        long lba,
        CancellationToken cancellationToken)
    {
        try
        {
            ReadExactlySync(plan.SourceStream!, destination, cancellationToken);
        }
        catch (EndOfStreamException ex)
        {
            SkeletonSourceMatch match = plan.Match
                ?? throw new InvalidOperationException($"No source match is available for '{plan.Entry.Path}'.", ex);
            long logicalOffset = checked(plan.SourceOffset + plan.Consumed);
            long available = plan.SourceStream?.Length ?? -1;
            throw new EndOfStreamException(
                $"Unexpected end of matched source while restoring '{plan.Entry.Path}' at LBA {lba:N0}. " +
                $"Source: '{match.SourcePath}'; logical source offset {logicalOffset:N0}; " +
                $"requested {destination.Length:N0} byte(s); match length {plan.SourceLength:N0}; stream length {available:N0}.",
                ex);
        }
    }

    private static void FinishRawPlan(RawSequentialPlan plan, List<RawSequentialPlan> completed)
    {
        plan.DisposeSource();
        if (plan.CompletesEntry)
            completed.Add(plan);
    }

    private static void ReportSequentialProgress(
        IProgress<SkeletonResurrectionProgress>? progress,
        long bytesProcessed,
        long bytesTotal,
        string message)
    {
        progress?.Report(new SkeletonResurrectionProgress(
            SkeletonResurrectionEventKind.CopyingSkeleton,
            Math.Min(bytesProcessed, bytesTotal),
            bytesTotal,
            message));
    }

    private static void CopyExactSync(
        Stream source,
        Stream destination,
        long count,
        byte[] buffer,
        CancellationToken cancellationToken,
        Action<long>? progress)
    {
        long remaining = count;
        while (remaining > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int wanted = (int)Math.Min((long)buffer.Length, remaining);
            int read = source.Read(buffer, 0, wanted);
            if (read == 0)
                throw new EndOfStreamException("Unexpected end of file during resurrection.");
            destination.Write(buffer, 0, read);
            remaining -= read;
            progress?.Invoke(destination.Position);
        }
    }

    private static void ReadExactlySync(
        Stream stream,
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken)
    {
        int total = 0;
        while (total < count)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int read = stream.Read(buffer, offset + total, count - total);
            if (read == 0)
                throw new EndOfStreamException("Unexpected end of file during resurrection.");
            total += read;
        }
    }

    private static void ReadExactlySync(
        Stream stream,
        Span<byte> buffer,
        CancellationToken cancellationToken)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int read = stream.Read(buffer[total..]);
            if (read == 0)
                throw new EndOfStreamException("Unexpected end of source file during resurrection.");
            total += read;
        }
    }

    private sealed record CookedSequentialPlan(
        SkeletonContentEntry Entry,
        SkeletonSourceMatch Match,
        long StartOffset,
        long Length,
        long SourceOffset,
        bool CompletesEntry);

    private sealed class RawSequentialPlan
    {
        public RawSequentialPlan(
            SkeletonContentEntry entry,
            SkeletonSourceMatch? match,
            long startSectorIndex,
            long maxEndSectorIndex,
            long sourceOffset,
            long sourceLength,
            bool regenerateOnly,
            bool usePhysicalPayloadMap,
            bool completesEntry)
        {
            Entry = entry;
            Match = match;
            StartSectorIndex = startSectorIndex;
            MaxEndSectorIndex = maxEndSectorIndex;
            SourceOffset = sourceOffset;
            SourceLength = sourceLength;
            RegenerateOnly = regenerateOnly;
            UsePhysicalPayloadMap = usePhysicalPayloadMap;
            CompletesEntry = completesEntry;
        }

        public SkeletonContentEntry Entry { get; }
        public SkeletonSourceMatch? Match { get; }
        public long StartSectorIndex { get; }
        public long MaxEndSectorIndex { get; }
        public long SourceOffset { get; }
        public long SourceLength { get; }
        public bool RegenerateOnly { get; }
        public bool UsePhysicalPayloadMap { get; }
        public bool CompletesEntry { get; }
        public long Consumed { get; set; }
        public Stream? SourceStream { get; set; }

        public void DisposeSource()
        {
            SourceStream?.Dispose();
            SourceStream = null;
        }
    }

    public string SuggestOutputPath(SkeletonInspectionResult inspection)
    {
        string directory = Path.GetDirectoryName(inspection.SkeletonPath) ?? Directory.GetCurrentDirectory();
        string stem = Path.GetFileNameWithoutExtension(inspection.SkeletonPath);
        if (inspection.SourceKind == SkeletonSourceKind.DiscImageCreator &&
            stem.EndsWith("_DIC_skeleton", StringComparison.OrdinalIgnoreCase))
        {
            stem = stem[..^"_DIC_skeleton".Length];
        }
        string extension = inspection.ImageKind == SkeletonImageKind.Raw2352 ? ".bin" : ".iso";
        return Path.Combine(directory, $"{stem}_resurrected{extension}");
    }

    private static int CountMissingRequired(
        SkeletonInspectionResult inspection,
        IReadOnlyDictionary<string, SkeletonSourceMatch> matches)
    {
        int missing = 0;
        foreach (SkeletonContentEntry entry in inspection.Entries)
        {
            if (!entry.CanRestore || entry.IsEmpty)
                continue;
            if (entry.SpecialKind == SkeletonSpecialKind.SystemArea &&
                string.Equals(entry.Sha1, ZeroSystemAreaSha1, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!entry.RequiresSource && string.IsNullOrWhiteSpace(entry.Sha1) && string.IsNullOrWhiteSpace(entry.XaSha1))
                continue;
            if (!matches.ContainsKey(entry.Path))
                missing++;
        }
        return missing;
    }
private static bool IsMode2Form2NoEdc(SkeletonInspectionResult inspection, long lba)
        => inspection.NoEdcLbas?.Contains(lba) == true;

    private static bool IsDicLoggedMode2Form1EccError(SkeletonInspectionResult inspection, long lba)
        => inspection.DicMode2Form1QFaultLbas?.Contains(lba) == true;

    internal static void ApplyDicLoggedFramingOverrides(SkeletonInspectionResult inspection, long lba, Span<byte> sector)
    {
        if (inspection.DicRawHeaderOverrides is not null &&
            inspection.DicRawHeaderOverrides.TryGetValue(lba, out byte[]? header) &&
            header.Length is 3 or 4)
        {
            header.AsSpan().CopyTo(sector.Slice(12, header.Length));
        }

        if (inspection.DicXaSubheaderOverrides is not null &&
            inspection.DicXaSubheaderOverrides.TryGetValue(lba, out byte[]? xa) &&
            xa.Length == 8)
        {
            xa.AsSpan().CopyTo(sector.Slice(16, 8));
        }
    }

    internal static void ApplyDicFinalSectorRecipes(SkeletonInspectionResult inspection, long lba, Span<byte> sector)
    {
        if (inspection.DicExactRawSectorOverrides is not null &&
            inspection.DicExactRawSectorOverrides.TryGetValue(lba, out byte[]? exactRaw) &&
            exactRaw.Length == RawSectorSize)
        {
            // Some recovery bundles contain extensionless files named only by decimal
            // LBA. When validated by the DIC importer these are exact recovered raw
            // 2352-byte sectors and are stronger evidence than any generated payload,
            // donor framing, or generic protection fill recipe.
            exactRaw.AsSpan().CopyTo(sector);
            return;
        }

        if (inspection.DicExactZeroSectorLbas?.Contains(lba) == true)
        {
            // mainError can explicitly say "All zero sector. Skip descrambling".
            // Unlike a transient read-padding event, that statement proves the final
            // raw 2352-byte sector is zero and therefore outranks payload/donor writes.
            sector.Clear();
            return;
        }

        if (inspection.DicFill55ExceptHeaderLbas?.Contains(lba) == true)
        {
            // Some DIC/EccEdc versions explicitly state that unmatched sectors were
            // replaced with 0x55 except for the 16-byte sync/header. This is a final
            // image recipe, not an ECC regeneration rule, so apply it last.
            sector.Slice(16, RawSectorSize - 16).Fill(0x55);
            return;
        }

        if (ShouldInferMode1Fill55ExceptHeader(inspection, lba, sector))
        {
            // Proven on a DIC-flagged Mode 1 mastering pattern: the logical 2048-byte
            // payload is itself entirely 0x55, while the stored EDC/reserved/ECC bytes
            // were also left as 0x55 instead of being generated canonically.
            //
            // This inference is intentionally narrow. It is considered only for a
            // physical LBA that DIC mapped as an unresolved ECC/EDC mismatch, only
            // after recovered user data is present, and only while the current raw
            // sector still carries the canonical Mode 1 protection fields generated
            // from that payload. If stronger raw evidence is still present at this
            // stage, this inference does not replace it.
            sector.Slice(16, RawSectorSize - 16).Fill(0x55);
            return;
        }

        if (inspection.DicMode2Form1QFaultLbas?.Contains(lba) == true)
            RebuildMode2Form1ProtectionFields(sector, dicLoggedMode2Form1EccError: true);
    }

    private static bool ShouldInferMode1Fill55ExceptHeader(
        SkeletonInspectionResult inspection,
        long lba,
        ReadOnlySpan<byte> sector)
    {
        if (inspection.DicUnresolvedEccEdcMismatchLbas?.Contains(lba) != true ||
            sector.Length < RawSectorSize ||
            sector[15] != 1)
            return false;

        ReadOnlySpan<byte> userData = sector.Slice(16, CookedSectorSize);
        for (int i = 0; i < userData.Length; i++)
        {
            if (userData[i] != 0x55)
                return false;
        }

        // Do not replace stronger raw-sector evidence that is still present at this
        // stage. In the normal source-file reconstruction path this sector is canonical
        // at this point; any non-canonical protection bytes cause the inference to fail.
        Span<byte> canonical = stackalloc byte[RawSectorSize];
        byte[]? rawHeaderOverride = null;
        if (inspection.DicRawHeaderOverrides is not null)
            inspection.DicRawHeaderOverrides.TryGetValue(lba, out rawHeaderOverride);

        BuildMode1Sector(lba, userData, canonical, rawHeaderOverride);
        return sector.Slice(2064, RawSectorSize - 2064)
            .SequenceEqual(canonical.Slice(2064, RawSectorSize - 2064));
    }

    private static async Task ReadRawSectorAsync(
        FileStream stream,
        long offset,
        byte[] sector,
        CancellationToken cancellationToken)
    {
        stream.Position = offset;
        await ReadExactlyAsync(stream, sector.AsMemory(0, RawSectorSize), cancellationToken);
        // Do not require canonical sync here. DIC can preserve intentionally malformed
        // or zero-sync sectors. Payload restoration must keep that exact framing when an
        // exact raw donor/capture supplied it, and GetRawPayloadKind still validates the
        // logical mode before any payload bytes are changed.
    }
}
