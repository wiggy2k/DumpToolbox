using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using DumpToolbox.Core;

namespace DumpToolbox;

public partial class MainWindow
{
    private enum DicJolietPaddingChoice
    {
        KeepStandard,
        TestDonor,
        ViewAffectedRecords
    }

    private async Task ConfigureDicDonorJolietPaddingAsync(DicDonorScanResult donor)
    {
        if (_dicState is null)
            return;

        _dicState.TestDonorJolietPadding = false;
        _dicState.DonorJolietPaddingApplied = false;
        _dicState.DonorJolietPaddingSourcePath = donor.ImagePath;
        _dicState.DonorJolietPaddingRecords = donor.NonZeroJolietPaddingRecords.ToList();

        int count = donor.NonZeroJolietPaddingRecords.Count;
        if (count == 0)
            return;

        AppendDicLog(
            $"JOLIET: donor contains {count:N0} directory record(s) with non-zero identifier-padding bytes. " +
            "These bytes are retained as evidence but will not be applied unless the temporary candidate matches the original whole-image hashes.");

        DicJolietPaddingChoice choice;
        do
        {
            choice = await ShowDicJolietPaddingChoiceAsync(count);
            if (choice == DicJolietPaddingChoice.ViewAffectedRecords)
                await ShowDicJolietPaddingRecordsAsync(donor.NonZeroJolietPaddingRecords);
        }
        while (choice == DicJolietPaddingChoice.ViewAffectedRecords);

        _dicState.TestDonorJolietPadding = choice == DicJolietPaddingChoice.TestDonor;
        AppendDicLog(_dicState.TestDonorJolietPadding
            ? "JOLIET: donor padding will be tested in a temporary candidate after a complete rebuild. The rebuilt destination will be replaced only if all available original hashes match."
            : "JOLIET: standard zero identifier padding will be kept; donor padding evidence remains saved for this recovery.");
    }

    private Task<DicJolietPaddingChoice> ShowDicJolietPaddingChoiceAsync(int recordCount)
    {
        var completion = new TaskCompletionSource<DicJolietPaddingChoice>();
        var keep = new Button
        {
            Content = "Keep standard padding",
            MinWidth = 160,
            IsDefault = true
        };
        var test = new Button
        {
            Content = "Test donor padding",
            MinWidth = 150
        };
        var view = new Button
        {
            Content = "View affected records",
            MinWidth = 160
        };
        var dialog = new Window
        {
            Title = "Non-standard Joliet padding found",
            Width = 700,
            MinHeight = 300,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(20),
                Spacing = 14,
                Children =
                {
                    new TextBlock
                    {
                        Text = $"The donor image has non-zero padding bytes in {recordCount:N0} Joliet directory record(s).",
                        FontWeight = FontWeight.SemiBold,
                        TextWrapping = TextWrapping.Wrap
                    },
                    new TextBlock
                    {
                        Text = "This is a known quirk of some mastering software, including CeQuadrat. The bytes do not change filenames or file contents, but they do change the final BIN hashes. A donor from another mastering run may use different values.",
                        TextWrapping = TextWrapping.Wrap
                    },
                    new TextBlock
                    {
                        Text = "Test donor padding creates and hashes a temporary candidate. DumpToolbox replaces the rebuilt BIN only when all available original hashes match; otherwise it discards the candidate and leaves the rebuilt BIN unchanged.",
                        TextWrapping = TextWrapping.Wrap
                    },
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Spacing = 8,
                        Children = { view, test, keep }
                    }
                }
            }
        };

        ApplyThemeClassToWindow(dialog);
        keep.Click += (_, _) =>
        {
            completion.TrySetResult(DicJolietPaddingChoice.KeepStandard);
            dialog.Close();
        };
        test.Click += (_, _) =>
        {
            completion.TrySetResult(DicJolietPaddingChoice.TestDonor);
            dialog.Close();
        };
        view.Click += (_, _) =>
        {
            completion.TrySetResult(DicJolietPaddingChoice.ViewAffectedRecords);
            dialog.Close();
        };
        dialog.Closed += (_, _) => completion.TrySetResult(DicJolietPaddingChoice.KeepStandard);
        _ = dialog.ShowDialog(this);
        return completion.Task;
    }

    private async Task ShowDicJolietPaddingRecordsAsync(
        IReadOnlyList<DicJolietPaddingEvidence> records)
    {
        var details = new StringBuilder();
        foreach (DicJolietPaddingEvidence record in records)
        {
            details.Append("0x")
                .Append(record.PaddingValue.ToString("X2"))
                .Append("  LBA ")
                .Append(record.DirectoryExtentLba + (uint)(record.DirectoryRecordOffset / 2048))
                .Append("  ")
                .AppendLine(record.Path);
        }

        var close = new Button
        {
            Content = "Close",
            HorizontalAlignment = HorizontalAlignment.Right,
            MinWidth = 90,
            IsDefault = true
        };
        var dialog = new Window
        {
            Title = "Affected Joliet directory records",
            Width = 780,
            Height = 520,
            CanResize = true,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new Grid
            {
                Margin = new Thickness(18),
                RowDefinitions = new RowDefinitions("Auto,*,Auto"),
                RowSpacing = 12,
                Children =
                {
                    new TextBlock
                    {
                        Text = $"{records.Count:N0} record(s). Values shown are the donor's non-zero identifier-padding bytes.",
                        TextWrapping = TextWrapping.Wrap
                    },
                    new TextBox
                    {
                        [Grid.RowProperty] = 1,
                        Text = details.ToString(),
                        IsReadOnly = true,
                        AcceptsReturn = true,
                        TextWrapping = TextWrapping.NoWrap,
                        FontFamily = new FontFamily("Consolas"),
                        [ScrollViewer.HorizontalScrollBarVisibilityProperty] = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                        [ScrollViewer.VerticalScrollBarVisibilityProperty] = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
                    },
                    close
                }
            }
        };
        Grid.SetRow(close, 2);
        ApplyThemeClassToWindow(dialog);
        close.Click += (_, _) => dialog.Close();
        await dialog.ShowDialog(this);
    }

    private async Task TryTestDicDonorJolietPaddingAsync(
        SkeletonInspectionResult inspection,
        SkeletonResurrectionResult result,
        bool? rebuiltHashesMatch,
        CancellationToken cancellationToken)
    {
        if (_dicState is null ||
            (!_dicState.TestDonorJolietPadding && !_dicState.DonorJolietPaddingApplied) ||
            _dicState.DonorJolietPaddingRecords.Count == 0)
            return;

        bool paddingWasPreviouslyVerified = _dicState.DonorJolietPaddingApplied;

        if (result.MissingEntries > 0)
        {
            AppendDicLog(
                $"JOLIET CANDIDATE: donor padding test deferred because {result.MissingEntries:N0} required payload(s) remain missing. " +
                "The choice is saved and will be tested after a complete rebuild.");
            return;
        }

        if (rebuiltHashesMatch == true)
        {
            AppendDicLog("JOLIET CANDIDATE: standard output already matches every available original hash; donor padding was not needed.");
            _dicState.TestDonorJolietPadding = false;
            await PersistDicStateAsync(cancellationToken);
            return;
        }

        string candidatePath = result.OutputPath + $".{Guid.NewGuid():N}.joliet-padding-candidate";
        try
        {
            var patchProgress = new Progress<DicDonorProgress>(progress =>
            {
                DicProgressBar.Value = progress.Fraction * 100;
                DicProgressText.Text = progress.Message;
                SetWindowStatus($"DIC — {progress.Message}");
            });
            AppendDicLog(
                $"JOLIET CANDIDATE: testing {_dicState.DonorJolietPaddingRecords.Count:N0} saved non-zero donor padding observation(s) " +
                $"from {_dicState.DonorJolietPaddingSourcePath ?? "the donor"}.");

            DicJolietPaddingCandidateResult candidate = await _dicDonorImageService.CreateJolietPaddingCandidateAsync(
                inspection,
                result.OutputPath,
                candidatePath,
                _dicState.DonorJolietPaddingRecords,
                patchProgress,
                cancellationToken);

            AppendDicLog(
                $"JOLIET CANDIDATE: applied {candidate.AppliedRecords:N0} record(s); " +
                $"{candidate.AlreadyMatchingRecords:N0} already matched the donor; {candidate.SkippedRecords:N0} skipped.");
            foreach (string warning in candidate.Warnings)
                AppendDicLog("JOLIET CANDIDATE: " + warning);

            if (candidate.AppliedRecords == 0)
            {
                _dicState.TestDonorJolietPadding = false;
                AppendDicLog("JOLIET CANDIDATE: no safely identifiable padding bytes required a change; the rebuilt BIN was left unchanged.");
                await PersistDicStateAsync(cancellationToken);
                return;
            }

            bool hasExpectedHashes = HasDicExpectedHashes(inspection);
            var options = hasExpectedHashes
                ? new HashCalculationOptions(
                    Crc32: !string.IsNullOrWhiteSpace(inspection.ExpectedImageCrc32),
                    Md5: !string.IsNullOrWhiteSpace(inspection.ExpectedImageMd5),
                    Sha1: !string.IsNullOrWhiteSpace(inspection.ExpectedImageSha1))
                : new HashCalculationOptions(Crc32: true, Md5: true, Sha1: true);
            var hashProgress = new Progress<HashCalculationProgress>(progress =>
            {
                DicProgressBar.Value = progress.Fraction * 100;
                DicProgressText.Text = "Hashing temporary Joliet-padding candidate";
                SetWindowStatus("DIC — hashing temporary Joliet-padding candidate");
            });
            HashCalculationResult hashes = await _hashCalculationService.CalculateAsync(
                candidate.CandidatePath,
                options,
                hashProgress,
                cancellationToken);

            bool candidateMatches = hasExpectedHashes &&
                LogDicExpectedHashComparison("JOLIET CANDIDATE", inspection, hashes);
            if (!hasExpectedHashes)
            {
                AppendDicLog(
                    $"JOLIET CANDIDATE: CRC32 {hashes.Hashes["CRC32"]}; MD5 {hashes.Hashes["MD5"]}; SHA1 {hashes.Hashes["SHA-1"]}.");
                AppendDicLog("JOLIET CANDIDATE: no original whole-image hashes are available, so the candidate cannot be verified and was discarded.");
            }

            _dicState.TestDonorJolietPadding = false;
            if (candidateMatches)
            {
                cancellationToken.ThrowIfCancellationRequested();
                File.Move(candidate.CandidatePath, result.OutputPath, true);
                _dicState.DonorJolietPaddingApplied = true;
                AppendDicLog(
                    $"JOLIET CANDIDATE: BYTE-EXACT match confirmed. Promoted the verified candidate with {candidate.AppliedRecords:N0} donor padding byte(s) to the rebuilt BIN.");
            }
            else
            {
                TryDeleteDicJolietPaddingCandidate(candidate.CandidatePath);
                _dicState.DonorJolietPaddingApplied = paddingWasPreviouslyVerified;
                if (hasExpectedHashes)
                    AppendDicLog("JOLIET CANDIDATE: original hashes did not all match. The candidate was discarded and the rebuilt BIN was left unchanged.");
            }

            // Once verification has selected or rejected a complete candidate, save
            // that decision atomically even if Cancel was clicked at the boundary.
            await PersistDicStateAsync(CancellationToken.None);
        }
        finally
        {
            TryDeleteDicJolietPaddingCandidate(candidatePath);
            DicProgressBar.Value = 100;
        }
    }

    private bool LogDicExpectedHashComparison(
        string prefix,
        SkeletonInspectionResult inspection,
        HashCalculationResult hashes)
    {
        bool allMatch = true;
        allMatch &= LogDicExpectedHash(prefix, "CRC32", inspection.ExpectedImageCrc32, hashes);
        allMatch &= LogDicExpectedHash(prefix, "MD5", inspection.ExpectedImageMd5, hashes);
        allMatch &= LogDicExpectedHash(prefix, "SHA-1", inspection.ExpectedImageSha1, hashes);
        return allMatch;
    }

    private bool LogDicExpectedHash(
        string prefix,
        string algorithm,
        string? expected,
        HashCalculationResult hashes)
    {
        if (string.IsNullOrWhiteSpace(expected))
            return true;
        if (!hashes.Hashes.TryGetValue(algorithm, out string? actual))
        {
            AppendDicLog($"{prefix}: {algorithm} could not be calculated; expected {expected}.");
            return false;
        }

        bool match = actual.Equals(expected, StringComparison.OrdinalIgnoreCase);
        string displayAlgorithm = algorithm == "SHA-1" ? "SHA1" : algorithm;
        AppendDicLog($"{prefix}: {displayAlgorithm} {(match ? "MATCH" : "DIFFERS")} — expected {expected}, actual {actual}.");
        return match;
    }

    private static bool HasDicExpectedHashes(SkeletonInspectionResult inspection)
        => !string.IsNullOrWhiteSpace(inspection.ExpectedImageCrc32) ||
           !string.IsNullOrWhiteSpace(inspection.ExpectedImageMd5) ||
           !string.IsNullOrWhiteSpace(inspection.ExpectedImageSha1);

    private static void TryDeleteDicJolietPaddingCandidate(string path)
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

