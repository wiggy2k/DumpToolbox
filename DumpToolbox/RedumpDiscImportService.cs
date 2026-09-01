using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;

namespace DumpToolbox;

internal sealed record RedumpDiscImportResult(
    int DiscId,
    string DiscTitle,
    string TargetText,
    string? CuePath,
    int TargetCount);

/// <summary>
/// Imports the public Redump disc page for FindCRCs and Audio recovery. This intentionally parses
/// only the stable Files table and downloads Redump's own CUE when available;
/// it does not depend on authenticated/private Redump APIs.
/// </summary>
internal static partial class RedumpDiscImportService
{
    [GeneratedRegex(@"^\s*(?:(?:https?://)?(?:www\.)?redump\.info/disc/)?(?<id>[0-9]+)(?:/)?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex DiscInputRegex();

    [GeneratedRegex(@"<h3[^>]*>\s*Files\s*</h3>(?<body>.*?)(?=<h3|$)", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex FilesSectionRegex();

    [GeneratedRegex(@"<tr[^>]*>(?<row>.*?)</tr>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex RowRegex();

    [GeneratedRegex(@"<t[dh][^>]*>(?<cell>.*?)</t[dh]>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex CellRegex();

    [GeneratedRegex(@"<h2[^>]*>(?<title>.*?)</h2>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex TitleRegex();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex TagRegex();

    public static bool TryParseDiscId(string? input, out int discId)
    {
        discId = 0;
        if (string.IsNullOrWhiteSpace(input))
            return false;
        Match m = DiscInputRegex().Match(input);
        return m.Success && int.TryParse(m.Groups["id"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out discId) && discId > 0;
    }

    public static async Task<RedumpDiscImportResult> ImportAsync(int discId, CancellationToken cancellationToken = default)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("DumpToolbox", "0.8.99"));
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("(+https://redump.info/)"));

        string pageUrl = $"https://redump.info/disc/{discId}/";
        string html;
        try
        {
            html = await client.GetStringAsync(pageUrl, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new InvalidOperationException($"Could not retrieve Redump disc {discId} from {pageUrl}: {ex.Message}", ex);
        }

        string title = DecodeCell(TitleRegex().Match(html).Groups["title"].Value);
        if (title.Length == 0)
            title = $"Redump disc {discId}";

        Match filesSection = FilesSectionRegex().Match(html);
        if (!filesSection.Success)
            throw new InvalidOperationException($"Redump disc {discId} did not contain a recognizable Files table.");

        var payloadRows = new List<(string Name, long Size, string Crc, string Md5, string Sha1)>();
        foreach (Match rowMatch in RowRegex().Matches(filesSection.Groups["body"].Value))
        {
            string[] cells = CellRegex().Matches(rowMatch.Groups["row"].Value)
                .Select(m => DecodeCell(m.Groups["cell"].Value))
                .ToArray();
            if (cells.Length < 5)
                continue;

            string name = cells[0].Trim();
            if (name.Length == 0 || name.EndsWith(".cue", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!long.TryParse(cells[1].Replace(",", "", StringComparison.Ordinal), NumberStyles.None, CultureInfo.InvariantCulture, out long size) || size <= 0)
                continue;
            string crc = NormalizeHex(cells[2], 8);
            string md5 = NormalizeHex(cells[3], 32);
            string sha1 = NormalizeHex(cells[4], 40);
            if (crc.Length != 8 || md5.Length != 32)
                continue;

            payloadRows.Add((name, size, crc, md5, sha1));
        }

        // Redump CD entries can include an .img alias with the same size/hash as
        // the real .bin. Prefer BIN payloads where they exist so FindCRCs does not
        // search the same target twice and the destination filenames stay useful.
        var binRows = payloadRows.Where(r => r.Name.EndsWith(".bin", StringComparison.OrdinalIgnoreCase)).ToList();
        IReadOnlyList<(string Name, long Size, string Crc, string Md5, string Sha1)> selected =
            binRows.Count > 0 ? binRows : payloadRows;

        if (selected.Count == 0)
            throw new InvalidOperationException($"Redump disc {discId} did not expose any payload rows with CRC32 and MD5 hashes.");

        var text = new StringBuilder();
        foreach (var row in selected)
        {
            text.Append(row.Name).Append('\t')
                .Append(row.Size.ToString(CultureInfo.InvariantCulture)).Append('\t')
                .Append(row.Crc).Append('\t')
                .Append(row.Md5);
            if (row.Sha1.Length == 40)
                text.Append('\t').Append(row.Sha1);
            text.AppendLine();
        }

        string? cuePath = null;
        try
        {
            using HttpResponseMessage cueResponse = await client.GetAsync($"https://redump.info/disc/{discId}/cue", cancellationToken).ConfigureAwait(false);
            if (cueResponse.IsSuccessStatusCode)
            {
                string cue = await cueResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                if (cue.Contains("TRACK", StringComparison.OrdinalIgnoreCase))
                {
                    string root = Path.Combine(Path.GetTempPath(), "DumpToolbox", "Redump");
                    Directory.CreateDirectory(root);
                    cuePath = Path.Combine(root, $"redump_{discId}.cue");
                    await File.WriteAllTextAsync(cuePath, cue, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch
        {
            // Hash import remains useful even if Redump's CUE endpoint is
            // temporarily unavailable; the caller logs that CUE import failed.
            cuePath = null;
        }

        return new RedumpDiscImportResult(discId, title, text.ToString().TrimEnd(), cuePath, selected.Count);
    }

    private static string DecodeCell(string html)
    {
        string noTags = TagRegex().Replace(html, string.Empty);
        return WebUtility.HtmlDecode(noTags).Replace("\u00A0", " ", StringComparison.Ordinal).Trim();
    }

    private static string NormalizeHex(string value, int expectedLength)
    {
        string hex = Regex.Replace(value ?? string.Empty, "[^0-9A-Fa-f]", string.Empty).ToLowerInvariant();
        return hex.Length == expectedLength ? hex : string.Empty;
    }
}
