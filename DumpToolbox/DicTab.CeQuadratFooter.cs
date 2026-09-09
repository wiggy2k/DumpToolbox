using DumpToolbox.Core;

namespace DumpToolbox;

public partial class MainWindow
{
    private async Task<bool?> TryTestDicCeQuadratFooterCandidatesAsync(
        SkeletonInspectionResult inspection,
        SkeletonResurrectionResult result,
        bool? rebuiltHashesMatch,
        CancellationToken cancellationToken)
    {
        if (result.MissingEntries > 0 || rebuiltHashesMatch == true)
            return rebuiltHashesMatch;

        var candidateProgress = new Progress<DicDonorProgress>(progress =>
        {
            DicProgressBar.Value = progress.Fraction * 100;
            DicProgressText.Text = progress.Message;
            SetWindowStatus($"DIC — {progress.Message}");
        });
        DicCeQuadratFooterRecoveryResult recovery = await _dicDonorImageService.TryRecoverCeQuadratFooterAsync(
            inspection,
            result.OutputPath,
            _dicState?.LastDonorImagePath,
            candidateProgress,
            cancellationToken);

        foreach (string warning in recovery.Warnings)
            AppendDicLog("CEQUADRAT FOOTER: " + warning);
        if (!recovery.Eligible)
            return rebuiltHashesMatch;

        AppendDicLog(
            "CEQUADRAT FOOTER: the ordinary no-footer rebuild did not match. Testing exact donor sectors when available, then the three known deterministic end-of-volume layouts.");
        foreach (DicCeQuadratFooterAttempt attempt in recovery.Attempts)
        {
            AppendDicLog($"CEQUADRAT FOOTER: tested {attempt.Description}.");
            LogDicExpectedHashComparison("CEQUADRAT FOOTER", inspection, attempt.Hashes);
        }

        if (recovery.Matched)
        {
            DicCeQuadratFooterAttempt matched = recovery.Attempts.Single(attempt => attempt.MatchesTarget);
            AppendDicLog(
                $"CEQUADRAT FOOTER: BYTE-EXACT match confirmed using {matched.Description}. The verified candidate replaced the rebuilt BIN.");
            return true;
        }

        AppendDicLog(
            "CEQUADRAT FOOTER: none of the exact donor or deterministic footer candidates matched every available target hash. All candidates were discarded and the original rebuilt BIN was left unchanged.");
        return false;
    }
}
