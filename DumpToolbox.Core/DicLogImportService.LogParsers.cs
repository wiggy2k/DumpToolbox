using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using DumpToolbox.Core.Mastering;

namespace DumpToolbox.Core;

public sealed partial class DicLogImportService
{
    private static void ReadExactly(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int read = stream.Read(buffer, offset, buffer.Length - offset);
            if (read == 0)
                throw new EndOfStreamException("Unexpected end of DIC synthetic skeleton while updating Joliet metadata.");
            offset += read;
        }
    }

    private static DicVolumeInfo ParseVolumeDescription(string path, CancellationToken cancellationToken)
    {
        string volumeIdentifier = string.Empty;
        long volumeSpaceSize = 0;
        DateTimeOffset? volumeRecordingTime = null;
        var metadataLbas = new HashSet<long>();
        var primaryDirectoryLbas = new HashSet<long>();
        var records = new List<DicFileRecord>();
        var donorOnlyRecords = new List<DicFileRecord>();
        DicFileRecordBuilder? current = null;
        long? primaryPathTableLba = null;
        long primaryPathTableSize = 0;
        var primaryPathTableRecords = new List<DicPathTableRecord>();
        long supplementaryPathTableSize = 0;
        var supplementaryPathTableLocations = new HashSet<long>();
        var supplementaryPathTableRecords = new List<DicPathTableRecord>();
        var supplementaryDirectoryHints = new List<DicSupplementaryDirectoryHint>();
        var supplementaryDirectoryPathByNumber = new Dictionary<int, string>();
        DicPathTableRecordBuilder? currentSupplementaryPathTableRecord = null;
        var primaryDirectoryPathByNumber = new Dictionary<int, string>();
        var primaryDirectoryPathByStartLba = new Dictionary<long, string>();
        var primaryDirectoryPathBySectorLba = new Dictionary<long, string>();
        DicPathTableRecordBuilder? currentPathTableRecord = null;
        string currentDirectoryPath = string.Empty;
        int? currentVolumeDescriptorType = null;
        var primaryDescriptorVolumeSpaceSizes = new List<long>();
        var supplementaryDescriptorVolumeSpaceSizes = new List<long>();
        bool inPrimaryPathTable = false;
        bool inSupplementaryPathTable = false;
        bool inPrimaryDirectory = false;

        void FlushPathTableRecord()
        {
            if (currentPathTableRecord is null)
                return;

            if (currentPathTableRecord.IdentifierLength >= 0 &&
                currentPathTableRecord.ExtentLba >= 0 &&
                currentPathTableRecord.ParentDirectoryNumber >= 0)
            {
                byte[] identifier;
                if (currentPathTableRecord.IdentifierLength == 1 &&
                    (string.IsNullOrEmpty(currentPathTableRecord.Identifier) || currentPathTableRecord.Identifier[0] == '\0'))
                {
                    identifier = new byte[] { 0 };
                }
                else
                {
                    identifier = Encoding.ASCII.GetBytes(currentPathTableRecord.Identifier ?? string.Empty);
                    if (identifier.Length != currentPathTableRecord.IdentifierLength)
                        Array.Resize(ref identifier, currentPathTableRecord.IdentifierLength);
                }

                primaryPathTableRecords.Add(new DicPathTableRecord(
                    checked((byte)currentPathTableRecord.IdentifierLength),
                    checked((byte)currentPathTableRecord.ExtendedAttributeLength),
                    checked((uint)currentPathTableRecord.ExtentLba),
                    checked((ushort)currentPathTableRecord.ParentDirectoryNumber),
                    identifier));

                // Older DiscImageCreator volDesc logs do not emit FullPath: lines for
                // directory records.  ISO 9660 path-table records are ordered by directory
                // number and carry the parent directory number, so reconstruct the primary
                // directory tree here and use it later to qualify File Identifier values.
                int directoryNumber = primaryPathTableRecords.Count;
                string directoryPath;
                bool isRoot = directoryNumber == 1 ||
                              (identifier.Length == 1 && identifier[0] == 0);
                if (isRoot)
                {
                    directoryPath = "/";
                }
                else
                {
                    string identifierText = Encoding.ASCII.GetString(identifier).TrimEnd('\0');
                    if (!primaryDirectoryPathByNumber.TryGetValue(
                            currentPathTableRecord.ParentDirectoryNumber,
                            out string? parentPath))
                    {
                        parentPath = "/";
                    }

                    directoryPath = NormalizeIsoPath(
                        parentPath.TrimEnd('/') + "/" + identifierText);
                }

                primaryDirectoryPathByNumber[directoryNumber] = directoryPath;
                primaryDirectoryPathByStartLba[currentPathTableRecord.ExtentLba] = directoryPath;
                primaryDirectoryPathBySectorLba[currentPathTableRecord.ExtentLba] = directoryPath;
            }

            currentPathTableRecord = null;
        }

        void FlushSupplementaryPathTableRecord()
        {
            if (currentSupplementaryPathTableRecord is null)
                return;

            if (currentSupplementaryPathTableRecord.IdentifierLength >= 0 &&
                currentSupplementaryPathTableRecord.ExtentLba >= 0 &&
                currentSupplementaryPathTableRecord.ParentDirectoryNumber >= 0)
            {
                byte[] identifier;
                if (currentSupplementaryPathTableRecord.IdentifierLength == 1 &&
                    (string.IsNullOrEmpty(currentSupplementaryPathTableRecord.Identifier) || currentSupplementaryPathTableRecord.Identifier[0] == '\0'))
                {
                    identifier = new byte[] { 0 };
                }
                else
                {
                    // volDesc renders the supplementary directory identifier as readable
                    // text.  The raw path-table bytes are UCS-2BE, but the recovery model
                    // only needs the namespace path plus the original extent/parent order.
                    identifier = Encoding.UTF8.GetBytes(currentSupplementaryPathTableRecord.Identifier ?? string.Empty);
                }

                supplementaryPathTableRecords.Add(new DicPathTableRecord(
                    checked((byte)Math.Min(255, Math.Max(0, currentSupplementaryPathTableRecord.IdentifierLength))),
                    checked((byte)currentSupplementaryPathTableRecord.ExtendedAttributeLength),
                    checked((uint)currentSupplementaryPathTableRecord.ExtentLba),
                    checked((ushort)currentSupplementaryPathTableRecord.ParentDirectoryNumber),
                    identifier));

                int directoryNumber = supplementaryPathTableRecords.Count;
                string directoryPath;
                bool isRoot = directoryNumber == 1 ||
                              (currentSupplementaryPathTableRecord.IdentifierLength == 1 &&
                               string.IsNullOrEmpty(currentSupplementaryPathTableRecord.Identifier));
                if (isRoot)
                {
                    directoryPath = "/";
                }
                else
                {
                    string identifierText = (currentSupplementaryPathTableRecord.Identifier ?? string.Empty).TrimEnd('\0');
                    if (!supplementaryDirectoryPathByNumber.TryGetValue(
                            currentSupplementaryPathTableRecord.ParentDirectoryNumber,
                            out string? parentPath))
                    {
                        parentPath = "/";
                    }
                    directoryPath = NormalizeIsoPath(parentPath.TrimEnd('/') + "/" + identifierText);
                }

                supplementaryDirectoryPathByNumber[directoryNumber] = directoryPath;
                supplementaryDirectoryHints.Add(new DicSupplementaryDirectoryHint(
                    directoryPath,
                    checked((uint)currentSupplementaryPathTableRecord.ExtentLba),
                    checked((ushort)currentSupplementaryPathTableRecord.ParentDirectoryNumber),
                    directoryNumber));
            }

            currentSupplementaryPathTableRecord = null;
        }

        int fileRecordSequence = 0;
        int pathsReconstructedFromIdentifiers = 0;

        void FlushRecord()
        {
            if (current is null)
                return;

            if (current.Include && current.ExtentLba >= 0 && current.DataLength >= 0)
            {
                if ((current.Flags & IsoDirectoryRecordFlags.Directory) != 0)
                {
                    // Extended Attribute Records precede the directory's actual data.
                    // The directory record's extent points at the XAR; directory bytes
                    // begin after ExtendedAttributeRecordLength logical blocks.
                    long directoryStartLba = current.ExtentLba + Math.Max(0, current.ExtendedAttributeRecordLength);
                    long count = Math.Max(1, DivideRoundUp(current.DataLength, CookedSectorSize));
                    for (long i = 0; i < count; i++)
                    {
                        long directoryLba = directoryStartLba + i;
                        metadataLbas.Add(directoryLba);
                        primaryDirectoryLbas.Add(directoryLba);
                    }

                    string directoryPath;
                    if (!string.IsNullOrWhiteSpace(current.FullPath))
                    {
                        directoryPath = NormalizeIsoPath(current.FullPath);
                    }
                    else if (primaryDirectoryPathByStartLba.TryGetValue(current.ExtentLba, out string? mappedDirectoryPath))
                    {
                        directoryPath = mappedDirectoryPath;
                    }
                    else if (!string.IsNullOrWhiteSpace(currentDirectoryPath) &&
                             IsCurrentDirectoryIdentifier(current.FileIdentifier))
                    {
                        directoryPath = currentDirectoryPath;
                    }
                    else
                    {
                        directoryPath = $"<directory at LBA {current.ExtentLba:N0}>";
                    }

                    // A directory may span multiple 2048-byte sectors.  Old DIC logs can
                    // emit a separate "Directory Record" heading for continuation sectors
                    // even though only the first sector appears in the ISO path table.
                    // Remember the resolved directory path for every sector in its extent.
                    if (!directoryPath.StartsWith("<directory at LBA ", StringComparison.Ordinal))
                    {
                        for (long i = 0; i < count; i++)
                            primaryDirectoryPathBySectorLba[directoryStartLba + i] = directoryPath;
                    }

                    var directoryRecord = new DicFileRecord(
                        directoryPath,
                        current.ExtentLba,
                        current.DataLength,
                        current.ExtendedAttributeRecordLength,
                        current.FileUnitSize,
                        current.InterleaveGapSize,
                        RecordingTime: current.RecordingTime,
                        Flags: current.Flags,
                        Sequence: fileRecordSequence++);
                    if (directoryRecord.ExtendedAttributeRecordLength > 0)
                        donorOnlyRecords.Add(directoryRecord);
                }
                else
                {
                    string resolvedPath = string.Empty;
                    if (!string.IsNullOrWhiteSpace(current.FullPath))
                    {
                        resolvedPath = NormalizeIsoPath(current.FullPath);
                    }
                    else if (!string.IsNullOrWhiteSpace(current.FileIdentifier) &&
                             !IsSpecialDirectoryIdentifier(current.FileIdentifier) &&
                             !string.IsNullOrWhiteSpace(currentDirectoryPath))
                    {
                        // Older DIC versions log File Identifier but omit FullPath.
                        // Qualify the filename with the directory reconstructed from the
                        // primary ISO 9660 path table / current directory sector.
                        resolvedPath = NormalizeIsoPath(
                            currentDirectoryPath.TrimEnd('/') + "/" + current.FileIdentifier);
                        pathsReconstructedFromIdentifiers++;
                    }

                    if (!string.IsNullOrWhiteSpace(resolvedPath))
                    {
                        // Keep the ISO 9660 directory-record flags as part of the parsed
                        // record. Associated files are valid ISO 9660 records and are
                        // filtered later from ordinary source-file restoration rather
                        // than being discarded by the filesystem parser.
                        records.Add(new DicFileRecord(
                            resolvedPath,
                            current.ExtentLba,
                            current.DataLength,
                            current.ExtendedAttributeRecordLength,
                            current.FileUnitSize,
                            current.InterleaveGapSize,
                            RecordingTime: current.RecordingTime,
                            Flags: current.Flags,
                            Sequence: fileRecordSequence++));
                    }
                }
            }
            current = null;
        }

        foreach (string rawLine in File.ReadLines(path))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string line = rawLine.TrimEnd();

            Match heading = LbaHeadingRegex.Match(line);
            if (heading.Success)
            {
                FlushRecord();
                FlushPathTableRecord();
                FlushSupplementaryPathTableRecord();
                long lba = long.Parse(heading.Groups["lba"].Value, CultureInfo.InvariantCulture);
                string kind = heading.Groups["kind"].Value;
                currentVolumeDescriptorType = null;
                long primaryPathTableSectorCount = Math.Max(1, DivideRoundUp(primaryPathTableSize, CookedSectorSize));
                inPrimaryPathTable = kind.Contains("Path Table Record", StringComparison.OrdinalIgnoreCase) &&
                                     primaryPathTableLba is long pathTableStart &&
                                     lba >= pathTableStart &&
                                     lba < pathTableStart + primaryPathTableSectorCount;
                long supplementaryPathTableSectorCount = Math.Max(1, DivideRoundUp(supplementaryPathTableSize, CookedSectorSize));
                long? preferredSupplementaryPathTableLba = supplementaryPathTableLocations.Count > 0
                    ? supplementaryPathTableLocations.Min()
                    : null;
                inSupplementaryPathTable = kind.Contains("Path Table Record", StringComparison.OrdinalIgnoreCase) &&
                                           preferredSupplementaryPathTableLba is long supplementaryStart &&
                                           lba >= supplementaryStart &&
                                           lba < supplementaryStart + supplementaryPathTableSectorCount;
                inPrimaryDirectory = kind.Contains("Directory Record", StringComparison.OrdinalIgnoreCase) &&
                                     primaryDirectoryLbas.Contains(lba);
                currentDirectoryPath = inPrimaryDirectory &&
                                       primaryDirectoryPathBySectorLba.TryGetValue(lba, out string? directoryPathForSector)
                    ? directoryPathForSector
                    : string.Empty;

                // Keep the descriptor sequence itself so the primary PVD and terminator
                // can be restored. Supplementary descriptors are explicitly stripped
                // after mainInfo parsing; only the primary path table/directory sectors
                // are admitted as filesystem metadata.
                if (kind.Contains("Volume Descriptor", StringComparison.OrdinalIgnoreCase))
                    metadataLbas.Add(lba);
                else if (inPrimaryPathTable || inPrimaryDirectory)
                    metadataLbas.Add(lba);
                continue;
            }

            if (TryLongValue(line, "Volume Descriptor Type:", out long descriptorType))
            {
                currentVolumeDescriptorType = checked((int)descriptorType);
                continue;
            }

            if (currentVolumeDescriptorType == 1)
            {
                if (TryValue(line, "Volume Identifier:", out string primaryVolumeId) && string.IsNullOrWhiteSpace(volumeIdentifier))
                    volumeIdentifier = primaryVolumeId.Trim();
                else if (TryLongValue(line, "Volume Space Size:", out long vss))
                {
                    primaryDescriptorVolumeSpaceSizes.Add(vss);
                    if (volumeSpaceSize == 0)
                        volumeSpaceSize = vss;
                }
                else if (TryLongValue(line, "Path Table Size:", out long pathTableSize))
                    primaryPathTableSize = pathTableSize;
                else if (TryLongValue(line, "Location of Occurrence of Path Table:", out long pathTableLba))
                {
                    primaryPathTableLba = pathTableLba;
                    long pathSectors = Math.Max(1, DivideRoundUp(primaryPathTableSize, CookedSectorSize));
                    for (long i = 0; i < pathSectors; i++)
                        metadataLbas.Add(pathTableLba + i);
                }
                else if (volumeRecordingTime is null &&
                         TryValue(line, "Volume Creation Date and Time:", out string volumeDateText) &&
                         TryParseDicTimestamp(volumeDateText, out DateTimeOffset parsedVolumeTime))
                    volumeRecordingTime = parsedVolumeTime;
            }

            if (currentVolumeDescriptorType == 2)
            {
                if (TryLongValue(line, "Volume Space Size:", out long supplementaryVss))
                {
                    supplementaryDescriptorVolumeSpaceSizes.Add(supplementaryVss);
                }
                else if (TryLongValue(line, "Path Table Size:", out long supplementaryTableSize))
                {
                    supplementaryPathTableSize = supplementaryTableSize;
                }
                else if (line.Contains("Path Table", StringComparison.OrdinalIgnoreCase) &&
                         line.Contains("Location", StringComparison.OrdinalIgnoreCase))
                {
                    int colon = line.IndexOf(':');
                    if (colon >= 0)
                    {
                        Match locationNumber = Regex.Match(line[(colon + 1)..], @"-?\d+");
                        if (locationNumber.Success &&
                            long.TryParse(locationNumber.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long supplementaryTableLba) &&
                            supplementaryTableLba > 0)
                        {
                            supplementaryPathTableLocations.Add(supplementaryTableLba);
                        }
                    }
                }
            }

            if (inSupplementaryPathTable)
            {
                if (TryLongValue(line, "Length of Directory Identifier:", out long identifierLength))
                {
                    FlushSupplementaryPathTableRecord();
                    currentSupplementaryPathTableRecord = new DicPathTableRecordBuilder
                    {
                        IdentifierLength = checked((int)identifierLength)
                    };
                    continue;
                }

                if (currentSupplementaryPathTableRecord is not null)
                {
                    if (TryLongValue(line, "Length of Extended Attribute Record:", out long pathTableXarLength))
                    {
                        currentSupplementaryPathTableRecord.ExtendedAttributeLength = checked((int)pathTableXarLength);
                        continue;
                    }
                    if (TryLongValue(line, "Position of Extent:", out long pathExtent))
                    {
                        currentSupplementaryPathTableRecord.ExtentLba = pathExtent;
                        continue;
                    }
                    if (TryLongValue(line, "Number of Upper Directory:", out long parentNumber))
                    {
                        currentSupplementaryPathTableRecord.ParentDirectoryNumber = checked((int)parentNumber);
                        continue;
                    }
                    if (TryValue(line, "Directory Identifier:", out string directoryIdentifier))
                    {
                        currentSupplementaryPathTableRecord.Identifier = directoryIdentifier;
                        FlushSupplementaryPathTableRecord();
                        continue;
                    }
                }
            }

            if (inPrimaryPathTable)
            {
                if (TryLongValue(line, "Length of Directory Identifier:", out long identifierLength))
                {
                    FlushPathTableRecord();
                    currentPathTableRecord = new DicPathTableRecordBuilder
                    {
                        IdentifierLength = checked((int)identifierLength)
                    };
                    continue;
                }

                if (currentPathTableRecord is not null)
                {
                    if (TryLongValue(line, "Length of Extended Attribute Record:", out long pathTableXarLength))
                    {
                        currentPathTableRecord.ExtendedAttributeLength = checked((int)pathTableXarLength);
                        continue;
                    }
                    if (TryLongValue(line, "Position of Extent:", out long pathExtent))
                    {
                        currentPathTableRecord.ExtentLba = pathExtent;
                        long directoryDataLba = pathExtent + Math.Max(0, currentPathTableRecord.ExtendedAttributeLength);
                        primaryDirectoryLbas.Add(directoryDataLba);
                        metadataLbas.Add(directoryDataLba);
                        continue;
                    }
                    if (TryLongValue(line, "Number of Upper Directory:", out long parentNumber))
                    {
                        currentPathTableRecord.ParentDirectoryNumber = checked((int)parentNumber);
                        continue;
                    }
                    if (TryValue(line, "Directory Identifier:", out string directoryIdentifier))
                    {
                        currentPathTableRecord.Identifier = directoryIdentifier;
                        FlushPathTableRecord();
                        continue;
                    }
                }
            }

            if (line.Contains("Length of Directory Record:", StringComparison.OrdinalIgnoreCase))
            {
                FlushRecord();
                current = new DicFileRecordBuilder { Include = inPrimaryDirectory };
                continue;
            }

            if (current is null)
                continue;

            if (TryLongValue(line, "Extended Attribute Record Length:", out long xarLength)) current.ExtendedAttributeRecordLength = checked((int)xarLength);
            else if (TryLongValue(line, "Location of Extent:", out long extent)) current.ExtentLba = extent;
            else if (TryLongValue(line, "Data Length:", out long dataLength)) current.DataLength = dataLength;
            else if (TryValue(line, "Recording Date and Time:", out string recordingText) &&
                     TryParseDicTimestamp(recordingText, out DateTimeOffset recordingTime)) current.RecordingTime = recordingTime;
            else if (TryLongValue(line, "File Flags:", out long flags)) current.Flags = (IsoDirectoryRecordFlags)(byte)flags;
            else if (TryLongValue(line, "File Unit Size:", out long fileUnit)) current.FileUnitSize = (int)fileUnit;
            else if (TryLongValue(line, "Interleave Gap Size:", out long gap)) current.InterleaveGapSize = (int)gap;
            else if (TryValue(line, "File Identifier:", out string fileIdentifier)) current.FileIdentifier = fileIdentifier;
            else if (TryValue(line, "FullPath:", out string fullPath)) current.FullPath = fullPath.Trim();
        }
        FlushPathTableRecord();
        FlushSupplementaryPathTableRecord();
        FlushRecord();

        // Only the primary ISO9660 directory records are retained. If the same extent
        // is deliberately referenced by multiple ISO9660 paths, keep those paths as
        // aliases, but never import supplementary/Joliet names into the recovery model.
        DicFileRecord[] files = records
            .GroupBy(r => (r.ExtentLba, r.DataLength, Associated: (r.Flags & IsoDirectoryRecordFlags.Associated) != 0))
            .Select(group =>
            {
                DicFileRecord preferred = group
                    .OrderBy(r => DicIsoPathScore(r.Path))
                    .ThenBy(r => r.Path.Length)
                    .First();
                string[] aliases = group
                    .Select(r => r.Path)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(path => DicIsoPathScore(path))
                    .ThenBy(path => path.Length)
                    .ToArray();

                DateTimeOffset? recordingTime = group
                    .Select(r => r.RecordingTime)
                    .FirstOrDefault(value => value is not null);

                return preferred with { Aliases = aliases, RecordingTime = recordingTime };
            })
            .OrderBy(r => r.ExtentLba)
            .ThenBy(r => r.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        DateTimeOffset defaultRecordingTime = volumeRecordingTime
            ?? files.Select(file => file.RecordingTime).FirstOrDefault(value => value is not null)
            ?? new DateTimeOffset(1900, 1, 1, 0, 0, 0, TimeSpan.Zero);

        return new DicVolumeInfo(
            volumeIdentifier,
            volumeSpaceSize,
            files,
            donorOnlyRecords,
            metadataLbas,
            defaultRecordingTime,
            primaryPathTableLba,
            primaryPathTableSize,
            primaryPathTableRecords,
            pathsReconstructedFromIdentifiers,
            supplementaryDirectoryHints,
            primaryDescriptorVolumeSpaceSizes,
            supplementaryDescriptorVolumeSpaceSizes);
    }


    private static DicDiscInfo ParseDiscInfo(string path, CancellationToken cancellationToken)
    {
        var result = new DicDiscInfo();
        var tracks = new Dictionary<int, DicDiscTrackInfo>();
        var trackRangeRegex = new Regex(
            @"(?<type>Data|Audio)\s+Track\s+(?<track>\d+),\s+LBA\s+(?<start>-?\d+)\s+-\s+(?<end>-?\d+),\s+Length\s+(?<len>\d+)",
            RegexOptions.IgnoreCase);
        var trackControlRegex = new Regex(
            @"Track\s+(?<track>\d+),\s+Ctl\s+\d+,\s+Mode\s+(?<mode>[012])(?<rest>.*)$",
            RegexOptions.IgnoreCase);
        var dataSectorRegex = new Regex(
            @"Track\s+(?<track>\d+)\s+Data\s+Sector:\s*(?<start>-?\d+)\s+-\s+(?<end>-?\d+)",
            RegexOptions.IgnoreCase);
        var index0Regex = new Regex(@"Index0\s+(?<v>-?\d+)", RegexOptions.IgnoreCase);
        var index1Regex = new Regex(@"Index1\s+(?<v>-?\d+)", RegexOptions.IgnoreCase);

        DicDiscTrackInfo GetTrack(int number)
        {
            if (!tracks.TryGetValue(number, out DicDiscTrackInfo? track))
            {
                track = new DicDiscTrackInfo { TrackNumber = number };
                tracks[number] = track;
            }
            return track;
        }

        foreach (string line in File.ReadLines(path))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (line.Contains("BookType:", StringComparison.OrdinalIgnoreCase) &&
                line.Contains("DVD", StringComparison.OrdinalIgnoreCase))
            {
                result.IsDvd = true;
            }

            if (line.Contains("NumberOfLayers:", StringComparison.OrdinalIgnoreCase))
            {
                if (line.Contains("Single Layer", StringComparison.OrdinalIgnoreCase))
                    result.DvdIsSingleLayer = true;
                else if (line.Contains("Dual Layer", StringComparison.OrdinalIgnoreCase))
                    result.DvdIsSingleLayer = false;
            }

            Match dvdSectorLength = Regex.Match(line, @"LayerZeroSector:\s*(?<count>\d+)", RegexOptions.IgnoreCase);
            if (dvdSectorLength.Success)
                result.DvdSectorCount = long.Parse(dvdSectorLength.Groups["count"].Value, CultureInfo.InvariantCulture);

            Match dvdPfiStart = Regex.Match(line, @"StartingDataSector:\s*(?<sector>\d+)", RegexOptions.IgnoreCase);
            if (dvdPfiStart.Success)
                result.DvdPfiStartingDataSector = long.Parse(dvdPfiStart.Groups["sector"].Value, CultureInfo.InvariantCulture);

            Match dvdPfiEnd = Regex.Match(line, @"EndDataSector:\s*(?<sector>\d+)", RegexOptions.IgnoreCase);
            if (dvdPfiEnd.Success && !line.Contains("EndLayerZeroSector", StringComparison.OrdinalIgnoreCase))
                result.DvdPfiEndDataSector = long.Parse(dvdPfiEnd.Groups["sector"].Value, CultureInfo.InvariantCulture);

            Match range = trackRangeRegex.Match(line);
            if (range.Success)
            {
                int number = int.Parse(range.Groups["track"].Value, CultureInfo.InvariantCulture);
                DicDiscTrackInfo track = GetTrack(number);
                track.IsAudio = range.Groups["type"].Value.Equals("Audio", StringComparison.OrdinalIgnoreCase);
                track.StartLba = long.Parse(range.Groups["start"].Value, CultureInfo.InvariantCulture);
                track.EndLba = long.Parse(range.Groups["end"].Value, CultureInfo.InvariantCulture);

                if (!track.IsAudio && result.TrackSectorCount is null)
                {
                    result.TrackStartLba = track.StartLba;
                    result.TrackEndLba = track.EndLba;
                    result.TrackSectorCount = long.Parse(range.Groups["len"].Value, CultureInfo.InvariantCulture);
                }
            }

            Match control = trackControlRegex.Match(line);
            if (control.Success)
            {
                int number = int.Parse(control.Groups["track"].Value, CultureInfo.InvariantCulture);
                DicDiscTrackInfo track = GetTrack(number);
                track.Mode = int.Parse(control.Groups["mode"].Value, CultureInfo.InvariantCulture);
                string rest = control.Groups["rest"].Value;
                Match index0 = index0Regex.Match(rest);
                Match index1 = index1Regex.Match(rest);
                if (index0.Success)
                    track.Index0Lba = long.Parse(index0.Groups["v"].Value, CultureInfo.InvariantCulture);
                if (index1.Success)
                    track.Index1Lba = long.Parse(index1.Groups["v"].Value, CultureInfo.InvariantCulture);
            }

            Match dataSectors = dataSectorRegex.Match(line);
            if (dataSectors.Success)
            {
                int number = int.Parse(dataSectors.Groups["track"].Value, CultureInfo.InvariantCulture);
                DicDiscTrackInfo track = GetTrack(number);
                track.DataStartLba = long.Parse(dataSectors.Groups["start"].Value, CultureInfo.InvariantCulture);
                track.DataEndLba = long.Parse(dataSectors.Groups["end"].Value, CultureInfo.InvariantCulture);
            }

            if (line.Contains("DiscType:", StringComparison.OrdinalIgnoreCase) && line.Contains("XA", StringComparison.OrdinalIgnoreCase))
                result.IsXa = true;

            Match hash = ImgHashRegex.Match(line);
            if (hash.Success)
            {
                result.ImageSize = long.Parse(hash.Groups["size"].Value, CultureInfo.InvariantCulture);
                result.ImageCrc32 = hash.Groups["crc"].Value.ToLowerInvariant();
                result.ImageMd5 = hash.Groups["md5"].Value.ToLowerInvariant();
                result.ImageSha1 = hash.Groups["sha1"].Value.ToLowerInvariant();
            }
        }

        if (result.IsDvd && result.DvdIsSingleLayer &&
            result.DvdPfiStartingDataSector is long pfiStart &&
            result.DvdPfiEndDataSector is long pfiEnd &&
            pfiStart >= 0 && pfiEnd >= pfiStart)
        {
            result.DvdPfiSectorCount = checked(pfiEnd - pfiStart + 1);
        }

        foreach (DicDiscTrackInfo track in tracks.Values.OrderBy(track => track.TrackNumber))
            result.Tracks.Add(track);

        DicDiscTrackInfo? firstDataTrack = result.Tracks.FirstOrDefault(track => !track.IsAudio && track.Mode is 1 or 2);
        if (firstDataTrack is not null)
            result.TrackMode = firstDataTrack.Mode;

        return result;
    }

    private static DicDatImageInfo? TryParseDatImageInfo(string path, long minimumSectorCount, CancellationToken cancellationToken)
    {
        var candidates = new List<DicDatImageInfo>();
        foreach (string line in File.ReadLines(path))
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (Match match in DatRomRegex.Matches(line))
            {
                string name = match.Groups["name"].Value;
                bool isIso = name.EndsWith(".iso", StringComparison.OrdinalIgnoreCase);
                bool isImg = name.EndsWith(".img", StringComparison.OrdinalIgnoreCase);
                bool isBin = name.EndsWith(".bin", StringComparison.OrdinalIgnoreCase);
                if (!isIso && !isImg && !isBin)
                    continue;

                long size = long.Parse(match.Groups["size"].Value, CultureInfo.InvariantCulture);
                int sectorSize = 0;

                // A DAT entry explicitly named .iso is cooked logical-sector evidence.  Its
                // whole-image length may legitimately exceed ISO9660 Volume Space Size (for
                // example because mastering left post-volume sectors), so require only that
                // it is 2048-aligned and covers every LBA already proven by the DIC logs.
                if (isIso && size > 0 && size % CookedSectorSize == 0 &&
                    size / CookedSectorSize >= minimumSectorCount)
                {
                    sectorSize = CookedSectorSize;
                }
                else if (size == checked(minimumSectorCount * (long)CookedSectorSize))
                {
                    sectorSize = CookedSectorSize;
                }
                else if (size == checked(minimumSectorCount * (long)RawSectorSize))
                {
                    sectorSize = RawSectorSize;
                }

                if (sectorSize == 0)
                    continue;

                candidates.Add(new DicDatImageInfo(
                    name,
                    size,
                    sectorSize,
                    match.Groups["crc"].Value.ToLowerInvariant(),
                    match.Groups["md5"].Value.ToLowerInvariant(),
                    match.Groups["sha1"].Value.ToLowerInvariant()));
            }
        }

        return candidates.Count == 1 ? candidates[0] : null;
    }

    private static DicEccEdcParseResult ParseEccEdc(string path, CancellationToken cancellationToken)
    {
        var layouts = new Dictionary<long, DicSectorLayout>();
        var eccErrorReportedLbas = new List<long>();
        var invalidModeReportedLbas = new List<long>();
        var invalidSyncReportedLbas = new List<long>();
        var zeroSyncReportedLbas = new List<long>();
        var badMsfReportedLbas = new List<long>();
        var subheaderMismatchReportedLbas = new List<long>();
        var expectedZeroMismatchReportedLbas = new List<long>();
        var fill55ReportedLbas = new List<long>();
        int reportedEccErrorCount = 0;
        int reportedInvalidModeCount = 0;
        int reportedFill55Count = 0;
        int fill55ExceptHeaderCount = 0;
        int malformedSectorRecordCount = 0;
        long? streamTruncatedAtPhysicalSector = null;
        SummaryListKind summaryList = SummaryListKind.None;
        long physicalSector = 0;
        bool sectorStreamTrusted = true;

        List<long>? ActiveSummaryOutput() => summaryList switch
        {
            SummaryListKind.EccEdc => eccErrorReportedLbas,
            SummaryListKind.InvalidMode => invalidModeReportedLbas,
            SummaryListKind.InvalidSync => invalidSyncReportedLbas,
            SummaryListKind.ZeroSync => zeroSyncReportedLbas,
            SummaryListKind.BadMsf => badMsfReportedLbas,
            SummaryListKind.SubheaderMismatch => subheaderMismatchReportedLbas,
            SummaryListKind.ExpectedZeroMismatch => expectedZeroMismatchReportedLbas,
            SummaryListKind.Fill55 => fill55ReportedLbas,
            _ => null
        };

        foreach (string line in File.ReadLines(path))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string trimmed = line.Trim();

            Match fill55Match = Fill55RecipeRegex.Match(line);
            if (fill55Match.Success &&
                int.TryParse(fill55Match.Groups["count"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedFill55Count))
            {
                fill55ExceptHeaderCount = parsedFill55Count;
            }

            if (trimmed.StartsWith("[ERROR] Number of sector(s) where user data doesn't match the expected ECC/EDC:", StringComparison.OrdinalIgnoreCase))
            {
                reportedEccErrorCount = ParseTrailingNonNegativeInt(trimmed);
                summaryList = SummaryListKind.EccEdc;
                continue;
            }

            if (trimmed.StartsWith("[ERROR] Number of sector(s) where mode is invalid:", StringComparison.OrdinalIgnoreCase))
            {
                reportedInvalidModeCount = ParseTrailingNonNegativeInt(trimmed);
                summaryList = SummaryListKind.InvalidMode;
                continue;
            }

            if (trimmed.StartsWith("[ERROR] Number of sector(s) where 2336 byte is all 0x55:", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("[WARNING] Number of sector(s) where 2336 byte is all 0x55:", StringComparison.OrdinalIgnoreCase))
            {
                reportedFill55Count = ParseTrailingNonNegativeInt(trimmed);
                summaryList = SummaryListKind.Fill55;
                continue;
            }

            if (trimmed.StartsWith("[ERROR] Number of sector(s) where bad MSF:", StringComparison.OrdinalIgnoreCase))
            {
                summaryList = SummaryListKind.BadMsf;
                continue;
            }

            if (trimmed.StartsWith("[ERROR] Number of sector(s) where sync(0x00 - 0x0c) is invalid:", StringComparison.OrdinalIgnoreCase))
            {
                summaryList = SummaryListKind.InvalidSync;
                continue;
            }

            if (trimmed.StartsWith("[ERROR] Number of sector(s) where sync(0x00 - 0x0c) is zero:", StringComparison.OrdinalIgnoreCase))
            {
                summaryList = SummaryListKind.ZeroSync;
                continue;
            }

            if (trimmed.StartsWith("[ERROR] Number of sector(s) where mode2 NoEdc subheader(0x10 - 0x17) isn't same:", StringComparison.OrdinalIgnoreCase))
            {
                summaryList = SummaryListKind.SubheaderMismatch;
                continue;
            }

            if (trimmed.StartsWith("[ERROR] Number of sector(s) where user data doesn't all zero sector:", StringComparison.OrdinalIgnoreCase))
            {
                summaryList = SummaryListKind.ExpectedZeroMismatch;
                continue;
            }

            if (summaryList != SummaryListKind.None)
            {
                List<long>? activeSummaryOutput = ActiveSummaryOutput();
                if (trimmed.StartsWith("Sector:", StringComparison.OrdinalIgnoreCase))
                {
                    ReadOnlySpan<char> values = trimmed.AsSpan(trimmed.IndexOf(':') + 1);
                    if (activeSummaryOutput is not null)
                        ParseSignedDecimalLbas(values, activeSummaryOutput);
                    continue;
                }

                if (trimmed.Length > 0 && (char.IsDigit(trimmed[0]) || trimmed[0] == '-'))
                {
                    if (activeSummaryOutput is not null)
                        ParseSignedDecimalLbas(trimmed.AsSpan(), activeSummaryOutput);
                    continue;
                }

                if (trimmed.StartsWith("[", StringComparison.Ordinal) || trimmed.StartsWith("Total ", StringComparison.OrdinalIgnoreCase))
                    summaryList = SummaryListKind.None;
            }

            if (!trimmed.StartsWith("LBA[", StringComparison.OrdinalIgnoreCase))
                continue;

            Match sectorRecord = EccSectorRecordRegex.Match(trimmed);
            if (!sectorRecord.Success)
            {
                malformedSectorRecordCount++;
                if (sectorStreamTrusted)
                {
                    // Once a per-sector record is textually corrupted we no longer know
                    // how many physical records were lost inside that damaged line. Do not
                    // continue advancing the physical ordinal from later-looking records;
                    // disc.txt track evidence is a safer fallback for the remainder.
                    sectorStreamTrusted = false;
                    streamTruncatedAtPhysicalSector = physicalSector;
                }
                continue;
            }

            if (!sectorStreamTrusted)
                continue;

            long reportedLba = long.Parse(sectorRecord.Groups["lba"].Value, CultureInfo.InvariantCulture);
            Match msfMatch = EccMsfRegex.Match(line);
            Match modeMatch = EccModeRegex.Match(line);
            bool isAudio = Regex.IsMatch(line, @"\]\s*,?\s*audio\b|\]\s+audio\b", RegexOptions.IgnoreCase) ||
                           line.Contains("] audio", StringComparison.OrdinalIgnoreCase);
            bool explicitFill55 = PerSectorFill55Regex.IsMatch(line);
            int mode = modeMatch.Success
                ? int.Parse(modeMatch.Groups["mode"].Value, CultureInfo.InvariantCulture)
                : isAudio ? -1 : -2;

            bool blockIndicators = line.Contains("Block Indicators", StringComparison.OrdinalIgnoreCase);
            bool invalidSync = line.Contains("invalid sync", StringComparison.OrdinalIgnoreCase);
            bool zeroSync = line.Contains("zero sync", StringComparison.OrdinalIgnoreCase);
            bool badMsf = line.Contains("bad msf", StringComparison.OrdinalIgnoreCase);

            Match invalidModeMatch = InvalidModeRegex.Match(line);
            byte? exactRawMode = null;
            if (invalidModeMatch.Success &&
                byte.TryParse(invalidModeMatch.Groups["v"].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte invalidMode))
            {
                exactRawMode = invalidMode;
                int logical = invalidMode & 0x03;
                if (logical is 0 or 1 or 2)
                    mode = logical;
            }
            else if (mode is 0 or 1 or 2 && !blockIndicators)
            {
                exactRawMode = (byte)mode;
            }

            byte[]? rawHeaderOverride =
                !isAudio && msfMatch.Success
                    ? BuildLoggedHeaderOverride(msfMatch, exactRawMode)
                    : null;

            bool noEdc = mode == 2 &&
                         ((modeMatch.Success && modeMatch.Groups["noedc"].Success) ||
                          line.Contains("mode 2 no edc", StringComparison.OrdinalIgnoreCase));

            byte fileNumber = ParseHexByte(FileNumberRegex.Match(line));
            byte channelNumber = ParseHexByte(ChannelNumberRegex.Match(line));
            Match submodeMatch = SubmodeRegex.Match(line);
            byte parsedSubmode = ParseHexByte(submodeMatch);

            int form;
            if (mode != 2)
            {
                form = 1;
            }
            else if (submodeMatch.Success)
            {
                // XA submode is authoritative. DIC can print "mode 2 no edc" for a
                // malformed Form-1 sector, so the textual phrase must not force Form 2.
                form = (parsedSubmode & 0x20) != 0 ? 2 : 1;
            }
            else if (modeMatch.Groups["form"].Success)
            {
                form = int.Parse(modeMatch.Groups["form"].Value, CultureInfo.InvariantCulture);
            }
            else if (noEdc)
            {
                form = 2;
            }
            else
            {
                form = 1;
            }

            byte submode = submodeMatch.Success
                ? parsedSubmode
                : mode == 2 ? (byte)(form == 2 ? 0x28 : 0x08) : (byte)0;
            byte codingInfo = ParseHexByte(CodingInfoRegex.Match(line));
            bool hasEdc = !(mode == 2 && form == 2 && noEdc);

            byte[]? xaSubheader = null;
            if (mode == 2)
            {
                xaSubheader = new[]
                {
                    fileNumber, channelNumber, submode, codingInfo,
                    fileNumber, channelNumber, submode, codingInfo
                };

                if (line.Contains("Subheader isn't same", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("SubHeader isn't same", StringComparison.OrdinalIgnoreCase))
                {
                    byte[] exact = new byte[8];
                    int found = 0;
                    foreach (Match match in EccSubheaderByteRegex.Matches(line))
                    {
                        if (!int.TryParse(match.Groups["n"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int index) ||
                            index < 0 || index > 7)
                            continue;

                        string valueText = match.Groups["v"].Value;
                        if (!ushort.TryParse(valueText, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ushort value))
                            continue;

                        exact[index] = (byte)(value & 0xff);
                        found++;
                    }

                    if (found == 8)
                    {
                        xaSubheader = exact;
                        fileNumber = exact[0];
                        channelNumber = exact[1];
                        submode = exact[2];
                        codingInfo = exact[3];
                        form = (submode & 0x20) != 0 ? 2 : 1;
                        hasEdc = !(form == 2 && noEdc);
                    }
                }
            }

            bool perSectorEccMismatch =
                line.Contains("User data vs. ecc/edc doesn't match", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("User data doesn't match the expected ECC/EDC", StringComparison.OrdinalIgnoreCase);

            layouts[physicalSector] = new DicSectorLayout(
                mode,
                form,
                fileNumber,
                channelNumber,
                submode,
                codingInfo,
                HasEdc: hasEdc,
                ReportedLba: reportedLba,
                RawHeaderOverride: rawHeaderOverride,
                XaSubheaderOverride: xaSubheader,
                IsAudio: isAudio,
                IsUnknown: mode < 0 && !isAudio,
                HasBlockIndicators: blockIndicators,
                HasInvalidSync: invalidSync,
                HasZeroSync: zeroSync,
                HasBadMsf: badMsf,
                HasMissingMsf: !isAudio && !msfMatch.Success,
                HasInvalidMode: invalidModeMatch.Success,
                HasEccMismatch: perSectorEccMismatch,
                HasExplicitFill55: explicitFill55);

            physicalSector++;
        }

        List<long> eccErrorPhysical = MapReportedSummaryLbasToPhysical(
            layouts, eccErrorReportedLbas, out int unmappedEcc, layout => layout.HasEccMismatch);
        List<long> invalidModePhysical = MapReportedSummaryLbasToPhysical(
            layouts, invalidModeReportedLbas, out int unmappedInvalidMode, layout => layout.HasInvalidMode);
        List<long> invalidSyncPhysical = MapReportedSummaryLbasToPhysical(
            layouts, invalidSyncReportedLbas, out _, layout => layout.HasInvalidSync);
        List<long> zeroSyncPhysical = MapReportedSummaryLbasToPhysical(
            layouts, zeroSyncReportedLbas, out _, layout => layout.HasZeroSync);
        List<long> badMsfPhysical = MapReportedSummaryLbasToPhysical(
            layouts, badMsfReportedLbas, out _, layout => layout.HasBadMsf);
        List<long> subheaderMismatchPhysical = MapReportedSummaryLbasToPhysical(
            layouts,
            subheaderMismatchReportedLbas,
            out _,
            layout => layout.XaSubheaderCopiesDiffer);
        List<long> expectedZeroMismatchPhysical = MapReportedSummaryLbasToPhysical(
            layouts, expectedZeroMismatchReportedLbas, out _);
        List<long> fill55SummaryPhysical = MapReportedSummaryLbasToPhysical(
            layouts, fill55ReportedLbas, out _, layout => layout.HasExplicitFill55);

        foreach (long physical in invalidModePhysical)
        {
            if (layouts.TryGetValue(physical, out DicSectorLayout? layout))
                layouts[physical] = layout with { SummaryInvalidMode = true };
        }
        foreach (long physical in invalidSyncPhysical)
        {
            if (layouts.TryGetValue(physical, out DicSectorLayout? layout))
                layouts[physical] = layout with { SummaryInvalidSync = true };
        }
        foreach (long physical in zeroSyncPhysical)
        {
            if (layouts.TryGetValue(physical, out DicSectorLayout? layout))
                layouts[physical] = layout with { SummaryZeroSync = true };
        }
        foreach (long physical in badMsfPhysical)
        {
            if (layouts.TryGetValue(physical, out DicSectorLayout? layout))
                layouts[physical] = layout with { SummaryBadMsf = true };
        }
        foreach (long physical in subheaderMismatchPhysical)
        {
            if (layouts.TryGetValue(physical, out DicSectorLayout? layout))
                layouts[physical] = layout with { SummarySubheaderMismatch = true };
        }
        foreach (long physical in expectedZeroMismatchPhysical)
        {
            if (layouts.TryGetValue(physical, out DicSectorLayout? layout))
                layouts[physical] = layout with { SummaryExpectedZeroMismatch = true };
        }

        var explicitFill55Physical = layouts
            .Where(pair => pair.Value.HasExplicitFill55)
            .Select(pair => pair.Key)
            .ToHashSet();
        explicitFill55Physical.UnionWith(fill55SummaryPhysical);

        return new DicEccEdcParseResult(
            layouts,
            eccErrorPhysical,
            invalidModePhysical,
            explicitFill55Physical.OrderBy(value => value).ToArray(),
            reportedEccErrorCount,
            reportedInvalidModeCount,
            reportedFill55Count,
            unmappedEcc,
            unmappedInvalidMode,
            malformedSectorRecordCount,
            streamTruncatedAtPhysicalSector,
            fill55ExceptHeaderCount);
    }

    private static int ParseTrailingNonNegativeInt(string line)
    {
        int colon = line.LastIndexOf(':');
        string text = colon >= 0 ? line[(colon + 1)..].Trim() : string.Empty;
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) && value >= 0
            ? value
            : 0;
    }

    private static void ParseSignedDecimalLbas(ReadOnlySpan<char> text, List<long> output)
    {
        int i = 0;
        while (i < text.Length)
        {
            while (i < text.Length && !char.IsDigit(text[i]) && text[i] != '-')
                i++;

            if (i >= text.Length)
                break;

            int start = i++;
            while (i < text.Length && char.IsDigit(text[i]))
                i++;

            if (long.TryParse(text[start..i], NumberStyles.Integer, CultureInfo.InvariantCulture, out long value))
                output.Add(value);
        }
    }

    private static byte[] BuildLoggedHeaderOverride(Match msfMatch, byte? rawMode)
    {
        var bytes = new List<byte>(4);
        foreach (string groupName in new[] { "m", "s", "f" })
        {
            if (!byte.TryParse(msfMatch.Groups[groupName].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte value))
                return Array.Empty<byte>();
            bytes.Add(value);
        }

        if (rawMode is byte mode)
            bytes.Add(mode);
        return bytes.ToArray();
    }

    private static List<long> MapReportedSummaryLbasToPhysical(
        IReadOnlyDictionary<long, DicSectorLayout> layouts,
        IReadOnlyList<long> summaryLbas,
        out int unmapped,
        Func<DicSectorLayout, bool>? directPhysicalPredicate = null)
    {
        var byReported = layouts
            .GroupBy(pair => pair.Value.ReportedLba)
            .ToDictionary(
                group => group.Key,
                group => new Queue<long>(group.Select(pair => pair.Key).OrderBy(v => v)));

        // Some historical EccEdc summaries describe an anomaly that is not repeated
        // on the corresponding per-sector line (Warcraft II is the important example:
        // 68,736 Q-ECC faults exist only in the final summary). Other protections such
        // as SmartE deliberately repeat one reported/header LBA across several physical
        // anomalous sectors. Keep a second reported-LBA queue containing only records
        // that visibly exhibit the requested anomaly so those are always preferred.
        Dictionary<long, Queue<long>>? matchingByReported = directPhysicalPredicate is null
            ? null
            : layouts
                .Where(pair => directPhysicalPredicate(pair.Value))
                .GroupBy(pair => pair.Value.ReportedLba)
                .ToDictionary(
                    group => group.Key,
                    group => new Queue<long>(group.Select(pair => pair.Key).OrderBy(v => v)));

        HashSet<long>? reportedValuesWithMatchingAnomaly = matchingByReported is null
            ? null
            : matchingByReported.Keys.ToHashSet();

        var used = new HashSet<long>();
        var result = new List<long>(summaryLbas.Count);
        unmapped = 0;

        foreach (long value in summaryLbas)
        {
            // First choice: the summary value is a physical sector and the per-sector
            // record at that position visibly has the same anomaly.
            if (value >= 0 &&
                layouts.TryGetValue(value, out DicSectorLayout? direct) &&
                !used.Contains(value) &&
                (directPhysicalPredicate is null || directPhysicalPredicate(direct)))
            {
                used.Add(value);
                result.Add(value);
                continue;
            }

            // Second choice: a damaged header may cause many physical sectors to report
            // the same LBA. Prefer candidates whose per-sector record actually exhibits
            // this summary's anomaly. This is what keeps the normal SmartE sector out
            // of a repeated-LBA error set.
            if (matchingByReported is not null &&
                matchingByReported.TryGetValue(value, out Queue<long>? matchingCandidates))
            {
                while (matchingCandidates.Count > 0 && used.Contains(matchingCandidates.Peek()))
                    matchingCandidates.Dequeue();

                if (matchingCandidates.Count > 0)
                {
                    long physical = matchingCandidates.Dequeue();
                    used.Add(physical);
                    result.Add(physical);
                    continue;
                }

                // There were positively identified anomaly records for this reported LBA
                // but they have all been consumed. Do not now fall through and consume a
                // neighbouring normal sector that happens to carry the same header LBA.
                unmapped++;
                continue;
            }

            // Third choice: historical summary-only anomalies. If no per-sector record
            // carrying this reported LBA visibly has the anomaly, allow the exact
            // physical/report match. This restores the v0.7.11 Warcraft behaviour while
            // retaining the SmartE protection above.
            if (value >= 0 &&
                layouts.TryGetValue(value, out direct) &&
                !used.Contains(value) &&
                direct.ReportedLba == value &&
                (reportedValuesWithMatchingAnomaly is null ||
                 !reportedValuesWithMatchingAnomaly.Contains(value)))
            {
                used.Add(value);
                result.Add(value);
                continue;
            }

            // Last resort for old logs whose summary uses the header-derived LBA but
            // does not echo the anomaly in the per-sector stream. This is deliberately
            // disabled whenever positively identified anomaly candidates exist for the
            // same reported value.
            if ((reportedValuesWithMatchingAnomaly is null ||
                 !reportedValuesWithMatchingAnomaly.Contains(value)) &&
                byReported.TryGetValue(value, out Queue<long>? candidates))
            {
                while (candidates.Count > 0 && used.Contains(candidates.Peek()))
                    candidates.Dequeue();

                if (candidates.Count > 0)
                {
                    long physical = candidates.Dequeue();
                    used.Add(physical);
                    result.Add(physical);
                    continue;
                }
            }

            unmapped++;
        }

        return result;
    }

    private static DicSupplementalEccEdcVerification? TryFindCompleteFinalEccEdcVerification(
        string directory,
        string? primaryEccEdcPath,
        long sectorCount,
        CancellationToken cancellationToken)
    {
        if (sectorCount <= 0 || sectorCount > int.MaxValue)
            return null;

        string? primaryFullPath = string.IsNullOrWhiteSpace(primaryEccEdcPath)
            ? null
            : Path.GetFullPath(primaryEccEdcPath);

        string[] candidates = Directory.EnumerateFiles(directory, "*.txt", SearchOption.TopDirectoryOnly)
            .Where(path =>
            {
                string name = Path.GetFileName(path);
                if (!name.Contains("EdcEcc_Track_", StringComparison.OrdinalIgnoreCase))
                    return false;
                if (name.Contains("(old)", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("_old", StringComparison.OrdinalIgnoreCase))
                    return false;
                if (primaryFullPath is not null &&
                    Path.GetFullPath(path).Equals(primaryFullPath, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                    return false;
                return true;
            })
            .OrderByDescending(path =>
            {
                try { return File.GetLastWriteTimeUtc(path); }
                catch { return DateTime.MinValue; }
            })
            .ToArray();

        foreach (string candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DicEccEdcParseResult parsed;
            try
            {
                parsed = ParseEccEdc(candidate, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                continue;
            }

            if (parsed.MalformedSectorRecordCount != 0 ||
                parsed.StreamTruncatedAtPhysicalSector is not null ||
                parsed.Layouts.Count != sectorCount)
                continue;

            bool absoluteLbaComplete = true;
            for (long physical = 0; physical < sectorCount; physical++)
            {
                if (!parsed.Layouts.TryGetValue(physical, out DicSectorLayout? layout) ||
                    layout.ReportedLba != physical)
                {
                    absoluteLbaComplete = false;
                    break;
                }
            }

            if (absoluteLbaComplete)
                return new DicSupplementalEccEdcVerification(Path.GetFullPath(candidate), parsed);
        }

        return null;
    }

    private static DicExactRawSectorEvidence ParseExactRawSectorFiles(
        string directory,
        long sectorCount,
        IReadOnlyDictionary<long, DicSectorLayout> layouts,
        CancellationToken cancellationToken)
    {
        var sectors = new Dictionary<long, byte[]>();
        var paths = new List<string>();
        int ignored = 0;

        foreach (string path in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string fileName = Path.GetFileName(path);
            if (Path.HasExtension(fileName) ||
                !long.TryParse(fileName, NumberStyles.None, CultureInfo.InvariantCulture, out long lba) ||
                lba < 0 || lba >= sectorCount)
                continue;

            long length;
            try { length = new FileInfo(path).Length; }
            catch { continue; }
            if (length != RawSectorSize)
                continue;

            byte[] raw;
            try { raw = File.ReadAllBytes(path); }
            catch { ignored++; continue; }

            if (!IsCanonicalRawSectorForLba(raw, lba, layouts))
            {
                ignored++;
                continue;
            }

            sectors[lba] = raw;
            paths.Add(Path.GetFullPath(path));
        }

        return new DicExactRawSectorEvidence(sectors, paths, ignored);
    }

    private static bool IsCanonicalRawSectorForLba(
        ReadOnlySpan<byte> raw,
        long lba,
        IReadOnlyDictionary<long, DicSectorLayout> layouts)
    {
        if (raw.Length != RawSectorSize || !raw.Slice(0, CdRawSync.Length).SequenceEqual(CdRawSync))
            return false;

        byte[]? expectedMsf = TryBuildCanonicalMsf(lba);
        if (expectedMsf is null || !raw.Slice(12, 3).SequenceEqual(expectedMsf))
            return false;

        int logicalMode = raw[15] & 0x03;
        if (logicalMode is not (0 or 1 or 2))
            return false;

        if (layouts.TryGetValue(lba, out DicSectorLayout? layout))
        {
            if (layout.IsAudio || layout.HasExplicitFill55)
                return false;
            if (!layout.IsUnknown && (layout.Mode is 0 or 1 or 2) && layout.Mode != logicalMode)
                return false;
        }

        return true;
    }

    private static byte[]? TryBuildCanonicalMsf(long lba)
    {
        long absolute = lba + 150;
        if (absolute < 0)
            return null;

        long minute = absolute / (75 * 60);
        long remainder = absolute % (75 * 60);
        long second = remainder / 75;
        long frame = remainder % 75;
        if (minute > 99 || second > 99 || frame > 99)
            return null;

        static byte ToBcdUnchecked(long value) => (byte)(((value / 10) << 4) | (value % 10));
        return new[] { ToBcdUnchecked(minute), ToBcdUnchecked(second), ToBcdUnchecked(frame) };
    }

    private static HashSet<long> ParseMainErrorExactZeroLbas(
        string path,
        long sectorCount,
        CancellationToken cancellationToken)
    {
        var result = new HashSet<long>();
        foreach (string rawLine in File.ReadLines(path))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Match match = MainErrorAllZeroSkipDescrambleRegex.Match(rawLine.Trim());
            if (!match.Success ||
                !long.TryParse(match.Groups["lba"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long lba) ||
                lba < 0 || lba >= sectorCount)
                continue;

            result.Add(lba);
        }
        return result;
    }

    private static DicOffsetEvidenceResult ParseMainInfoOffsetEvidence(
        string path,
        CancellationToken cancellationToken)
    {
        var captureSets = new List<Dictionary<long, RawCaptureBuffer>>();
        Dictionary<long, RawCaptureBuffer>? currentSet = null;
        RawCaptureBuffer? currentCapture = null;
        bool inOffsetCheck = false;

        void FlushCapture()
        {
            if (currentCapture is null || currentSet is null)
            {
                currentCapture = null;
                return;
            }

            if (currentCapture.Written.Count(value => value) == RawSectorSize)
                currentSet[currentCapture.Lba] = currentCapture;
            currentCapture = null;
        }

        foreach (string rawLine in File.ReadLines(path))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string line = rawLine.TrimEnd();

            if (line.StartsWith("========== OpCode", StringComparison.OrdinalIgnoreCase))
            {
                FlushCapture();
                inOffsetCheck = line.Contains("Check Drive + CD offset", StringComparison.OrdinalIgnoreCase);
                if (inOffsetCheck)
                {
                    currentSet = new Dictionary<long, RawCaptureBuffer>();
                    captureSets.Add(currentSet);
                }
                else
                {
                    currentSet = null;
                }
                continue;
            }

            Match heading = LbaHeadingRegex.Match(line);
            if (heading.Success)
            {
                FlushCapture();
                string kind = heading.Groups["kind"].Value;
                if (inOffsetCheck && currentSet is not null && kind.Contains("Main Channel", StringComparison.OrdinalIgnoreCase))
                {
                    long lba = long.Parse(heading.Groups["lba"].Value, CultureInfo.InvariantCulture);
                    currentCapture = new RawCaptureBuffer(lba);
                }
                continue;
            }

            if (currentCapture is null)
                continue;

            Match hexLine = MainHexLineRegex.Match(line);
            if (!hexLine.Success)
                continue;

            int offset = int.Parse(hexLine.Groups["ofs"].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            string[] tokens = hexLine.Groups["bytes"].Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            int byteCount = 0;
            foreach (string token in tokens)
            {
                if (byteCount >= 16 || token.Length != 2 ||
                    !byte.TryParse(token, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte value))
                    break;

                int destination = offset + byteCount;
                if ((uint)destination < RawSectorSize)
                {
                    currentCapture.Data[destination] = value;
                    currentCapture.Written[destination] = true;
                }
                byteCount++;
            }
        }
        FlushCapture();

        var evidence = new Dictionary<long, DicPayloadEvidence>();
        int stitchedSectors = 0;
        foreach (Dictionary<long, RawCaptureBuffer> set in captureSets)
        {
            foreach (KeyValuePair<long, RawCaptureBuffer> pair in set.OrderBy(item => item.Key))
            {
                long lba = pair.Key;
                RawCaptureBuffer capture = pair.Value;
                int syncOffset = FindRawSyncOffset(capture.Data);
                if (syncOffset < 0)
                    continue;

                byte[] rawSector = new byte[RawSectorSize];
                bool[] known = new bool[RawSectorSize];
                int fromCurrent = RawSectorSize - syncOffset;
                Buffer.BlockCopy(capture.Data, syncOffset, rawSector, 0, fromCurrent);
                Array.Fill(known, true, 0, fromCurrent);

                if (syncOffset > 0 && set.TryGetValue(lba + 1, out RawCaptureBuffer? nextCapture))
                {
                    int nextSyncOffset = FindRawSyncOffset(nextCapture.Data);
                    if (nextSyncOffset == syncOffset)
                    {
                        Buffer.BlockCopy(nextCapture.Data, 0, rawSector, fromCurrent, syncOffset);
                        Array.Fill(known, true, fromCurrent, syncOffset);
                    }
                }

                // The offset-test reads are the scrambled physical main-channel bytes.
                // CD-ROM scrambling is XOR, so applying the same mask descrambles every
                // byte we actually captured; unknown bytes remain ignored via `known`.
                CdPregapScrambleService.ScrambleSectorInPlace(rawSector);

                if (!known[15])
                    continue;

                int payloadOffset;
                int payloadLength;
                if (rawSector[15] == 1)
                {
                    payloadOffset = 16;
                    payloadLength = CookedSectorSize;
                }
                else if (rawSector[15] == 2 && known[18] && (rawSector[18] & 0x20) == 0)
                {
                    payloadOffset = 24;
                    payloadLength = CookedSectorSize;
                }
                else
                {
                    continue;
                }

                // The LBA printed on a DIC "Check Drive + CD offset" capture is the
                // requested read position, not necessarily the raw sector whose sync we
                // recovered after accounting for the drive/CD byte offset.  Derive the
                // recovered sector address from its descrambled MSF header instead.
                //
                // Older code keyed this evidence by the capture heading and could move
                // valid file payload bytes into ISO system-area LBAs 0-15.  Dead Man's
                // Hand (install disc) exposed this clearly: PE/version-resource bytes
                // recovered from an offset test were written into LBA 0 even though the
                // original system area is zero.
                if (!TryDecodeCdHeaderLba(rawSector, known, out long recoveredLba))
                    continue;

                if (!evidence.TryGetValue(recoveredLba, out DicPayloadEvidence? target))
                {
                    target = new DicPayloadEvidence();
                    evidence[recoveredLba] = target;
                }

                for (int i = 0; i < payloadLength; i++)
                {
                    int rawIndex = payloadOffset + i;
                    if (rawIndex < RawSectorSize && known[rawIndex])
                        target.Merge(i, rawSector[rawIndex]);
                }

                if (target.KnownByteCount == CookedSectorSize)
                    stitchedSectors++;
            }
        }

        int conflicts = evidence.Values.Sum(item => item.ConflictCount);
        return new DicOffsetEvidenceResult(evidence, captureSets.Count, stitchedSectors, conflicts);
    }

    private static bool TryDecodeCdHeaderLba(ReadOnlySpan<byte> rawSector, ReadOnlySpan<bool> known, out long lba)
    {
        lba = 0;
        if (rawSector.Length < 16 || known.Length < 16 ||
            !known[12] || !known[13] || !known[14] || !known[15])
            return false;

        static bool TryBcd(byte value, out int decoded)
        {
            int hi = (value >> 4) & 0x0F;
            int lo = value & 0x0F;
            if (hi > 9 || lo > 9)
            {
                decoded = 0;
                return false;
            }
            decoded = hi * 10 + lo;
            return true;
        }

        if (!TryBcd(rawSector[12], out int minute) ||
            !TryBcd(rawSector[13], out int second) ||
            !TryBcd(rawSector[14], out int frame) ||
            second >= 60 || frame >= 75)
            return false;

        // CD-ROM sector headers use absolute MSF, with logical LBA 0 at 00:02:00.
        long absoluteFrame = checked(((long)minute * 60 + second) * 75 + frame);
        long decodedLba = absoluteFrame - 150;
        if (decodedLba < 0)
            return false;

        // Only Mode 1/2 data headers are useful to this recovery path.
        if (rawSector[15] is not 1 and not 2)
            return false;

        lba = decodedLba;
        return true;
    }

    private static int FindRawSyncOffset(ReadOnlySpan<byte> data)
    {
        for (int offset = 0; offset <= data.Length - CdRawSync.Length; offset++)
        {
            if (data.Slice(offset, CdRawSync.Length).SequenceEqual(CdRawSync))
                return offset;
        }
        return -1;
    }

    private static Dictionary<long, byte[]> ParseMainInfoMetadata(
        string path,
        CancellationToken cancellationToken)
    {
        // A complete non-offset-check Main Channel dump in mainInfo is original-disc
        // evidence regardless of whether volDesc happened to classify that LBA as
        // primary ISO9660 metadata.  Older code filtered these dumps through
        // volume.MetadataLbas, which intentionally contains only primary metadata and
        // therefore discarded exact supplementary/Joliet, slack and other non-file
        // sectors that DIC had actually captured.
        //
        // Do NOT admit the early "Check Drive + CD offset" captures here: those are
        // raw/scrambled 2352-byte reads and are decoded separately by
        // ParseMainInfoOffsetEvidence.
        var buffers = new Dictionary<long, MetadataBuffer>();
        long? dumpBaseLba = null;
        bool inOffsetCheck = false;

        foreach (string rawLine in File.ReadLines(path))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string line = rawLine.TrimEnd();

            if (line.StartsWith("========== OpCode", StringComparison.OrdinalIgnoreCase))
            {
                inOffsetCheck = line.Contains("Check Drive + CD offset", StringComparison.OrdinalIgnoreCase);
                dumpBaseLba = null;
                continue;
            }

            Match heading = LbaHeadingRegex.Match(line);
            if (heading.Success)
            {
                string kind = heading.Groups["kind"].Value;
                long parsedLba = long.Parse(heading.Groups["lba"].Value, CultureInfo.InvariantCulture);

                // mainInfo reuses the text "LBA[000000]: Main Channel" heading for
                // several internal binary-analysis buffers (for example PE/EXE export
                // and version-resource dumps).  Those buffers are not disc sectors, but
                // older recovery code treated any complete 2048-byte chunk following
                // such a heading as exact on-disc evidence.  A large LBA-0 analysis
                // buffer could therefore spill across logical LBAs 0-15 and overwrite
                // the ISO system area with executable/resource data.
                //
                // The system area has a dedicated, byte-aligned evidence path above:
                // DIC's "Check Drive + CD offset" captures are stitched, descrambled
                // and addressed from their real CD-ROM MSF headers.  Do not admit
                // generic non-offset mainInfo buffers for LBAs 0-15 here.  If offset
                // evidence is incomplete, those bytes remain explicitly unproven/zero
                // assumed (or donor-capable) instead of accepting an ambiguous buffer.
                dumpBaseLba = !inOffsetCheck &&
                              kind.Contains("Main Channel", StringComparison.OrdinalIgnoreCase) &&
                              parsedLba >= 16
                    ? parsedLba
                    : null;
                continue;
            }

            if (dumpBaseLba is null)
                continue;

            Match hexLine = MainHexLineRegex.Match(line);
            if (!hexLine.Success)
                continue;

            int offset = int.Parse(hexLine.Groups["ofs"].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            string[] tokens = hexLine.Groups["bytes"].Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            int byteCount = 0;
            foreach (string token in tokens)
            {
                if (byteCount >= 16 || token.Length != 2 || !byte.TryParse(token, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte value))
                    break;

                long absoluteOffset = (long)offset + byteCount;
                long lba = dumpBaseLba.Value + absoluteOffset / CookedSectorSize;
                int within = (int)(absoluteOffset % CookedSectorSize);
                if (!buffers.TryGetValue(lba, out MetadataBuffer? target) || target is null)
                {
                    target = new MetadataBuffer();
                    buffers[lba] = target;
                }
                target.Data[within] = value;
                target.Written[within] = true;
                byteCount++;
            }
        }

        return buffers
            .Where(pair => pair.Value.Written.Count(w => w) == CookedSectorSize)
            .ToDictionary(pair => pair.Key, pair => pair.Value.Data);
    }


}
