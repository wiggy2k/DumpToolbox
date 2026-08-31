# Heads and Tails v0.8.79

- `audio_heads_tails.sqlite` remains fixed beside the executable.
- `AudioHeadsandTails.bin` is user-configurable in Settings > Heads and Tails and persisted as `[HeadsAndTails] CorpusPath`.
- Collection scans recurse for loose `.cue` files and supported archives.
- Archives are opened once per scan and CUE/payload entries are processed directly from archive streams; no whole-archive extraction is performed.
- Each AUDIO track contributes the first 256 bytes beginning at its first non-zero byte and the final 256 bytes ending at its last non-zero byte.
- Completely zero-filled AUDIO tracks are recorded in `track_observations` as all-zero and add no bytes to the corpus.
- Archive size/mtime is used for incremental change detection of contained CUE sources.
- If any source errors occur, unseen historical records are retained rather than incorrectly marked missing.

## v0.8.81 scanner changes
- Configurable scan thread count (1-64), default 4, saved as `[HeadsAndTails] Threads`.
- Loose CUEs and archives are processed concurrently; each archive remains isolated to one worker.
- Recursive enumeration reports progress while walking large collection trees.
- Verbose logs now cover archive open/indexing, contained CUE parsing, unchanged skips, payload streaming, catalogue writes, errors, and corpus rebuild start/completion.
