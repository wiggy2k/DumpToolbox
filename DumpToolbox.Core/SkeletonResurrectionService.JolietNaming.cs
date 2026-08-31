using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace DumpToolbox.Core;

/// <summary>
/// Deterministic Joliet/ISO9660 source-name projection and alias-family helpers.
/// Split from SkeletonResurrectionService in v0.8.0 without changing rule order or behaviour.
/// </summary>
public sealed partial class SkeletonResurrectionService
{
    private static string StripIsoVersionForMatching(string value)
        => Regex.Replace(value.Normalize(NormalizationForm.FormC), @";\d+$", string.Empty);

    private static int? GetTildeAliasIndex(SkeletonContentEntry entry)
        => TryGetTildeAliasInfo(entry, out _, out _, out int index, out _) ? index : null;

    private static bool TryGetTildeAliasInfo(
        SkeletonContentEntry entry,
        out string parent,
        out string familyKey,
        out int aliasIndex,
        out string extension)
    {
        foreach (string alias in GetDicEntryAliases(entry))
        {
            string normalized = NormalizeDicRelativePath(alias);
            string leaf = StripIsoVersionForMatching(GetDicFilename(normalized));
            int dot = leaf.LastIndexOf('.');
            string stem = dot > 0 ? leaf[..dot] : leaf;
            extension = dot > 0 ? leaf[(dot + 1)..].ToUpperInvariant() : string.Empty;
            Match match = Regex.Match(stem, @"^(?<prefix>.*)~(?<index>[1-9][0-9]*)$", RegexOptions.IgnoreCase);
            if (!match.Success || !int.TryParse(match.Groups["index"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out aliasIndex))
                continue;

            string stablePrefix = new(match.Groups["prefix"].Value
                .Where(char.IsLetterOrDigit)
                .Select(char.ToUpperInvariant)
                .ToArray());
            if (stablePrefix.Length > 5)
                stablePrefix = stablePrefix[..5];
            if (stablePrefix.Length < 2)
                continue;

            parent = GetDicParentPath(normalized).ToUpperInvariant();
            familyKey = stablePrefix;
            return true;
        }

        parent = string.Empty;
        familyKey = string.Empty;
        aliasIndex = 0;
        extension = string.Empty;
        return false;
    }

    private static bool TryGetTerminalNumber(string filename, out int number)
    {
        number = 0;
        string leaf = StripIsoVersionForMatching(filename);
        int dot = leaf.LastIndexOf('.');
        string stem = dot > 0 ? leaf[..dot] : leaf;
        Match match = Regex.Match(stem, @"(?<number>[0-9]+)$", RegexOptions.CultureInvariant);
        return match.Success && int.TryParse(match.Groups["number"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out number);
    }

    private static bool SourceMatchesAliasFamily(string sourceFilename, string familyKey, string extension)
    {
        string leaf = StripIsoVersionForMatching(sourceFilename);
        int dot = leaf.LastIndexOf('.');
        string stem = dot > 0 ? leaf[..dot] : leaf;
        string sourceExtension = dot > 0 ? leaf[(dot + 1)..] : string.Empty;
        if (!sourceExtension.Equals(extension, StringComparison.OrdinalIgnoreCase))
            return false;

        string normalizedStem = new(stem.Normalize(NormalizationForm.FormD)
            .Where(ch => CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
            .Where(char.IsLetterOrDigit)
            .Select(char.ToUpperInvariant)
            .ToArray());
        return normalizedStem.StartsWith(familyKey, StringComparison.OrdinalIgnoreCase);
    }

    private static bool WindowsHashed83PathProjectsToTarget(string sourceRelativePath, string targetRelativePath)
    {
        string[] source = NormalizeDicRelativePath(sourceRelativePath).Split('/', StringSplitOptions.RemoveEmptyEntries);
        string[] target = NormalizeDicRelativePath(targetRelativePath).Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (source.Length != target.Length)
            return false;

        for (int i = 0; i < source.Length; i++)
        {
            bool isFile = i == source.Length - 1;
            string targetComponent = StripIsoVersionForMatching(target[i]);
            if (JolietComponentProjectsToIsoComponent(source[i], targetComponent, isFile))
                continue;
            if (WindowsHashed83Leaf(source[i]).Equals(targetComponent, StringComparison.OrdinalIgnoreCase))
                continue;
            return false;
        }
        return true;
    }

    private static bool Prefix3HexOrdinalPathProjectsToTarget(string sourceRoot, string sourceRelativePath, string targetRelativePath)
    {
        string normalizedSource = NormalizeDicRelativePath(sourceRelativePath);
        string[] source = normalizedSource.Split('/', StringSplitOptions.RemoveEmptyEntries);
        string[] target = NormalizeDicRelativePath(targetRelativePath).Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (source.Length != target.Length)
            return false;

        string currentSourceParent = string.Empty;
        for (int i = 0; i < source.Length; i++)
        {
            bool isFile = i == source.Length - 1;
            string targetComponent = StripIsoVersionForMatching(target[i]);
            if (JolietComponentProjectsToIsoComponent(source[i], targetComponent, isFile))
            {
                currentSourceParent = currentSourceParent.Length == 0 ? source[i] : currentSourceParent + "/" + source[i];
                continue;
            }

            string parentFull = currentSourceParent.Length == 0
                ? sourceRoot
                : Path.Combine(sourceRoot, currentSourceParent.Replace('/', Path.DirectorySeparatorChar));
            if (!Directory.Exists(parentFull))
                return false;

            string[] siblings;
            try
            {
                siblings = Directory.EnumerateFileSystemEntries(parentFull)
                    .Select(Path.GetFileName)
                    .Where(name => !string.IsNullOrEmpty(name))
                    .Select(name => name!)
                    .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(name => name, StringComparer.Ordinal)
                    .ToArray();
            }
            catch
            {
                return false;
            }

            int ordinal = Array.FindIndex(siblings, name => name.Equals(source[i], StringComparison.OrdinalIgnoreCase)) + 1;
            if (ordinal <= 0 || !Prefix3HexOrdinalLeaf(source[i], ordinal).Equals(targetComponent, StringComparison.OrdinalIgnoreCase))
                return false;

            currentSourceParent = currentSourceParent.Length == 0 ? source[i] : currentSourceParent + "/" + source[i];
        }
        return true;
    }

    private static string Prefix3HexOrdinalLeaf(string sourceFilename, int ordinal)
    {
        string leaf = StripIsoVersionForMatching(sourceFilename);
        int dot = leaf.LastIndexOf('.');
        string stem = dot > 0 ? leaf[..dot] : leaf;
        string extension = dot > 0 ? leaf[(dot + 1)..] : string.Empty;
        string folded = new(stem.Normalize(NormalizationForm.FormD)
            .Where(ch => CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
            .Select(char.ToUpperInvariant)
            .Where(ch => (ch >= 'A' && ch <= 'Z') || (ch >= '0' && ch <= '9'))
            .Take(3)
            .ToArray());
        if (folded.Length == 0)
            return string.Empty;
        string targetStem = $"{folded}_{ordinal:X4}";
        string targetExt = new(extension.ToUpperInvariant().Take(3).ToArray());
        return targetExt.Length == 0 ? targetStem : targetStem + "." + targetExt;
    }

    private static string WindowsHashed83Leaf(string sourceFilename)
    {
        string name = StripIsoVersionForMatching(sourceFilename);
        int dot = name.LastIndexOf('.');
        string baseName = dot >= 0 ? name[..dot] : name;
        string extension = dot >= 0 ? name[(dot + 1)..] : string.Empty;

        static bool IsShortIllegal(char ch)
            => ch < 128 && "*?<>|\"+=,;[]:/\\".Contains(ch);

        string legalBase = new(baseName
            .Where(ch => ch > ' ' && ch != '.')
            .Select(ch => IsShortIllegal(ch) ? '_' : char.ToUpperInvariant(ch))
            .Take(6)
            .ToArray());
        if (legalBase.Length < 2)
            legalBase = legalBase.PadRight(2, '_');

        ushort checksum = Windows83Checksum(name);
        var hex = new StringBuilder(4);
        ushort value = checksum;
        for (int i = 0; i < 4; i++)
        {
            int nibble = value & 0xF;
            hex.Append((char)(nibble > 9 ? 'A' + nibble - 10 : '0' + nibble));
            value >>= 4;
        }

        string ext = new(extension
            .Where(ch => ch > ' ' && ch != '.')
            .Select(ch => IsShortIllegal(ch) ? '_' : char.ToUpperInvariant(ch))
            .Take(3)
            .ToArray());
        string result = legalBase[..2] + hex + "~1";
        return ext.Length == 0 ? result : result + "." + ext;
    }

    private static ushort Windows83Checksum(string name)
    {
        if (name.Length == 0) return 0;
        if (name.Length == 1) return name[0];
        unchecked
        {
            ushort hash = (ushort)((name[0] << 8) + name[1]);
            if (name.Length == 2) return hash;
            ushort saved = hash;
            int length = 2;
            while (length < name.Length)
            {
                int index = length;
                hash = (ushort)((hash << 7) + name[index]);
                hash = (ushort)((saved >> 1) + (hash << 8));
                if (length + 1 < name.Length)
                    hash = (ushort)(hash + name[index + 1]);
                saved = hash;
                length += 2;
            }
            return hash;
        }
    }

    private static bool SkeletonHasJolietDescriptor(SkeletonInspectionResult inspection)
    {
        try
        {
            using var stream = new FileStream(inspection.SkeletonPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            for (int descriptor = 16; descriptor < 32; descriptor++)
            {
                long physicalLba = inspection.BaseLba + descriptor;
                byte[] payload = new byte[CookedSectorSize];
                if (inspection.ImageKind == SkeletonImageKind.Cooked2048)
                {
                    long offset = checked(physicalLba * CookedSectorSize);
                    if (offset < 0 || offset + CookedSectorSize > stream.Length)
                        return false;
                    stream.Position = offset;
                    stream.ReadExactly(payload);
                }
                else
                {
                    long offset = checked(physicalLba * RawSectorSize);
                    if (offset < 0 || offset + RawSectorSize > stream.Length)
                        return false;
                    stream.Position = offset;
                    byte[] raw = new byte[RawSectorSize];
                    stream.ReadExactly(raw);
                    int userOffset = raw[15] == 2 ? 24 : 16;
                    Buffer.BlockCopy(raw, userOffset, payload, 0, CookedSectorSize);
                }

                if (payload.Length < 91 || !payload.AsSpan(1, 5).SequenceEqual("CD001"u8))
                    continue;
                if (payload[0] == 255)
                    break;
                if (payload[0] == 2 && payload[88] == 0x25 && payload[89] == 0x2F &&
                    (payload[90] == 0x40 || payload[90] == 0x43 || payload[90] == 0x45))
                    return true;
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    private static string GetDicFilename(string path)
    {
        string normalized = NormalizeDicRelativePath(path);
        int slash = normalized.LastIndexOf('/');
        return slash >= 0 ? normalized[(slash + 1)..] : normalized;
    }

    private static string GetDicParentPath(string path)
    {
        string normalized = NormalizeDicRelativePath(path);
        int slash = normalized.LastIndexOf('/');
        return slash >= 0 ? normalized[..slash] : string.Empty;
    }

    private static bool JolietParentProjectsToIsoParent(string jolietParentPath, string isoParentPath)
    {
        string[] joliet = NormalizeDicRelativePath(jolietParentPath)
            .Split('/', StringSplitOptions.RemoveEmptyEntries);
        string[] iso = NormalizeDicRelativePath(isoParentPath)
            .Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (joliet.Length != iso.Length)
            return false;

        for (int i = 0; i < joliet.Length; i++)
        {
            if (!JolietComponentProjectsToIsoComponent(joliet[i], iso[i], false))
                return false;
        }

        return true;
    }


    private static string[] GetDicEntryAliases(SkeletonContentEntry entry)
    {
        IEnumerable<string> aliases = (entry.PathAliases ?? Array.Empty<string>())
            .Append(entry.Path)
            .Where(path => !string.IsNullOrWhiteSpace(path));

        return aliases
            .Select(path => "/" + NormalizeDicRelativePath(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string NormalizeDicRelativePath(string path)
    {
        string value = path.Normalize(NormalizationForm.FormC).Replace('\\', '/').Trim().TrimStart('/');
        while (value.Contains("//", StringComparison.Ordinal))
            value = value.Replace("//", "/", StringComparison.Ordinal);
        return value;
    }


    private static bool SourceTimestampMatchesDicRecordingTime(FileInfo source, DateTimeOffset dicTime)
    {
        // ISO9660 directory timestamps carry one-second resolution plus an explicit
        // GMT offset. Extracted files may preserve that instant as UTC/local file time,
        // or some tools may preserve the displayed wall-clock value while dropping the
        // ISO offset. Accept either representation, but only as a secondary discriminator
        // after path projection and exact size have already matched.
        DateTime utc = source.LastWriteTimeUtc;
        DateTimeOffset sourceUtc = new(utc, TimeSpan.Zero);
        if (Math.Abs((sourceUtc - dicTime.ToUniversalTime()).TotalSeconds) < 1.0)
            return true;

        DateTime sourceLocal = source.LastWriteTime;
        DateTime dicWallClock = dicTime.DateTime;
        return Math.Abs((sourceLocal - dicWallClock).TotalSeconds) < 1.0;
    }

    internal static bool DonorJolietPathProjectsToIsoPath(string jolietRelativePath, string isoRelativePath)
        => JolietPathProjectsToIsoPath(jolietRelativePath, isoRelativePath, null);

    internal static bool DonorJolietPathProjectsToIsoPath(string jolietRelativePath, string isoRelativePath, JolietNamingProfile? profile)
        => JolietPathProjectsToIsoPath(jolietRelativePath, isoRelativePath, profile);

    private static bool JolietPathProjectsToIsoPath(string jolietRelativePath, string isoRelativePath)
        => JolietPathProjectsToIsoPath(jolietRelativePath, isoRelativePath, null);

    private static bool JolietPathProjectsToIsoPath(string jolietRelativePath, string isoRelativePath, JolietNamingProfile? profile)
    {
        string[] joliet = NormalizeDicRelativePath(jolietRelativePath)
            .Split('/', StringSplitOptions.RemoveEmptyEntries);
        string[] iso = NormalizeDicRelativePath(isoRelativePath)
            .Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (joliet.Length != iso.Length || joliet.Length == 0)
            return false;

        for (int i = 0; i < joliet.Length; i++)
        {
            bool isFile = i == joliet.Length - 1;
            if (!JolietComponentProjectsToIsoComponent(joliet[i], iso[i], isFile, profile))
                return false;
        }

        return true;
    }

    private static bool JolietPathMatchesIsoCollisionAlias(string jolietRelativePath, string isoRelativePath)
    {
        string[] joliet = NormalizeDicRelativePath(jolietRelativePath)
            .Split('/', StringSplitOptions.RemoveEmptyEntries);
        string[] iso = NormalizeDicRelativePath(isoRelativePath)
            .Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (joliet.Length != iso.Length || joliet.Length == 0)
            return false;

        for (int i = 0; i < joliet.Length - 1; i++)
        {
            if (!JolietComponentProjectsToIsoComponent(joliet[i], iso[i], false))
                return false;
        }

        string source = Regex.Replace(joliet[^1].Normalize(NormalizationForm.FormC), @";\d+$", string.Empty);
        string target = Regex.Replace(iso[^1].Normalize(NormalizationForm.FormC), @";\d+$", string.Empty);
        string projected = ProjectJolietComponentToIsoLevel1(source, true);

        static (string Stem, string Extension) SplitFileComponent(string value)
        {
            int dot = value.LastIndexOf('.');
            return dot > 0 && dot < value.Length - 1
                ? (value[..dot], value[(dot + 1)..])
                : (value, string.Empty);
        }

        (string projectedStem, string projectedExtension) = SplitFileComponent(projected);
        (string targetStem, string targetExtension) = SplitFileComponent(target.ToUpperInvariant());
        if (!projectedExtension.Equals(targetExtension, StringComparison.OrdinalIgnoreCase))
            return false;
        if (projectedStem.Equals(targetStem, StringComparison.OrdinalIgnoreCase))
            return false; // stronger ordinary projection handles the non-collision member

        int commonPrefixLength = 0;
        int comparableLength = Math.Min(projectedStem.Length, targetStem.Length);
        while (commonPrefixLength < comparableLength &&
               char.ToUpperInvariant(projectedStem[commonPrefixLength]) == char.ToUpperInvariant(targetStem[commonPrefixLength]))
        {
            commonPrefixLength++;
        }

        if (commonPrefixLength == 0 ||
            commonPrefixLength >= projectedStem.Length ||
            commonPrefixLength >= targetStem.Length)
        {
            return false;
        }

        // This family is deliberately narrow: the ordinary Level-1 projection must
        // differ from the numbered alias only where trailing underscore placeholder(s)
        // have been replaced by decimal collision discriminator digits. Comparing from
        // the first differing character avoids misreading digits that legitimately form
        // part of the base name (APR2007_, MAR2009_, etc.).
        string displacedProjection = projectedStem[commonPrefixLength..];
        string discriminator = targetStem[commonPrefixLength..];
        return displacedProjection.Length == discriminator.Length &&
               displacedProjection.All(ch => ch == '_') &&
               discriminator.All(char.IsDigit) &&
               discriminator[0] >= '2';
    }

    private static bool JolietComponentProjectsToIsoComponent(string jolietComponent, string isoComponent, bool isFile)
        => JolietComponentProjectsToIsoComponent(jolietComponent, isoComponent, isFile, null);

    private static bool JolietComponentProjectsToIsoComponent(string jolietComponent, string isoComponent, bool isFile, JolietNamingProfile? profile)
    {
        string source = Regex.Replace(jolietComponent.Normalize(NormalizationForm.FormC), @";\d+$", string.Empty);
        string target = Regex.Replace(isoComponent.Normalize(NormalizationForm.FormC), @";\d+$", string.Empty);

        if (source.Equals(target, StringComparison.OrdinalIgnoreCase))
            return true;

        if (JolietNamingRuleService.ProfileAllows(profile, "Level1") && ProjectJolietComponentToIsoLevel1(source, isFile)
            .Equals(target, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // ISO9660 interchange level 2 permits identifiers up to 31 characters.
        // Nero commonly keeps a 3-character extension and therefore truncates a
        // file stem to 27 characters. Example from Project Eden:
        // ArcadeInstallPROJECTEDEN108c.exe -> ARCADEINSTALLPROJECTEDEN108.EXE.
        if (JolietNamingRuleService.ProfileAllows(profile, "Level2") && ProjectJolietComponentToIsoLevel2(source, isFile)
            .Equals(target, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Some mastering applications derive the primary ISO9660 identifier from
        // the Joliet/user-visible name by eliding punctuation instead of replacing
        // it with an underscore.  Do not impose an 8.3 limit here: DIC can log
        // primary ISO9660 names produced under less restrictive interchange levels.
        // Example: Joliet directory "DirectX8.0a" -> primary "DIRECTX80A".
        // The caller still requires exact byte length and bidirectional uniqueness,
        // so this alternate spelling cannot by itself select an ambiguous record.
        if (JolietNamingRuleService.ProfileAllows(profile, "PunctuationElision") && ProjectJolietComponentByElidingPunctuation(source, isFile)
            .Equals(target, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Some authoring tools disagree about whether an underscore separator is
        // retained between otherwise identical ISO/Joliet name components.  CHY
        // contains the reverse of the usual punctuation-elision case, for example
        // Joliet "TRDUBL.a6e" versus primary "TR_DUBL.A6E".  Compare a strictly
        // alphanumeric projection of both sides so this works symmetrically.  The
        // caller still requires complete-path compatibility, exact size and
        // reverse uniqueness before accepting a source file.
        if (JolietNamingRuleService.ProfileAllows(profile, "SeparatorInsensitive") && ProjectJolietComponentByRemovingSeparators(source, isFile)
            .Equals(ProjectJolietComponentByRemovingSeparators(target, isFile), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Some ISO authoring tools expose a DOS-style numeric short alias in the
        // primary tree while retaining the long display name in Joliet, e.g.
        // "Desktop Theme" -> "DESKTO~1". The numeric suffix is collision-dependent,
        // so do not predict ~1/~2. Merely recognise the alias shape and prefix; the
        // caller's complete-path, exact-size and reverse-uniqueness checks decide
        // whether the association is safe.
        return JolietNamingRuleService.ProfileAllows(profile, "NumericAlias") && JolietComponentMatchesNumericShortAlias(source, target, isFile);
    }

    private static bool TryGetNumericShortAliasFamilyParts(
        SkeletonContentEntry entry,
        out string parent,
        out string prefix,
        out string extension)
    {
        foreach (string alias in GetDicEntryAliases(entry))
        {
            string normalized = NormalizeDicRelativePath(alias);
            string file = GetDicFilename(normalized);
            int dot = file.LastIndexOf('.');
            string stem = dot > 0 ? file[..dot] : file;
            Match match = Regex.Match(stem, @"^(?<prefix>[A-Z0-9_]{1,6})(?:~|_)[1-9][0-9]*$", RegexOptions.IgnoreCase);
            if (!match.Success)
                continue;

            parent = GetDicParentPath(normalized).ToUpperInvariant();
            prefix = match.Groups["prefix"].Value.ToUpperInvariant();
            extension = dot > 0 ? file[(dot + 1)..].ToUpperInvariant() : string.Empty;
            return true;
        }

        parent = string.Empty;
        prefix = string.Empty;
        extension = string.Empty;
        return false;
    }

    private static bool NumericAliasPrefixesCompatible(string left, string right)
    {
        string a = left.Replace("_", string.Empty, StringComparison.Ordinal).ToUpperInvariant();
        string b = right.Replace("_", string.Empty, StringComparison.Ordinal).ToUpperInvariant();
        return a.StartsWith(b, StringComparison.Ordinal) || b.StartsWith(a, StringComparison.Ordinal);
    }

    private static bool EntryBelongsToNumericShortAliasFamily(
        SkeletonContentEntry entry,
        string familyParent,
        string familyPrefix,
        string familyExtension,
        bool allowPlainFirstMember = true)
    {
        foreach (string alias in GetDicEntryAliases(entry))
        {
            string normalized = NormalizeDicRelativePath(alias);
            if (!GetDicParentPath(normalized).Equals(familyParent, StringComparison.OrdinalIgnoreCase))
                continue;

            string file = GetDicFilename(normalized);
            int dot = file.LastIndexOf('.');
            string stem = dot > 0 ? file[..dot] : file;
            string extension = dot > 0 ? file[(dot + 1)..].ToUpperInvariant() : string.Empty;
            if (!extension.Equals(familyExtension, StringComparison.OrdinalIgnoreCase))
                continue;

            Match numeric = Regex.Match(stem, @"^(?<prefix>[A-Z0-9_]{1,6})(?:~|_)[1-9][0-9]*$", RegexOptions.IgnoreCase);
            if (numeric.Success)
                return NumericAliasPrefixesCompatible(numeric.Groups["prefix"].Value, familyPrefix);

            // The first member of a DOS collision family can retain a complete
            // eight-character primary name while later members use ~N aliases.
            // It may participate in an unresolved ambiguity set (for example
            // Z5BUBBLE + Z5BUB~15), but an already-matched plain filename must NOT
            // be used as an ordinal anchor merely because it shares the prefix.
            // Files such as CURSOR00/CURSORX are ordinary siblings, not collision
            // aliases, and including them as anchors can falsely break an otherwise
            // proven numeric-alias ordering.
            if (!allowPlainFirstMember)
                continue;

            string plain = Regex.Replace(stem.ToUpperInvariant(), @"[^A-Z0-9_]", string.Empty);
            return NumericAliasPrefixesCompatible(plain, familyPrefix);
        }

        return false;
    }

    private static bool JolietSourceBelongsToNumericShortAliasFamily(
        string sourceFile,
        string familyPrefix,
        string familyExtension)
    {
        string syntheticTarget = familyPrefix + "~1";
        if (familyExtension.Length > 0)
            syntheticTarget += "." + familyExtension;
        return JolietComponentMatchesNumericShortAlias(sourceFile, syntheticTarget, true);
    }
private static bool JolietComponentMatchesNumericShortAlias(string source, string target, bool isFile)
    {
        static string KeepShortNameCharacters(string part)
        {
            var builder = new StringBuilder(part.Length);
            foreach (char raw in part.Normalize(NormalizationForm.FormC))
            {
                char ch = char.ToUpperInvariant(raw);
                if ((ch >= 'A' && ch <= 'Z') || (ch >= '0' && ch <= '9') || ch == '_')
                    builder.Append(ch);
            }
            return builder.ToString();
        }

        string sourceStem = source;
        string sourceExtension = string.Empty;
        string targetStem = target;
        string targetExtension = string.Empty;
        if (isFile)
        {
            int sourceDot = source.LastIndexOf('.');
            if (sourceDot > 0 && sourceDot < source.Length - 1)
            {
                sourceStem = source[..sourceDot];
                sourceExtension = source[(sourceDot + 1)..];
            }
            int targetDot = target.LastIndexOf('.');
            if (targetDot > 0 && targetDot < target.Length - 1)
            {
                targetStem = target[..targetDot];
                targetExtension = target[(targetDot + 1)..];
            }
        }

        // In addition to the familiar DOS-style ~N alias, some mastering tools
        // (including the one used for Cumhuriyet Bonus Disc) write the collision
        // suffix as _N, e.g. "3D_Modeller" -> "3D_MOD_1" and
        // "Kurtulus Savasi Destani.avi" -> "KURTUL_1.AVI".  Treat the
        // underscore form as the same kind of collision-dependent short alias.
        Match match = Regex.Match(targetStem, @"^(?<prefix>[A-Z0-9_]{1,6})(?:~|_)(?<n>[1-9][0-9]*)$", RegexOptions.IgnoreCase);
        if (!match.Success)
            return false;

        string normalizedSourceStem = KeepShortNameCharacters(sourceStem);
        string prefix = match.Groups["prefix"].Value.ToUpperInvariant();

        // Authoring tools disagree about whether punctuation from the long name is
        // retained as '_' in the short alias (for example Ata'nin -> ATA_NI_1).
        // Compare the alias prefix after removing underscores on both sides; exact
        // size, the complete path and reverse-uniqueness are still enforced by the
        // caller before any source is accepted.
        string comparableSourceStem = normalizedSourceStem.Replace("_", string.Empty, StringComparison.Ordinal);
        string comparablePrefix = prefix.Replace("_", string.Empty, StringComparison.Ordinal);
        if (comparableSourceStem.Length < comparablePrefix.Length ||
            !comparableSourceStem.StartsWith(comparablePrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!isFile)
            return true;

        string normalizedSourceExtension = KeepShortNameCharacters(sourceExtension);
        string normalizedTargetExtension = KeepShortNameCharacters(targetExtension);
        if (normalizedTargetExtension.Length == 0)
            return normalizedSourceExtension.Length == 0;

        string expectedExtension = normalizedSourceExtension.Length <= 3
            ? normalizedSourceExtension
            : normalizedSourceExtension[..3];
        return expectedExtension.Equals(normalizedTargetExtension, StringComparison.OrdinalIgnoreCase);
    }

    private static string ProjectJolietComponentByElidingPunctuation(string value, bool isFile)
    {
        string stem = value;
        string extension = string.Empty;
        if (isFile)
        {
            int dot = value.LastIndexOf('.');
            if (dot > 0 && dot < value.Length - 1)
            {
                stem = value[..dot];
                extension = value[(dot + 1)..];
            }
        }

        static string KeepDCharacters(string part)
        {
            var builder = new StringBuilder(part.Length);
            foreach (char raw in part.Normalize(NormalizationForm.FormC))
            {
                char ch = char.ToUpperInvariant(raw);
                if ((ch >= 'A' && ch <= 'Z') || (ch >= '0' && ch <= '9') || ch == '_')
                    builder.Append(ch);
            }
            return builder.ToString();
        }

        string projectedStem = KeepDCharacters(stem);
        if (!isFile || extension.Length == 0)
            return projectedStem;

        string projectedExtension = KeepDCharacters(extension);
        return projectedExtension.Length == 0 ? projectedStem : projectedStem + "." + projectedExtension;
    }

    private static string ProjectJolietComponentByRemovingSeparators(string value, bool isFile)
    {
        string stem = value;
        string extension = string.Empty;
        if (isFile)
        {
            int dot = value.LastIndexOf('.');
            if (dot > 0 && dot < value.Length - 1)
            {
                stem = value[..dot];
                extension = value[(dot + 1)..];
            }
        }

        static string KeepAlphaNumeric(string part)
        {
            var builder = new StringBuilder(part.Length);
            foreach (char raw in part.Normalize(NormalizationForm.FormC))
            {
                char ch = char.ToUpperInvariant(raw);
                if ((ch >= 'A' && ch <= 'Z') || (ch >= '0' && ch <= '9'))
                    builder.Append(ch);
            }
            return builder.ToString();
        }

        string projectedStem = KeepAlphaNumeric(stem);
        if (!isFile || extension.Length == 0)
            return projectedStem;

        string projectedExtension = KeepAlphaNumeric(extension);
        return projectedExtension.Length == 0 ? projectedStem : projectedStem + "." + projectedExtension;
    }

    private static string ProjectJolietComponentToIsoLevel2(string value, bool isFile)
    {
        string stem = value;
        string extension = string.Empty;
        if (isFile)
        {
            int dot = value.LastIndexOf('.');
            if (dot > 0 && dot < value.Length - 1)
            {
                stem = value[..dot];
                extension = value[(dot + 1)..];
            }
        }

        static string NormalizePart(string part, int maximumLength)
        {
            var builder = new StringBuilder(part.Length);
            foreach (char raw in part.Normalize(NormalizationForm.FormC))
            {
                char ch = char.ToUpperInvariant(raw);
                bool allowed = (ch >= 'A' && ch <= 'Z') || (ch >= '0' && ch <= '9') || ch == '_';
                builder.Append(allowed ? ch : '_');
            }

            string result = builder.ToString();
            return result.Length <= maximumLength ? result : result[..maximumLength];
        }

        if (!isFile)
            return NormalizePart(stem, 31);

        string projectedExtension = NormalizePart(extension, 3);
        int stemLimit = projectedExtension.Length == 0 ? 31 : 27;
        string projectedStem = NormalizePart(stem, stemLimit);
        return projectedExtension.Length == 0 ? projectedStem : projectedStem + "." + projectedExtension;
    }

    private static string ProjectJolietComponentToIsoLevel1(string value, bool isFile)
    {
        string stem = value;
        string extension = string.Empty;
        if (isFile)
        {
            int dot = value.LastIndexOf('.');
            if (dot > 0 && dot < value.Length - 1)
            {
                stem = value[..dot];
                extension = value[(dot + 1)..];
            }
        }

        static string NormalizePart(string part, int maximumLength)
        {
            var builder = new StringBuilder(part.Length);
            foreach (char raw in part.Normalize(NormalizationForm.FormC))
            {
                char ch = char.ToUpperInvariant(raw);
                bool allowed = (ch >= 'A' && ch <= 'Z') || (ch >= '0' && ch <= '9') || ch == '_';
                builder.Append(allowed ? ch : '_');
            }

            string result = builder.ToString();
            return result.Length <= maximumLength ? result : result[..maximumLength];
        }

        string projectedStem = NormalizePart(stem, 8);
        if (!isFile || extension.Length == 0)
            return projectedStem;

        string projectedExtension = NormalizePart(extension, 3);
        return projectedExtension.Length == 0 ? projectedStem : projectedStem + "." + projectedExtension;
    }

    private static string DicPathSizeKey(string relativePath, long size) => $"{size}:{NormalizeDicRelativePath(relativePath)}";

}
