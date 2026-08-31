namespace DumpToolbox.Core;

public sealed record SearchResult(
    HashTarget Target,
    long? Offset,
    bool Found,
    string Status,
    long CrcCandidates = 0,
    string? OutputPath = null)
{
    public string OffsetDisplay => Offset is null ? "" : Offset.Value.ToString();
}

public enum SearchEventKind
{
    Progress,
    CrcCandidate,
    Md5Rejected,
    MatchFound,
    Extracted
}

public sealed record SearchProgress(
    int TargetIndex,
    int TargetCount,
    HashTarget Target,
    long BytesScanned,
    long SearchableBytes,
    long CrcCandidates,
    string Message,
    SearchEventKind Kind = SearchEventKind.Progress,
    long? Offset = null,
    string? ActualMd5 = null,
    string? OutputPath = null)
{
    public double Fraction => SearchableBytes <= 0
        ? 0
        : Math.Clamp((double)BytesScanned / SearchableBytes, 0, 1);
}
