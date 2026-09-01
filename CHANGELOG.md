# Changelog

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
