# Changelog

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
