# Changelog

## 0.8.105 — 2026-09-09

### Added

- ISO Extractor now recognises UDF-only cooked ISO and raw BIN/IMG images, including write-once virtual partitions backed by a Virtual Allocation Table, and records UDF source paths in its DIC-compatible manifest.
- SkeleTool can hash and restore logical files directly from UDF-only source images, and the SHA-1 catalogue can index and later materialize UDF files.
- DIC donor scans can use UDF-only images as conservative pathname-and-size file-payload sources without treating UDF metadata as ISO9660, Joliet, or raw-sector evidence.
- DIC can test exact donor footer sectors and all known deterministic CeQuadrat/WinOnCD end-of-volume footer layouts after a complete rebuild, promoting a candidate only when every available target hash matches.
- Disc Evidence now validates CeQuadrat text and binary footer structure, self-LBA fields, checksums and zero-filled tails before classifying exact footer evidence.

### Fixed

- Joliet donor matching now recognises Nero's numbered Level-1 collision aliases while requiring compatible parent paths and filename shapes.
- WinOnCD preparer identities are consistently recognised as CeQuadrat-family mastering evidence.

### Tests

- Added coverage for raw Mode 1/Mode 2 Form 1 UDF payload views, Form 2 rejection, UDF extraction-manifest compatibility, ISO/BIN/IMG SHA-1 catalogue discovery, Nero collision aliases, and deterministic CeQuadrat footer variants.

## 0.8.104 — 2026-09-06

### Added

- Disc Evidence now records ISO9660 and Joliet identifier-padding bytes, System Use data, raw directory-record fingerprints, out-of-volume records, metadata/file overlaps, and non-zero system-area, unclaimed, descriptor and post-volume regions.
- Analysis exports now include complete filesystem-record observations and classified mastering-region observations, including duplicate locations for matching post-volume sectors.
- DIC can retain non-zero Joliet identifier-padding evidence from a donor, build a temporary candidate after a complete recovery, and promote it only when the original whole-image hashes verify it.

### Fixed

- CeQuadrat/WinOnCD Joliet directory bridges can now span the full reserved range before the primary Type-L path table instead of being limited to one sector.
- CeQuadrat footer metadata is no longer guessed from the formatter signature alone; exact logged or same-disc donor evidence is preserved, while unsupported footer layouts remain unsynthesized.
- Disc detection accepts both the standard `CeQuadrat` spelling and the observed legacy `CeQudrat` spelling.

### Tests

- Added regression coverage for non-zero Joliet padding, protected-sector regeneration, safe candidate patching, and a 402-directory CeQuadrat bridge spanning two sectors.

## 0.8.103 — 2026-09-06

### Added

- Disc Evidence exports now retain decoded and raw ISO9660/Joliet recording timestamps and report name-mapping candidate counts both before and after timestamp filtering.
- The Disc Evidence database migrates existing catalogues for the new timestamp and candidate-count fields and queues stale evidence for refresh.

### Fixed

- DIC Joliet recovery now accepts primary ISO9660 filenames ending in a trailing period when the mapped Joliet filename omits the empty extension, and recognises exact donor matches carrying mapped Joliet provenance as trustworthy naming evidence.
- DIC donor-image matching no longer rejects an otherwise exact payload solely because the ISO9660 hidden/existence flag differs between masterings; structural file flags remain strict.

### Tests

- Added regression coverage for timestamp parsing and filtering, evidence export fields, trailing-period Joliet aliases, mapped donor provenance, and hidden-flag-compatible payload matching.

## 0.8.102 — 2026-09-04

### Fixed

- NRG-to-BIN conversion now reconstructs Nero DAO mixed-mode Track 2's three-second pregap as 75 scrambled data sectors followed by 150 stored audio sectors, while omitting the duplicated first-track lead-in.
- SkeleTool now checks both directions between the skeleton's ISO9660 file table and companion `.hash` manifest, warns about missing and unused entries together or separately, and marks unused hash-only entries as ignored during source matching.

### Tests

- Added regression coverage for Nero DAO mixed-mode pregap geometry, sector scrambling, output timing, and SkeleTool manifest completeness checks.

## 0.8.101 — 2026-09-03

### Added

- Disc Evidence now retains volume-descriptor sequence and geometry, raw ISO9660/Joliet directory identifiers and record positions, Type-L/Type-M path-table ordering, and explicit primary-to-supplementary record pairs for mastering analysis.
- Evidence schema updates automatically queue existing catalogue units for evidence refresh and add dedicated ordering-analysis exports.

### Fixed

- DIC source-folder scans can read the Joliet namespace directly from a mounted optical disc and attach uniquely paired supplementary paths to already-verified source matches without requiring an extracted filesystem first.
- Exact primary ISO9660 matches from donor BIN/ISO images now retain their uniquely mapped Joliet pathname authority, allowing supplementary metadata synthesis instead of incorrectly reporting every matched file as lacking a trustworthy Joliet identity.

### Tests

- Added regression coverage for mounted-disc primary/Joliet pairing, ambiguous shared geometry, and donor-match Joliet provenance.

## 0.8.100 — 2026-09-03

### Fixed

- Joliet-to-ISO9660 matching now recognises numeric short-name aliases whose prefix contains valid punctuation, including names such as `Sam& Shara.bik` mapped to `SAM&SH~1.BIK`.
- Synthetic Joliet path tables and directory records now default to the mandatory case-sensitive UCS-2 ordering declared by `%/@`, `%/C`, or `%/E`; only mastering profiles with contrary evidence retain primary ISO9660 path-table order.
- Easy CD Creator masters retain their formatter-proven primary ISO9660 directory-record sequence while continuing to use UCS-2 path-table ordering.
- Paired XA masters with a non-sector-rounded SVD root and complete one-to-one primary-directory extent evidence retain primary ordering for both Joliet directory records and path-table numbering.

### Tests

- Added regression coverage for punctuated numeric aliases in both source matching and Joliet reconstruction.
- Added mastering-profile coverage for standards-default Joliet ordering plus the evidence-backed Easy CD Creator record-order and CeQuadrat path-order exceptions.

## 0.8.99 — 2026-09-01

### Changed

- Kept SHA-1 collection scans, Heads and Tails scans, Disc Evidence work, database access, and cache housekeeping away from the UI thread. Progress and activity logs are now batched so large scans keep the application responsive.
- First-track audio inspection now saves the original partial and a `.leading-zero-trimmed.partial` copy with complete leading zero PCM frames removed.
- Final-track audio inspection now saves the original partial and a `.trailing-zero-trimmed.partial` copy with complete trailing zero PCM frames removed.
- Audio trimming is aligned to 4-byte stereo PCM frames.
- CUE-aware audio partials now require adjacent matched AUDIO tracks. Singleton audio tracks and data-track boundaries are no longer used as partial anchors.

### Fixed

- Saving inspection partials is now controlled by the relevant option instead of being enabled implicitly during an audio edge-repair attempt.
- First-track under-dump partials stop at the adjacent audio-track anchor instead of extending into that track to reach the expected size.

### Tests

- Added coverage for first- and final-track zero trimming in both Audio and CUE-aware FindCRCs recovery, including rejection of singleton and data-track anchors.
