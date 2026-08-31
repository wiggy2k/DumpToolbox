using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using DumpToolbox.Core.Mastering;

namespace DumpToolbox.Core;

public sealed record DicLogSet(
    string BaseName,
    string Directory,
    string? VolDescPath,
    string? DiscPath,
    string? EccEdcPath,
    string? MainInfoPath,
    string? MainErrorPath,
    string? DatPath)
{
    public IReadOnlyList<string> ExistingPaths => new[] { VolDescPath, DiscPath, EccEdcPath, MainInfoPath, MainErrorPath, DatPath }
        .Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
        .Select(path => Path.GetFullPath(path!))
        .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
        .ToArray();
}

public sealed record DicImportProgress(string Phase, long Completed, long Total, string Message)
{
    public double Fraction => Total <= 0 ? 0 : Math.Clamp((double)Completed / Total, 0, 1);
}

public enum DicRecoveryCoverageKind
{
    ExactFromDic,
    DeterministicSynthesis,
    SourcePayload,
    ProvenBytes,
    AssumedZero
}

public sealed record DicRecoveryCoverageItem(
    DicRecoveryCoverageKind Kind,
    string Description,
    long ByteCount,
    long? StartLba = null,
    long? EndLba = null,
    bool DonorCapable = false);

public sealed record DicImportResult(
    SkeletonInspectionResult Inspection,
    DicLogSet Logs,
    int MetadataSectorsRecovered,
    int Mode1Sectors,
    int Mode2Form1Sectors,
    int Mode2Form2Sectors,
    int Mode0Sectors,
    int AudioSectors,
    int UnknownSectors,
    IReadOnlyList<DicRecoveryCoverageItem> CoverageAudit,
    IReadOnlyList<string> Warnings);

public sealed record DicJolietNameUpdateResult(
    bool Updated,
    int MatchedFilesUsed,
    int DicLongAliasesUsed,
    int SourcePathsUsed,
    string Strategy,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Imports the text logs produced by DiscImageCreator and turns them into a synthetic
/// 2352-byte raw skeleton.  The physical sector framing/XA subheaders come from
/// *_EccEdc.txt, filesystem extents come from *_volDesc.txt, and original ISO metadata
/// bytes are copied from *_mainInfo.txt when available.
/// </summary>
public sealed partial class DicLogImportService
{
    private const int CookedSectorSize = SkeletonResurrectionService.CookedSectorSize;
    private const int RawSectorSize = SkeletonResurrectionService.RawSectorSize;

    private static readonly Regex LbaHeadingRegex = new(
        @"^==========\s+LBA\[(?<lba>-?\d+),[^\]]*\]:\s+(?<kind>.*?)\s+==========\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex EccSectorRecordRegex = new(
        @"^LBA\[(?<lba>-?\d+),\s*(?:0x)?(?<hex>[0-9A-Fa-f]+)\](?:,\s*MSF\[(?<msf>[0-9A-Fa-f]{2}:[0-9A-Fa-f]{2}:[0-9A-Fa-f]{2})\])?\s*,?\s*(?<desc>.+?)\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex EccMsfRegex = new(@"MSF\[(?<m>[0-9A-Fa-f]{2}):(?<s>[0-9A-Fa-f]{2}):(?<f>[0-9A-Fa-f]{2})\]", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex EccModeRegex = new(@"\bmode\s+(?<mode>[012])(?:\s+(?:form\s+(?<form>[12])|(?<noedc>no\s+edc)))?", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex InvalidModeRegex = new(@"Invalid\s+mode:\s*\[(?<v>[0-9A-Fa-f]{2})\]", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex EccSubheaderByteRegex = new(@"\[0x1(?<n>[0-7])\]\s*:\s*(?:0x)?(?<v>[0-9A-Fa-f]{2,4})", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex Fill55RecipeRegex = new(@"(?<count>\d+)\s+unmatch\s+sector\s+is\s+replaced\s+at\s+0x55\s+except\s+header", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex PerSectorFill55Regex = new(@"(?<count>2336)\s+bytes\s+have\s+been\s+already\s+replaced\s+at\s+0x55", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex MainErrorAllZeroSkipDescrambleRegex = new(
        @"^LBA\[(?<lba>-?\d+),[^\]]*\]:\s*Track\[\d+\]:\s*All\s+zero\s+sector\.\s*Skip\s+descrambling\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex FileNumberRegex = new(@"FileNum\[(?<v>[0-9A-Fa-f]{2})\]", RegexOptions.Compiled);
    private static readonly Regex ChannelNumberRegex = new(@"ChannelNum\[(?<v>[0-9A-Fa-f]{2})\]", RegexOptions.Compiled);
    private static readonly Regex SubmodeRegex = new(@"Submode\[(?<v>[0-9A-Fa-f]{2})\]", RegexOptions.Compiled);
    private static readonly Regex CodingInfoRegex = new(@"CodingInfo\[(?<v>[0-9A-Fa-f]{2})\]", RegexOptions.Compiled);
    private static readonly Regex MainHexLineRegex = new(@"^\s*(?<ofs>[0-9A-Fa-f]{4,8})\s*:\s*(?<bytes>.*)$", RegexOptions.Compiled);
    private static readonly Regex ImgHashRegex = new(
        @"<rom\s+name=""[^""]*\.img""\s+size=""(?<size>\d+)""\s+crc=""(?<crc>[0-9A-Fa-f]{8})""\s+md5=""(?<md5>[0-9A-Fa-f]{32})""\s+sha1=""(?<sha1>[0-9A-Fa-f]{40})""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex DatRomRegex = new(
        @"<rom\s+name=""(?<name>[^""]+)""\s+size=""(?<size>\d+)""\s+crc=""(?<crc>[0-9A-Fa-f]{8})""\s+md5=""(?<md5>[0-9A-Fa-f]{32})""\s+sha1=""(?<sha1>[0-9A-Fa-f]{40})""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly byte[] CdRawSync =
    {
        0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,
        0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00
    };

    public DicLogSet Discover(string selectedLogLocation)
    {
        if (string.IsNullOrWhiteSpace(selectedLogLocation))
            throw new ArgumentException("Choose the folder containing the DiscImageCreator logs.", nameof(selectedLogLocation));

        string selected = Path.GetFullPath(selectedLogLocation);
        if (Directory.Exists(selected))
            return DiscoverFromDirectory(selected);

        // Backward-compatible core/API path: older saved settings and callers may
        // still provide one companion file.  The GUI now selects the folder.
        if (File.Exists(selected))
        {
            string directory = Path.GetDirectoryName(selected) ?? Directory.GetCurrentDirectory();
            string baseName = StripKnownSuffix(Path.GetFileName(selected));
            return BuildLogSet(directory, baseName);
        }

        throw new FileNotFoundException("DiscImageCreator log folder or companion file not found.", selected);
    }

    private static DicLogSet DiscoverFromDirectory(string directory)
    {
        string[] files = Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly).ToArray();
        var baseNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string path in files)
        {
            string fileName = Path.GetFileName(path);
            if (TryGetDicBaseName(fileName, out string baseName))
                baseNames.Add(baseName);
        }

        if (baseNames.Count == 0)
        {
            throw new InvalidOperationException(
                "The selected folder does not contain a recognizable DiscImageCreator log set. " +
                "Expected companions such as *_volDesc.txt, *_disc.txt, *_mainInfo.txt, *_mainError.txt or *EccEdc.txt.");
        }

        if (baseNames.Count > 1)
        {
            string names = string.Join(", ", baseNames.OrderBy(name => name, StringComparer.OrdinalIgnoreCase));
            throw new InvalidOperationException(
                $"The selected folder contains more than one DiscImageCreator log set ({names}). " +
                "Place/select one log set per folder so DumpToolbox does not guess which disc to import.");
        }

        return BuildLogSet(directory, baseNames.Single());
    }

    private static DicLogSet BuildLogSet(string directory, string baseName)
    {
        string? volDesc = FindCompanion(directory, baseName + "_volDesc.txt");
        string? disc = FindCompanion(directory, baseName + "_disc.txt");
        string? mainInfo = FindCompanion(directory, baseName + "_mainInfo.txt");
        string? mainError = FindCompanion(directory, baseName + "_mainError.txt");
        string? dat = FindCompanion(directory, baseName + ".dat");
        string? eccEdc = FindCompanion(directory, baseName + ".img_EccEdc.txt")
                         ?? FindCompanion(directory, baseName + ".scm_EccEdc.txt")
                         ?? FindCompanion(directory, baseName + "_EccEdc.txt");

        return new DicLogSet(baseName, directory, volDesc, disc, eccEdc, mainInfo, mainError, dat);
    }

    private static bool TryGetDicBaseName(string fileName, out string baseName)
    {
        string[] suffixes =
        {
            ".img_EccEdc.txt",
            ".scm_EccEdc.txt",
            "_EccEdc.txt",
            "_volDesc.txt",
            "_disc.txt",
            "_mainInfo.txt",
            "_mainError.txt"
        };

        foreach (string suffix in suffixes)
        {
            if (!fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                continue;

            baseName = fileName[..^suffix.Length];
            return !string.IsNullOrWhiteSpace(baseName);
        }

        baseName = string.Empty;
        return false;
    }

    public Task<DicImportResult> ImportAsync(
        string selectedLogPath,
        IProgress<DicImportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() => ImportCore(selectedLogPath, progress, cancellationToken), cancellationToken);
    }

    private DicImportResult ImportCore(
        string selectedLogPath,
        IProgress<DicImportProgress>? progress,
        CancellationToken cancellationToken)
    {
        DicLogSet logs = Discover(selectedLogPath);
        var warnings = new List<string>();

        if (logs.VolDescPath is null)
            throw new InvalidOperationException("A matching *_volDesc.txt file is required to reconstruct the ISO filesystem and file extents.");

        progress?.Report(new DicImportProgress("Parsing", 0, 1, "Reading volume descriptor log"));
        DicVolumeInfo volume = ParseVolumeDescription(logs.VolDescPath, cancellationToken);
        if (volume.Files.Count == 0)
            throw new InvalidOperationException("No recoverable file extents were found in the DIC volume descriptor log.");

        if (volume.PathsReconstructedFromIdentifiers > 0)
        {
            warnings.Add(
                $"Older DIC volDesc format detected: no FullPath value was available for " +
                $"{volume.PathsReconstructedFromIdentifiers:N0} file record(s). Their ISO paths were reconstructed " +
                "from the primary path table plus File Identifier values.");
        }

        DicDiscInfo disc = logs.DiscPath is null
            ? new DicDiscInfo()
            : ParseDiscInfo(logs.DiscPath, cancellationToken);

        DicEccEdcParseResult primaryEccEdc = logs.EccEdcPath is null
            ? new DicEccEdcParseResult(
                new Dictionary<long, DicSectorLayout>(),
                Array.Empty<long>(),
                Array.Empty<long>(),
                Array.Empty<long>(),
                0,
                0,
                0,
                0,
                0,
                0,
                null,
                Fill55ExceptHeaderCount: 0)
            : ParseEccEdc(logs.EccEdcPath, cancellationToken);
        Dictionary<long, DicSectorLayout> sectorLayouts = primaryEccEdc.Layouts;

        long sectorCount = ResolveSectorCount(volume, disc, sectorLayouts);
        if (sectorCount <= 0)
            throw new InvalidOperationException("Could not determine the data-track sector count from the DIC logs.");

        long dicProvenMinimumSectorCount = sectorCount;

        // DVD Physical Format Information (PFI) addresses the embossed data zone using
        // absolute DVD physical-sector numbers.  For a single-layer DVD, subtracting
        // StartingDataSector from EndDataSector and adding one yields the complete
        // 2048-byte logical-sector image length.  This length may legitimately exceed
        // ISO9660 Volume Space Size because post-volume sectors are still part of the
        // recorded DVD data zone.  Treat this as independent physical geometry evidence,
        // not as a filesystem size.
        bool dvdPfiAccepted = false;
        if (disc.IsDvd && disc.DvdIsSingleLayer && disc.DvdPfiSectorCount is long pfiSectorCount && pfiSectorCount > 0)
        {
            if (pfiSectorCount < dicProvenMinimumSectorCount)
            {
                warnings.Add(
                    $"DVD PFI describes {pfiSectorCount:N0} logical sector(s), but DIC filesystem/track evidence already requires " +
                    $"at least {dicProvenMinimumSectorCount:N0}; PFI geometry was not used to shrink the image.");
            }
            else
            {
                dvdPfiAccepted = true;
                if (pfiSectorCount > sectorCount)
                {
                    warnings.Add(
                        $"DVD PFI proves a complete recorded data-zone length of {pfiSectorCount:N0} sector(s) " +
                        $"(physical {disc.DvdPfiStartingDataSector:N0}-{disc.DvdPfiEndDataSector:N0}), extending " +
                        $"{pfiSectorCount - sectorCount:N0} sector(s) beyond the {sectorCount:N0}-sector minimum established by " +
                        "DIC filesystem/track evidence. Those trailing sectors are retained as post-volume image space.");
                    sectorCount = pfiSectorCount;
                }
            }
        }

        if (dvdPfiAccepted && disc.DvdSectorCount is long reportedDvdSectors && reportedDvdSectors > 0 &&
            reportedDvdSectors != sectorCount)
        {
            warnings.Add(
                $"disc.txt DVD sector-length evidence ({reportedDvdSectors:N0}) disagrees with the PFI-derived recorded data-zone length " +
                $"({sectorCount:N0}); the independently address-derived PFI length remains authoritative for cooked geometry.");
        }

        // After accepting stronger physical DVD geometry, require DAT evidence to cover that
        // complete proven image rather than only the ISO9660 Volume Space Size.
        dicProvenMinimumSectorCount = sectorCount;
        DicDatImageInfo? datImage = logs.DatPath is null
            ? null
            : TryParseDatImageInfo(logs.DatPath, dicProvenMinimumSectorCount, cancellationToken);
        bool datAccepted = false;
        if (datImage is not null)
        {
            bool datDisagreesWithPfi = dvdPfiAccepted &&
                                       datImage.SectorSize == CookedSectorSize &&
                                       datImage.Size / CookedSectorSize != sectorCount;
            if (datDisagreesWithPfi)
            {
                warnings.Add(
                    $"DIC .dat cooked-image size ({datImage.Size:N0} bytes = {datImage.Size / CookedSectorSize:N0} sector(s)) disagrees with " +
                    $"the independently derived single-layer DVD PFI data-zone length ({sectorCount:N0} sector(s)); PFI geometry remains authoritative " +
                    "and the .dat hash anchor was ignored rather than changing physical capacity.");
            }
            else if (disc.ImageSize is not null && disc.ImageSize != datImage.Size)
            {
                warnings.Add(
                    $"DIC .dat image size ({datImage.Size:N0}) disagrees with disc.txt whole-image size ({disc.ImageSize:N0}); " +
                    "disc.txt remains authoritative and the .dat hash anchor was ignored.");
            }
            else
            {
                datAccepted = true;
                disc.ImageSize ??= datImage.Size;
                disc.ImageCrc32 ??= datImage.Crc32;
                disc.ImageMd5 ??= datImage.Md5;
                disc.ImageSha1 ??= datImage.Sha1;
                warnings.Add(
                    $"Loaded original image size/hash evidence from '{Path.GetFileName(logs.DatPath)}': " +
                    $"{datImage.Name}, {datImage.Size:N0} bytes, CRC32 {datImage.Crc32}, MD5 {datImage.Md5}, SHA1 {datImage.Sha1}.");

                if (datImage.SectorSize == CookedSectorSize)
                {
                    long datSectorCount = datImage.Size / CookedSectorSize;
                    if (datSectorCount > sectorCount)
                    {
                        warnings.Add(
                            $"The .dat proves a cooked ISO length of {datSectorCount:N0} sector(s), extending {datSectorCount - sectorCount:N0} sector(s) beyond the " +
                            $"{sectorCount:N0}-sector minimum established by DIC filesystem/track evidence. Those trailing sectors are retained as post-volume image space rather than forcing Volume Space Size to equal whole-image length.");
                        sectorCount = datSectorCount;
                    }
                }
            }
        }

        bool datOrDiscProvesCooked =
            (datAccepted && datImage?.SectorSize == CookedSectorSize && datImage.Size == checked(sectorCount * (long)CookedSectorSize)) ||
            (disc.IsDvd && disc.ImageSize is long imageSize && imageSize == checked(sectorCount * (long)CookedSectorSize));
        bool dvdGeometryProvesCooked = disc.IsDvd &&
                                       ((dvdPfiAccepted && disc.DvdPfiSectorCount == sectorCount) ||
                                        (disc.DvdSectorCount is long dvdSectors && dvdSectors == sectorCount));
        bool isCooked2048Image = datOrDiscProvesCooked || dvdGeometryProvesCooked;
        bool isRaw2352Image = disc.ImageSize is long rawImageSize &&
                              rawImageSize == checked(sectorCount * (long)RawSectorSize);

        if (isCooked2048Image)
        {
            string evidence = datOrDiscProvesCooked
                ? $"image size {disc.ImageSize:N0} bytes / {sectorCount:N0} sectors"
                : dvdPfiAccepted
                    ? $"DVD PFI physical data zone {disc.DvdPfiStartingDataSector:N0}-{disc.DvdPfiEndDataSector:N0} = {sectorCount:N0} logical sectors"
                    : $"DVD BookType + SectorLength {disc.DvdSectorCount:N0} / volume {sectorCount:N0} sectors";
            warnings.Add(
                $"DIC image geometry identifies a cooked 2048-byte/sector image ({evidence}). " +
                "CD raw sync/MSF/mode/EDC/ECC framing will not be synthesized. This is the expected representation for DVD-class DIC images.");
        }
        else if (!isRaw2352Image && sectorCount > 449_850)
        {
            throw new InvalidDataException(
                $"The DIC image has {sectorCount:N0} sectors, which exceeds the addressable range of a raw CD BCD MSF header, " +
                "but neither DVD PFI/sector-length evidence in disc.txt nor a sibling .dat supplied consistent DVD/cooked-image geometry. " +
                "DVD reconstruction was not guessed.");
        }

        DicSupplementalEccEdcVerification? finalVerification = TryFindCompleteFinalEccEdcVerification(
            logs.Directory,
            logs.EccEdcPath,
            sectorCount,
            cancellationToken);

        bool hasAuthoritativeOriginalImageHash =
            !string.IsNullOrWhiteSpace(disc.ImageCrc32) ||
            !string.IsNullOrWhiteSpace(disc.ImageMd5) ||
            !string.IsNullOrWhiteSpace(disc.ImageSha1);

        DicEccEdcParseResult eccEdc = primaryEccEdc;
        if (finalVerification is not null && !hasAuthoritativeOriginalImageHash)
        {
            eccEdc = finalVerification.ParseResult;
            sectorLayouts = eccEdc.Layouts;
            warnings.Add(
                $"Complete post-repair EDC/ECC verification map '{Path.GetFileName(finalVerification.Path)}' covers all {sectorCount:N0} physical sector(s) in absolute-LBA order. " +
                "Because the DIC logs do not provide an original whole-image hash anchor, this later complete verification map supersedes the earlier DIC per-sector state for final-image reconstruction; the original *_img_EccEdc log remains read/protection history.");
        }
        else if (finalVerification is not null)
        {
            warnings.Add(
                $"A complete later EDC/ECC verification map '{Path.GetFileName(finalVerification.Path)}' was found, but the DIC logs already provide original whole-image hash evidence. " +
                "The hash-anchored DIC image remains the reconstruction target, so the later post-repair map is not allowed to change final-sector state automatically.");
            finalVerification = null;
        }

        int defaultMode = disc.TrackMode ?? (disc.IsXa ? 2 : 1);
        int parsedEccEdcSectorCount = sectorLayouts.Count;
        int trackFallbackSectorCount = ApplyDiscTrackFallbacksAndFillInference(
            disc,
            sectorLayouts,
            sectorCount,
            defaultMode);

        DicExactRawSectorEvidence exactRawSectorEvidence = hasAuthoritativeOriginalImageHash
            ? new DicExactRawSectorEvidence(new Dictionary<long, byte[]>(), Array.Empty<string>(), 0)
            : ParseExactRawSectorFiles(
                logs.Directory,
                sectorCount,
                sectorLayouts,
                cancellationToken);
        IReadOnlyDictionary<long, byte[]> dicExactRawSectorOverrides = exactRawSectorEvidence.Sectors;

        if (dicExactRawSectorOverrides.Count > 0)
        {
            warnings.Add(
                $"Detected {dicExactRawSectorOverrides.Count:N0} validated extensionless 2352-byte raw-sector replacement file(s) named by decimal LBA. " +
                "These exact recovered sectors outrank generated payloads, donor framing, and 0x55/protection recipes at the same physical positions.");
        }
        if (exactRawSectorEvidence.IgnoredCandidateCount > 0)
        {
            warnings.Add(
                $"Ignored {exactRawSectorEvidence.IgnoredCandidateCount:N0} decimal-named extensionless 2352-byte candidate sector file(s) because their raw framing did not validate against the filename LBA/sector map.");
        }

        if (eccEdc.MalformedSectorRecordCount > 0)
        {
            warnings.Add(
                $"The EccEdc per-sector stream contains {eccEdc.MalformedSectorRecordCount:N0} malformed LBA record(s). " +
                $"Physical ordinal tracking was stopped at sector {(eccEdc.StreamTruncatedAtPhysicalSector ?? parsedEccEdcSectorCount):N0}; " +
                "later-looking records were not trusted because a damaged text line can hide one or more physical sectors. disc.txt track evidence is used as fallback coverage where available.");
        }

        if (parsedEccEdcSectorCount > 0 && parsedEccEdcSectorCount < sectorCount)
        {
            warnings.Add(
                $"The reliable EccEdc stream covers {parsedEccEdcSectorCount:N0} of {sectorCount:N0} physical sector(s). " +
                $"disc.txt supplied track-level fallback classification for {trackFallbackSectorCount:N0} uncovered sector(s); " +
                "fallback sectors do not invent protection/header anomalies that were not logged.");
        }

        if (parsedEccEdcSectorCount == 0)
        {
            warnings.Add(disc.Tracks.Count > 0
                ? $"No reliable EccEdc per-sector map was found. disc.txt track geometry is being used as the baseline for {trackFallbackSectorCount:N0} sector(s); data-sector framing within those ranges is conventional unless stronger DIC evidence exists."
                : defaultMode == 2
                    ? "No EccEdc log was found. Mode 2 sectors will use a generic Form 1 XA subheader (00 00 08 00)."
                    : "No EccEdc log was found. Sector framing is being inferred as Mode 1.");
        }

        var dicExactZeroSectorLbas = logs.MainErrorPath is null
            ? new HashSet<long>()
            : ParseMainErrorExactZeroLbas(logs.MainErrorPath, sectorCount, cancellationToken);
        if (dicExactZeroSectorLbas.Count > 0)
        {
            warnings.Add(
                $"mainError explicitly identifies {dicExactZeroSectorLbas.Count:N0} sector(s) as 'All zero sector. Skip descrambling'. " +
                "Those are deterministic final-image sectors and will remain 2352 bytes of 0x00 after source/donor restoration. Generic read-error padding messages are still treated only as retry history.");
        }

        if (eccEdc.ReportedEccErrorCount != eccEdc.EccErrorPhysicalLbas.Count || eccEdc.UnmappedEccErrorCount > 0)
        {
            warnings.Add(
                $"DIC EccEdc reports {eccEdc.ReportedEccErrorCount:N0} ECC/EDC error occurrence(s); " +
                $"{eccEdc.EccErrorPhysicalLbas.Count:N0} were mapped safely to physical image sectors and " +
                $"{eccEdc.UnmappedEccErrorCount:N0} could not be mapped. Reported/header LBAs are not assumed to be physical positions when MSF/sync is damaged.");
        }

        if (eccEdc.ReportedInvalidModeCount > 0)
        {
            warnings.Add(
                $"DIC EccEdc reports {eccEdc.ReportedInvalidModeCount:N0} invalid-mode sector occurrence(s); " +
                $"{eccEdc.InvalidModePhysicalLbas.Count:N0} were overlaid onto the physical sector map from the summary list.");
        }

        var dicMode2Form1QFaultLbas = new HashSet<long>();
        var dicFill55ExceptHeaderLbas = new HashSet<long>();

        // Newer/historical EccEdc builds can state the final sector body directly:
        // "2336 bytes have been already replaced at 0x55".  That is stronger than
        // a protection-name heuristic.  Historical dump-time maps still require an
        // explicitly logged raw MSF/header before the recipe is considered complete.
        // A later *complete* EdcEcc_Track verifier is different: TryFindCompleteFinalEccEdcVerification
        // has already proved that every physical record is present and that reported LBA ==
        // physical LBA for the entire image.  Such verifier lines commonly omit MSF on
        // all-0x55 sectors, but the header address is nevertheless deterministic from the
        // verified physical LBA plus disc.txt track mode.
        int fill55UsingCanonicalVerifierHeader = 0;
        foreach (long lba in eccEdc.ExplicitFill55PhysicalLbas)
        {
            if (!sectorLayouts.TryGetValue(lba, out DicSectorLayout? layout) ||
                layout.Mode is not (1 or 2) ||
                layout.HasInvalidSync || layout.HasZeroSync || layout.SummaryInvalidSync || layout.SummaryZeroSync)
                continue;

            bool hasExplicitLoggedHeader = layout.RawHeaderOverride is { Length: >= 3 };
            bool hasCanonicalFinalVerifierHeader =
                finalVerification is not null &&
                layout.ReportedLba == lba &&
                !layout.HasBadMsf && !layout.SummaryBadMsf;

            if (!hasExplicitLoggedHeader && !hasCanonicalFinalVerifierHeader)
                continue;

            dicFill55ExceptHeaderLbas.Add(lba);
            if (!hasExplicitLoggedHeader && hasCanonicalFinalVerifierHeader)
                fill55UsingCanonicalVerifierHeader++;
        }

        if (eccEdc.ExplicitFill55PhysicalLbas.Count > 0)
        {
            string verifierHeaderNote = fill55UsingCanonicalVerifierHeader > 0
                ? $" {fill55UsingCanonicalVerifierHeader:N0} of those use canonical header addresses proven by the complete post-repair verifier even though its individual 0x55 lines omit MSF."
                : string.Empty;
            warnings.Add(
                $"DIC EccEdc explicitly identifies {eccEdc.ExplicitFill55PhysicalLbas.Count:N0} physical sector(s) whose 2336 bytes after the 16-byte header are 0x55. " +
                $"{dicFill55ExceptHeaderLbas.Count:N0} have enough logged track/header evidence to be rebuilt deterministically; any remainder stays raw-donor exactness evidence." +
                verifierHeaderNote);
        }

        if (eccEdc.ReportedFill55Count > 0 && eccEdc.ReportedFill55Count != eccEdc.ExplicitFill55PhysicalLbas.Count)
        {
            warnings.Add(
                $"The EccEdc summary reports {eccEdc.ReportedFill55Count:N0} all-0x55 sector occurrence(s), while {eccEdc.ExplicitFill55PhysicalLbas.Count:N0} were tied safely to physical per-sector records. " +
                "Only the safely mapped records are used as deterministic fill recipes.");
        }

        bool isKnownWarcraftQFault =
            string.Equals(disc.ImageSha1, "8fae1a878deb63850de4e5a83d5567e28c5ef78b", StringComparison.OrdinalIgnoreCase) &&
            eccEdc.ReportedEccErrorCount == 68736 &&
            eccEdc.EccErrorPhysicalLbas.Count == 68736 &&
            eccEdc.UnmappedEccErrorCount == 0 &&
            eccEdc.EccErrorPhysicalLbas.All(lba =>
                sectorLayouts.TryGetValue(lba, out DicSectorLayout? layout) && layout.Mode == 2 && layout.Form == 1);

        if (isKnownWarcraftQFault)
        {
            dicMode2Form1QFaultLbas.UnionWith(eccEdc.EccErrorPhysicalLbas);
            warnings.Add(
                $"Recognized the proven Warcraft II Mode 2 Form 1 Q-ECC mastering pattern on {dicMode2Form1QFaultLbas.Count:N0} sector(s). " +
                "EDC and P will remain correct while Q is calculated with raw byte 0x873 temporarily forced to 00.");
        }
        else if (eccEdc.Fill55ExceptHeaderCount > 0 &&
                 eccEdc.Fill55ExceptHeaderCount == eccEdc.ReportedEccErrorCount &&
                 eccEdc.EccErrorPhysicalLbas.Count == eccEdc.ReportedEccErrorCount &&
                 eccEdc.ReportedEccErrorCount > 0 &&
                 eccEdc.UnmappedEccErrorCount == 0)
        {
            dicFill55ExceptHeaderLbas.UnionWith(eccEdc.EccErrorPhysicalLbas);
            warnings.Add(
                $"DIC EccEdc explicitly states that {eccEdc.EccErrorPhysicalLbas.Count:N0} unmatched sector(s) are replaced with 0x55 except the 16-byte header. " +
                "DumpToolbox will reproduce that logged final-image recipe only on those mapped physical sectors.");
        }
        else if (eccEdc.Fill55ExceptHeaderCount > 0 &&
                 eccEdc.Fill55ExceptHeaderCount != eccEdc.ReportedEccErrorCount)
        {
            warnings.Add(
                $"DIC EccEdc contains a 0x55-except-header recipe for {eccEdc.Fill55ExceptHeaderCount:N0} sector(s), " +
                $"but the explicit ECC/EDC summary reports {eccEdc.ReportedEccErrorCount:N0}. The counts do not agree, so the recipe will not be applied automatically.");
        }

        var unresolvedEccErrorLbas = eccEdc.EccErrorPhysicalLbas
            .Where(lba => !dicMode2Form1QFaultLbas.Contains(lba) &&
                          !dicFill55ExceptHeaderLbas.Contains(lba) &&
                          !dicExactZeroSectorLbas.Contains(lba))
            .ToArray();
        if (unresolvedEccErrorLbas.Length > 0)
        {
            warnings.Add(
                $"DIC EccEdc identifies {unresolvedEccErrorLbas.Length:N0} mapped physical sector(s) with ECC/EDC mismatch for which no explicit byte-level recipe is logged. " +
                "DumpToolbox will keep these donor-capable, but during final Mode 1 reconstruction it can now recognize the narrowly proven 0x55 mastering form when the recovered 2048-byte user payload is entirely 0x55 and the sector otherwise remains canonical. If stronger non-canonical raw evidence is still present at final-recipe time, the inference is not applied.");
        }

        int perSectorEccMismatchCount = sectorLayouts.Count(pair => pair.Value.HasEccMismatch);
        if (perSectorEccMismatchCount > 0 && eccEdc.ReportedEccErrorCount == 0)
        {
            warnings.Add(
                $"The EccEdc per-sector stream identifies {perSectorEccMismatchCount:N0} ECC/EDC mismatch sector(s) even though no matching final summary count was parsed. " +
                "They remain marked as raw-donor exactness regions unless a proven explicit recipe exists.");
        }

        if (disc.TrackSectorCount is long trackCount && trackCount != sectorCount)
            warnings.Add($"disc.txt reports {trackCount:N0} sectors while the selected reconstruction size is {sectorCount:N0}; the larger consistent value was used.");

        // Associated records are legitimate ISO9660 file records, but a normal mounted
        // filesystem commonly hides their separate payload.  Non-empty Associated records
        // are therefore exposed as distinct source requirements.  DumpToolbox ISO Extractor
        // preserves them in a manifest-backed extraction folder so they can be restored
        // without keeping the source image attached to the DIC workflow.
        DicFileRecord[] associatedPayloadFiles = volume.Files
            .Where(file => IsAssociated(file) && file.DataLength > 0)
            .ToArray();

        int zeroLengthAssociatedCount = volume.Files.Count(file => IsAssociated(file) && file.DataLength == 0);
        if (zeroLengthAssociatedCount > 0)
        {
            warnings.Add(
                $"Detected {zeroLengthAssociatedCount:N0} zero-length ISO 9660 Associated File record(s). " +
                "They carry no additional payload, so no donor is required for those records.");
        }

        int zeroLengthOrdinaryCount = volume.Files.Count(file => !IsAssociated(file) && file.DataLength == 0);
        if (zeroLengthOrdinaryCount > 0)
        {
            warnings.Add(
                $"Detected {zeroLengthOrdinaryCount:N0} zero-length ordinary ISO 9660 file record(s). " +
                "Their directory records are preserved as filesystem metadata, but they contain no payload and require no source file or hash match.");
        }

        if (associatedPayloadFiles.Length > 0)
        {
            warnings.Add(
                $"Detected {associatedPayloadFiles.Length:N0} non-empty ISO 9660 Associated File record(s). " +
                "Do not rely on a normal mounted-filesystem copy for these records. Use the ISO Extractor tab on a source ISO/BIN and then choose that extractor output as the DIC source folder; its manifest preserves the hidden Associated payloads.");
        }

        // A mounted/extracted filesystem cannot be assumed to expose every ISO record
        // that collapses to the same normalized pathname.  Size differences do not make
        // that safe: the second record may simply be hidden by the filesystem driver.
        //
        // Associated/normal pairs are handled separately: the one non-associated record
        // remains the ordinary (PC-visible) source, while the Associated payload is exposed
        // as a distinct manifest-aware source requirement.
        // For two or more NON-associated records with the same normalized path, require an
        // exact donor unless they form a valid Multi-Extent chain (handled below).
        HashSet<string> duplicateNonAssociatedPaths = volume.Files
            .Where(file => !IsAssociated(file))
            .GroupBy(file => file.Path, StringComparer.OrdinalIgnoreCase)
            .Where(group => !group.Any(file => (file.Flags & IsoDirectoryRecordFlags.MultiExtent) != 0))
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        DicFileRecord[] duplicateNonAssociatedFiles = volume.Files
            .Where(file => !IsAssociated(file) && duplicateNonAssociatedPaths.Contains(file.Path))
            .ToArray();

        if (duplicateNonAssociatedFiles.Length > 0)
        {
            warnings.Add(
                $"Detected {duplicateNonAssociatedPaths.Count:N0} normalized ISO 9660 path(s) containing multiple non-associated records " +
                $"({duplicateNonAssociatedFiles.Length:N0} records total). Different byte lengths do NOT prove that both files were exposed by a mounted filesystem, " +
                "so these duplicate records require an exact donor. A normal+Associated pair is not affected by this rule: the normal record remains source-matchable and the Associated record is supplied separately by a DumpToolbox ISO Extractor manifest (or an exact donor scan).");
        }

        DicFileRecord[] ordinaryFiles = volume.Files
            .Where(file => !IsAssociated(file))
            .Where(file => !duplicateNonAssociatedPaths.Contains(file.Path))
            .ToArray();

        DicFileRecord[] associatedSourceFiles = associatedPayloadFiles
            .Select(file => file with
            {
                OriginalPath = file.Path,
                Path = BuildAssociatedSourceKey(file),
                Aliases = (file.Aliases ?? Array.Empty<string>())
                    .Append(file.Path)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray()
            })
            .ToArray();

        int existenceCount = ordinaryFiles.Count(file => (file.Flags & IsoDirectoryRecordFlags.Existence) != 0);
        if (existenceCount > 0)
        {
            warnings.Add(
                $"Detected {existenceCount:N0} ISO 9660 Existence-flagged record(s) (File Flags 0x01). " +
                "They remain eligible for normal source matching, but some mounted filesystems may hide them.");
        }

        int reservedFlagCount = volume.Files.Count(file => (((byte)file.Flags) & 0x60) != 0);
        if (reservedFlagCount > 0)
        {
            warnings.Add(
                $"Detected {reservedFlagCount:N0} ISO 9660 record(s) with reserved File Flag bits 0x20/0x40 set. " +
                "The disc is non-standard, but DIC records the exact flag byte, so DumpToolbox will preserve it and attempt normal payload recovery rather than requiring a donor solely for those bits.");
        }

        int recordProtectionWithoutXar = volume.Files.Count(file =>
            file.ExtendedAttributeRecordLength == 0 &&
            (file.Flags & (IsoDirectoryRecordFlags.Record | IsoDirectoryRecordFlags.Protection)) != 0);
        if (recordProtectionWithoutXar > 0)
        {
            warnings.Add(
                $"Detected {recordProtectionWithoutXar:N0} record(s) with Record/Protection flags but no Extended Attribute Record blocks. " +
                "The known flag byte will be preserved; no donor is required because there are no separate EAR bytes to recover.");
        }

        int interleavedCount = ordinaryFiles.Count(file => file.FileUnitSize != 0 || file.InterleaveGapSize != 0);
        if (interleavedCount > 0)
        {
            warnings.Add(
                $"Detected {interleavedCount:N0} ISO 9660 interleaved file record(s). " +
                "DumpToolbox will reconstruct them from ordinary source files using the logged File Unit Size and Interleave Gap Size; gap sectors are left for their own recovery owners.");
        }

        int multiExtentRecordCount = ordinaryFiles.Count(file => (file.Flags & IsoDirectoryRecordFlags.MultiExtent) != 0);
        if (multiExtentRecordCount > 0)
        {
            warnings.Add(
                $"Detected {multiExtentRecordCount:N0} ISO 9660 Multi-Extent record(s) (File Flags 0x80). " +
                "DumpToolbox will treat consecutive same-path file sections as one logical source file and distribute its bytes across the recorded extents.");
        }

        var donorRequirementList = new List<SkeletonDonorRequirement>();

        // Multiple non-associated records that normalize to the same pathname are not
        // assumed to be available as separate mounted files, even when their sizes differ.
        // A donor supplies each record's exact on-disc payload without guessing which one
        // the host filesystem chose to expose.
        foreach (DicFileRecord file in duplicateNonAssociatedFiles)
            donorRequirementList.Add(BuildFullPayloadDonorRequirement(file, sectorLayouts, defaultMode, sectorCount, "duplicate non-associated ISO pathname"));

        // EAR bytes are the important donor-only part. The ordinary file payload that
        // follows an EAR remains recoverable from the extracted file and is NOT copied
        // from the donor unless some independent condition above requires the full record.
        foreach (DicFileRecord file in volume.Files.Where(file => file.ExtendedAttributeRecordLength > 0))
        {
            bool alreadyCovered = duplicateNonAssociatedFiles.Contains(file);
            if (!alreadyCovered)
                donorRequirementList.Add(BuildXarDonorRequirement(file, sectorLayouts, defaultMode, sectorCount));
        }

        foreach (DicFileRecord directory in volume.DonorOnlyRecords.Where(file => file.ExtendedAttributeRecordLength > 0))
            donorRequirementList.Add(BuildXarDonorRequirement(directory, sectorLayouts, defaultMode, sectorCount));

        // The path table also carries a directory Extended Attribute Record length.
        // This catches the root directory and any directory XAR not represented by a
        // parsed directory record. Only the XAR blocks themselves need a donor.
        foreach (DicPathTableRecord pathRecord in volume.PrimaryPathTableRecords.Where(record => record.ExtendedAttributeLength > 0))
        {
            if (donorRequirementList.Any(requirement =>
                    requirement.ExtentLba == pathRecord.ExtentLba &&
                    requirement.ExtendedAttributeRecordLength == pathRecord.ExtendedAttributeLength &&
                    requirement.Reason.Contains("Extended Attribute", StringComparison.OrdinalIgnoreCase)))
                continue;

            long xarSectors = pathRecord.ExtendedAttributeLength;
            bool containsForm2 = RegionContainsForm2(pathRecord.ExtentLba, xarSectors, sectorLayouts, defaultMode, sectorCount);

            donorRequirementList.Add(new SkeletonDonorRequirement(
                $"<directory XAR at LBA {pathRecord.ExtentLba:N0}>",
                pathRecord.ExtentLba,
                0,
                xarSectors,
                containsForm2,
                0,
                pathRecord.ExtendedAttributeLength,
                0,
                0,
                $"directory Extended Attribute Record ({pathRecord.ExtendedAttributeLength} block(s))",
                RequireRecordMatch: false));
        }

        IReadOnlyList<SkeletonDonorRequirement> mandatoryDonorRequirements = donorRequirementList
            .GroupBy(requirement => $"{requirement.Path}\u001F{requirement.ExtentLba}\u001F{requirement.PhysicalSectorCount}\u001F{requirement.Reason}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();

        if (mandatoryDonorRequirements.Count > 0)
        {
            string reasons = string.Join(", ", mandatoryDonorRequirements
                .Select(requirement => requirement.Reason)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(6));
            warnings.Add(
                $"Only {mandatoryDonorRequirements.Count:N0} genuinely unavailable on-disc region(s) require an exact same-disc ISO/BIN donor ({reasons}). " +
                "Normal file payloads, Multi-Extent files and interleaved files remain recoverable from ordinary extracted files.");
        }

        Dictionary<long, byte[]> metadata = logs.MainInfoPath is null
            ? new Dictionary<long, byte[]>()
            : ParseMainInfoMetadata(logs.MainInfoPath, cancellationToken);
        HashSet<long> exactMainInfoLbas = metadata.Keys.ToHashSet();
        int exactMainInfoOutsidePrimaryMetadata = exactMainInfoLbas.Count(lba => !volume.MetadataLbas.Contains(lba));
        if (exactMainInfoOutsidePrimaryMetadata > 0)
        {
            warnings.Add(
                $"Recovered {exactMainInfoOutsidePrimaryMetadata:N0} complete original Main Channel sector(s) from mainInfo outside the primary ISO9660 metadata whitelist. " +
                "These exact original-disc bytes are retained as evidence (including supplementary/Joliet or non-file sectors where present) instead of being discarded.");
        }

        DicOffsetEvidenceResult offsetEvidence = logs.MainInfoPath is null
            ? new DicOffsetEvidenceResult(new Dictionary<long, DicPayloadEvidence>(), 0, 0, 0)
            : ParseMainInfoOffsetEvidence(logs.MainInfoPath, cancellationToken);

        // DIC's early "Check Drive + CD offset" captures are raw/scrambled main-channel
        // reads.  When adjacent captures overlap we can stitch them into exact sectors,
        // descramble them, and recover otherwise-unlogged ISO system-area payload bytes.
        // Partial captures are also useful: known bytes are copied into the synthetic
        // payload while the remaining unknown bytes stay zero and are reported explicitly.
        foreach (KeyValuePair<long, DicPayloadEvidence> pair in offsetEvidence.Payloads)
        {
            long lba = pair.Key;
            DicPayloadEvidence evidence = pair.Value;
            if (lba < 0 || lba >= Math.Min(16, sectorCount) || evidence.KnownByteCount == 0)
                continue;
            if (exactMainInfoLbas.Contains(lba))
                continue;

            byte[] payload = new byte[CookedSectorSize];
            for (int i = 0; i < CookedSectorSize; i++)
            {
                if (evidence.Known[i])
                    payload[i] = evidence.Data[i];
            }
            metadata[lba] = payload;
        }

        if (offsetEvidence.ConflictCount > 0)
        {
            warnings.Add(
                $"DIC drive-offset captures disagreed on {offsetEvidence.ConflictCount:N0} recovered system-area byte(s). " +
                "Conflicting bytes were not trusted and remain zero-assumed unless supplied by a donor.");
        }

        // CeQuadrat/WinOnCD writes a deterministic formatter-information block in
        // the final logical sector of the ISO volume. DIC's volDesc/PVD evidence
        // proves both the mastering family and Volume Space Size, while mainInfo
        // may legitimately omit this private non-filesystem sector. Recreate it
        // only when no stronger exact metadata evidence already occupies VSS-1.
        TrySynthesizeCeQuadratFormatterInformationBlock(volume, metadata, warnings);

        HashSet<long> beforeSynthesis = metadata.Keys.ToHashSet();

        // volDesc contains the parsed primary ISO9660 path-table records even when
        // mainInfo does not include raw path-table sectors. Rebuild those sectors
        // before generating the raw image so they are not left as zero user data.
        SynthesizePrimaryIsoPathTables(volume, metadata, warnings);
        HashSet<long> synthesizedMetadataLbas = metadata.Keys
            .Where(lba => !beforeSynthesis.Contains(lba))
            .ToHashSet();

        HashSet<long> missingMetadataLbas = volume.MetadataLbas
            .Where(lba => lba >= 0 && lba < sectorCount && !metadata.ContainsKey(lba))
            .ToHashSet();

        if (logs.MainInfoPath is null)
            warnings.Add("No *_mainInfo.txt file was found, so original ISO9660 metadata bytes cannot be pre-populated in the synthetic image.");
        else
        {
            if (missingMetadataLbas.Count > 0)
                warnings.Add($"mainInfo.txt did not contain a complete raw user-data dump for {missingMetadataLbas.Count:N0} expected ISO9660 metadata sector(s); those sectors remain zero-filled unless an exact same-disc donor supplies them.");

            // Primary ISO9660 remains authoritative at import time. Supplementary
            // descriptors are preserved now; a missing Joliet directory/path-table tree
            // may be synthesized later only after a matched Joliet source folder supplies
            // the names/casing that DIC did not log.
            ReportIgnoredSupplementaryDescriptors(metadata, warnings);
        }

        IReadOnlyList<SkeletonContentEntry> entries = BuildContentEntries(
            ordinaryFiles.Concat(associatedSourceFiles).ToArray(),
            sectorLayouts,
            defaultMode,
            sectorCount,
            warnings);

        IReadOnlyList<DicFileSlackRegion> slackRegions = FindFileTailSlackRegions(
            entries,
            sectorLayouts,
            defaultMode,
            sectorCount);

        IReadOnlyList<DicUnclaimedSectorRegion> unclaimedRegions = FindUnclaimedVolumeRegions(
            volume,
            sectorCount,
            entries,
            metadata.Keys);

        DicApplePartitionMapInfo? applePartitionMap = TryParseApplePartitionMap(offsetEvidence.Payloads);
        IReadOnlyList<DicHfsPartitionInspection> hfsPartitions = BuildHfsPartitionInspections(applePartitionMap, metadata);
        HashSet<long> beforeHfsSynthesis = metadata.Keys.ToHashSet();
        hfsPartitions = SynthesizeClassicHfsPhase1(volume, entries, hfsPartitions, metadata, warnings);
        synthesizedMetadataLbas.UnionWith(metadata.Keys.Where(lba => !beforeHfsSynthesis.Contains(lba)));
        foreach (DicHfsPartitionInspection partition in hfsPartitions)
        {
            long endByte = checked((partition.StartBlock + partition.BlockCount) * 512L - 1);
            long endLba = endByte / CookedSectorSize;
            string mdbEvidence = partition.MasterDirectoryBlockPresentInDicEvidence
                ? "MDB bytes are present in DIC evidence"
                : partition.Phase1Synthesized
                    ? "MDB bytes were not present in DIC evidence; v0.3.1 generated a provisional classic-HFS phase-1 seed (MDB, allocation bitmap and empty B-tree header scaffolds) from proven partition/ISO geometry"
                    : "MDB bytes are not present in DIC evidence and remain zero in the synthetic skeleton until HFS reconstruction or a same-disc donor supplies them";
            warnings.Add(
                $"Apple hybrid partition map detected from DIC-proven system-area bytes: {partition.Name} ({partition.Type}), " +
                $"512-byte blocks {partition.StartBlock:N0}-{partition.StartBlock + partition.BlockCount - 1:N0} " +
                $"(CD LBA {partition.PartitionStartLba:N0}+0x{partition.PartitionStartByteOffset:X3}-{endLba:N0}). " +
                $"Classic HFS MDB is expected at CD LBA {partition.MasterDirectoryBlockLba:N0}+0x{partition.MasterDirectoryBlockByteOffset:X3}; " +
                $"volume bitmap begins at CD LBA {partition.VolumeBitmapStartLba:N0}+0x{partition.VolumeBitmapStartByteOffset:X3}. {mdbEvidence}.");

            if (partition.MasterDirectoryBlock is DicHfsMasterDirectoryBlock mdb)
            {
                warnings.Add(
                    $"HFS MDB inspection: volume '{mdb.VolumeName}', allocation blocks {mdb.AllocationBlockCount:N0} x {mdb.AllocationBlockSize:N0} bytes, " +
                    $"first allocation block {mdb.FirstAllocationBlock:N0}, free blocks {mdb.FreeAllocationBlocks:N0}, next CNID {mdb.NextCatalogNodeId:N0}, " +
                    $"catalog file {mdb.CatalogFileSize:N0} bytes, extents overflow file {mdb.ExtentsOverflowFileSize:N0} bytes.");
            }
            else if (partition.Phase1Synthesized && partition.SynthesizedMasterDirectoryBlock is DicHfsMasterDirectoryBlock synthesizedMdb)
            {
                warnings.Add(
                    $"HFS PHASE1: generated provisional MDB for volume '{synthesizedMdb.VolumeName}', allocation blocks {synthesizedMdb.AllocationBlockCount:N0} x {synthesizedMdb.AllocationBlockSize:N0} bytes, " +
                    $"first allocation block {synthesizedMdb.FirstAllocationBlock:N0}; bitmap marks {partition.SynthesizedBitmapUsedBlocks:N0} used / {partition.SynthesizedBitmapFreeBlocks:N0} free block(s). " +
                    "Catalog/Extents files currently contain empty B-tree header scaffolds only; catalog records, Finder metadata/resource-fork ownership and Toast's exact original MDB values are not guessed in v0.3.1.");
            }
        }

        IReadOnlyList<SkeletonDonorRequirement> optionalExactnessRequirements = BuildOptionalExactnessDonorRequirements(
            volume,
            sectorCount,
            sectorLayouts,
            defaultMode,
            slackRegions,
            unclaimedRegions,
            offsetEvidence.Payloads,
            synthesizedMetadataLbas,
            missingMetadataLbas);

        var knownFinalRecipeLbas = new HashSet<long>(dicMode2Form1QFaultLbas);
        knownFinalRecipeLbas.UnionWith(dicFill55ExceptHeaderLbas);
        knownFinalRecipeLbas.UnionWith(dicExactZeroSectorLbas);
        knownFinalRecipeLbas.UnionWith(dicExactRawSectorOverrides.Keys);
        IReadOnlyList<SkeletonDonorRequirement> eccEdcExactnessRequirements = isCooked2048Image
            ? Array.Empty<SkeletonDonorRequirement>()
            : BuildEccEdcExactnessDonorRequirements(
                sectorLayouts,
                eccEdc.EccErrorPhysicalLbas,
                knownFinalRecipeLbas,
                sectorCount);

        IReadOnlyList<SkeletonDonorRequirement> donorRequirements = mandatoryDonorRequirements
            .Concat(optionalExactnessRequirements)
            .Concat(eccEdcExactnessRequirements)
            .GroupBy(requirement => $"{requirement.Path}\u001F{requirement.ExtentLba}\u001F{requirement.PhysicalSectorCount}\u001F{requirement.Reason}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();

        IReadOnlyList<DicRecoveryCoverageItem> coverageAudit = BuildRecoveryCoverageAudit(
            volume,
            sectorCount,
            entries,
            slackRegions,
            unclaimedRegions,
            sectorLayouts,
            defaultMode,
            exactMainInfoLbas,
            offsetEvidence.Payloads,
            synthesizedMetadataLbas,
            missingMetadataLbas);

        long assumedZeroBytes = coverageAudit
            .Where(item => item.Kind == DicRecoveryCoverageKind.AssumedZero)
            .Sum(item => item.ByteCount);
        if (assumedZeroBytes > 0)
        {
            warnings.Add(
                $"Exactness audit found {assumedZeroBytes:N0} user-data byte(s) that are currently reconstructed as zero because DIC logs/ordinary files do not prove their original value. " +
                "These regions are donor-capable but do not block a best-effort rebuild; the original whole-image hashes remain the final authority.");
        }

        int reportedLbaDiffersFromPhysicalCount = sectorLayouts.Count(pair => pair.Value.ReportedLba != pair.Key);
        int badMsfLoggedCount = sectorLayouts.Count(pair => pair.Value.HasBadMsf || pair.Value.SummaryBadMsf);
        int invalidSyncLoggedCount = sectorLayouts.Count(pair =>
            pair.Value.HasInvalidSync || pair.Value.HasZeroSync ||
            pair.Value.SummaryInvalidSync || pair.Value.SummaryZeroSync);

        if (reportedLbaDiffersFromPhysicalCount > 0)
        {
            warnings.Add(
                $"For {reportedLbaDiffersFromPhysicalCount:N0} EccEdc sector record(s), the header-derived/reported LBA differs from the sector's physical position in the IMG (for example because of track/session gaps or malformed headers). " +
                "DumpToolbox now keys the sector map by physical record ordinal and preserves the logged MSF bytes separately, so reported-LBA discontinuities cannot redirect reconstruction writes.");
        }

        if (badMsfLoggedCount > 0)
            warnings.Add($"The EccEdc map identifies {badMsfLoggedCount:N0} sector(s) with non-canonical MSF. The three logged raw MSF bytes are preserved instead of being regenerated from physical LBA.");

        if (invalidSyncLoggedCount > 0)
            warnings.Add($"The EccEdc map identifies {invalidSyncLoggedCount:N0} sector(s) with invalid or zero sync. DumpToolbox will not normalize those sectors when an exact raw donor supplies their framing.");

        int mode0LoggedCount = sectorLayouts.Count(pair => pair.Value.Mode == 0);
        int audioLoggedCount = sectorLayouts.Count(pair => pair.Value.IsAudio);
        int unknownLoggedCount = sectorLayouts.Count(pair => pair.Value.IsUnknown);
        int blockIndicatorCount = sectorLayouts.Count(pair => pair.Value.HasBlockIndicators);
        int unequalXaSubheaderCount = sectorLayouts.Count(pair => pair.Value.XaSubheaderCopiesDiffer);

        if (mode0LoggedCount > 0)
            warnings.Add($"The EccEdc map contains {mode0LoggedCount:N0} Mode 0 sector(s); DumpToolbox now preserves Mode 0 as a distinct final sector class instead of treating it as Mode 1/2 data.");

        if (audioLoggedCount > 0)
            warnings.Add($"The EccEdc map contains {audioLoggedCount:N0} audio sector(s). Audio is opaque 2352-byte content and cannot be synthesized from ISO files; exact raw donor/capture coverage is preferred for byte-perfect regeneration.");

        if (unknownLoggedCount > 0)
            warnings.Add($"The EccEdc map contains {unknownLoggedCount:N0} sector(s) whose final data-sector mode could not be identified safely. These are retained as raw-donor exactness regions rather than guessed.");

        if (blockIndicatorCount > 0)
            warnings.Add($"The EccEdc map contains {blockIndicatorCount:N0} sector(s) with Mode-byte Block Indicators. The class is known but the complete raw Mode byte is not printed by EccEdc, so an exact raw donor/capture is required for byte-perfect reproduction.");

        if (unequalXaSubheaderCount > 0)
            warnings.Add($"The EccEdc map contains {unequalXaSubheaderCount:N0} Mode 2 sector(s) whose two XA subheader copies differ. DumpToolbox will preserve all eight logged subheader bytes independently instead of normalizing the copies.");

        string skeletonPath = Path.Combine(logs.Directory, logs.BaseName + "_DIC_skeleton.bin");
        int mode1Count;
        int mode2Form1Count;
        int mode2Form2Count;
        if (isCooked2048Image)
        {
            BuildSyntheticCookedSkeleton(
                skeletonPath,
                sectorCount,
                metadata,
                progress,
                cancellationToken);
            mode1Count = 0;
            mode2Form1Count = 0;
            mode2Form2Count = 0;
        }
        else
        {
            BuildSyntheticSkeleton(
                skeletonPath,
                sectorCount,
                sectorLayouts,
                defaultMode,
                metadata,
                dicMode2Form1QFaultLbas,
                dicFill55ExceptHeaderLbas,
                dicExactZeroSectorLbas,
                dicExactRawSectorOverrides,
                progress,
                cancellationToken,
                out mode1Count,
                out mode2Form1Count,
                out mode2Form2Count);
        }

        if (mode2Form2Count > 0)
        {
            warnings.Add(
                $"The EccEdc map contains {mode2Form2Count:N0} Mode 2 Form 2 sector(s). " +
                "Their exact DIC XA subheaders are preserved and 2324-byte payload placement is supported, " +
                "but Form 2 recovery should be verified against a known-good image when possible.");
        }

        HashSet<long> noEdcLbas = sectorLayouts
            .Where(pair => pair.Value.Mode == 2 && pair.Value.Form == 2 && !pair.Value.HasEdc)
            .Select(pair => pair.Key)
            .ToHashSet();

        if (noEdcLbas.Count > 0)
        {
            warnings.Add(
                $"The EccEdc map contains {noEdcLbas.Count:N0} Mode 2 Form 2 sector(s) with no EDC. " +
                "These sectors will keep the four-byte EDC/spare field zero and will not receive P/Q ECC.");
        }

        IReadOnlyDictionary<long, byte[]> rawHeaderOverrides = sectorLayouts
            .Where(pair => !pair.Value.IsAudio && pair.Value.RawHeaderOverride is { Length: > 0 })
            .ToDictionary(pair => pair.Key, pair => pair.Value.RawHeaderOverride!);
        IReadOnlyDictionary<long, byte[]> xaSubheaderOverrides = sectorLayouts
            .Where(pair => pair.Value.XaSubheaderOverride is { Length: 8 })
            .ToDictionary(pair => pair.Key, pair => pair.Value.XaSubheaderOverride!);

        IReadOnlyList<string> companionPaths = logs.ExistingPaths
            .Concat(finalVerification is null ? Array.Empty<string>() : new[] { finalVerification.Path })
            .Concat(exactRawSectorEvidence.Paths)
            .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
            .ToArray();

        var inspection = new SkeletonInspectionResult(
            skeletonPath,
            logs.VolDescPath,
            isCooked2048Image ? SkeletonImageKind.Cooked2048 : SkeletonImageKind.Raw2352,
            isCooked2048Image ? CookedSectorSize : RawSectorSize,
            0,
            sectorCount,
            entries,
            volume.VolumeIdentifier,
            entries.Count(e => e.RequiresSource),
            0,
            SkeletonSourceKind.DiscImageCreator,
            disc.ImageCrc32,
            disc.ImageMd5,
            disc.ImageSha1,
            companionPaths,
            isCooked2048Image ? new HashSet<long>() : noEdcLbas,
            isCooked2048Image ? new HashSet<long>() : dicMode2Form1QFaultLbas,
            isCooked2048Image ? new HashSet<long>() : dicFill55ExceptHeaderLbas,
            isCooked2048Image ? new HashSet<long>() : dicExactZeroSectorLbas,
            isCooked2048Image ? new Dictionary<long, byte[]>() : rawHeaderOverrides,
            isCooked2048Image ? new Dictionary<long, byte[]>() : xaSubheaderOverrides,
            donorRequirements,
            isCooked2048Image ? new Dictionary<long, byte[]>() : dicExactRawSectorOverrides,
            isCooked2048Image ? new HashSet<long>() : unresolvedEccErrorLbas.ToHashSet(),
            exactMainInfoLbas,
            volume.SupplementaryDirectoryHints,
            hfsPartitions);

        progress?.Report(new DicImportProgress("Complete", sectorCount, sectorCount, "DIC synthetic skeleton ready"));
        return new DicImportResult(
            inspection,
            logs,
            metadata.Count,
            mode1Count,
            mode2Form1Count,
            mode2Form2Count,
            mode0LoggedCount,
            audioLoggedCount,
            unknownLoggedCount,
            coverageAudit,
            warnings);
    }

}
