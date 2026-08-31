# DumpToolbox v0.8.5 refactor

This is a no-intended-behaviour-change continuation of the v0.8.x modular cleanup.

`SkeletonResurrectionService.cs` has been reduced from roughly 4,300 lines to a small shared-state/model shell. Existing implementations were moved intact into partial-class modules:

- `SkeletonResurrectionService.Inspection.cs` — skeleton/hash inspection orchestration.
- `SkeletonResurrectionService.SourceMatching.cs` — directory/image source discovery, matching, source claims and scan progress.
- `SkeletonResurrectionService.Resurrection.cs` — resurrection planning/application, mastering EOF rules, DAT verification, raw/cooked sequential restoration and DIC framing recipes.
- `SkeletonResurrectionService.IsoInspection.cs` — hash-manifest parsing, ISO tree/directory parsing, skeleton-image reading helpers and common stream/path helpers used by inspection.

Existing earlier partials remain unchanged in purpose:

- `SkeletonResurrectionService.JolietNaming.cs`
- `SkeletonResurrectionService.RawCdSectors.cs`
- `SkeletonResurrectionService.SourceHashCache.cs`

No Joliet rule ordering, source-selection criteria, Easy CD EOF rule semantics, raw-sector framing, hash verification, or resurrection behaviour is intentionally changed.

v0.7.99 remains the pre-refactor rollback baseline; v0.8.0 through v0.8.4 are confirmed compiling/working refactor checkpoints.
