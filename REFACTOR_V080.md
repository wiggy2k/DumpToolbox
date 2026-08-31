# v0.8.0 modularisation

This release is intentionally a structural cleanup only.

## Split from SkeletonResurrectionService.cs

- `SkeletonResurrectionService.JolietNaming.cs` — Joliet/ISO9660 projections, hashed 8.3/ordinal aliases, alias-family helpers.
- `SkeletonResurrectionService.RawCdSectors.cs` — raw 2352-byte Mode 0/1/2 framing, EDC/ECC, XA subheaders and payload replacement.
- `SkeletonResurrectionService.SourceHashCache.cs` — source hash-cache persistence and expected-hash bookkeeping.

The service remains a partial class so the moved code is mechanically identical and private state stays private. This reduces refactor risk while establishing module boundaries for later extraction into independent services.

## Proven-unused private code removed

Only private methods with no source-tree call sites were removed. Avalonia event handlers were deliberately excluded from this analysis because XAML references are not C# call sites.

No reconstruction heuristics or matching precedence were intentionally changed.
