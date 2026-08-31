# DumpToolbox v0.8.5 refactor

Behavior-preserving modularisation of `DicDonorImageService`.

The service is now split into partial-class units:

- `DicDonorImageService.cs` — public extraction/matching orchestration and donor result models.
- `DicDonorImageService.Payloads.cs` — source aliases/cache paths, donor file extraction, and required payload application.
- `DicDonorImageService.Metadata.cs` — same-disc metadata-sector application and target logical-sector reads.
- `DicDonorImageService.Filesystem.cs` — ISO9660/Joliet descriptor, directory, multi-extent and identifier parsing.
- `DicDonorImageService.Reader.cs` — cooked/raw donor image recognition and sector I/O.

No donor matching, ISO extraction, Joliet correspondence, payload eligibility, raw/cooked-sector behavior, or metadata authority rule is intentionally changed.
