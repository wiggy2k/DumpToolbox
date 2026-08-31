using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

namespace DumpToolbox.Core;

public static partial class TargetParser
{
    // Redump's old track-table and new filename formats both contain the same
    // useful adjacent sequence somewhere in the line:
    //   SIZE  CRC32  MD5  [SHA1]
    // Everything before/after that sequence is deliberately ignored.
    [GeneratedRegex(@"(?<![0-9A-Za-z])(?<size>[0-9]+)[\t ,]+(?<crc>[0-9a-fA-F]{8})[\t ,]+(?<md5>[0-9a-fA-F]{32})(?:[\t ,]+(?<sha1>[0-9a-fA-F]{40}))?(?![0-9A-Za-z])")]
    private static partial Regex FullTargetRegex();

    // Retain compatibility with the small hand-entered form: SIZE CRC32.
    [GeneratedRegex(@"^\s*(?<size>[0-9]+)[\t ,]+(?:0x)?(?<crc>[0-9a-fA-F]{8})\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex CrcOnlyRegex();

    [GeneratedRegex(@"(?i)(?<name>[^\t\r\n]*?\.bin)(?=[\t ,]+[0-9]+[\t ,]+[0-9a-fA-F]{8})")]
    private static partial Regex BinFilenameRegex();

    [GeneratedRegex(@"(?i)\.cue(?:\s|\t|,|$)")]
    private static partial Regex CueFilenameRegex();

    [GeneratedRegex(@"^\s*(?<track>[0-9]+)(?:\s|\t)")]
    private static partial Regex OldTrackNumberRegex();

    // XML DATs (Redump/ClrMamePro/MAME-style) carry file hashes on <rom ...>
    // elements. Parse the element itself rather than depending on a particular
    // <game>, <machine> or <datafile> container so complete DATs and pasted
    // fragments are handled identically.
    [GeneratedRegex(@"<\s*rom\b(?<attrs>[^>]*)>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex XmlRomElementRegex();

    [GeneratedRegex(@"(?<key>[A-Za-z_:][A-Za-z0-9_:.-]*)\s*=\s*(?:""(?<dq>[^""]*)""|'(?<sq>[^']*)')", RegexOptions.Singleline)]
    private static partial Regex XmlAttributeRegex();

    public static IReadOnlyList<HashTarget> Parse(string text)
    {
        // XML is an additive input form. Check it first so a full DAT, a
        // <game>...</game> fragment, or one/more bare <rom .../> elements can
        // be pasted into the same boxes used for ordinary Redump rows.
        IReadOnlyList<HashTarget> xmlTargets = ParseXmlDatTargets(text);
        if (xmlTargets.Count > 0)
            return xmlTargets;

        var targets = new List<HashTarget>();
        var ignored = new List<int>();
        int lineNumber = 0;

        foreach (string rawLine in text.Replace("\r", "", StringComparison.Ordinal).Split('\n'))
        {
            lineNumber++;
            string line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;

            // New Redump file lists often include the .cue itself. It is not a
            // track payload and should never be searched for inside a BIN image.
            if (CueFilenameRegex().IsMatch(line))
                continue;

            Match match = FullTargetRegex().Match(line);
            if (match.Success)
            {
                var identity = BuildIdentity(line, lineNumber, match.Groups["md5"].Value);
                targets.Add(CreateTarget(match, identity.Label, identity.OutputFileName));
                continue;
            }

            Match crcOnly = CrcOnlyRegex().Match(line);
            if (crcOnly.Success)
            {
                targets.Add(CreateTarget(crcOnly, $"Line {lineNumber}", null));
                continue;
            }

            // Be permissive with pasted Redump output. Header/footer/noise lines
            // are ignored instead of causing the whole paste to fail.
            ignored.Add(lineNumber);
        }

        if (targets.Count == 0)
        {
            string suffix = ignored.Count > 0
                ? $" Non-target text was seen on line(s): {string.Join(", ", ignored)}."
                : string.Empty;
            throw new FormatException("No hash targets were found. Expected XML DAT <rom> entries, a SIZE CRC32 MD5 sequence (SHA1 may follow), or SIZE CRC32." + suffix);
        }

        return targets;
    }

    private static IReadOnlyList<HashTarget> ParseXmlDatTargets(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.IndexOf("<rom", StringComparison.OrdinalIgnoreCase) < 0)
            return Array.Empty<HashTarget>();

        var targets = new List<HashTarget>();
        int romNumber = 0;

        foreach (Match romMatch in XmlRomElementRegex().Matches(text))
        {
            romNumber++;
            var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match attributeMatch in XmlAttributeRegex().Matches(romMatch.Groups["attrs"].Value))
            {
                string key = attributeMatch.Groups["key"].Value;
                string value = attributeMatch.Groups["dq"].Success
                    ? attributeMatch.Groups["dq"].Value
                    : attributeMatch.Groups["sq"].Value;
                attributes[key] = WebUtility.HtmlDecode(value).Trim();
            }

            if (!attributes.TryGetValue("size", out string? sizeText) ||
                !long.TryParse(sizeText, NumberStyles.None, CultureInfo.InvariantCulture, out long size) ||
                size <= 0)
            {
                continue;
            }

            if (!attributes.TryGetValue("crc", out string? crcText))
                continue;

            crcText = crcText.Trim();
            if (crcText.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                crcText = crcText[2..];
            if (crcText.Length != 8 || !uint.TryParse(crcText, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint crc))
                continue;

            string? md5 = NormalizeXmlHash(attributes, "md5", 32);
            string? sha1 = NormalizeXmlHash(attributes, "sha1", 40);

            attributes.TryGetValue("name", out string? name);
            name = string.IsNullOrWhiteSpace(name) ? null : Path.GetFileName(name.Trim());

            // A DAT can contain a cuesheet <rom>; it is metadata, not a payload
            // target for FindCRCs/Audio/ISO2BIN.
            if (name?.EndsWith(".cue", StringComparison.OrdinalIgnoreCase) == true)
                continue;

            string label = name ?? $"XML ROM {romNumber}";
            targets.Add(new HashTarget(size, crc, md5, label, name, sha1));
        }

        return targets;
    }

    private static string? NormalizeXmlHash(Dictionary<string, string> attributes, string name, int expectedLength)
    {
        if (!attributes.TryGetValue(name, out string? value) || string.IsNullOrWhiteSpace(value))
            return null;

        string normalized = value.Replace("-", "", StringComparison.Ordinal).Trim().ToLowerInvariant();
        if (normalized.Length != expectedLength || normalized.Any(c => !Uri.IsHexDigit(c)))
            return null;
        return normalized;
    }

    private static HashTarget CreateTarget(Match match, string label, string? outputFileName)
    {
        if (!long.TryParse(match.Groups["size"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out long size) || size <= 0)
            throw new FormatException($"Invalid target size '{match.Groups["size"].Value}'.");

        if (!uint.TryParse(match.Groups["crc"].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint crc))
            throw new FormatException($"Invalid CRC32 '{match.Groups["crc"].Value}'.");

        string? md5 = match.Groups["md5"].Success
            ? match.Groups["md5"].Value.ToLowerInvariant()
            : null;
        string? sha1 = match.Groups["sha1"].Success
            ? match.Groups["sha1"].Value.ToLowerInvariant()
            : null;

        return new HashTarget(size, crc, md5, label, outputFileName, sha1);
    }

    private static (string Label, string? OutputFileName) BuildIdentity(string line, int lineNumber, string md5)
    {
        Match filename = BinFilenameRegex().Match(line);
        if (filename.Success)
        {
            string name = Path.GetFileName(filename.Groups["name"].Value.Trim());
            return (name, name);
        }

        Match track = OldTrackNumberRegex().Match(line);
        if (track.Success && int.TryParse(track.Groups["track"].Value, out int trackNumber))
        {
            string normalizedMd5 = md5.ToLowerInvariant();
            return ($"Track {trackNumber:00}", $"Track_{trackNumber}_{normalizedMd5}.bin");
        }

        return ($"Line {lineNumber}", null);
    }
}
