# v0.8.1 DicLogImportService modularisation

This release is a structural cleanup only. v0.8.0 is the confirmed compiling first-refactor checkpoint and v0.7.99 remains the pre-refactor rollback baseline.

## Boundaries

`DicLogImportService` remains one `public sealed partial class`; private state and private nested models are therefore unchanged semantically while implementation is separated by concern. No public API was intentionally changed.

- **DicLogImportService.cs** — discovery, import orchestration, shared constants and regular expressions.
- **JolietIdentity** — post-match Joliet identity application and primary directory metadata reading.
- **LogParsers** — DIC companion-log parsing and evidence decoding.
- **JolietSynthesis** — primary path tables, SVD/Joliet directory/path-table synthesis, CEQuadrat supplementary structures.
- **ContentAndSlack** — payload donor requirements, content-entry construction, file-tail slack and unclaimed-volume detection.
- **Hfs** — Apple partition map and classic HFS support.
- **RecoveryAndSkeleton** — coverage audit, exactness donors, and synthetic skeleton writers.
- **Models** — private records/classes used by the parser and synthesizers.

## Dead-code audit

Private C# method declarations were checked against source-tree references. Avalonia handlers were not treated as dead merely because they are referenced through XAML. One core method was provably unreferenced and removed: `SkeletonResurrectionService.PatchRawExtentAsync`.

## Behaviour policy

No rule order, mapping criterion, sector synthesis rule, EOF-slack rule, CEQuadrat behaviour, HFS behaviour, or verification policy is intentionally changed by this release.
