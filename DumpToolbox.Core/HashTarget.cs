namespace DumpToolbox.Core;

public sealed record HashTarget(
    long Size,
    uint Crc32,
    string? Md5 = null,
    string? Label = null,
    string? OutputFileName = null,
    string? Sha1 = null)
{
    public string Crc32Hex => Crc32.ToString("x8");
    public string? NormalizedMd5 => string.IsNullOrWhiteSpace(Md5)
        ? null
        : Md5.Replace("-", "", StringComparison.Ordinal).Trim().ToLowerInvariant();

    public string? NormalizedSha1 => string.IsNullOrWhiteSpace(Sha1)
        ? null
        : Sha1.Replace("-", "", StringComparison.Ordinal).Trim().ToLowerInvariant();
}
