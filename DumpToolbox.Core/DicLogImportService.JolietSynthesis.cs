using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using DumpToolbox.Core.Mastering;

namespace DumpToolbox.Core;

public sealed partial class DicLogImportService
{
    private static void SynthesizePrimaryIsoPathTables(
        DicVolumeInfo volume,
        Dictionary<long, byte[]> metadata,
        List<string> warnings)
    {
        if (volume.PrimaryPathTableRecords.Count == 0)
            return;

        byte[] littleEndian = BuildIsoPathTable(volume.PrimaryPathTableRecords, littleEndian: true);
        byte[] bigEndian = BuildIsoPathTable(volume.PrimaryPathTableRecords, littleEndian: false);

        if (volume.PrimaryPathTableSize > 0 && littleEndian.Length != volume.PrimaryPathTableSize)
        {
            warnings.Add(
                $"DIC volDesc path-table records encode to {littleEndian.Length:N0} bytes, while the primary PVD reports " +
                $"{volume.PrimaryPathTableSize:N0} bytes. The parsed records were still used, but this disc should be verified.");
        }

        bool wroteAny = false;
        KeyValuePair<long, byte[]>? pvdPair = metadata
            .Where(pair => IsPrimaryVolumeDescriptor(pair.Value))
            .OrderBy(pair => pair.Key)
            .Select(pair => (KeyValuePair<long, byte[]>?)pair)
            .FirstOrDefault();

        if (pvdPair is KeyValuePair<long, byte[]> pvd)
        {
            ReadOnlySpan<byte> payload = pvd.Value;
            uint pathTableSize = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(132, 4));
            uint typeL = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(140, 4));
            uint optionalTypeL = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(144, 4));
            uint typeM = BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(148, 4));
            uint optionalTypeM = BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(152, 4));

            int declaredSize = checked((int)Math.Min(pathTableSize, int.MaxValue));
            WritePathTableCopies(metadata, littleEndian, declaredSize, typeL, optionalTypeL);
            WritePathTableCopies(metadata, bigEndian, declaredSize, typeM, optionalTypeM);
            wroteAny = typeL != 0 || optionalTypeL != 0 || typeM != 0 || optionalTypeM != 0;
        }

        if (!wroteAny && volume.PrimaryPathTableLba is long loggedLba && loggedLba >= 0)
        {
            int declaredSize = volume.PrimaryPathTableSize > 0
                ? checked((int)Math.Min(volume.PrimaryPathTableSize, int.MaxValue))
                : littleEndian.Length;
            WritePathTableCopies(metadata, littleEndian, declaredSize, checked((uint)loggedLba));
            wroteAny = true;
        }

        if (wroteAny)
        {
            warnings.Add(
                $"Reconstructed the primary ISO9660 path table from {volume.PrimaryPathTableRecords.Count:N0} DIC volDesc record(s) " +
                "instead of leaving missing path-table sectors zero-filled.");
        }
    }

    private static bool IsPrimaryVolumeDescriptor(ReadOnlySpan<byte> payload)
    {
        return payload.Length >= CookedSectorSize &&
               payload[0] == 1 &&
               payload[1] == (byte)'C' &&
               payload[2] == (byte)'D' &&
               payload[3] == (byte)'0' &&
               payload[4] == (byte)'0' &&
               payload[5] == (byte)'1';
    }

    private static byte[] BuildIsoPathTable(
        IReadOnlyList<DicPathTableRecord> records,
        bool littleEndian)
    {
        using var stream = new MemoryStream();
        Span<byte> numeric = stackalloc byte[4];

        foreach (DicPathTableRecord record in records)
        {
            int identifierLength = record.Identifier.Length;
            if (identifierLength <= 0 || identifierLength > byte.MaxValue)
                throw new InvalidDataException("DIC ISO9660 path-table entry has an invalid directory identifier length.");

            stream.WriteByte(checked((byte)identifierLength));
            stream.WriteByte(record.ExtendedAttributeLength);

            if (littleEndian)
                BinaryPrimitives.WriteUInt32LittleEndian(numeric, record.ExtentLba);
            else
                BinaryPrimitives.WriteUInt32BigEndian(numeric, record.ExtentLba);
            stream.Write(numeric);

            Span<byte> parent = numeric[..2];
            if (littleEndian)
                BinaryPrimitives.WriteUInt16LittleEndian(parent, record.ParentDirectoryNumber);
            else
                BinaryPrimitives.WriteUInt16BigEndian(parent, record.ParentDirectoryNumber);
            stream.Write(parent);

            stream.Write(record.Identifier);
            if ((identifierLength & 1) != 0)
                stream.WriteByte(0);
        }

        return stream.ToArray();
    }

    private static void WritePathTableCopies(
        Dictionary<long, byte[]> metadata,
        byte[] table,
        int declaredSize,
        params uint[] locations)
    {
        int bytesToStore = declaredSize > 0 ? Math.Max(declaredSize, table.Length) : table.Length;
        int sectorCount = checked((int)Math.Max(1, DivideRoundUp(bytesToStore, CookedSectorSize)));

        foreach (uint location in locations.Distinct())
        {
            if (location == 0)
                continue;

            for (int sector = 0; sector < sectorCount; sector++)
            {
                var payload = new byte[CookedSectorSize];
                int sourceOffset = sector * CookedSectorSize;
                int remaining = table.Length - sourceOffset;
                if (remaining > 0)
                    Buffer.BlockCopy(table, sourceOffset, payload, 0, Math.Min(CookedSectorSize, remaining));
                long targetLba = (long)location + sector;
                if (!metadata.ContainsKey(targetLba))
                    metadata[targetLba] = payload;
            }
        }
    }

    private static string ReadJolietApplicationIdentifierForMastering(ReadOnlySpan<byte> descriptor)
    {
        // ECMA-119 application identifier: bytes 575-702 (1-based), offset 574, length 128.
        // Joliet stores this field as UCS-2BE.  Kept here as evidence extraction; policy
        // interpretation lives in MasteringProfileDetector.
        if (descriptor.Length < 702)
            return string.Empty;
        try
        {
            return Encoding.BigEndianUnicode.GetString(descriptor.Slice(574, 128)).Trim('\0', ' ');
        }
        catch
        {
            return string.Empty;
        }
    }

    private static bool TrySynthesizeJolietMetadata(
        DicVolumeInfo volume,
        Dictionary<long, byte[]> metadata,
        List<string> warnings,
        string filenameSourceDescription = "the best filenames available in volDesc",
        IReadOnlyDictionary<string, PrimaryDirectoryMetadata>? directoryMetadata = null,
        CeQuadratLinkTableContext? ceQuadratLinkTable = null)
    {
        KeyValuePair<long, byte[]>? svdPair = metadata
            .Where(pair => IsJolietSupplementaryDescriptor(pair.Value))
            .Select(pair => (KeyValuePair<long, byte[]>?)pair)
            .FirstOrDefault();

        if (svdPair is null)
            return false;

        long svdLba = svdPair.Value.Key;
        byte[] svd = (byte[])svdPair.Value.Value.Clone();

        uint typeLPathTableLba = BinaryPrimitives.ReadUInt32LittleEndian(svd.AsSpan(140, 4));
        uint optionalTypeLPathTableLba = BinaryPrimitives.ReadUInt32LittleEndian(svd.AsSpan(144, 4));
        uint typeMPathTableLba = BinaryPrimitives.ReadUInt32BigEndian(svd.AsSpan(148, 4));
        uint optionalTypeMPathTableLba = BinaryPrimitives.ReadUInt32BigEndian(svd.AsSpan(152, 4));
        uint rootLba = BinaryPrimitives.ReadUInt32LittleEndian(svd.AsSpan(158, 4));

        uint[] pathTableLocations =
        {
            typeLPathTableLba,
            optionalTypeLPathTableLba,
            typeMPathTableLba,
            optionalTypeMPathTableLba
        };
        if (rootLba == 0 || pathTableLocations.All(location => location == 0))
        {
            warnings.Add("A Joliet supplementary volume descriptor was found, but its path-table/root locations are invalid; Joliet metadata was not synthesized.");
            return false;
        }

        // v0.2.0: central mastering-profile detection.  The synthesizer supplies
        // immutable descriptor/geometry evidence and receives policy only.
        long svdVss = BinaryPrimitives.ReadUInt32LittleEndian(svd.AsSpan(80, 4));
        string applicationIdentifier = ReadJolietApplicationIdentifierForMastering(svd);

        // Preserve the v0.1.x fallback: volDesc normally carries descriptor order, but
        // some log sets only leave exact descriptor sectors in metadata.
        IReadOnlyList<long> primaryDescriptorVssEvidence = volume.PrimaryDescriptorVolumeSpaceSizes.Count > 0
            ? volume.PrimaryDescriptorVolumeSpaceSizes.Select(value => (long)value).ToArray()
            : metadata
                .Where(pair => IsPrimaryVolumeDescriptor(pair.Value))
                .OrderBy(pair => pair.Key)
                .Select(pair => (long)BinaryPrimitives.ReadUInt32LittleEndian(pair.Value.AsSpan(80, 4)))
                .ToArray();
        IReadOnlyList<long> supplementaryDescriptorVssEvidence = volume.SupplementaryDescriptorVolumeSpaceSizes.Count > 0
            ? volume.SupplementaryDescriptorVolumeSpaceSizes.Select(value => (long)value).ToArray()
            : metadata
                .Where(pair => IsJolietSupplementaryDescriptor(pair.Value))
                .OrderBy(pair => pair.Key)
                .Select(pair => (long)BinaryPrimitives.ReadUInt32LittleEndian(pair.Value.AsSpan(80, 4)))
                .ToArray();

        var masteringEvidence = new MasteringEvidence(
            applicationIdentifier,
            primaryDescriptorVssEvidence,
            supplementaryDescriptorVssEvidence,
            svdVss,
            ceQuadratLinkTable is not null);
        IMasteringProfile masteringProfile = MasteringProfileDetector.Detect(masteringEvidence);
        bool orderDirectoryEntriesByJolietIdentifier =
            masteringProfile.JolietRecordOrdering == JolietRecordOrdering.AccentFoldedCaseSensitiveIdentifier;
        byte? supplementaryRootXaFileNumber = masteringProfile.SupplementaryRootXaFileNumber;

        if (masteringProfile.MatchedRules.Count > 0)
            warnings.Add($"JOLIET: Mastering profile '{masteringProfile.Name}' selected from evidence: {string.Join("; ", masteringProfile.MatchedRules)}.");

        // v0.1.18: supplementary/Joliet directory records normally need fresh minimal
        // records (Tiny Toon Adventures proved that primary zero/padding System Use must
        // not be copied). CD-ROM XA mastering is different: a 14-byte XA System Use
        // record is semantic metadata. However, Lionheart Bonus CD proves that the XA
        // record is not always an indivisible namespace-independent blob: the primary
        // directory-body root record and the supplementary root record can share the
        // ownership/attribute/signature prefix while legitimately carrying different
        // file-number/reserved bytes.
        //
        // Keep provenance field-specific and evidence-driven:
        //   bytes 0..7  : primary directory-record evidence (IDs/attributes + "XA")
        //   bytes 8..13 : supplementary evidence when the SVD proves it for the Joliet root
        // If one side is unavailable, retain the complete semantic XA record from the
        // available side rather than inventing values. Ordinary child/file records keep
        // their proven primary XA record unless future supplementary evidence proves a
        // different tail for that individual record.
        static bool IsSemanticXa(ReadOnlySpan<byte> systemUse)
            => systemUse.Length == 14 &&
               systemUse[6] == (byte)'X' && systemUse[7] == (byte)'A';

        static byte[]? CloneSemanticXa(byte[]? systemUse)
        {
            if (systemUse is { Length: 14 } && IsSemanticXa(systemUse))
                return (byte[])systemUse.Clone();
            return null;
        }

        byte[]? ReadSvdRootXa()
        {
            if (svd.Length < 190)
                return null;

            int svdRootRecordLength = svd[156];
            if (svdRootRecordLength < 34 || 156 + svdRootRecordLength > svd.Length)
                return null;

            ReadOnlySpan<byte> svdRootRecord = svd.AsSpan(156, svdRootRecordLength);
            int identifierLength = svdRootRecord[32];
            int systemUseOffset = 33 + identifierLength + ((identifierLength & 1) == 0 ? 1 : 0);
            if (systemUseOffset >= svdRootRecord.Length)
                return null;

            ReadOnlySpan<byte> svdSystemUse = svdRootRecord[systemUseOffset..];
            return IsSemanticXa(svdSystemUse) ? svdSystemUse.ToArray() : null;
        }

        static byte[] ComposeSupplementaryRootXa(
            byte[]? primaryXa,
            byte[]? supplementaryXa,
            byte? supplementaryRootXaFileNumber)
        {
            bool hasPrimary = primaryXa is { Length: 14 } && IsSemanticXa(primaryXa);
            bool hasSupplementary = supplementaryXa is { Length: 14 } && IsSemanticXa(supplementaryXa);

            if (hasPrimary && hasSupplementary)
            {
                byte[] combined = new byte[14];
                // IDs, attributes and the XA signature are proven by the corresponding
                // primary directory-body record.
                Buffer.BlockCopy(primaryXa!, 0, combined, 0, 8);
                // File number and reserved tail are namespace/record-specific; when the
                // SVD actually contains an XA System Use record it is direct evidence.
                Buffer.BlockCopy(supplementaryXa!, 8, combined, 8, 6);
                return combined;
            }

            if (hasPrimary)
            {
                byte[] combined = (byte[])primaryXa!.Clone();

                // Adaptec Easy CD Creator's CD-ROM XA + Joliet layout uses a distinct
                // supplementary-root XA file number even though the SVD's embedded root
                // directory record has no System Use bytes.  The mastering fingerprint is
                // carried in the Joliet SVD application identifier, so this is gated on
                // that family rather than on a disc title, pathname, extent or hash.
                // Primary root records produced by this family use FF; supplementary root
                // '.'/'..' use CC, while ordinary supplementary directories/files retain
                // their normal per-record XA values.
                if (supplementaryRootXaFileNumber is byte supplementaryRootFileNumber && combined[8] == 0xFF)
                {
                    combined[8] = supplementaryRootFileNumber;
                    Array.Clear(combined, 9, 5);
                }

                return combined;
            }

            if (hasSupplementary)
                return (byte[])supplementaryXa!.Clone();
            return Array.Empty<byte>();
        }

        JolietDirectoryNode root = BuildJolietDirectoryTree(
            volume.Files, volume.DefaultRecordingTime, directoryMetadata, inheritPrimarySystemUse: false);

        byte[]? svdRootXa = ReadSvdRootXa();
        byte[]? primaryRootSelfXa = null;
        byte[]? primaryRootParentXa = null;
        if (directoryMetadata is not null &&
            directoryMetadata.TryGetValue("/", out PrimaryDirectoryMetadata? exactPrimaryRoot))
        {
            primaryRootSelfXa = CloneSemanticXa(exactPrimaryRoot.SelfSystemUse ?? exactPrimaryRoot.SystemUse);
            primaryRootParentXa = CloneSemanticXa(exactPrimaryRoot.ParentLinkSystemUse ?? exactPrimaryRoot.SelfSystemUse ?? exactPrimaryRoot.SystemUse);
        }

        void ApplySupplementaryRootSystemUseFallback(JolietDirectoryNode targetRoot)
        {
            byte[] composedSelfXa = ComposeSupplementaryRootXa(primaryRootSelfXa, svdRootXa, supplementaryRootXaFileNumber);
            if (composedSelfXa.Length == 14)
                targetRoot.SelfSystemUse = composedSelfXa;

            // The SVD only embeds one root record, but on an XA root directory the '.' and
            // '..' records describe the same supplementary root object. Use the same proven
            // supplementary tail while retaining each primary record's own attribute prefix.
            byte[] composedParentXa = ComposeSupplementaryRootXa(primaryRootParentXa, svdRootXa, supplementaryRootXaFileNumber);
            if (composedParentXa.Length == 14)
                targetRoot.ParentLinkSystemUse = composedParentXa;
        }

        ApplySupplementaryRootSystemUseFallback(root);

        if (supplementaryRootXaFileNumber is byte supplementaryRootFileNumber &&
            svdRootXa is null &&
            primaryRootSelfXa is { Length: 14 } &&
            primaryRootSelfXa[8] == 0xFF)
        {
            warnings.Add($"JOLIET: Mastering profile applied supplementary-root XA file-number convention (primary FF -> supplementary {supplementaryRootFileNumber:X2}) from the SVD formatter/version fingerprint; no disc-specific title/path/hash rule was used.");
        }

        // v0.1.28: source folders cannot represent empty directories. Restore only
        // directory nodes explicitly proven by the primary Type-L path table and parsed
        // primary directory metadata. Actua Soccer 3 has six such language directories.
        if (directoryMetadata is not null && volume.PrimaryPathTableRecords.Count > 0)
        {
            var nodeByPrimaryExtent = new Dictionary<long, JolietDirectoryNode>();
            foreach (JolietDirectoryNode node in FlattenJolietDirectories(root))
            {
                if (node.PrimaryExtentLba is long extent)
                    nodeByPrimaryExtent.TryAdd(extent, node);
            }

            var metadataByExtent = directoryMetadata.Values
                .Where(item => item.PrimaryExtentLba is not null)
                .GroupBy(item => item.PrimaryExtentLba!.Value)
                .Where(group => group.Count() == 1)
                .ToDictionary(group => group.Key, group => group.First());

            static string InferMissingJolietDirectoryName(string primaryName, JolietDirectoryNode parent)
            {
                JolietDirectoryNode[] siblingsWithLetters = parent.Children.Values
                    .Where(child => child.Name.Any(char.IsLetter))
                    .ToArray();
                if (siblingsWithLetters.Length > 0 &&
                    siblingsWithLetters.All(child => child.Name == child.Name.ToLowerInvariant()))
                    return primaryName.ToLowerInvariant();
                return primaryName;
            }

            int addedEmptyDirectories = 0;
            for (int index = 1; index < volume.PrimaryPathTableRecords.Count; index++)
            {
                DicPathTableRecord record = volume.PrimaryPathTableRecords[index];
                long extent = record.ExtentLba;
                if (nodeByPrimaryExtent.ContainsKey(extent))
                    continue;

                int parentIndex = record.ParentDirectoryNumber - 1;
                if (parentIndex < 0 || parentIndex >= volume.PrimaryPathTableRecords.Count)
                    continue;
                long parentExtent = volume.PrimaryPathTableRecords[parentIndex].ExtentLba;
                if (!nodeByPrimaryExtent.TryGetValue(parentExtent, out JolietDirectoryNode? parent) ||
                    !metadataByExtent.TryGetValue(extent, out PrimaryDirectoryMetadata? primaryMetadata))
                    continue;

                string primaryName = Encoding.ASCII.GetString(record.Identifier).TrimEnd('\0', ' ');
                if (string.IsNullOrWhiteSpace(primaryName))
                    continue;
                string jolietName = InferMissingJolietDirectoryName(primaryName, parent);
                if (parent.Children.ContainsKey(jolietName))
                    continue;

                var child = new JolietDirectoryNode(jolietName, parent, primaryMetadata);
                parent.Children.Add(jolietName, child);
                nodeByPrimaryExtent[extent] = child;
                addedEmptyDirectories++;
            }

            if (addedEmptyDirectories > 0)
                warnings.Add($"JOLIET: Restored {addedEmptyDirectories:N0} empty directory node(s) from DIC primary path-table evidence that could not be represented by the extracted source-file tree.");
        }

        List<JolietDirectoryNode> directories = FlattenJolietDirectories(root);
        if (directories.Count == 0)
            return false;

        // v0.1.24: ISO9660 Volume Space Size is not always the upper bound of the
        // physical/logical LBA namespace used by XA multi-track masters. Actua Soccer 3
        // is the proving case: VSS counts the 321,628 Form-1 sectors, while 762 Form-2
        // sectors are interspersed in the 322,390-sector image. Valid primary/Joliet
        // directory and file extents therefore exist numerically beyond VSS.
        //
        // Use the strongest imported address evidence available to establish a safe
        // synthesis ceiling: VSS, the end of every DIC file extent, and the highest
        // exact metadata LBA already recovered from the logs. This does not invent an
        // image size; it merely prevents mastering-specific allocators from rejecting
        // already-proven extents solely because their LBA is greater than VSS.
        long logicalAddressLimit = Math.Max(0, volume.VolumeSpaceSize);
        foreach (DicFileRecord file in volume.Files.Where(file => file.ExtentLba >= 0))
        {
            long fileSectors = Math.Max(1, DivideRoundUp(file.DataLength, CookedSectorSize));
            logicalAddressLimit = Math.Max(logicalAddressLimit, checked(file.ExtentLba + fileSectors));
        }
        if (metadata.Count > 0)
            logicalAddressLimit = Math.Max(logicalAddressLimit, checked(metadata.Keys.Max() + 1));

        uint originalSvdRootDataLength = BinaryPrimitives.ReadUInt32LittleEndian(svd.AsSpan(166, 4));
        bool preserveExactDirectoryByteLengths = originalSvdRootDataLength > 0 && (originalSvdRootDataLength % CookedSectorSize) != 0;

        bool appendFileVersionSuffix = true;
        foreach (JolietDirectoryNode directory in directories)
            directory.DataLength = ComputeJolietDirectoryDataLength(directory, appendFileVersionSuffix, orderDirectoryEntriesByJolietIdentifier, roundToSector: !preserveExactDirectoryByteLengths);

        long earliestFileLba = volume.Files
            .Where(file => file.DataLength > 0 && file.ExtentLba >= 0)
            .Select(file => file.ExtentLba)
            .DefaultIfEmpty(volume.VolumeSpaceSize)
            .Min();

        static bool RangesOverlap(long aStart, long aCount, long bStart, long bCount)
            => aCount > 0 && bCount > 0 && aStart < checked(bStart + bCount) && bStart < checked(aStart + aCount);

        static string GetJolietDirectoryNodePath(JolietDirectoryNode directory)
        {
            if (directory.Parent is null)
                return "/";

            var parts = new Stack<string>();
            for (JolietDirectoryNode? current = directory; current is not null && current.Parent is not null; current = current.Parent)
                parts.Push(current.Name);

            return "/" + string.Join("/", parts);
        }

        bool TryAllocateLoggedSupplementaryPathTableLayout()
        {
            // v0.7.71: when DIC volDesc preserves the supplementary/Joliet path-table
            // records, those records are stronger placement evidence than any inferred
            // contiguous/translated/paired allocator.  They give the original extent for
            // every directory plus the directory-number/parent relationship.  Use them
            // only when the complete generated Joliet tree maps one-to-one by path and all
            // resulting ranges are safe.
            if (volume.SupplementaryDirectoryHints.Count == 0)
                return false;

            var hintsByPath = volume.SupplementaryDirectoryHints
                .GroupBy(hint => NormalizeIsoPath(hint.Path), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);

            if (!hintsByPath.TryGetValue("/", out DicSupplementaryDirectoryHint[]? rootHints) ||
                rootHints.Length != 1 || rootHints[0].ExtentLba != rootLba)
                return false;

            var proposed = new List<(JolietDirectoryNode Directory, long Start, long Count)>();
            foreach (JolietDirectoryNode directory in directories)
            {
                string path = NormalizeIsoPath(GetJolietDirectoryNodePath(directory));
                if (!hintsByPath.TryGetValue(path, out DicSupplementaryDirectoryHint[]? hints) || hints.Length != 1)
                    return false;

                long start = hints[0].ExtentLba;
                long count = DivideRoundUp(directory.DataLength, CookedSectorSize);
                if (start < 0 || count <= 0 || checked(start + count) > logicalAddressLimit)
                    return false;

                foreach (uint tableLba in pathTableLocations.Where(location => location != 0))
                {
                    if (RangesOverlap(start, count, tableLba, 1))
                        return false;
                }

                foreach (DicFileRecord file in volume.Files.Where(file => file.DataLength > 0 && file.ExtentLba >= 0))
                {
                    long fileCount = DivideRoundUp(file.DataLength, CookedSectorSize);
                    if (RangesOverlap(start, count, file.ExtentLba, fileCount))
                        return false;
                }

                if (proposed.Any(existing => RangesOverlap(start, count, existing.Start, existing.Count)))
                    return false;

                proposed.Add((directory, start, count));
            }

            if (proposed.Count != volume.SupplementaryDirectoryHints.Count)
                return false;

            foreach ((JolietDirectoryNode directory, long start, _) in proposed)
                directory.ExtentLba = start;

            string extents = string.Join(", ", volume.SupplementaryDirectoryHints
                .OrderBy(hint => hint.DirectoryNumber)
                .Select(hint => $"{hint.Path}={hint.ExtentLba}"));
            warnings.Add(
                $"Joliet directory placement uses {volume.SupplementaryDirectoryHints.Count:N0} original supplementary path-table record(s) parsed from DIC volDesc; " +
                $"child-directory LBAs are therefore DIC evidence rather than a contiguous allocator guess. Layout: {extents}.");
            return true;
        }

        bool TryAllocateTranslatedPrimaryLayout()
        {
            // Some mastering tools build the supplementary/Joliet directory tree as a
            // fixed-LBA translation of the primary ISO9660 directory tree.  The SVD root
            // extent independently establishes the translation delta; we then accept the
            // same delta for children only when every primary directory is mapped and every
            // synthesized range is structurally safe.
            if (root.PrimaryExtentLba is not long primaryRootLba)
                return false;

            long translation = checked((long)rootLba - primaryRootLba);
            if (translation <= 0)
                return false;

            var proposed = new List<(JolietDirectoryNode Directory, long Start, long Count)>();
            foreach (JolietDirectoryNode directory in directories)
            {
                if (directory.PrimaryExtentLba is not long primaryLba)
                    return false;

                long start = checked(primaryLba + translation);
                long count = DivideRoundUp(directory.DataLength, CookedSectorSize);
                if (start < 0 || count <= 0 || checked(start + count) > logicalAddressLimit)
                    return false;

                foreach (uint tableLba in pathTableLocations.Where(location => location != 0))
                {
                    if (RangesOverlap(start, count, tableLba, 1))
                        return false;
                }

                foreach (DicFileRecord file in volume.Files.Where(file => file.DataLength > 0 && file.ExtentLba >= 0))
                {
                    long fileCount = DivideRoundUp(file.DataLength, CookedSectorSize);
                    if (RangesOverlap(start, count, file.ExtentLba, fileCount))
                        return false;
                }

                if (proposed.Any(existing => RangesOverlap(start, count, existing.Start, existing.Count)))
                    return false;

                proposed.Add((directory, start, count));
            }

            // The root is the independent SVD anchor for the translation.
            if (proposed.FirstOrDefault(item => ReferenceEquals(item.Directory, root)).Start != rootLba)
                return false;

            foreach ((JolietDirectoryNode directory, long start, _) in proposed)
                directory.ExtentLba = start;

            warnings.Add(
                $"Joliet directory placement follows an SVD-proven fixed-offset primary/supplementary layout: " +
                $"the Joliet root is {translation:+#;-#;0} sector(s) from the primary root, and applying the same translation to every mapped primary directory produces non-overlapping supplementary ranges that avoid files and declared path tables.");
            return true;
        }

        string? pairedPathTableFailure = null;
        bool pairedPathTableLayoutSelected = false;
        bool TryAllocatePairedPrimaryPathTableOrderLayout()
        {
            // v0.1.24 diagnostics: retain the v0.1.21 paired allocator, but record
            // the first concrete reason it rejects a candidate layout.  This is
            // intentionally not title-specific and makes mastering-family discovery
            // auditable from the normal recovery log.
            bool Reject(string reason)
            {
                pairedPathTableFailure ??= reason;
                return false;
            }

            if (volume.PrimaryPathTableRecords.Count == 0)
                return Reject("primary Type-L path table has no recovered records");
            if (volume.PrimaryPathTableRecords.Count != directories.Count)
            {
                int distinctPrimaryPathExtents = volume.PrimaryPathTableRecords
                    .Select(record => record.ExtentLba)
                    .Distinct()
                    .Count();
                string duplicatePrimaryPathExtents = string.Join(", ", volume.PrimaryPathTableRecords
                    .Select((record, index) => (record, index))
                    .GroupBy(item => item.record.ExtentLba)
                    .Where(group => group.Count() > 1)
                    .Take(12)
                    .Select(group => $"LBA {group.Key:N0}: path-table entries " + string.Join(",", group.Select(item => (item.index + 1).ToString()))));
                JolietDirectoryNode[] mappedDirectories = directories
                    .Where(directory => directory.PrimaryExtentLba is not null)
                    .ToArray();
                int distinctMappedExtents = mappedDirectories
                    .Select(directory => directory.PrimaryExtentLba!.Value)
                    .Distinct()
                    .Count();
                string duplicateMapped = string.Join(", ", mappedDirectories
                    .GroupBy(directory => directory.PrimaryExtentLba!.Value)
                    .Where(group => group.Count() > 1)
                    .Take(12)
                    .Select(group => $"LBA {group.Key:N0}: " + string.Join(" | ", group.Select(GetJolietDirectoryNodePath))));
                string unmapped = string.Join(", ", directories
                    .Where(directory => directory.PrimaryExtentLba is null)
                    .Take(24)
                    .Select(GetJolietDirectoryNodePath));

                return Reject(
                    $"primary path-table directory count {volume.PrimaryPathTableRecords.Count:N0} != generated Joliet directory count {directories.Count:N0}; " +
                    $"primary path-table distinct extents={distinctPrimaryPathExtents:N0}" +
                    (duplicatePrimaryPathExtents.Length > 0 ? $"; duplicate primary path-table extents: {duplicatePrimaryPathExtents}" : string.Empty) + "; " +
                    $"generated directories with mapped primary extents={mappedDirectories.Length:N0}, distinct mapped primary extents={distinctMappedExtents:N0}, " +
                    $"unmapped={directories.Count - mappedDirectories.Length:N0}" +
                    (duplicateMapped.Length > 0 ? $"; duplicate-primary-extent groups: {duplicateMapped}" : string.Empty) +
                    (unmapped.Length > 0 ? $"; first unmapped generated paths: {unmapped}" : string.Empty));
            }
            if (directoryMetadata is null)
                return Reject("primary directory metadata map is unavailable");
            if (root.PrimaryExtentLba is not long primaryRootLba)
                return Reject("generated Joliet root has no mapped primary root extent");
            if (root.PrimaryDataLength is not long primaryRootLength)
                return Reject("generated Joliet root has no mapped primary root byte length");

            long predictedRoot = checked(primaryRootLba + DivideRoundUp(primaryRootLength, CookedSectorSize));
            if (predictedRoot != rootLba)
                return Reject($"SVD root LBA {rootLba:N0} does not equal primary root {primaryRootLba:N0} + its {DivideRoundUp(primaryRootLength, CookedSectorSize):N0}-sector allocation (predicted {predictedRoot:N0})");

            var primaryLengthsByExtent = directoryMetadata.Values
                .Where(item => item.PrimaryExtentLba is not null && item.PrimaryDataLength is not null)
                .GroupBy(item => item.PrimaryExtentLba!.Value)
                .Where(group => group.Select(item => item.PrimaryDataLength!.Value).Distinct().Count() == 1)
                .ToDictionary(group => group.Key, group => group.First().PrimaryDataLength!.Value);

            var proposed = new List<(JolietDirectoryNode Directory, long Start, long Count)>();
            for (int index = 0; index < directories.Count; index++)
            {
                JolietDirectoryNode directory = directories[index];
                DicPathTableRecord primaryRecord = volume.PrimaryPathTableRecords[index];
                long primaryLba = primaryRecord.ExtentLba;

                if (!primaryLengthsByExtent.TryGetValue(primaryLba, out long primaryLength) || primaryLength <= 0)
                    return Reject($"primary path-table entry #{index + 1:N0} at LBA {primaryLba:N0} has no unambiguous parsed primary directory length");

                long start = checked(primaryLba + DivideRoundUp(primaryLength, CookedSectorSize));
                long count = DivideRoundUp(directory.DataLength, CookedSectorSize);
                if (start < 0 || count <= 0)
                    return Reject($"entry #{index + 1:N0} produced invalid supplementary range start={start:N0}, count={count:N0}");
                if (checked(start + count) > logicalAddressLimit)
                    return Reject($"entry #{index + 1:N0} supplementary range LBA {start:N0}-{start + count - 1:N0} exceeds evidence-derived logical ceiling {logicalAddressLimit - 1:N0}");

                foreach (uint tableLba in pathTableLocations.Where(location => location != 0))
                {
                    if (RangesOverlap(start, count, tableLba, 1))
                        return Reject($"entry #{index + 1:N0} supplementary range LBA {start:N0}-{start + count - 1:N0} overlaps declared path-table LBA {tableLba:N0}");
                }

                foreach (DicFileRecord file in volume.Files.Where(file => file.DataLength > 0 && file.ExtentLba >= 0))
                {
                    long fileCount = DivideRoundUp(file.DataLength, CookedSectorSize);
                    if (RangesOverlap(start, count, file.ExtentLba, fileCount))
                        return Reject($"entry #{index + 1:N0} supplementary range LBA {start:N0}-{start + count - 1:N0} overlaps file '{file.Path}' at LBA {file.ExtentLba:N0}-{file.ExtentLba + fileCount - 1:N0}");
                }

                if (proposed.FirstOrDefault(existing => RangesOverlap(start, count, existing.Start, existing.Count)) is var overlap && overlap.Directory is not null)
                    return Reject($"entry #{index + 1:N0} supplementary range LBA {start:N0}-{start + count - 1:N0} overlaps another proposed Joliet directory at LBA {overlap.Start:N0}-{overlap.Start + overlap.Count - 1:N0}");

                proposed.Add((directory, start, count));
            }

            if (proposed.Count == 0)
                return Reject("no supplementary directory ranges were proposed");
            if (!ReferenceEquals(proposed[0].Directory, root))
                return Reject("generated Joliet directory order does not begin with the root directory");
            if (proposed[0].Start != rootLba)
                return Reject($"first paired range starts at LBA {proposed[0].Start:N0}, not SVD root LBA {rootLba:N0}");

            foreach ((JolietDirectoryNode directory, long start, _) in proposed)
                directory.ExtentLba = start;

            pairedPathTableFailure = null;
            pairedPathTableLayoutSelected = true;
            warnings.Add(
                "Joliet directory placement follows a DIC-proven path-table-paired primary/supplementary layout: " +
                "the SVD root sits immediately after the primary root allocation, and primary Type-L path-table directory order maps every supplementary directory to the sector immediately after its corresponding primary directory. " +
                "This permits byte-safe reconstruction even when Joliet long directory names cannot be joined back to primary 8.3 paths by pathname alone.");
            return true;
        }

        bool TryAllocatePairedPrimaryLayout()
        {
            // Some mastering tools place each Joliet directory immediately after the
            // corresponding primary ISO9660 directory instead of reserving one contiguous
            // supplementary-directory area.  This is only accepted when the SVD independently
            // confirms the rule for the root directory and every proposed child range is safe.
            if (root.PrimaryExtentLba is not long primaryRootLba || root.PrimaryDataLength is not long primaryRootLength)
                return false;

            long predictedRoot = checked(primaryRootLba + DivideRoundUp(primaryRootLength, CookedSectorSize));
            if (predictedRoot != rootLba)
                return false;

            var proposed = new List<(JolietDirectoryNode Directory, long Start, long Count)>();
            foreach (JolietDirectoryNode directory in directories)
            {
                if (directory.PrimaryExtentLba is not long primaryLba || directory.PrimaryDataLength is not long primaryLength)
                    return false;

                long start = checked(primaryLba + DivideRoundUp(primaryLength, CookedSectorSize));
                long count = DivideRoundUp(directory.DataLength, CookedSectorSize);
                if (start < 0 || count <= 0 || checked(start + count) > logicalAddressLimit)
                    return false;

                // Declared Joliet path-table sectors may not be overwritten by directories.
                foreach (uint tableLba in pathTableLocations.Where(location => location != 0))
                {
                    if (RangesOverlap(start, count, tableLba, 1))
                        return false;
                }

                // Never place synthesized directory metadata over an ordinary file extent.
                foreach (DicFileRecord file in volume.Files.Where(file => file.DataLength > 0 && file.ExtentLba >= 0))
                {
                    long fileCount = DivideRoundUp(file.DataLength, CookedSectorSize);
                    if (RangesOverlap(start, count, file.ExtentLba, fileCount))
                        return false;
                }

                if (proposed.Any(existing => RangesOverlap(start, count, existing.Start, existing.Count)))
                    return false;

                proposed.Add((directory, start, count));
            }

            foreach ((JolietDirectoryNode directory, long start, _) in proposed)
                directory.ExtentLba = start;

            warnings.Add(
                "Joliet directory placement follows a DIC-proven paired primary/supplementary layout: " +
                "the SVD root extent equals the sector immediately after the primary root allocation, and every child directory fits safely immediately after its corresponding primary ISO9660 directory.");
            return true;
        }

        bool TryAllocateCeQuadratPrimaryExtentOrder()
        {
            // CeQuadrat/WinOnCD keeps the Joliet path table in ISO9660 path-table
            // directory order, but lays the actual supplementary directory bodies out
            // contiguously from the SVD root in ascending *primary extent* order.
            // The private link-table sector exists precisely because those two orders can
            // differ.  Rebellion demonstrates the rule cleanly: primary extents
            // 47,48,49,50,52,57,58,60,61 become contiguous Joliet directories starting
            // at the SVD-root LBA, while the bridge is still written in primary path-table
            // order.  Only accept this layout when the independently detected CeQuadrat
            // bridge context covers every generated directory and all proposed ranges are
            // structurally safe.
            if (ceQuadratLinkTable is null ||
                directories.Count != ceQuadratLinkTable.PrimaryDirectoryExtents.Count ||
                root.PrimaryExtentLba is not long primaryRootLba ||
                primaryRootLba < 0)
                return false;

            var byPrimaryExtent = new Dictionary<uint, JolietDirectoryNode>();
            foreach (JolietDirectoryNode directory in directories)
            {
                if (directory.PrimaryExtentLba is not long primaryLba ||
                    primaryLba < 0 || primaryLba > uint.MaxValue ||
                    !byPrimaryExtent.TryAdd(checked((uint)primaryLba), directory))
                    return false;
            }

            // The context came from the actual primary Type-L path table.  Requiring the
            // exact same extent set prevents an inferred/ambiguous directory mapping from
            // selecting this mastering-specific allocator.
            if (!ceQuadratLinkTable.PrimaryDirectoryExtents.ToHashSet().SetEquals(byPrimaryExtent.Keys))
                return false;

            long cursor = rootLba;
            var proposed = new List<(JolietDirectoryNode Directory, long Start, long Count)>();
            foreach (KeyValuePair<uint, JolietDirectoryNode> pair in byPrimaryExtent.OrderBy(pair => pair.Key))
            {
                JolietDirectoryNode directory = pair.Value;
                long count = DivideRoundUp(directory.DataLength, CookedSectorSize);
                if (count <= 0)
                    return false;

                long start;
                if (ceQuadratLinkTable.ExistingJolietByPrimary is not null)
                {
                    if (!ceQuadratLinkTable.ExistingJolietByPrimary.TryGetValue(pair.Key, out uint provenJolietLba))
                        return false;
                    start = provenJolietLba;
                }
                else
                {
                    start = cursor;
                }
                if (start < 0 || checked(start + count) > logicalAddressLimit)
                    return false;

                foreach (uint tableLba in pathTableLocations.Where(location => location != 0))
                {
                    if (RangesOverlap(start, count, tableLba, 1))
                        return false;
                }

                foreach (DicFileRecord file in volume.Files.Where(file => file.DataLength > 0 && file.ExtentLba >= 0))
                {
                    long fileCount = DivideRoundUp(file.DataLength, CookedSectorSize);
                    if (RangesOverlap(start, count, file.ExtentLba, fileCount))
                        return false;
                }

                proposed.Add((directory, start, count));
                cursor = checked(cursor + count);
            }

            // SVD root extent is the independent anchor: the first primary extent must be
            // the primary root and its supplementary body must start exactly at rootLba.
            JolietDirectoryNode first = proposed[0].Directory;
            if (!ReferenceEquals(first, root) || proposed[0].Start != rootLba)
                return false;

            foreach ((JolietDirectoryNode directory, long start, _) in proposed)
                directory.ExtentLba = start;

            warnings.Add(
                "Joliet directory placement follows the CeQuadrat/WinOnCD layout proven by the private directory-link-table context: " +
                "directory bodies are packed contiguously from the SVD root in ascending primary-directory extent order, while the Joliet path table retains primary path-table directory order.");
            warnings.Add(
                "JOLIET: CeQuadrat/WinOnCD supplementary child records are emitted in ordinal Joliet-identifier order; primary ISO9660 record order remains unchanged and is used by non-CeQuadrat mastering paths.");
            return true;
        }

        bool allocatedProvenLayout = TryAllocateLoggedSupplementaryPathTableLayout() || TryAllocateCeQuadratPrimaryExtentOrder() || TryAllocatePairedPrimaryPathTableOrderLayout() || TryAllocatePairedPrimaryLayout() || TryAllocateTranslatedPrimaryLayout();

        if (!allocatedProvenLayout)
        {
            // A UCS-2/Joliet file identifier normally omits the ISO9660 version suffix.
            // Preserve the historical/versioned form first because existing byte-exact
            // regressions prove that some real-world mastering tools emitted it anyway.
            // If that representation makes an independently SVD-proven layout impossible,
            // retry the exact same metadata with unversioned Joliet identifiers before
            // discarding any System Use evidence.
            appendFileVersionSuffix = false;
            foreach (JolietDirectoryNode directory in directories)
                directory.DataLength = ComputeJolietDirectoryDataLength(directory, appendFileVersionSuffix, orderDirectoryEntriesByJolietIdentifier, roundToSector: !preserveExactDirectoryByteLengths);

            allocatedProvenLayout = TryAllocateLoggedSupplementaryPathTableLayout() || TryAllocateCeQuadratPrimaryExtentOrder() || TryAllocatePairedPrimaryPathTableOrderLayout() || TryAllocatePairedPrimaryLayout() || TryAllocateTranslatedPrimaryLayout();
            if (allocatedProvenLayout)
            {
                warnings.Add(
                    "The SVD-proven Joliet directory geometry is incompatible with ISO9660-style ';1' version suffixes in supplementary file identifiers. " +
                    "Joliet file identifiers were therefore generated without the version suffix while preserving all inherited System Use evidence.");
            }
        }

        if (!allocatedProvenLayout && directoryMetadata is not null)
        {
            // System Use belongs to an individual directory record, not automatically to
            // every namespace describing the same file.  Preserve it ahead of stripping,
            // and prefer standards-style unversioned Joliet identifiers when testing the
            // stripped variant.  A final versioned+stripped retry remains for compatibility
            // with unusual historical mastering layouts.
            JolietDirectoryNode originalRoot = root;
            List<JolietDirectoryNode> originalDirectories = directories;

            JolietDirectoryNode strippedRoot = BuildJolietDirectoryTree(
                volume.Files, volume.DefaultRecordingTime, directoryMetadata, inheritPrimarySystemUse: false);
            ApplySupplementaryRootSystemUseFallback(strippedRoot);
            List<JolietDirectoryNode> strippedDirectories = FlattenJolietDirectories(strippedRoot);

            appendFileVersionSuffix = false;
            foreach (JolietDirectoryNode directory in strippedDirectories)
                directory.DataLength = ComputeJolietDirectoryDataLength(directory, appendFileVersionSuffix, orderDirectoryEntriesByJolietIdentifier, roundToSector: !preserveExactDirectoryByteLengths);

            root = strippedRoot;
            directories = strippedDirectories;
            allocatedProvenLayout = TryAllocateLoggedSupplementaryPathTableLayout() || TryAllocateCeQuadratPrimaryExtentOrder() || TryAllocatePairedPrimaryPathTableOrderLayout() || TryAllocatePairedPrimaryLayout() || TryAllocateTranslatedPrimaryLayout();
            if (allocatedProvenLayout)
            {
                warnings.Add(
                    "The SVD-proven Joliet directory geometry could not be satisfied while inheriting primary ISO9660 System Use bytes. " +
                    "Supplementary records were generated without inherited System Use and with unversioned Joliet file identifiers; all proven extents then fit safely without overlap.");
            }
            else
            {
                appendFileVersionSuffix = true;
                foreach (JolietDirectoryNode directory in strippedDirectories)
                    directory.DataLength = ComputeJolietDirectoryDataLength(directory, appendFileVersionSuffix, orderDirectoryEntriesByJolietIdentifier, roundToSector: !preserveExactDirectoryByteLengths);

                allocatedProvenLayout = TryAllocateLoggedSupplementaryPathTableLayout() || TryAllocateCeQuadratPrimaryExtentOrder() || TryAllocatePairedPrimaryPathTableOrderLayout() || TryAllocatePairedPrimaryLayout() || TryAllocateTranslatedPrimaryLayout();
                if (allocatedProvenLayout)
                {
                    warnings.Add(
                        "The SVD-proven Joliet directory geometry required omitting inherited primary System Use while retaining ISO9660-style ';1' supplementary file-version suffixes.");
                }
                else
                {
                    root = originalRoot;
                    directories = originalDirectories;
                    appendFileVersionSuffix = true;
                    foreach (JolietDirectoryNode directory in directories)
                        directory.DataLength = ComputeJolietDirectoryDataLength(directory, appendFileVersionSuffix, orderDirectoryEntriesByJolietIdentifier, roundToSector: !preserveExactDirectoryByteLengths);
                }
            }
        }

        if (!allocatedProvenLayout)
        {
            // Default layout used by the existing exact regressions: place supplementary
            // directories contiguously from the SVD-declared root extent, but only when the
            // complete generated tree fits before any other known disc content.
            //
            // v0.7.68: the compact Joliet representations above used to be tested only
            // against the mastering-specific translated/paired/CeQuadrat allocators. If no
            // such layout was proven, the code restored the largest (versioned + inherited
            // System Use) representation before testing this ordinary contiguous layout.
            // ALFABE exposes the bug: the large representation needs 23 sectors while the
            // SVD-root window contains 19. Test the same safe representation fallbacks
            // against the SVD-anchored contiguous window before giving up.
            long directoryLimit = earliestFileLba;
            foreach (uint pathTableLocation in pathTableLocations.Where(location => location > rootLba))
                directoryLimit = Math.Min(directoryLimit, pathTableLocation);

            long availableSectors = Math.Max(0, directoryLimit - rootLba);

            bool TryAllocateDefaultContiguous(
                JolietDirectoryNode candidateRoot,
                List<JolietDirectoryNode> candidateDirectories,
                out long neededSectors)
            {
                long cursor = rootLba;
                foreach (JolietDirectoryNode directory in candidateDirectories)
                {
                    directory.ExtentLba = cursor;
                    cursor = checked(cursor + DivideRoundUp(directory.DataLength, CookedSectorSize));
                }

                neededSectors = checked(cursor - rootLba);
                return cursor <= directoryLimit &&
                       candidateDirectories.Count > 0 &&
                       ReferenceEquals(candidateDirectories[0], candidateRoot) &&
                       candidateRoot.ExtentLba == rootLba;
            }

            long originalNeeded;
            if (!TryAllocateDefaultContiguous(root, directories, out originalNeeded))
            {
                bool compactAllocated = false;

                // First prefer standards-style unversioned Joliet identifiers while
                // retaining any inherited System Use evidence.
                appendFileVersionSuffix = false;
                foreach (JolietDirectoryNode directory in directories)
                    directory.DataLength = ComputeJolietDirectoryDataLength(directory, appendFileVersionSuffix, orderDirectoryEntriesByJolietIdentifier, roundToSector: !preserveExactDirectoryByteLengths);

                if (TryAllocateDefaultContiguous(root, directories, out long unversionedNeeded))
                {
                    compactAllocated = true;
                    warnings.Add(
                        $"The SVD-anchored Joliet directory area provides {availableSectors:N0} sector(s) from LBA {rootLba:N0}. " +
                        $"The ISO9660-style versioned supplementary representation needed {originalNeeded:N0} sector(s), but standards-style unversioned Joliet identifiers need {unversionedNeeded:N0} and fit exactly/safely in the original area. " +
                        "Primary ISO9660 metadata remains unchanged.");
                }
                else if (directoryMetadata is not null)
                {
                    // If inherited primary System Use is what pushes the supplementary
                    // directory bodies over the original allocation, rebuild a Joliet-only
                    // tree without that namespace-specific evidence and retry unversioned.
                    JolietDirectoryNode strippedRoot = BuildJolietDirectoryTree(
                        volume.Files, volume.DefaultRecordingTime, directoryMetadata, inheritPrimarySystemUse: false);
                    ApplySupplementaryRootSystemUseFallback(strippedRoot);
                    List<JolietDirectoryNode> strippedDirectories = FlattenJolietDirectories(strippedRoot);
                    foreach (JolietDirectoryNode directory in strippedDirectories)
                        directory.DataLength = ComputeJolietDirectoryDataLength(directory, appendFileVersionSuffix: false, orderByJolietIdentifier: orderDirectoryEntriesByJolietIdentifier, roundToSector: !preserveExactDirectoryByteLengths);

                    if (TryAllocateDefaultContiguous(strippedRoot, strippedDirectories, out long strippedUnversionedNeeded))
                    {
                        root = strippedRoot;
                        directories = strippedDirectories;
                        appendFileVersionSuffix = false;
                        compactAllocated = true;
                        warnings.Add(
                            $"The original Joliet directory window provides {availableSectors:N0} sector(s) from LBA {rootLba:N0}. " +
                            $"The inherited/versioned representation needed {originalNeeded:N0}; omitting primary-only System Use and using unversioned Joliet identifiers reduces it to {strippedUnversionedNeeded:N0} sector(s), which fits safely. " +
                            "DIC primary ISO9660 records are still preserved byte-for-byte.");
                    }
                    else
                    {
                        // Some historical mastering tools retained ';1' in Joliet even
                        // when they did not copy primary System Use. Keep that as the last
                        // compact representation before refusing synthesis.
                        appendFileVersionSuffix = true;
                        foreach (JolietDirectoryNode directory in strippedDirectories)
                            directory.DataLength = ComputeJolietDirectoryDataLength(directory, appendFileVersionSuffix, orderDirectoryEntriesByJolietIdentifier, roundToSector: !preserveExactDirectoryByteLengths);

                        if (TryAllocateDefaultContiguous(strippedRoot, strippedDirectories, out long strippedVersionedNeeded))
                        {
                            root = strippedRoot;
                            directories = strippedDirectories;
                            compactAllocated = true;
                            warnings.Add(
                                $"The original Joliet directory window provides {availableSectors:N0} sector(s) from LBA {rootLba:N0}. " +
                                $"Omitting inherited primary System Use reduces the supplementary tree from {originalNeeded:N0} to {strippedVersionedNeeded:N0} sector(s), allowing the original SVD-anchored layout to be retained.");
                        }
                    }
                }

                if (!compactAllocated)
                {
                    // Restore the historically conservative representation for diagnostics.
                    appendFileVersionSuffix = true;
                    root = BuildJolietDirectoryTree(
                        volume.Files, volume.DefaultRecordingTime, directoryMetadata, inheritPrimarySystemUse: false);
                    ApplySupplementaryRootSystemUseFallback(root);
                    directories = FlattenJolietDirectories(root);
                    foreach (JolietDirectoryNode directory in directories)
                        directory.DataLength = ComputeJolietDirectoryDataLength(directory, appendFileVersionSuffix, orderDirectoryEntriesByJolietIdentifier, roundToSector: !preserveExactDirectoryByteLengths);
                    TryAllocateDefaultContiguous(root, directories, out long finalNeeded);

                    if (!string.IsNullOrWhiteSpace(pairedPathTableFailure))
                        warnings.Add($"JOLIET PAIRED DIAGNOSTIC: {pairedPathTableFailure}.");

                    warnings.Add(
                        $"Joliet metadata needs {finalNeeded:N0} contiguous sector(s) from LBA {rootLba:N0}, " +
                        $"but only {availableSectors:N0} sector(s) are available before other disc content. " +
                        "Version-suffix and primary-System-Use compaction were also tested and still did not fit, " +
                        "and no SVD-validated translated or paired primary/supplementary directory layout could be proven. " +
                        "The supplementary filesystem was left unchanged.");
                    return false;
                }
            }
        }

        byte[] typeLPathTable = BuildJolietPathTable(directories, littleEndian: true);
        byte[] typeMPathTable = BuildJolietPathTable(directories, littleEndian: false);
        if (typeLPathTable.Length != typeMPathTable.Length)
            throw new InvalidOperationException("Joliet Type-L and Type-M path tables unexpectedly differ in byte length.");

        int pathTableSectorCount = checked((int)DivideRoundUp(typeLPathTable.Length, CookedSectorSize));
        var tableCopies = new List<(uint Lba, bool LittleEndian)>();
        if (typeLPathTableLba != 0) tableCopies.Add((typeLPathTableLba, true));
        if (optionalTypeLPathTableLba != 0) tableCopies.Add((optionalTypeLPathTableLba, true));
        if (typeMPathTableLba != 0) tableCopies.Add((typeMPathTableLba, false));
        if (optionalTypeMPathTableLba != 0) tableCopies.Add((optionalTypeMPathTableLba, false));

        foreach ((uint tableLba, bool littleEndian) in tableCopies)
        {
            long tableStart = tableLba;
            long tableCount = pathTableSectorCount;
            bool overlapsDirectories = directories.Any(directory =>
                RangesOverlap(tableStart, tableCount, directory.ExtentLba, DivideRoundUp(directory.DataLength, CookedSectorSize)));
            bool overlapsFiles = volume.Files
                .Where(file => file.DataLength > 0 && file.ExtentLba >= 0)
                .Any(file => RangesOverlap(tableStart, tableCount, file.ExtentLba, DivideRoundUp(file.DataLength, CookedSectorSize)));
            if (overlapsDirectories || overlapsFiles)
            {
                warnings.Add(
                    $"The Joliet {(littleEndian ? "Type-L" : "Type-M")} path table at LBA {tableLba:N0} overlaps known directory/file content; " +
                    "the supplementary filesystem was left unchanged.");
                return false;
            }
        }

        // Reject a corrupt descriptor that points Type-L and Type-M copies at the
        // same physical bytes.  Same-endian duplicate/optional pointers are harmless.
        foreach (IGrouping<uint, (uint Lba, bool LittleEndian)> group in tableCopies.GroupBy(copy => copy.Lba))
        {
            if (group.Select(copy => copy.LittleEndian).Distinct().Count() > 1)
            {
                warnings.Add(
                    $"The Joliet SVD points Type-L and Type-M path tables at the same LBA {group.Key:N0}; " +
                    "the supplementary filesystem was left unchanged rather than guessing which byte order belongs there.");
                return false;
            }
        }

        // Preserve and populate every path-table copy named by the original exact
        // SVD.  Type-L uses little endian; Type-M uses big endian.  DIC volDesc only
        // prints the primary occurrence, but mainInfo often preserves the complete
        // descriptor including the Type-M location (Black Mirror: L=27, M=28).
        WritePathTableCopies(
            metadata,
            typeLPathTable,
            typeLPathTable.Length,
            typeLPathTableLba,
            optionalTypeLPathTableLba);
        WritePathTableCopies(
            metadata,
            typeMPathTable,
            typeMPathTable.Length,
            typeMPathTableLba,
            optionalTypeMPathTableLba);

        // Install every directory sector.  All file extents remain the original
        // DIC/ISO extents; only the supplementary filesystem metadata is generated.
        bool rootParentUsesSelfIdentifier = false;
        if (root.PrimaryExtentLba is long primaryRootExtent &&
            metadata.TryGetValue(primaryRootExtent, out byte[]? primaryRootSector) &&
            primaryRootSector.Length >= 68)
        {
            int firstLength = primaryRootSector[0];
            if (firstLength >= 34 && firstLength + 34 <= primaryRootSector.Length)
            {
                int second = firstLength;
                rootParentUsesSelfIdentifier = primaryRootSector[second] >= 34 &&
                    primaryRootSector[second + 32] == 1 && primaryRootSector[second + 33] == 0;
            }
        }

        // v0.1.28: Actua Soccer 3 exposes a formatter family whose supplementary
        // tree is path-table-paired and whose SVD root records an exact, non-sector-
        // rounded directory byte length.  A byte-exact original comparison proves
        // that this family encodes BOTH special root directory records with file
        // identifier 0x00 (rather than the conventional 0x00 / 0x01 pair).  The
        // original Joliet directory body is not present in DIC logs, so this must be
        // inferred from the independently proven mastering signature rather than from
        // source-folder contents.  Keep the rule tightly gated to that signature.
        if (pairedPathTableLayoutSelected && preserveExactDirectoryByteLengths)
            rootParentUsesSelfIdentifier = true;

        foreach (JolietDirectoryNode directory in directories)
        {
            byte[] directoryBytes = BuildJolietDirectoryBytes(directory, appendFileVersionSuffix, orderDirectoryEntriesByJolietIdentifier, rootParentUsesSelfIdentifier);
            int sectors = checked((int)DivideRoundUp(directoryBytes.Length, CookedSectorSize));
            for (int sector = 0; sector < sectors; sector++)
            {
                byte[] payload = new byte[CookedSectorSize];
                int sourceOffset = sector * CookedSectorSize;
                int remaining = directoryBytes.Length - sourceOffset;
                if (remaining > 0)
                    Buffer.BlockCopy(directoryBytes, sourceOffset, payload, 0, Math.Min(CookedSectorSize, remaining));
                metadata[directory.ExtentLba + sector] = payload;
            }
        }

        TrySynthesizeCeQuadratJolietDirectoryLinkTable(metadata, directories, ceQuadratLinkTable, warnings);

        // Patch the existing SVD so its path-table size and root record describe the
        // generated supplementary tree.  Keep all other original SVD fields verbatim.
        WriteBothEndianUInt32(svd.AsSpan(132, 8), checked((uint)typeLPathTable.Length));
        // Keep the four original SVD path-table location fields byte-for-byte.  They
        // are structural evidence from mainInfo and must not be normalized away.
        WriteBothEndianUInt32(svd.AsSpan(158, 8), checked((uint)root.ExtentLba));
        WriteBothEndianUInt32(svd.AsSpan(166, 8), checked((uint)root.DataLength));
        metadata[svdLba] = svd;

        warnings.Add(
            $"Synthesized Joliet filesystem metadata: {directories.Count:N0} director{(directories.Count == 1 ? "y" : "ies")}, " +
            $"{volume.Files.Count:N0} file entr{(volume.Files.Count == 1 ? "y" : "ies")}. " +
            $"DIC does not preserve the original Joliet directory sectors, so the generated Joliet tree uses {filenameSourceDescription}; file LBAs and byte sizes remain original.");
        string pathCopyDescription = string.Join(", ", tableCopies
            .OrderBy(copy => copy.Lba)
            .Select(copy => $"{(copy.LittleEndian ? "Type-L" : "Type-M")} LBA {copy.Lba:N0}"));
        warnings.Add($"Preserved Joliet SVD path-table location evidence and generated every declared copy: {pathCopyDescription}.");
        return true;
    }

    private static void TrySynthesizeCeQuadratFormatterInformationBlock(
        DicVolumeInfo volume,
        Dictionary<long, byte[]> metadata,
        List<string> warnings)
    {
        // CeQuadrat 32-bit ISO formatter private information sector, observed on
        // WinOnCD/CeQuadrat mastered images. The payload is deterministic and
        // self-identifying:
        //   0x000  "CeQuadrat ISO 9660 formatter information block"
        //   0x080  formatter LBA as UInt32 little-endian
        //   0x084  formatter LBA as UInt32 big-endian
        //   0x7FC  AA 55 55 AA
        //   all other bytes zero.
        //
        // The sector sits at VolumeSpaceSize - 1.  We only synthesize it when an
        // exact Type-1 PVD captured by DIC proves CeQuadrat as the Data Preparer.
        // Existing mainInfo/raw evidence always wins and is never overwritten.
        if (volume.VolumeSpaceSize <= 0 || volume.VolumeSpaceSize > uint.MaxValue)
            return;

        bool ceQuadratPvd = false;
        foreach (byte[] payload in metadata.Values)
        {
            if (payload.Length < CookedSectorSize ||
                payload[0] != 1 ||
                payload[1] != (byte)'C' || payload[2] != (byte)'D' ||
                payload[3] != (byte)'0' || payload[4] != (byte)'0' ||
                payload[5] != (byte)'1' || payload[6] != 1)
                continue;

            const int dataPreparerOffset = 446;
            const int dataPreparerLength = 128;
            string preparer = Encoding.ASCII
                .GetString(payload, dataPreparerOffset, dataPreparerLength)
                .TrimEnd(' ', '\0');
            if (preparer.StartsWith("CeQuadrat ", StringComparison.OrdinalIgnoreCase))
            {
                ceQuadratPvd = true;
                break;
            }
        }

        if (!ceQuadratPvd)
            return;

        long formatterLba = volume.VolumeSpaceSize - 1;
        if (formatterLba < 0 || formatterLba > uint.MaxValue || metadata.ContainsKey(formatterLba))
            return;

        byte[] formatter = new byte[CookedSectorSize];
        ReadOnlySpan<byte> signature = "CeQuadrat ISO 9660 formatter information block"u8;
        signature.CopyTo(formatter);
        uint lba = checked((uint)formatterLba);
        BinaryPrimitives.WriteUInt32LittleEndian(formatter.AsSpan(0x80, 4), lba);
        BinaryPrimitives.WriteUInt32BigEndian(formatter.AsSpan(0x84, 4), lba);
        formatter[0x7FC] = 0xAA;
        formatter[0x7FD] = 0x55;
        formatter[0x7FE] = 0x55;
        formatter[0x7FF] = 0xAA;

        metadata[formatterLba] = formatter;
        warnings.Add(
            $"Synthesized CeQuadrat/WinOnCD formatter information block at LBA {formatterLba:N0} (VolumeSpaceSize - 1), " +
            "using the CeQuadrat Data Preparer signature proven by the original PVD. The block encodes its own LBA in both byte orders and the standard AA 55 55 AA trailer; exact logged metadata would take precedence if present.");
    }

    private static void TrySynthesizeCeQuadratJolietDirectoryLinkTable(
        Dictionary<long, byte[]> metadata,
        IReadOnlyList<JolietDirectoryNode> directories,
        CeQuadratLinkTableContext? context,
        List<string> warnings)
    {
        // WinOnCD/CeQuadrat writes a private one-sector bridge between the Joliet and
        // primary ISO9660 directory trees.  The sector is not referenced by ISO9660,
        // so DIC volDesc quite reasonably does not parse it; however its contents are
        // completely derivable once both directory geometries are proven.
        //
        // Observed format (all DWORDs little-endian):
        //   0x00  "CeQuadrat Joliet directory link table" (37 bytes)
        //   0x25  seven zero bytes
        //   0x2c  directory count
        //   0x30  repeated { Joliet directory LBA, primary directory LBA }
        //   rest of 2048-byte logical sector = zero
        if (context is null || directories.Count != context.PrimaryDirectoryExtents.Count)
            return;

        // Exact logged/supplied bytes always outrank synthesis.
        if (metadata.ContainsKey(context.LinkTableLba))
            return;

        var jolietByPrimaryExtent = new Dictionary<uint, JolietDirectoryNode>();
        foreach (JolietDirectoryNode directory in directories)
        {
            if (directory.PrimaryExtentLba is not long primaryLba ||
                primaryLba < 0 || primaryLba > uint.MaxValue ||
                directory.ExtentLba < 0 || directory.ExtentLba > uint.MaxValue)
                return;

            uint primary = checked((uint)primaryLba);
            if (!jolietByPrimaryExtent.TryAdd(primary, directory))
                return;
        }

        var pairs = new List<(uint JolietLba, uint PrimaryLba)>(context.PrimaryDirectoryExtents.Count);
        foreach (uint primaryLba in context.PrimaryDirectoryExtents)
        {
            if (!jolietByPrimaryExtent.TryGetValue(primaryLba, out JolietDirectoryNode? directory))
                return;

            pairs.Add((checked((uint)directory.ExtentLba), primaryLba));
        }

        const int pairStart = 48;
        if (pairStart + checked(pairs.Count * 8) > CookedSectorSize)
            return;

        byte[] payload = new byte[CookedSectorSize];
        ReadOnlySpan<byte> signature = "CeQuadrat Joliet directory link table"u8;
        signature.CopyTo(payload);
        // Bytes 37..43 deliberately remain zero.
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(44, 4), checked((uint)pairs.Count));

        int offset = pairStart;
        foreach ((uint jolietLba, uint primaryLba) in pairs)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(offset, 4), jolietLba);
            BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(offset + 4, 4), primaryLba);
            offset += 8;
        }

        metadata[context.LinkTableLba] = payload;
        warnings.Add(
            $"Synthesized CeQuadrat/WinOnCD Joliet directory link table at LBA {context.LinkTableLba:N0}: " +
            $"{pairs.Count:N0} proven Joliet↔primary directory extent pair(s), derived in the original primary Type-L path-table order. " +
            "The remaining logical-sector bytes are zero and normal Mode 1 EDC/ECC is regenerated from the synthesized payload.");
    }

    private static bool IsVolumeDescriptorTerminator(ReadOnlySpan<byte> payload)
    {
        return payload.Length >= CookedSectorSize &&
               payload[0] == 255 &&
               payload[1] == (byte)'C' &&
               payload[2] == (byte)'D' &&
               payload[3] == (byte)'0' &&
               payload[4] == (byte)'0' &&
               payload[5] == (byte)'1' &&
               payload[6] == 1;
    }

    private static string ReadIsoAsciiField(byte[] payload, int offset, int length)
    {
        if (offset < 0 || length < 0 || offset + length > payload.Length)
            return string.Empty;

        return Encoding.ASCII.GetString(payload, offset, length).TrimEnd(' ', '\0');
    }

    private static bool IsJolietSupplementaryDescriptor(byte[] payload)
    {
        return payload.Length >= CookedSectorSize &&
               payload[0] == 2 &&
               payload[1] == (byte)'C' &&
               payload[2] == (byte)'D' &&
               payload[3] == (byte)'0' &&
               payload[4] == (byte)'0' &&
               payload[5] == (byte)'1' &&
               payload[6] == 1 &&
               payload[88] == (byte)'%' &&
               payload[89] == (byte)'/' &&
               payload[90] is (byte)'@' or (byte)'C' or (byte)'E';
    }

    private static JolietDirectoryNode BuildJolietDirectoryTree(
        IReadOnlyList<DicFileRecord> files,
        DateTimeOffset defaultRecordingTime,
        IReadOnlyDictionary<string, PrimaryDirectoryMetadata>? primaryDirectoryMetadata = null,
        bool inheritPrimarySystemUse = true)
    {
        static byte[]? KeepSemanticXaSystemUse(byte[]? systemUse)
        {
            // CD-ROM XA directory records use a 14-byte System Use field whose
            // signature is ASCII "XA" at bytes 6..7.  Lionheart Bonus CD proves
            // this record is reproduced in both the primary ISO9660 and Joliet
            // namespaces.  Do not treat arbitrary zero padding as transferable.
            if (systemUse is { Length: 14 } &&
                systemUse[6] == (byte)'X' && systemUse[7] == (byte)'A')
                return (byte[])systemUse.Clone();
            return null;
        }

        static PrimaryDirectoryMetadata StripNonSemanticSystemUse(PrimaryDirectoryMetadata metadata)
            => metadata with
            {
                SystemUse = KeepSemanticXaSystemUse(metadata.SystemUse),
                SelfSystemUse = KeepSemanticXaSystemUse(metadata.SelfSystemUse),
                ParentLinkSystemUse = KeepSemanticXaSystemUse(metadata.ParentLinkSystemUse)
            };

        PrimaryDirectoryMetadata rootMetadata = primaryDirectoryMetadata is not null &&
            primaryDirectoryMetadata.TryGetValue("/", out PrimaryDirectoryMetadata? loggedRoot)
                ? loggedRoot
                : new PrimaryDirectoryMetadata(defaultRecordingTime, (byte)IsoDirectoryRecordFlags.Directory);
        if (!inheritPrimarySystemUse)
            rootMetadata = StripNonSemanticSystemUse(rootMetadata);
        var root = new JolietDirectoryNode(string.Empty, null, rootMetadata);

        foreach (DicFileRecord file in files
                     .OrderBy(file => file.Sequence)
                     .ThenBy(file => file.Path, StringComparer.Ordinal))
        {
            string normalized = NormalizeIsoPath(file.Path).Trim('/');
            if (string.IsNullOrWhiteSpace(normalized))
                continue;

            string[] components = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (components.Length == 0)
                continue;

            JolietDirectoryNode directory = root;
            string currentPath = string.Empty;
            for (int i = 0; i < components.Length - 1; i++)
            {
                string name = NormalizeJolietName(components[i], isDirectory: true);
                currentPath = currentPath.Length == 0 ? "/" + name : currentPath + "/" + name;
                if (!directory.Children.TryGetValue(name, out JolietDirectoryNode? child))
                {
                    PrimaryDirectoryMetadata metadata = primaryDirectoryMetadata is not null &&
                        TryResolvePrimaryDirectoryMetadata(currentPath, primaryDirectoryMetadata, out PrimaryDirectoryMetadata logged)
                            ? logged
                            : new PrimaryDirectoryMetadata(defaultRecordingTime, (byte)IsoDirectoryRecordFlags.Directory);
                    if (!inheritPrimarySystemUse)
                        metadata = StripNonSemanticSystemUse(metadata);

                    // v0.1.26: a recovered Joliet/source spelling may differ only in case
                    // from a primary ISO9660 spelling while both resolve to the same proven
                    // primary directory extent.  Do not create two logical directories for
                    // one physical directory.  Actua Soccer 3 exposes this as /GAMEDATA and
                    // /gamedata (and the same pattern recursively below it).  Extent identity
                    // is stronger evidence than pathname spelling, so reuse the existing node.
                    if (metadata.PrimaryExtentLba is long mappedExtent)
                    {
                        child = directory.Children.Values.FirstOrDefault(existing =>
                            existing.PrimaryExtentLba is long existingExtent && existingExtent == mappedExtent);
                    }

                    if (child is null)
                    {
                        child = new JolietDirectoryNode(name, directory, metadata);
                        directory.Children.Add(name, child);
                    }
                }
                directory = child;
            }

            string fileName = NormalizeJolietName(components[^1], isDirectory: false);
            if (string.IsNullOrWhiteSpace(fileName))
                continue;

            if (!directory.Files.Any(existing => existing.Name.Equals(fileName, StringComparison.Ordinal)))
                directory.Files.Add(new JolietFileNode(
                    fileName,
                    file.ExtentLba,
                    file.DataLength,
                    file.RecordingTime ?? defaultRecordingTime,
                    (byte)file.Flags,
                    inheritPrimarySystemUse ? file.SystemUse : KeepSemanticXaSystemUse(file.SystemUse),
                    file.RawRecordingTime,
                    file.Sequence,
                    file.SupplementaryOnlyZeroLengthAlias));
        }

        return root;
    }

    private static List<JolietDirectoryNode> FlattenJolietDirectories(JolietDirectoryNode root)
    {
        var result = new List<JolietDirectoryNode>();
        var queue = new Queue<JolietDirectoryNode>();
        queue.Enqueue(root);

        while (queue.Count > 0)
        {
            JolietDirectoryNode current = queue.Dequeue();
            current.DirectoryNumber = result.Count + 1;
            result.Add(current);

            // Path-table directory numbers must remain stable with the primary ISO9660
            // namespace when a Joliet name is only a supplementary alias.  Some mastering
            // tools build both path tables from the same primary-directory ordering and
            // merely substitute the Joliet identifier.  Sorting by the visible Joliet name
            // can therefore change directory numbers (and every child's parent number).
            // Prefer the proven primary identifier when available; fall back to the Joliet
            // name only when no primary mapping exists.
            foreach (JolietDirectoryNode child in current.Children.Values
                         .OrderBy(node => GetIsoFilename(node.PrimaryPath ?? node.Name), StringComparer.Ordinal)
                         .ThenBy(node => node.Name, StringComparer.Ordinal))
                queue.Enqueue(child);
        }

        return result;
    }

    // v0.2.0: formatter-specific Joliet comparers live in Mastering/JolietNameComparers.cs.

    private static long ComputeJolietDirectoryDataLength(JolietDirectoryNode directory, bool appendFileVersionSuffix = true, bool orderByJolietIdentifier = false, bool roundToSector = true)
    {
        var recordLengths = new List<int>
        {
            DirectoryRecordLength(1, directory.SelfSystemUse?.Length ?? 0), // .
            DirectoryRecordLength(1, directory.ParentLinkSystemUse?.Length ?? 0)  // ..
        };

        var lengthItems = directory.Children.Values
            .Select(node => (Order: node.PrimaryRecordOrder, Name: node.Name, Length: DirectoryRecordLength(Encoding.BigEndianUnicode.GetByteCount(node.Name), node.SystemUse?.Length ?? 0), SupplementaryOnlyAlias: false))
            .Concat(directory.Files.Select(file => (Order: file.PrimaryRecordOrder, Name: file.Name, Length: DirectoryRecordLength(Encoding.BigEndianUnicode.GetByteCount(appendFileVersionSuffix ? file.Name + ";1" : file.Name), file.SystemUse?.Length ?? 0), SupplementaryOnlyAlias: file.SupplementaryOnlyZeroLengthAlias)))
            .ToList();

        if (orderByJolietIdentifier)
        {
            lengthItems = lengthItems
                .OrderBy(item => item.Name, JolietNameComparers.AccentFoldedCaseSensitive)
                .ThenBy(item => item.Order)
                .ToList();
        }
        else
        {
            // v0.1.30: retain the formatter-proven primary-record order for every
            // pre-existing record.  Supplementary-only zero-byte aliases are the only
            // records without a primary sequence, so insert just those into their
            // case-insensitive Joliet identifier position.  This reproduces Actua
            // Soccer 3's CTEAM order without perturbing unrelated directories such as
            // the root (whose exact 2448-byte length was already proven in v0.1.28).
            var ordered = lengthItems
                .Where(item => !item.SupplementaryOnlyAlias)
                .OrderBy(item => item.Order)
                .ThenBy(item => item.Name, StringComparer.Ordinal)
                .ToList();
            foreach (var alias in lengthItems
                         .Where(item => item.SupplementaryOnlyAlias)
                         .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                         .ThenBy(item => item.Order))
            {
                int insertAt = ordered.FindIndex(item =>
                    StringComparer.OrdinalIgnoreCase.Compare(item.Name, alias.Name) > 0);
                if (insertAt < 0)
                    ordered.Add(alias);
                else
                    ordered.Insert(insertAt, alias);
            }
            lengthItems = ordered;
        }

        recordLengths.AddRange(lengthItems.Select(item => item.Length));

        int position = 0;
        foreach (int recordLength in recordLengths)
        {
            int withinSector = position % CookedSectorSize;
            int remaining = CookedSectorSize - withinSector;
            if (recordLength > remaining)
                position += remaining;
            position += recordLength;
        }

        if (!roundToSector)
            return Math.Max(1, position);
        return Math.Max(CookedSectorSize, checked((int)(DivideRoundUp(position, CookedSectorSize) * CookedSectorSize)));
    }

    private static byte[] BuildJolietDirectoryBytes(JolietDirectoryNode directory, bool appendFileVersionSuffix = true, bool orderByJolietIdentifier = false, bool rootParentUsesSelfIdentifier = false)
    {
        byte[] result = new byte[checked((int)directory.DataLength)];
        int position = 0;

        void AppendRecord(uint extentLba, uint dataLength, byte flags, byte[] identifier, DateTimeOffset recordingTime, byte[]? systemUse, byte[]? rawRecordingTime)
        {
            int recordLength = DirectoryRecordLength(identifier.Length, systemUse?.Length ?? 0);
            int withinSector = position % CookedSectorSize;
            int remaining = CookedSectorSize - withinSector;
            if (recordLength > remaining)
                position += remaining;

            Span<byte> record = result.AsSpan(position, recordLength);
            WriteDirectoryRecord(record, extentLba, dataLength, flags, identifier, recordingTime, systemUse, rawRecordingTime);
            position += recordLength;
        }

        AppendRecord(
            checked((uint)directory.ExtentLba),
            checked((uint)directory.DataLength),
            (byte)IsoDirectoryRecordFlags.Directory,
            new byte[] { 0 },
            directory.SelfRecordingTime,
            directory.SelfSystemUse,
            directory.SelfRawRecordingTime);

        JolietDirectoryNode parent = directory.Parent ?? directory;
        AppendRecord(
            checked((uint)parent.ExtentLba),
            checked((uint)parent.DataLength),
            (byte)IsoDirectoryRecordFlags.Directory,
            directory.Parent is null && rootParentUsesSelfIdentifier ? new byte[] { 0 } : new byte[] { 1 },
            directory.ParentLinkRecordingTime,
            directory.ParentLinkSystemUse,
            directory.ParentLinkRawRecordingTime);

        var records = directory.Children.Values
            .Select(child => new JolietOutputRecord(
                child.PrimaryRecordOrder,
                child.Name,
                checked((uint)child.ExtentLba),
                checked((uint)child.DataLength),
                (byte)(child.Flags | (byte)IsoDirectoryRecordFlags.Directory),
                Encoding.BigEndianUnicode.GetBytes(child.Name),
                child.RecordingTime,
                child.SystemUse,
                child.RawRecordingTime,
                false))
            .Concat(directory.Files.Select(file => new JolietOutputRecord(
                file.PrimaryRecordOrder,
                file.Name,
                checked((uint)file.ExtentLba),
                checked((uint)file.DataLength),
                file.Flags,
                Encoding.BigEndianUnicode.GetBytes(appendFileVersionSuffix ? file.Name + ";1" : file.Name),
                file.RecordingTime,
                file.SystemUse,
                file.RawRecordingTime,
                file.SupplementaryOnlyZeroLengthAlias)));

        List<JolietOutputRecord> orderedRecords;
        if (orderByJolietIdentifier)
        {
            orderedRecords = records
                .OrderBy(record => record.SortName, JolietNameComparers.AccentFoldedCaseSensitive)
                .ThenBy(record => record.SortOrder)
                .ToList();
        }
        else
        {
            orderedRecords = records
                .Where(record => !record.SupplementaryOnlyZeroLengthAlias)
                .OrderBy(record => record.SortOrder)
                .ThenBy(record => record.SortName, StringComparer.Ordinal)
                .ToList();
            foreach (JolietOutputRecord alias in records
                         .Where(record => record.SupplementaryOnlyZeroLengthAlias)
                         .OrderBy(record => record.SortName, StringComparer.OrdinalIgnoreCase)
                         .ThenBy(record => record.SortOrder))
            {
                int insertAt = orderedRecords.FindIndex(record =>
                    StringComparer.OrdinalIgnoreCase.Compare(record.SortName, alias.SortName) > 0);
                if (insertAt < 0)
                    orderedRecords.Add(alias);
                else
                    orderedRecords.Insert(insertAt, alias);
            }
        }

        foreach (JolietOutputRecord record in orderedRecords)
            AppendRecord(record.ExtentLba, record.DataLength, record.Flags, record.Identifier, record.RecordingTime, record.SystemUse, record.RawRecordingTime);

        return result;
    }

    private static byte[] BuildJolietPathTable(
        IReadOnlyList<JolietDirectoryNode> directories,
        bool littleEndian)
    {
        using var stream = new MemoryStream();
        Span<byte> number = stackalloc byte[4];
        Span<byte> parentNumber = stackalloc byte[2];

        foreach (JolietDirectoryNode directory in directories)
        {
            byte[] identifier = directory.Parent is null
                ? new byte[] { 0 }
                : Encoding.BigEndianUnicode.GetBytes(directory.Name);

            if (identifier.Length > byte.MaxValue)
                throw new InvalidOperationException($"Joliet directory name '{directory.Name}' is too long for an ISO path-table record.");

            stream.WriteByte((byte)identifier.Length);
            stream.WriteByte(0);

            if (littleEndian)
                BinaryPrimitives.WriteUInt32LittleEndian(number, checked((uint)directory.ExtentLba));
            else
                BinaryPrimitives.WriteUInt32BigEndian(number, checked((uint)directory.ExtentLba));
            stream.Write(number);

            ushort parent = checked((ushort)(directory.Parent?.DirectoryNumber ?? 1));
            if (littleEndian)
                BinaryPrimitives.WriteUInt16LittleEndian(parentNumber, parent);
            else
                BinaryPrimitives.WriteUInt16BigEndian(parentNumber, parent);
            stream.Write(parentNumber);

            stream.Write(identifier, 0, identifier.Length);
            if ((identifier.Length & 1) != 0)
                stream.WriteByte(0);
        }

        return stream.ToArray();
    }

    private static void WriteDirectoryRecord(
        Span<byte> record,
        uint extentLba,
        uint dataLength,
        byte flags,
        ReadOnlySpan<byte> identifier,
        DateTimeOffset recordingTime,
        ReadOnlySpan<byte> systemUse,
        ReadOnlySpan<byte> rawRecordingTime)
    {
        record.Clear();
        record[0] = checked((byte)record.Length);
        record[1] = 0;
        WriteBothEndianUInt32(record.Slice(2, 8), extentLba);
        WriteBothEndianUInt32(record.Slice(10, 8), dataLength);

        // Seven-byte ISO9660 recording date/time.  Month/day zero is invalid;
        // preserve DIC's real per-file timestamp when available and use a valid
        // volume-level fallback for synthetic directory records.
        if (rawRecordingTime.Length == 7)
            rawRecordingTime.CopyTo(record.Slice(18, 7));
        else
            WriteIsoDirectoryTimestamp(record.Slice(18, 7), recordingTime);

        record[25] = flags;
        record[26] = 0;
        record[27] = 0;
        BinaryPrimitives.WriteUInt16LittleEndian(record.Slice(28, 2), 1);
        BinaryPrimitives.WriteUInt16BigEndian(record.Slice(30, 2), 1);
        record[32] = checked((byte)identifier.Length);
        identifier.CopyTo(record.Slice(33, identifier.Length));

        int systemUseOffset = 33 + identifier.Length + ((identifier.Length & 1) == 0 ? 1 : 0);
        if (!systemUse.IsEmpty)
            systemUse.CopyTo(record.Slice(systemUseOffset, systemUse.Length));
    }

    private static void WriteIsoDirectoryTimestamp(Span<byte> destination, DateTimeOffset value)
    {
        if (destination.Length < 7)
            throw new ArgumentException("ISO directory timestamp requires seven bytes.", nameof(destination));

        // ISO9660 stores the year as years since 1900 and the GMT displacement
        // in signed 15-minute intervals.  DIC timestamps are already expressed
        // with an explicit offset, so preserve that local wall-clock time.
        int year = Math.Clamp(value.Year, 1900, 2155);
        int quarterHours = (int)Math.Round(value.Offset.TotalMinutes / 15.0, MidpointRounding.AwayFromZero);
        quarterHours = Math.Clamp(quarterHours, -48, 52);

        destination[0] = checked((byte)(year - 1900));
        destination[1] = checked((byte)Math.Clamp(value.Month, 1, 12));
        destination[2] = checked((byte)Math.Clamp(value.Day, 1, DateTime.DaysInMonth(year, Math.Clamp(value.Month, 1, 12))));
        destination[3] = checked((byte)Math.Clamp(value.Hour, 0, 23));
        destination[4] = checked((byte)Math.Clamp(value.Minute, 0, 59));
        destination[5] = checked((byte)Math.Clamp(value.Second, 0, 59));
        destination[6] = unchecked((byte)(sbyte)quarterHours);
    }

    private static bool TryParseDicTimestamp(string text, out DateTimeOffset value)
    {
        value = default;
        Match match = Regex.Match(
            text.Trim(),
            @"^(?<year>\d{4})-(?<month>\d{2})-(?<day>\d{2})(?:T|\s+)(?<hour>\d{2}):(?<minute>\d{2}):(?<second>\d{2})(?:\.(?<fraction>\d+))?\s*(?<offset>[+-]\d{2}:\d{2})$");
        if (!match.Success)
            return false;

        if (!int.TryParse(match.Groups["year"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int year) ||
            !int.TryParse(match.Groups["month"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int month) ||
            !int.TryParse(match.Groups["day"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int day) ||
            !int.TryParse(match.Groups["hour"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int hour) ||
            !int.TryParse(match.Groups["minute"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int minute) ||
            !int.TryParse(match.Groups["second"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int second) ||
            year < 1900 || year > 2155 || month < 1 || month > 12 || day < 1 ||
            day > DateTime.DaysInMonth(year, month) || hour > 23 || minute > 59 || second > 59)
        {
            // DIC can emit sentinel values such as 1900-00-00. Treat those as
            // unavailable rather than encoding an invalid ISO directory date.
            return false;
        }

        string offsetText = match.Groups["offset"].Value;
        if (offsetText.Length != 6 ||
            !int.TryParse(offsetText.AsSpan(1, 2), NumberStyles.None, CultureInfo.InvariantCulture, out int offsetHours) ||
            !int.TryParse(offsetText.AsSpan(4, 2), NumberStyles.None, CultureInfo.InvariantCulture, out int offsetMinutes) ||
            offsetMinutes > 59)
            return false;

        TimeSpan offset = new(offsetHours, offsetMinutes, 0);
        if (offsetText[0] == '-')
            offset = -offset;
        if (offset < TimeSpan.FromHours(-14) || offset > TimeSpan.FromHours(14))
            return false;

        int milliseconds = 0;
        string fraction = match.Groups["fraction"].Value;
        if (fraction.Length > 0)
        {
            string msText = (fraction + "000")[..3];
            _ = int.TryParse(msText, NumberStyles.None, CultureInfo.InvariantCulture, out milliseconds);
        }

        try
        {
            value = new DateTimeOffset(year, month, day, hour, minute, second, milliseconds, offset);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool TryReadVolumeDescriptorTimestamp(ReadOnlySpan<byte> descriptor, out DateTimeOffset value)
    {
        value = default;
        // ISO9660 volume creation date/time is a 17-byte ASCII field at offset 813:
        // YYYYMMDDHHMMSScc plus a signed 15-minute GMT displacement byte.
        if (descriptor.Length < 830)
            return false;

        ReadOnlySpan<byte> field = descriptor.Slice(813, 17);
        string digits = Encoding.ASCII.GetString(field.Slice(0, 16));
        if (!digits.All(char.IsDigit) ||
            !int.TryParse(digits.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out int year) ||
            !int.TryParse(digits.AsSpan(4, 2), NumberStyles.None, CultureInfo.InvariantCulture, out int month) ||
            !int.TryParse(digits.AsSpan(6, 2), NumberStyles.None, CultureInfo.InvariantCulture, out int day) ||
            !int.TryParse(digits.AsSpan(8, 2), NumberStyles.None, CultureInfo.InvariantCulture, out int hour) ||
            !int.TryParse(digits.AsSpan(10, 2), NumberStyles.None, CultureInfo.InvariantCulture, out int minute) ||
            !int.TryParse(digits.AsSpan(12, 2), NumberStyles.None, CultureInfo.InvariantCulture, out int second) ||
            !int.TryParse(digits.AsSpan(14, 2), NumberStyles.None, CultureInfo.InvariantCulture, out int centiseconds) ||
            year < 1900 || year > 2155 || month < 1 || month > 12 || day < 1 ||
            day > DateTime.DaysInMonth(year, month) || hour > 23 || minute > 59 || second > 59)
            return false;

        sbyte offsetQuarters = unchecked((sbyte)field[16]);
        if (offsetQuarters < -48 || offsetQuarters > 52)
            offsetQuarters = 0;

        try
        {
            value = new DateTimeOffset(
                year, month, day, hour, minute, second,
                Math.Clamp(centiseconds, 0, 99) * 10,
                TimeSpan.FromMinutes(offsetQuarters * 15));
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static int DirectoryRecordLength(int identifierLength, int systemUseLength = 0)
    {
        int length = 33 + identifierLength;
        if ((identifierLength & 1) == 0)
            length++;
        length = checked(length + systemUseLength);
        if (length > byte.MaxValue)
            throw new InvalidOperationException("A generated Joliet directory record exceeds the ISO9660 255-byte record limit.");
        return length;
    }

    private static void WriteBothEndianUInt32(Span<byte> destination, uint value)
    {
        if (destination.Length < 8)
            throw new ArgumentException("Both-endian ISO field requires eight bytes.", nameof(destination));
        BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(0, 4), value);
        BinaryPrimitives.WriteUInt32BigEndian(destination.Slice(4, 4), value);
    }

    private static string NormalizeJolietName(string value, bool isDirectory)
    {
        string name = Regex.Replace(value.Trim(), @";\d+$", string.Empty);
        var builder = new StringBuilder(name.Length);
        foreach (char ch in name)
        {
            if (ch is '/' or '\\' or '\0' || char.IsControl(ch))
                builder.Append('_');
            else
                builder.Append(ch);
        }

        string normalized = builder.ToString().Trim();
        if (normalized.Length == 0)
            normalized = isDirectory ? "_" : "FILE";

        // Joliet directory identifiers have a 64-character practical limit.
        if (normalized.Length > 64)
            normalized = normalized[..64];

        return normalized;
    }

    private static void ReportIgnoredSupplementaryDescriptors(
        IReadOnlyDictionary<long, byte[]> metadata,
        List<string> warnings)
    {
        int count = metadata.Values.Count(sector =>
            sector.Length >= 7 && sector[0] == 2 &&
            sector[1] == (byte)'C' && sector[2] == (byte)'D' &&
            sector[3] == (byte)'0' && sector[4] == (byte)'0' && sector[5] == (byte)'1');

        if (count > 0)
        {
            warnings.Add(
                $"{count:N0} supplementary/Joliet volume descriptor(s) were preserved from DIC metadata. " +
                "Their directory/path-table sectors are not guessed during import; they can be synthesized later when a matched Joliet source tree supplies the missing names/casing.");
        }
    }



    private static bool IsAssociated(DicFileRecord file)
        => (file.Flags & IsoDirectoryRecordFlags.Associated) != 0;

    private static string BuildAssociatedSourceKey(DicFileRecord file)
    {
        string original = file.Path.Replace('\\', '/').Trim('/');
        return $"[Associated ISO records]/{original} @LBA {file.ExtentLba}";
    }

}
