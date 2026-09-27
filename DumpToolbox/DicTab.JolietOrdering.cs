using DumpToolbox.Core;
using DumpToolbox.Core.Mastering;

namespace DumpToolbox;

public partial class MainWindow
{
    private async Task<bool?> TryTestDicJolietOrderingCandidatesAsync(
        SkeletonInspectionResult inspection,
        SkeletonResurrectionResult result,
        DicJolietNameUpdateResult defaultJolietUpdate,
        bool forceMatchedJolietNames,
        bool? rebuiltHashesMatch,
        CancellationToken cancellationToken)
    {
        if (result.MissingEntries > 0 || rebuiltHashesMatch == true)
            return rebuiltHashesMatch;
        if (rebuiltHashesMatch is null || !HasDicExpectedHashes(inspection))
            return rebuiltHashesMatch;
        if (!defaultJolietUpdate.Updated ||
            defaultJolietUpdate.RecordOrdering is not JolietRecordOrdering defaultRecordOrdering ||
            defaultJolietUpdate.PathTableOrdering is not JolietPathTableOrdering defaultPathTableOrdering)
        {
            return rebuiltHashesMatch;
        }

        string defaultName = JolietOrderingCandidates.GetDisplayName(defaultRecordOrdering);
        IReadOnlyList<JolietRecordOrdering> alternatives =
            JolietOrderingCandidates.GetAlternatives(defaultRecordOrdering);
        var candidateProgress = new Progress<DicDonorProgress>(progress =>
        {
            DicProgressBar.Value = progress.Fraction * 100;
            DicProgressText.Text = progress.Message;
            SetWindowStatus($"DIC — {progress.Message}");
        });

        foreach (JolietRecordOrdering alternative in alternatives)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string alternativeName = JolietOrderingCandidates.GetDisplayName(alternative);
            AppendDicLog(
                $"JOLIET ORDERING: Trying {alternativeName} as the default {defaultName} did not produce a full-image match.");

            string candidatePath = result.OutputPath + $".{Guid.NewGuid():N}.joliet-ordering-candidate";
            try
            {
                DicJolietNameUpdateResult candidateUpdate =
                    await _dicLogImportService.CreateMatchedJolietOrderingCandidateAsync(
                        inspection,
                        _dicMatches,
                        DicSourceFolderBox.Text?.Trim() ?? string.Empty,
                        forceMatchedJolietNames,
                        result.OutputPath,
                        candidatePath,
                        alternative,
                        defaultPathTableOrdering,
                        candidateProgress,
                        cancellationToken);

                foreach (string warning in candidateUpdate.Warnings)
                    AppendDicLog("JOLIET ORDERING: " + warning);
                if (!candidateUpdate.Updated || !File.Exists(candidatePath))
                {
                    AppendDicLog(
                        $"JOLIET ORDERING: {alternativeName} could not be generated safely; moving to the next ordering.");
                    continue;
                }

                var options = new HashCalculationOptions(
                    Crc32: !string.IsNullOrWhiteSpace(inspection.ExpectedImageCrc32),
                    Md5: !string.IsNullOrWhiteSpace(inspection.ExpectedImageMd5),
                    Sha1: !string.IsNullOrWhiteSpace(inspection.ExpectedImageSha1));
                var hashProgress = new Progress<HashCalculationProgress>(progress =>
                {
                    DicProgressBar.Value = progress.Fraction * 100;
                    DicProgressText.Text = $"Hashing {alternativeName} candidate";
                    SetWindowStatus($"DIC — hashing {alternativeName} candidate");
                });
                HashCalculationResult hashes = await _hashCalculationService.CalculateAsync(
                    candidatePath,
                    options,
                    hashProgress,
                    cancellationToken);

                string hashPrefix = $"JOLIET ORDERING ({alternativeName})";
                bool candidateMatches = LogDicExpectedHashComparison(hashPrefix, inspection, hashes);
                if (!candidateMatches)
                {
                    AppendDicLog(
                        $"JOLIET ORDERING: {alternativeName} did not produce a full-image match; the candidate was discarded.");
                    continue;
                }

                cancellationToken.ThrowIfCancellationRequested();
                File.Move(candidatePath, result.OutputPath, true);
                AppendDicLog(
                    $"JOLIET ORDERING: BYTE-EXACT match confirmed using {alternativeName}. The verified candidate replaced the rebuilt BIN.");
                return true;
            }
            finally
            {
                TryDeleteDicJolietOrderingCandidate(candidatePath);
            }
        }

        AppendDicLog(
            "JOLIET ORDERING: none of the supported alternative record orderings matched every available original whole-image hash. " +
            "All candidates were discarded and the default rebuilt BIN was left unchanged.");
        return false;
    }

    private static void TryDeleteDicJolietOrderingCandidate(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Candidate cleanup must not hide the rebuild or verification result.
        }
    }
}
