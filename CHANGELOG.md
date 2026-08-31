## v0.8.98

- Made SkeleTool SHA-1 catalogue lookup metadata-only. Loading a skeleton or pressing Check SHA1 DB no longer extracts archive-backed images/files into the temporary catalogue cache.
- Catalogue payload materialization is deferred until Resurrection, after explicit local folder/image sources have had priority, and therefore runs only for individual files whose surviving selected source is the SHA-1 catalogue.
- Reuses one materialized image for multiple selected files from the same catalogue image during a rebuild.
- This removes archive I/O from normal hash-database lookup and prevents a small SQLite catalogue check from taking minutes just because the matching source lives in a large archive.

## v0.8.97

- SHA-1 catalogue archive scans now delete each extracted BIN/ISO immediately after all of that source image's data-track plans have been hashed and indexed, instead of retaining the large materialized image until the archive/session cleanup. This bounds temporary-disk growth during long catalogue scans while preserving multi-track CUE handling.
- Verified the seeded EOF rules include the newly confirmed 10-sector modes for Easy CD Creator 5.0 (352) with `CD-RTOS CD-BRIDGE` and Easy CD Creator 5.3 (158) with a blank System ID.

## v0.8.96
- DIC/SkeleTool: added ISOCD 1.04 by Pantaray FS/TM restoration as a shared resurrection post-pass. The PVD Application Use FS/TM record is parsed for the exact trademark payload length and LBA.
- Embedded the two proven ISOCD trademark payloads supplied for reconstruction: `CDTV.TM` (22,152 bytes; SHA1 `fd3e764e6393974dea05612909e25ddb2124eb8b`) and `CD32.TM` (2,048 bytes; SHA1 `c5ffcef2a5e33d2df606185823cd95d1c174d65f`).
- The trademark region is restored only when its target bytes are still zero. Exact existing bytes are left alone, and conflicting non-zero evidence is never overwritten. Raw 2352-byte images have EDC/ECC regenerated while preserving their existing sector framing.
- Proven against Astro Revisited and ZGR3D DIC reconstructions: inserting the PVD-declared 22,152-byte `CDTV.TM` at LBA 21 produces the exact expected whole-image hashes. Body Blows independently proves the 2,048-byte `CD32.TM` variant referenced by the same ISOCD FS/TM structure.

## v0.8.95

- Fixes CS1628 in `JolietNamingRuleService.ResolveForInspection`: the `out` mastering-identity value is copied to a normal local before use by the profile-selection lambda.
- No Joliet naming-rule behaviour changes from v0.8.94.

## v0.8.94

- Added runtime-configurable mastering-specific Joliet -> primary ISO9660 naming profiles in `JolietNamingRules.ini`.
- The file is seeded beside the executable on first run and can be edited without recompiling DumpToolbox.
- Target mastering identity is read from the DIC skeleton PVD (System ID / Application ID / Data Preparer ID) and used to select a profile.
- If no mastering profile matches, the existing generic Joliet/ISO9660 projection rules are used unchanged.
- Seeded initial profiles for Easy CD Creator, Roxio Burn Engine, STOMP RecordNow Max and Nero; these are intentionally conservative and can be refined as Disc Evidence grows.
- Added Settings > General > Reset JolietNamingRules.ini.
- Disc Evidence CSV already records SystemId/ApplicationId/DataPreparerId on every Joliet/ISO9660 observation, so future profile rules can be derived without a database/schema change.

## v0.8.93

- Fixed Disc Evidence raw CD detection. The scanner incorrectly required CD-ROM header bytes 12 and 13 (BCD MSF minute/second) to be zero, causing normal 2352-byte BIN images to be treated as cooked 2048-byte images and making ISO9660 PVD detection fail on essentially every raw CD.
- Raw CD detection now validates the complete 12-byte sync pattern (`00 FF FF FF FF FF FF FF FF FF FF 00`) plus Mode 1/Mode 2 at byte 15; MSF bytes 12-14 are intentionally ignored for geometry detection.
- Disc Evidence schema bumped from 1 to 2 so units previously marked complete with the broken detector are automatically re-queued and their image evidence is refreshed.

## v0.8.92

- DIC donor alias-family fallback now uses exact ISO9660 recording timestamps as an additional per-member discriminator when both source and target provide them.
- Family identity is still not split by timestamp; members with unique matching timestamps are paired first, and filesystem order is used only within equal-timestamp or timestamp-unavailable members.
- If all remaining donor members have timestamps but their timestamp multiset cannot satisfy the target family, the fallback now rejects the family instead of guessing by order.

## v0.8.91

- DIC donor image alias-family order fallback no longer requires every member of a short-name collision family to share one identical recording timestamp.
- Family identity now uses parent directory, alias stem, extension, exact logical size and file flags; donor directory-record order is then transposed onto target tilde-alias order after stronger matches have run.
- Fixes adjacent aliases such as `UBI_0004.BMP` / `UBI_0005.BMP` -> `UBI_LO~1.BMP` / `UBI_LO~2.BMP` when their DIC timestamps differ by a second.

## v0.8.90
- DIC donor-image payload matching adds a final conservative alias-family filesystem-order fallback for unresolved numeric short-name collisions.
- Families must share target parent, extension, exact size, exact timestamp and tilde-family stem; donor Joliet names must form one same-sized/same-timestamp family with matching cardinality.
- Donor family members are paired in primary filesystem directory-record order to target `~1`, `~2`, ... ordinal order. Any stronger already-proven member constrains the family; conflicting evidence rejects the fallback.
- This handles cases such as donor `UBI_0004.BMP` / `UBI_0005.BMP` (Joliet `ubi_logo_Click.bmp` / `ubi_logo_Highlight.bmp`) mapping to target `UBI_LO~1.BMP` / `UBI_LO~2.BMP` without introducing a size-only guess.

## v0.8.89

- Extended DIC non-exact donor/source-image matching for files whose primary ISO9660 short aliases differ between mastering runs.
- If exact primary path matching fails, an unambiguous donor primary<->Joliet mapping may now project the donor Joliet pathname onto the target DIC primary ISO9660 alias using the same conservative Joliet projection rules as extracted-folder matching.
- Exact logical size and compatible file flags remain mandatory, and reverse uniqueness is required so one donor Joliet pathname cannot satisfy multiple target records.
- This covers cases such as donor `SPL_0006.ICO` and target `SPLINT~1.ICO` when both are proven aliases of Joliet `SplinterCell.ico`.

## v0.8.88

- Fixed DIC non-exact donor/source-image matching so logical source files no longer have to occupy the same ISO9660 extent LBA as the target disc.
- Payload-source matching remains conservative: exact logical byte size, compatible ISO file flags, and exact primary ISO9660 relative path/name are still required.
- The target DIC filesystem record remains authoritative for destination extent/LBA placement; donor LBA equality is still relevant only to exact same-disc metadata/slack donation paths.
- Preserves the v0.8.86 donor Joliet pathname-evidence behavior and the v0.8.87 responsive Audio layout.

## v0.8.87

- Improved Audio-tab behaviour at narrower window sizes. The source-action toolbar now wraps onto additional rows instead of being clipped off the right edge.
- Replaced the fixed Audio recovery option columns with a wrapping option row so Heads and Tails / delete-working-files controls remain reachable at small widths.
- Changed the Audio output-folder / edge-silence split from a hard 220-pixel right column to a proportional layout so both fields shrink more naturally.

## v0.8.84

- Changed Heads and Tails catalogue to metadata-only SQLite storage; extracted head/tail audio bytes are no longer stored as BLOBs.
- Added one-time migration that drops the legacy `snippets` BLOB table and vacuums the catalogue database.
- `AudioHeadsandTails.bin` is now append-only during normal scans. Unchanged sources do not read source payloads and do not write anything to the corpus.
- New or changed sources append their newly extracted head/tail bytes through the existing single-writer queue, preserving safe multithreaded scanning.
- If the configured corpus file is missing or the configured corpus path changes, processed signatures are invalidated so source audio is re-read to repopulate the new corpus.
- Cancellation no longer attempts to rebuild the corpus from SQLite because the catalogue intentionally contains no audio bytes.

## 0.8.83 - Stream Heads and Tails corpus during scan

- Creates/truncates the configured `AudioHeadsandTails.bin` as soon as a Heads and Tails scan starts instead of waiting until the end.
- Adds a dedicated single-writer corpus queue so multiple scan workers can produce snippets concurrently without writing to the file simultaneously or corrupting file position.
- Changed and newly discovered sources stream their extracted head/tail snippets into the corpus immediately after their catalogue transaction commits.
- Unchanged sources stream their already-catalogued snippets into the same live corpus, so a full scan still produces a complete corpus without a final rebuild pass.
- Active collections not included in a one-collection scan are seeded from the catalogue before that scan begins.
- Failed sources retain their previously committed corpus snippets where possible; cancelled scans restore a catalogue-consistent corpus from SQLite.
- Fixed loose CUEs that change from audio to no-audio so stale snippet rows are cleared.
- Adds live corpus byte counts and explicit `CORPUS:` logging while the scan is running.

## 0.8.82 - Heads/Tails threads layout and temporary SkeleTool materialization cache
- Fixed the Heads and Tails scan-threads NumericUpDown layout by matching the wider SHA-1 catalogue control.
- SkeleTool SHA-1 archive/image materialization is no longer persisted beside the executable.
- Materialized archive entries, image slices, and extracted filesystem payloads now live in a per-process OS temporary cache only while required.
- The legacy `skeletool_sha1_cache` directory beside the executable is removed automatically on startup because its contents are reproducible working files, not catalogue data.
- Stale DumpToolbox SkeleTool temporary-cache sessions are cleaned at startup, and the current session cache is removed on normal process exit.
- The persistent `skeletool_sha1_catalogue.sqlite` database remains beside the executable and is unchanged.

## 0.8.81 - Heads and Tails parallel scanner and verbose progress
- Added configurable Heads and Tails scan threads (1-64), defaulting to 4 and persisted in DumpToolbox.ini.
- Parallelized independent loose-CUE/archive work while keeping each archive isolated to one worker.
- Added immediate recursive-enumeration progress plus verbose archive, CUE, payload streaming, catalogue-save and corpus-rebuild logging.
- The scanner now visibly reports work during very large collection enumeration instead of appearing idle until enumeration completes.

## 0.8.79

## 0.8.80

- Heads and Tails collection scanning now runs on a background worker instead of the Avalonia UI thread.
- Archive enumeration, CUE parsing, archive streaming/decompression, SQLite work, and corpus rebuilding no longer block the frontend.
- Progress and activity logging continue to marshal back to the UI, and cancellation remains responsive.
- Rebuilding the Heads and Tails corpus after removing a collection is also performed off the UI thread.

- Heads and Tails corpus output path is now explicitly configurable; the SQLite catalogue remains beside the executable.
- Heads and Tails collection scans now discover CUE sheets inside supported archives as well as loose CUEs.
- Archive CUEs and payloads are processed directly from archive streams where possible, avoiding full archive extraction.
- Archive change detection uses archive size/mtime signatures so unchanged archive CUEs can be skipped on later scans.
- Completely zero-filled AUDIO tracks are recorded as all-zero observations and contribute no synthetic bytes to AudioHeadsandTails.bin.
- Scan logs now report loose CUE/archive discovery, per-archive CUE counts, captured tracks, all-zero tracks, unchanged sources, and errors.

## v0.8.78
- Fixed CS0162 in `AudioHeadsTailsCatalogueService`: replaced the single-iteration next-track loop with a direct next-track boundary check.
- Fixed CS8602 in `MainWindow.HeadsTails`: safely guard the nullable settings store before persisting `HeadsAndTails/CorpusPath`.

## v0.8.77
- Heads and Tails availability now depends on a configured corpus path in `DumpToolbox.ini` **and** that file existing on disk.
- A successful Heads and Tails collection scan writes `[HeadsAndTails] CorpusPath=...` to the INI.
- With registered collections but no valid configured corpus, the Audio Recovery option is visible but disabled and unchecked.
- With no registered collections, the Audio Recovery option remains hidden.
- Audio Recovery now uses the INI-configured corpus path rather than the service's implicit default path.

# v0.8.76

- Replaced Audio Recovery “Hail Mary” mode with **Heads and Tails mode**.
- Added **Settings → Heads and Tails**, with persistent collection folders, last-scan/change tracking, progress and scan log.
- The scanner reads CUE-described raw BINARY disc images and stores the first and last non-zero 256-byte sample from every AUDIO track in a SQLite catalogue.
- Rebuilds `AudioHeadsandTails.bin` from active/present catalogue records after scans or collection removal.
- Heads and Tails mode uses `AudioHeadsandTails.bin` as the CRC-search source instead of the full combined audio input.
- The Audio Recovery Heads and Tails checkbox is hidden when no Heads and Tails collections exist.
- Existing normal zero-fill and Find Ends recovery still runs first; Heads and Tails remains an optional last-resort pass.

# v0.8.75

- Audio Hail Mary recovery now runs after normal zero-fill / Find Ends fails for a proven under-dumped first or final track, not only for exact-length unmatched outer tracks.
- A saved inspection partial no longer prevents the enabled Hail Mary fallback from being attempted.

## v0.8.74
- Audio Recovery: added opt-in **Save partial edge tracks** support, mirroring FindCRCs manual-inspection partials but intentionally restricted to unmatched first/final targets.
- Partial saving works independently of normal edge repair/Hail Mary when an adjacent verified track establishes a safe outer-track boundary.
- Audio partial preference is persisted in DumpToolbox.ini and reset by Clear saved inputs.

# v0.8.73

- MDF2BIN: when an AUDIO track's reported stored pregap physically overlaps the preceding MDF track region, the pregap main channel is now emitted as 2352-byte zero PCM/CDDA silence sectors instead of duplicating the preceding data-sector bytes. Genuine non-overlapping audio pregap payload remains preserved. Interleaved subchannel bytes, when requested, are still copied from the MDF.


## 0.8.72
- Replaced Audio Recovery Hail Mary repeated whole-source passes with a batched CRC-algebra search.
- All allowed inner/outer zero-padding layouts are converted to required source CRC32 targets up front.
- Inner and outer layouts with the same missing-source length now share a single rolling CRC scan.
- The combined audio source is read once in blocks; distinct missing lengths are scanned in parallel from each in-memory block.
- MD5 is calculated only for CRC32 hits, preserving exact acceptance criteria while greatly reducing source I/O and wall-clock time.
- The duplicate all-zero inner/outer layout is collapsed to one equivalent verification.
# DumpToolbox v0.8.68

- Audio Recovery Hail Mary edge recovery now exposes live progress instead of appearing stalled during exhaustive whole-source searches.
- Logs the number of source/zero splits, combined-source size, and worst-case rolling-search workload.
- Reports 25/50/75/100% progress within each whole-source Find Ends pass plus periodic overall split progress.
- No recovery acceptance criteria changed: candidates still require CRC32 and MD5 verification.

# v0.8.67

- Fixed Audio Hail Mary edge-recovery compile failure by adding the local synchronous `ReadSome` helper used by the PCM silence scanners.
- Audio Recovery activity log can now be hidden, undocked into a themed standalone window, shown again, and docked back into the Audio tab.
- Detached Audio log stays synchronized with live recovery output and log resets.


## 0.8.66 - Audio Recovery Hail-Mary edge search

- Added a final fallback to Audio Recovery's existing edge-fix option for exact-length unmatched first/final tracks.
- The fallback only runs when the appropriate adjacent track is hash-verified and therefore proves the outer track extent.
- Requires genuine zero-valued 16-bit PCM samples at the physical outside edge.
- Trims that known edge silence back to the nearest non-zero PCM sample and uses the entire combined CDDA block as the Find Ends source.
- Exhaustively retries every fixed-length split: recovered source segment plus progressively more forced 0x00 bytes at the physical outside edge.
- First-track handling is the exact mirror of final-track handling.
- Every accepted result must still match the target CRC32 and MD5.
- Cancellation remains active throughout the exhaustive search.
# v0.8.65

- Added the standard trash-can **Clear saved inputs** button to the IRD tab.
- Added IRD path/source/output/key-file and encryption-option persistence to `DumpToolbox.ini`. The directly typed disc key is deliberately never persisted.
- Clearing IRD saved inputs now resets the current IRD verification/tree state as well as the remembered values.
- Removed the explanatory text block from the bottom of the IRD tab.

# v0.8.64 — IRD encryption UI responsiveness

- Runs the CPU-heavy PS3 IRD encryption phase on a background worker so Avalonia remains responsive.
- Reports encryption progress after every processing chunk so the IRD progress bar advances continuously.
- Coalesces duplicate progress log messages, keeping region-level logging without flooding the activity log.

# v0.8.63

- Fixed PS3 IRD encrypted ISO region verification: IRD region hashes exclude the separately stored IRD header and footer, matching LibIRD/IRD generation semantics.
- Region verification now checks every region and logs its exact hashed sector range plus expected/actual MD5 on mismatch.

## v0.8.59
- IRD: added ISO9660 multi-extent file support. Logical file sizes now sum all continuation extents, source MD5 verification covers the complete logical file, the tree shows extent counts, and rebuild streams each source file across its recorded non-contiguous extents.

# DumpToolbox v0.8.58

- IRD tab now renders the IRD filesystem as a hierarchical PS3 disc tree instead of a flat verification list.
- IRD tree is populated immediately when an IRD is loaded, with file size and starting LBA.
- Source verification updates tree nodes in place: green check = MD5 valid, red cross = missing, orange exclamation = wrong size/hash.
- IRD activity log now uses the same equal-width tree/log workspace pattern as SkeleTool and DIC.
- IRD log can be hidden, undocked into its own themed window, shown again, and docked back into the main window.


## v0.8.53 - compact top-level tab icons

- Added small local vector icons beside the existing top-level tab labels.
- No icon package, font, external asset, or new runtime dependency was added.
- Nested tabs remain text-only to keep the UI change minimal.
- Icons inherit the normal tab foreground/theme colour.
## 0.8.50
- Fixed the two CS8604 nullable warnings in the SharpCompress archive-entry matching paths.
- Archive entry keys are now explicitly null-checked/pattern-matched before normalization; behavior for valid archive entries is unchanged.

## 0.8.49
- Restored DumpToolbox's single-executable packaging rule: the published application must not ship 7z.dll, 7za.dll, 7zxa.dll, architecture folders, or require an installed 7-Zip program.
- Removed SharpSevenZip and 7z.Libs, including the embedded/extracted native Windows 7-Zip DLL path.
- Replaced catalogue/archive extraction with the pure-managed SharpCompress library so archive support is bundled into the normal .NET 8 single-file executable on Windows and Linux.
- Retains the important catalogue formats including ZIP/TorrentZip, 7z (including solid archives), RAR, TAR and gzip/bzip2/xz/zstd-family inputs supported by SharpCompress.
- Archive extraction no longer writes a native 7-Zip engine into the system temporary directory.

## 0.8.48
- Added a new **Check SHA-1 DB** button to the SkeleTool tab alongside **Scan source folder** and **Scan source ISO/BIN**.
- This lets you manually query the SHA-1 catalogue after a skeleton is loaded, so catalogue matches can be pulled in on demand for missing files before resurrection.
- The button is enabled only when a skeleton is loaded and the SHA-1 catalogue option is enabled in Settings.
- SkeleTool now reports how many new matches the SHA-1 database contributed and reuses the same required-match counting logic as the folder scan summary.

## v0.8.47
- Rebuilt the SkeleTool SHA-1 catalogue as schema v2 to reduce database size substantially.
- SHA-1 values are now stored as 20-byte SQLite BLOBs rather than 40-character hexadecimal TEXT.
- Added a deduplicated `hashes` table keyed by SHA-1 + size; filesystem rows now reference a compact integer hash ID instead of repeating SHA-1 and size for every occurrence.
- Removed the redundant per-filesystem-file `scanned_utc`; scan timestamps remain at source/root level.
- Replaced the large `(sha1,size)` files index with compact hash-ID/image indexes.
- Existing schema-v1 catalogues are migrated in-place from their already calculated hashes without rescanning disc/archive contents, followed by WAL checkpoint + VACUUM to reclaim the old pages.

## v0.8.46
- Simplified the SHA-1 catalogue progress area: detailed current-source and scan statistics are now kept exclusively in the activity log, while the text below the progress bar shows only a compact `processed / total scanned` counter.

## v0.8.45
- Fixed the SHA-1 Database scan-thread selector layout so the numeric value field is visible instead of being collapsed to only the spinner buttons.
- Kept the Scan threads label and selector together as a compact layout group and increased the control width/minimum width for Avalonia's NumericUpDown template.

## v0.8.44
- Fixed SHA-1 Database activity-log compile errors on Avalonia 11.3.13 by using `Avalonia.Controls.GridLength` / `GridUnitType` rather than the root Avalonia namespace.
- Removed the nullable dereference warning when showing the detached SHA-1 scan log window.

# DumpToolbox v0.8.43

- Reworked the Settings > SHA-1 Database scan layout: the progress bar/status now sit directly beneath the collection controls, followed by the registered-folder list and a dedicated scan activity log.
- Added a docked SHA-1 catalogue activity log with Clear and Undock controls; the detached window can be docked back into Settings.
- Catalogue scans now treat archive/image/CUE/filesystem failures as per-source errors: the error is logged and remaining queued sources continue processing instead of aborting the whole scan.
- Added an error counter to catalogue progress and retain per-root completion status when a scan finishes with recoverable source errors.
- A source that fails during a change check is not interpreted as deleted; any previously-present record at that source location is preserved from the missing-source sweep.
- Incomplete/partially scanned catalogue units are no longer eligible for unchanged/moved-source reuse or automatic SkeleTool hash lookup. They are retried on a later change scan.
- Added per-source activity lines for checking, unchanged, successful scan, recoverable-error completion, and fatal/root-level failures.

# DumpToolbox v0.8.42

- Made CUE sheets authoritative for catalogue BIN scanning: only CUE-declared data-track extents are inspected, while referenced AUDIO BINs are never probed as standalone filesystems.
- Added a fallback CUE FILE-reference pass so BINs remain CUE-controlled even when an unusual CUE cannot be fully analysed.
- Added configurable multi-threaded SHA-1 catalogue scanning (1-64 workers) for independent images and archives; SQLite writes remain serialized by the catalogue database gate.
- Removed the SHA-1 Database tab text about SkeleTool no longer learning hashes.

# DumpToolbox v0.8.41

## Catalogue compile/API fixes

- Fixed SharpSevenZip integration to match the actual 2.0.109 API: the extractor has no `Canceled` property, so archive operations now honour the cancellation token before and after the library call instead of referencing a nonexistent member.
- Fixed the async SQLite transaction type returned by `BeginTransactionAsync` by explicitly using the provider `SqliteTransaction` required by `SqliteCommand.Transaction`.
- Re-audited the new archive backend against the current SharpSevenZip API surface; `ExtractFileAsync`, `ExtractArchiveAsync`, `ArchiveFileData`, `PreserveDirectoryStructure` and `SetLibraryPath` usages are retained.

# DumpToolbox v0.8.40

## Bundled archive engine

- Removed the SkeleTool catalogue's external `7z.exe` / `7zz.exe` process dependency.
- Added `SharpSevenZip` 2.0.109 as the managed archive API and `7z.Libs` 26.2.0 as the full native 7-Zip engine.
- The win-x64 `7z.dll` is embedded as a DumpToolbox.Core resource and extracted to DumpToolbox's private temporary native cache on first archive use, so no separately installed 7-Zip is required and the normal single-file publish remains self-contained from the user's point of view.
- ZIP/TorrentZip, solid 7z, RAR, Zstandard, XZ, GZip, BZip2, TAR and other formats understood by the full 7-Zip engine now use the same in-process backend.
- Archive-backed ISO/UDF filesystem fallback and on-demand single-entry materialization use the same bundled engine.
- Fixed the v0.8.39 catalogue image insertion path to use the intended single SQLite transaction per image/filesystem instead of the removed per-row helper calls.

# DumpToolbox v0.8.39

## SkeleTool SHA-1 catalogue rewrite

- Replaced the old SkeleTool run-history SHA-1 JSON with an independently maintained SQLite disc-collection catalogue.
- SkeleTool no longer records source-file sightings or successful-rebuild provenance while scanning/reconstructing.
- `Settings > SHA-1 Database` now manages registered CD/DVD image collection folders and shows each folder's last successful scan time.
- Added per-folder and global **Check for changes** scanning with cancellation/progress.
- Collection scans discover uncompressed `.iso`/`.bin` images plus images inside ZIP/7z/RAR/Zstandard and common compressed/tar archive forms. Archive extraction uses the in-process SharpSevenZip wrapper and the bundled full 7-Zip engine.
- CUE files are parsed and only CUE-declared data tracks are filesystem-scanned; audio tracks are ignored. Single-file mixed-mode data tracks are isolated using their CUE INDEX geometry.
- Every source archive and every direct disc image is SHA-1 identified. An unchanged size/timestamp skips immediately; a moved/renamed source with an already-known whole-source SHA-1 is relinked without rescanning the disc filesystem.
- Files inside each detected disc filesystem are stored with source/parent-archive provenance, disc-relative pathname, byte size, SHA-1 and scan timestamp. ISO9660 files retain their image LBA when available.
- Removed sources are retained historically and marked not-present instead of deleting their hashes. A successfully enumerated registered root is required before absence is recorded, so disconnected/inaccessible roots do not invalidate the catalogue.
- If a historical source reappears with the same whole-source SHA-1 it becomes present again without rescanning its contained files.
- SkeleTool now consults only currently present, active catalogue sources. Explicit source folders/images supplied in the SkeleTool UI always override catalogue matches.
- Present uncompressed images are preferred ahead of compressed archive sources. Archive-backed images are materialized only when a skeleton actually needs one of their hashes.
- Added ISO/UDF-style fallback extraction through 7-Zip when the built-in ISO9660 reader cannot enumerate an image.
- Added `Microsoft.Data.Sqlite` for the portable `skeletool_sha1_catalogue.sqlite` database.

## v0.8.38

- FindCRCs partial inspection now covers **all unmatched AUDIO targets** when safe boundaries exist, rather than only the extreme audio tracks.
- Internal unmatched audio tracks with both immediate neighbours hash-matched now get a verified bounded source extent.
- Exact-sized internal extents run the existing signed zero-silence shift recovery.
- Short internal extents run exhaustive start/end zero-padding splits and the existing safe signed silence-shift follow-up.
- If recovery still fails, the bounded `.partial` is saved; short internal extents can additionally save `.forward.partial` and `.backward.partial` target-sized neighbour-anchored hypotheses when source geometry permits.
- Oversized internal extents are not destructively trimmed: the ordinary alignment-1 FindCRCs scan has already tested every target-sized source window, so they fall back to inspection output unless a verified recovery exists.

## v0.8.37
- Fixed CDI2BIN Windows output commit failures caused by renaming `.partial` BIN/SUB/ISO files while their `FileShare.None` streams were still open.
- Audited and fixed the same stream-lifetime bug in NRG2BIN for both CD BIN/CUE/SUB conversion and DVD ISO conversion.
- Conversion streams are now flushed and disposed before completed temporary files are renamed into place.

## v0.8.36

- Fixed DiscJuggler CDI 3.5 descriptor parsing using a real `0x80000006` multisession image (`Klax (USA) (Unl)`).
- CDI 3.5 track trailers are handled as 12 bytes shorter than the older descriptor layout, preventing the parser from stepping over the next track marker.
- CDI 3.5 inter-session descriptor blocks are handled as 13 bytes rather than the older single-byte separator.
- Corrected CDI stored session/track ordinals: session numbers are zero-based in the descriptor and track ordinals are session-local; generated CUE track numbering remains disc-global and sequential.

## 0.8.35

- NRG2BIN and CDI2BIN are now media-aware: CD images remain 2352-byte BIN/CUE conversions, while detected DVD images are written as native 2048-byte ISO images.
- NRG DVD detection uses Nero MTYP metadata (including known legacy DVD values) with a conservative capacity fallback when MTYP is absent/unrecognized.
- CDI DVD detection is conservative: a single native 2048-byte Mode 1 data track that exceeds normal CD capacity is treated as DVD. Smaller CDI data images remain CD unless a future CDI medium marker is established from samples.
- DVD conversion disables CUE/subchannel/FindCRCs controls and changes the output label/path to ISO after analysis. No synthetic 2352-byte CD framing is generated for DVD sectors.

## 0.8.34

- Fixed CDI2BIN compile error CS4012 by removing the `ReadOnlySpan<byte>` local from the asynchronous conversion loop.
- CDI sector parsing/conversion behavior is unchanged; synchronous span views are now passed directly to the existing sector builders and raw sectors use `Buffer.BlockCopy`.

## 0.8.33

- Added **CDI2BIN** under Convert for Padus DiscJuggler `.cdi` images (not Philips CD-i).
- Parses DiscJuggler CDI v2.0/v3.0/v3.5 trailing session/track descriptors and preserves multisession track payloads in a 2352-byte BIN plus CUE.
- Supports 2048-byte cooked, 2336-byte Mode 2 body and 2352-byte raw/audio storage, plus conservative geometry-based recognition of 2368-byte RAW-PQ and 2448-byte RAW-P-W sectors.
- Optional 96-byte `.sub` extraction is available for full 2448-byte P-W data and defaults off. 2368-byte PQ-only subcode is never padded/fabricated into a standard `.sub`.
- CDI2BIN paths/options participate in DumpToolbox.ini persistence, reset and per-tool clear-saved-input behavior.

# DumpToolbox v0.8.32

- Removed the long NRG2BIN capability/explanation text from the converter UI so it no longer consumes the right-hand side of the options row.
- Moved NRG2BIN format/support details into `README.md` and retained the fuller technical notes in `NRG2BIN.md`.

# DumpToolbox v0.8.31

- NRG2BIN subchannel output is now optional, matching MDF2BIN behaviour. The new **Also save 96-byte subchannel as .sub** option defaults to off and is persisted as `NRG2BIN.SaveSubchannel`.
- 2448-byte NRG sectors are always converted to 2352-byte BIN main-channel sectors; when SUB output is disabled the stored 96-byte subchannel portion is intentionally omitted.
- When SUB output is enabled, v0.8.30's lossless `.sub` extraction and mixed-track alignment behaviour is retained.

# DumpToolbox v0.8.30

- NRG2BIN now supports Nero 2448-byte raw sectors with stored 96-byte subchannel data. Main-channel bytes are written to the 2352-byte BIN and subchannel bytes are preserved in a companion `.sub` file.
- Mixed subchannel/non-subchannel NRGs remain sector-aligned by emitting zero SUB placeholders only for sectors where the NRG stores no subchannel bytes, with an explicit warning.
- NRG2BIN now parses repeated SINF + DAOI/DAOX or ETNF/ETN2 session geometry and converts all unambiguous sessions instead of rejecting multisession images.
- Generated CUE files retain session numbers and original track LBAs as REM metadata; physical session lead-in/lead-out sectors are not fabricated because standard CUE syntax cannot represent them.
- SINF track counts are validated against their corresponding session geometry. Ambiguous session-to-geometry layouts still fail explicitly.

# DumpToolbox v0.8.29

- Added **NRG2BIN** under Convert. It parses Nero NRG metadata from the trailing `NERO` (v1/32-bit) or `NER5` (v2/64-bit) footer and IFF-style chunk chain rather than assuming a fixed header.
- Supports conservative single-session CD DAO (`DAOI`/`DAOX`) and TAO (`ETNF`/`ETN2`) layouts when track geometry is explicit.
- Emits a conventional 2352-byte BIN plus CUE. Raw Mode 1/Mode 2 and audio sectors are copied; cooked 2048-byte Mode 1 and Mode 2 Form 1 sectors are expanded with regenerated sync/MSF/EDC/ECC.
- Rejects multisession, 2448-byte/interleaved-subchannel and ambiguous/unsupported layouts rather than silently discarding data or guessing.
- NRG2BIN paths and the optional “Use resulting BIN as FindCRCs source” preference participate in DumpToolbox.ini persistence and per-tool clear/reset behaviour.

## v0.8.28
- Generalizes the conservative Joliet/ISO9660 Level-1 collision-family resolver from same-size groups to complete sibling-directory allocator sequences. Rank resolution now requires equal source/target counts, unique target extents, an actual Level-1 projection collision, direct-projection anchors bracketing the displaced run, and exact source/target size agreement at every rank. This covers mixed-size alias sequences such as the Set V `MEDIA_29.HTM` case without weakening the ordinary projection rule.
- Disables the default OMI QuickTopix 2.0.3 = 128-sector EOF-slack observation and demotes it to LOW confidence because Set V demonstrates that the visible 2.0.3 mastering signature does not uniquely imply that fixed delta. QuickTopix 2.20 remains unchanged.

## v0.8.27

- Fixed the Settings > General menu-layout selector so Horizontal tabs and Vertical tabs remain mutually exclusive after changing the main TabControl placement.
- Switching back from Vertical tabs to Horizontal tabs now works immediately and persists normally.

## v0.8.26

- Added a persistent **Menu layout** preference under **Settings > General**.
- **Horizontal tabs** remains the default for new and reset configurations.
- Optional **Vertical tabs** places the main DumpToolbox tool tabs down the left side while leaving nested/sub-tool tabs, including Settings > General / SHA-1 Database, horizontal.
- The choice is stored as `Settings.MenuLayout` in `DumpToolbox.ini`; existing INIs without the key retain the horizontal default.

## v0.8.25

- Added a conservative Joliet/ISO9660 Level-1 collision-family resolver for sequential alias shifts such as Ultimate Solitaire 1000 card0_c1/card0_c10/card0_c2.
- The resolver requires one complete same-parent/same-size family, an actual duplicate Level-1 projection collision, equal source/target family cardinality, and at least two rank-consistent direct-projection anchors before pairing source lexical rank with target physical extent rank.
- This resolver runs before ordinary Joliet Level-1 projection+size matching so shifted siblings cannot be prematurely claimed by superficially matching names.
- Added enabled EOF seed rule: Tempra CD-Producer 1.2b -> 31 sectors (HIGH).
- Added a second enabled observation for Easy CD Creator 5.3 (158) + CD-RTOS CD-BRIDGE -> 2592 sectors (HIGH), alongside the existing 10-sector observation so the ambiguity/hash-verification workflow can choose safely.
- EOF seed rules remain note-free. Existing external `EOFSlackRules.ini` files are not overwritten automatically.

## v0.8.24

- Reorganised the Settings page into nested `General` and `SHA-1 Database` tabs.
- Existing theme, path, configuration-maintenance and About controls now live under `Settings > General`.
- Moved the SkeleTool local SHA-1 database preference to `Settings > SHA-1 Database`.
- Local SHA-1 database use is now opt-in and defaults to disabled for new or reset `DumpToolbox.ini` files.
- Existing saved SHA-1 database choices, including the legacy SkeleTool setting, are preserved during upgrade.

## v0.8.23

- Added Settings buttons to reset/recreate `DumpToolbox.ini` and `EOFSlackRules.ini`.
- Both destructive reset actions require explicit confirmation and explain exactly which custom settings will be lost.
- Resetting `DumpToolbox.ini` restores runtime defaults without touching custom EOF rules.
- Resetting `EOFSlackRules.ini` recreates the current embedded seed without touching user settings.
- Removed the old misplaced `Reset all settings` button from FindCRCs.

## v0.8.22

- Adds the newly validated Easy CD Creator 5.1 (079) + `CD-RTOS CD-BRIDGE` EOF-slack seed observation at 2592 sectors with HIGH confidence.
- Retains the existing Roxio Burn Engine 2.1 5120-sector HIGH-confidence seed rule; it was already present in v0.8.21, so no duplicate rule was added.
- EOF seed rules remain note-free. Existing external `EOFSlackRules.ini` files are not overwritten; delete or rename one to regenerate the current seed.

## v0.8.21

- Fix DIC Activity Log header grid column definition so the Show/Open Log and Dock/Undock controls no longer overlap after moving Verbose into the workspace header.

## v0.8.20 - DIC verbose control cleanup

- Moves the DIC verbose logging option from the crowded main action row into the Activity log workspace header.
- Shortens the visible label from `Verbose logging` to `Verbose` while preserving the existing setting and diagnostic behaviour.

## v0.8.19 - compact native vector icon pass

- Added Avalonia `PathIcon` vector icons to common secondary actions without adding a third-party icon package.
- Replaced Browse buttons with compact folder icons and tooltips across the UI.
- Replaced Clear saved inputs buttons with compact trash icons and tooltips.
- Added refresh icons to SkeleTool/DIC force-rehash options.
- Added stateful eye/window icons to the SkeleTool and DIC activity-log Hide/Show and Dock/Undock controls while retaining their changing text labels.
- Added a shared compact `icon-button` style so icon-only actions use consistent sizing and remain theme-aware.
- Primary workflow actions such as Load, Scan, Search, Resurrect and Rebuild retain text labels.

# v0.8.17

## v0.8.18 - responsive activity-log controls and SkeleTool log surface

- Moved SkeleTool and DIC activity-log Hide/Show and Dock/Undock controls out of the crowded action rows and into the workspace header, where they remain reachable at narrow window widths.
- Shortened the activity-log control captions to preserve space on smaller displays.
- Gave the SkeleTool docked activity log an explicit bordered output surface. In Custom theme mode it now uses the configured Input / logs colour and accent border instead of blending into the main background.
- Preserved the v0.8.17 dock/undock behaviour and accumulated-log handling.

- SkeleTool and DIC activity logs can now be hidden, giving the filesystem/match tree the full workspace width.
- Added draggable splitters for the docked activity-log panes.
- SkeleTool and DIC activity logs can be undocked into independent resizable windows and docked back later.
- Closing an undocked log window does not stop logging or discard accumulated output; reopening restores the complete log.
- Detached activity-log windows inherit the active custom theme.

# v0.8.16

- Custom theme now propagates to About, EOF ambiguity, and other dynamically-created message windows.
- Added a fourth Custom theme colour for input/content surfaces used by path fields, logs, list boxes and tree views.
- Persists the new colour as `Settings/CustomInput` in `DumpToolbox.ini`.

# v0.8.16 — Custom colour theme

- Expanded Settings > Theme to System, Dark, Light and Custom.
- Custom theme provides colour pickers for background, text and accent/highlight colours.
- Custom colours apply immediately and are persisted in DumpToolbox.ini.
- The underlying Fluent light/dark variant is chosen automatically from the custom background luminance so menus and flyouts retain appropriate contrast.
- Returning to System, Dark or Light removes the custom accent override and restores normal Avalonia theme behaviour.

# v0.8.14 — Warning cleanup / EOF seed cleanup / MDF2BIN UI cleanup

- Fixed nullable warning CS8600 in the ambiguous EOF-slack selection path by keeping the `FirstOrDefault` result nullable until it is checked.
- Removed `Notes` from the EOF slack rule model/parser and removed all `Note=`/`Notes=` entries from the first-run seed. Seed rules are intentionally note-free going forward.
- Removed both informational text blocks from the MDF2BIN tab and compacted the layout.
- Retains the validated v0.8.13 ambiguous EOF workflow unchanged.

# v0.8.13 — Ambiguous EOF-slack observations

- EOFSlackRules.ini FormatVersion 4 permits multiple enabled rules to intentionally match the same mastering signature.
- Added both observed EOF modes for conflicting Easy CD Creator signatures instead of forcing one global offset:
  - Easy CD Creator 5.0 (352) + CD-RTOS CD-BRIDGE: 10 and 2688 sectors.
  - Easy CD Creator 5.3 (010) + blank System ID: 10 and 3072 sectors.
  - Easy CD Creator 5.3 (034) + blank System ID: 10 and 3072 sectors.
- When multiple EOF rules match, SkeleTool/DIC asks which observed mode to apply.
- When expected destination CRC32/MD5/SHA-1 values are available, the dialog offers to try every matching observation and retain only the candidate that reproduces all available expected hashes.
- Failed trials restore the pre-EOF-remediation image before testing the next candidate; if none match, EOF slack remains at the normal zero-filled baseline.
- Existing external EOFSlackRules.ini files remain untouched; delete/rename one to regenerate the v4 seed.

# v0.8.12 — expanded EOF-slack mastering seed rules

- Extends EOFSlackRules.ini matching with optional `DataPreparerContains`, so mastering tools that identify themselves in ISO9660 PVD Data Preparer ID can be matched safely.
- Corrects Easy CD Creator 5.0 (352) + CD-RTOS CD-BRIDGE from 2688 to 10 sectors after C-cohort all-hit validation (95/95 unique EOF targets; 2688 absent).
- Adds Easy CD Creator 5.3 (010) + CD-RTOS CD-BRIDGE = 2592 sectors (12/12 across two discs).
- Adds CD-Producer v1.4/v1.7/v1.8 = 31 sectors.
- Adds OMI QuickTopix 2.0.3/2.20 = 128 sectors.
- Adds Roxio Burn Engine 3.0 = 5120 sectors.
- Existing external EOFSlackRules.ini files remain untouched; delete/rename one to regenerate the new seed.

# v0.8.11 — validated Easy CD Creator EOF-slack seed rules

- Updated only the first-run `EOFSlackRules.ini` embedded seed; EOF-slack reconstruction remains runtime-configurable and no mastering offsets are hard-coded into DIC/SkeleTool logic.
- Corrected `EASY CD CREATOR 5.3 (010)` + blank System ID from 10 to **3072 sectors**, based on 17/17 unique EOF targets containing an exact -3072-sector source match.
- Corrected `EASY CD CREATOR 5.3 (034)` + blank System ID from 10 to **3072 sectors**, based on 15/15 unique EOF targets containing an exact -3072-sector source match.
- Promoted `EASY CD CREATOR 5.0 (306)` + `CD-RTOS CD-BRIDGE` at **2688 sectors** to HIGH confidence (106/106 unique EOF targets).
- Promoted `EASY CD CREATOR 5.0 (314)` + blank System ID at **2976 sectors** to HIGH confidence (14/14 unique EOF targets).
- Retained the already-seeded `EASY CD CREATOR 5.0 (352)` + blank System ID at **2976 sectors**, now independently validated across 861/861 unique EOF targets.
- Added `EASY CD CREATOR 6.2 (134)` + blank System ID at **3072 sectors**, HIGH confidence (20/20 unique EOF targets).
- Existing external `EOFSlackRules.ini` files remain untouched. Delete or rename an existing file if you want DumpToolbox to regenerate the new defaults on next launch.

# DumpToolbox v0.8.10

## DIC iterative parent-directory matching — five-set validation

- Retains the v0.8.9 deterministic parent-directory/source-matching fixpoint unchanged after validation against the PC A, U, V, W and XYZ oracle corpora.
- The rule sequence remains: strong/direct evidence -> rebuild mutual-unique parent map -> restrict unresolved children -> size/timestamp uniqueness -> strict local alias tie-break -> residual mutual uniqueness -> repeat to fixpoint.
- Reapplying the parent-restricted elimination to the FINAL simulator graphs found only 5 additional singleton mappings across the five corpora (U: 4, V: 1; A/W/XYZ: 0); all 5 were oracle-correct. This is consistent with the simulator already converging almost completely before FINAL.
- No global lexical, reverse-lexical, directory-record-order or extent-order family guess has been enabled in production matching. Those remain diagnostic-only until a safe per-family classifier is established.
- This checkpoint therefore promotes the deterministic fixpoint after broader corpus validation without widening its evidence requirements.

# DumpToolbox v0.8.9

## DIC Joliet/source matching

- Adds `WINDOWS_NT_HASHED_83_FROM_PROVEN_DISC_PROFILE`: after a disc has already proved that it uses the Windows NT checksum-form 8.3 namespace, an exact deterministic hashed leaf may cross an unresolved parent-directory alias when size, recording timestamp, availability and uniqueness agree.
- Adds a fixpoint parent-directory rescan. A successfully proved child mapping establishes a mutual-unique primary-ISO/Joliet-source parent correspondence; unresolved siblings in that folder are immediately retried.
- Adds `PROVEN_PARENT_RESCAN_SIZE_TIMESTAMP`: within a proved parent pair, exact size plus compatible DIC recording timestamp may identify one remaining source uniquely.
- Adds `PROVEN_PARENT_STRICT_ALIAS_SIZE_TIMESTAMP`: when size/time still leave multiple siblings, a strict local `~N` family-prefix relation may break the tie without assuming lexical alias ordering.
- Adds `RESIDUAL_MUTUAL_UNIQUE_SIZE_TIMESTAMP` as the final fallback after stronger rules have reached their fixpoint. It requires exact size, compatible recording timestamp and bidirectional uniqueness among the remaining targets/sources.
- New match methods are trusted as proven source-relative Joliet identity for supplementary-filesystem reconstruction.
- No broad lexical `~N` ordering rule was added; corpus mining showed that generalisation can create incorrect mappings.

# DumpToolbox v0.8.8

- Refactor only: split `DicTab.cs` into focused partial files for load/import, matching, resurrection/state, tree/status, and logging responsibilities.
- Preserves all existing XAML event-handler names and DIC workflow behavior.
- No intended changes to reconstruction, Joliet matching, mastering EOF rules, source matching, or verification.

# v0.8.8

- UI partial cleanup: split legacy `UtilityTabs.cs` into feature-specific `MainWindow.HashCalc.cs`, `MainWindow.Base64.cs`, `MainWindow.FindEnds.cs`, and `MainWindow.Utilities.cs`.
- No intended functional changes. MDF2BIN, Audio Recovery, and ISO Extractor were already in their own partial files and remain unchanged.

# v0.8.6

- Refactor only: split `MainWindow.axaml.cs` into workflow-specific partial classes.
- Preserve XAML event-handler names/signatures and shared state.
- No intended reconstruction, matching, mastering, hashing, or UI behavior changes.

# v0.8.5

- Continued no-behaviour-change modular cleanup of `SkeletonResurrectionService`.
- Split inspection, source matching, resurrection/verification, and ISO-image inspection/IO into dedicated partial-class files.
- Kept all reconstruction algorithms and rule ordering intact.

# DumpToolbox v0.8.5

- Refactor-only release based on the confirmed-compiling v0.8.3 checkpoint.
- Split `DicDonorImageService` into focused partial modules for payload/source handling, metadata application, ISO9660/Joliet parsing, and cooked/raw donor image I/O.
- No intended changes to donor matching, ISO extraction, Joliet correspondence, payload eligibility, raw/cooked sector handling, or same-disc metadata authority.
- Updated application version/User-Agent to 0.8.5.

# DumpToolbox v0.8.3

Structural cleanup only; no intended behavioural changes.

- Split `Iso2BinService` into focused partial modules for CUE parsing/layout, raw-sector construction/ECC-EDC, XA metadata parsing, and I/O helpers.
- Kept public conversion/inspection orchestration in `Iso2BinService.cs`.
- Preserved existing nested types, constants, regexes, conversion ordering, and error handling.
- Updated application version/User-Agent to 0.8.3.

# DumpToolbox v0.8.2

## EdgeRecoveryService modularisation

- No intended recovery, matching, mastering, or UI behaviour changes.
- Split `EdgeRecoveryService.cs` into focused partial modules:
  - `EdgeRecoveryService.cs` — public orchestration and CUE/audio-edge decision flow.
  - `EdgeRecoveryService.SingleAudio.cs` — singleton-audio extent inference, short-audio zero-padding, and padding/shift candidate generation.
  - `EdgeRecoveryService.SilenceShift.cs` — silence-boundary shift and trailing-overage recovery.
  - `EdgeRecoveryService.IOAndVerification.cs` — generic edge repair, partial-output handling, copy/zero helpers, hashing/verification, output naming and cleanup.
- Removed `SkeletonResurrectionService.RawPhysicalOffset`, a private helper with no call sites anywhere in the C# source tree.
- Avalonia/XAML event handlers remain excluded from dead-code removal.

# DumpToolbox v0.8.1

## DicLogImportService modularisation

- No intended reconstruction, matching, mastering, or UI behaviour changes.
- Split the former ~8,500-line `DicLogImportService.cs` into partial modules with explicit responsibilities:
  - `DicLogImportService.JolietIdentity.cs` — matched-source/Joliet identity and primary-directory metadata handling.
  - `DicLogImportService.LogParsers.cs` — DIC volume/disc/DAT/EccEdc/mainInfo parsing and raw evidence decoding.
  - `DicLogImportService.JolietSynthesis.cs` — ISO path-table and Joliet metadata synthesis.
  - `DicLogImportService.ContentAndSlack.cs` — donor requirements, content entries, EOF slack and unclaimed-volume analysis.
  - `DicLogImportService.Hfs.cs` — Apple partition map and classic-HFS inspection/synthesis helpers.
  - `DicLogImportService.RecoveryAndSkeleton.cs` — recovery coverage, optional exactness donors, and synthetic skeleton construction.
  - `DicLogImportService.Models.cs` — private parser/synthesis records and helper buffer types.
- `DicLogImportService.cs` is now the orchestration/import entry point and shared constants/regex state.
- Removed `SkeletonResurrectionService.PatchRawExtentAsync`, a superseded private implementation with no call sites anywhere in the source tree.
- Event handlers referenced from Avalonia XAML were deliberately excluded from the unused-method cleanup even when they have no C# call site.

# DumpToolbox v0.8.0

- No intended behavioural changes from the confirmed v0.7.99 baseline.
- Started the modularisation pass by splitting `SkeletonResurrectionService` into partial-class modules for Joliet/source-name logic, raw CD sector framing/ECC/EDC logic, and source hash-cache logic.
- Removed private methods proven to have no call sites in the source tree (legacy resurrection helpers, old DIC path helpers, and superseded hash-search implementations).
- Preserved the existing public API, reconstruction rule order, mastering rules, Joliet matching behaviour, and UI workflow.
- v0.7.99 remains the stable pre-refactor rollback baseline.

# v0.7.99

- Compile-only fix for the v0.7.92/v0.7.98 zero-based Joliet ordinal-family code.
- Let the compiler infer the anonymous family-source projection type instead of incorrectly declaring it as `FileInfo[]`.
- No reconstruction, EOF-slack, naming, UI, or other behavioural changes from v0.7.98.

# v0.7.98

- Removed the Dataware-specific v0.7.93-v0.7.97 experimental recovery/popup path by rebasing this build on v0.7.92.
- Added PC XYZ Easy CD Creator EOF-slack signatures, including 5.0(352) environment split, 5.3(031), 5.3(034) blank, and 6.1(007) blank.
- Added deterministic Windows checksum 8.3 path-chain matching for hashed parent directories.
- Added deterministic PREFIX3_HEX_ORDINAL path-chain matching for directory/file hierarchies such as Xbox press-kit trees.

# v0.7.92

- Extends the seeded external EOF-slack rule database with newly evidenced Easy CD Creator cohorts: 5.0 (310) CD-BRIDGE -> 2688, 5.0 (314) CD-BRIDGE -> 2688, 5.3 (034) CD-BRIDGE -> 2592, 5.3 (060) CD-BRIDGE -> 2592, 5.3 (158) CD-BRIDGE -> 10, and 6.0 (210) blank -> 10 sectors.
- Raises the already heavily confirmed 5.3 (071) blank rule to HIGH confidence.
- Joliet matching: adds a strict closed-family zero-based terminal ordinal rule for patterns such as HIGHLI~1/~2 -> highlight0/highlight1.
- Joliet matching: timestamp fallback can no longer manufacture a singleton merely because sibling candidates were already consumed inside a numeric ~N family.
- Joliet matching: lexical alias-rank inference is now interpolation-only; an unresolved alias must be bracketed by proven family anchors on both sides. This prevents the Wall Street Tycoon wrong-mapping cascade.

# DumpToolbox v0.7.91

- Synchronises the embedded DIC recovery engine with standalone DICRecovery v0.3.5.4 (the accepted baseline; rejected v0.3.5.5 experiments are not included).
- Ports the newer mastering-profile architecture and conservative Joliet mapping rules, including Windows NT hashed 8.3, affine alias-index and lexical alias-rank family inference.
- Ports HFS hybrid inspection/phase-1 reconstruction improvements, including the corrected big-endian UInt32 writer.
- Ports v0.3.5.4 state persistence so applied entries retain proven source-relative/Joliet identity even after the original source file is moved or deleted.
- Ports the newer ISO Extractor: Joliet-visible extraction when safely mapped, primary/Joliet record identity in manifest v2, directory-record ordering evidence, associated/colliding-record preservation, and payload-only cross-pressing compatibility.
- Keeps DumpToolbox-specific EOFSlackRules.ini behaviour and all post-v0.7.75 toolbox changes.
- Replaces UI-thread verbose-log batching with a dedicated background DIC log pump. Recovery workers only enqueue log lines; a background consumer coalesces them and streams bounded batches to Avalonia at Background priority.

# DumpToolbox v0.7.90

- DIC verbose logging no longer updates the Avalonia TextBox once per forensic audit line.
- `VERBOSE DIC` lines are queued and flushed to the UI in 250 ms batches, reducing repeated full-text copies/layout passes that could make the frontend appear hung.
- Normal DIC progress, warning and error messages remain immediate; any pending verbose lines are flushed first to preserve ordering.
- The complete verbose log is retained; this is a presentation/performance change only and does not alter DIC recovery decisions.

# DumpToolbox v0.7.89

- SkeleTool Redumper DAT verification now uses true fixed columns: metric, `Expected:`/`Actual:`, and value positions align in the monospaced activity log.
- Removed the awkward padded-colon form (`Actual  :`).
- SkeleTool activity log now renders a terminal `MATCH` token in green and `MISMATCH` in red, without colouring the hash/value text.
- Overall Redumper DAT verification failure text is now `MISMATCH` for consistency with per-hash results.

# DumpToolbox v0.7.88

- Restores XML DAT parsing in the shared hash-target parser used by FindCRCs and Audio (and available to ISO2BIN where it uses the shared parser).
- Accepts complete XML DATs, `<game>...</game>` / `<machine>...</machine>` fragments, or bare `<rom .../>` entries.
- Reads `name`, `size`, `crc`, `md5`, and `sha1` attributes regardless of attribute order or single/double quote style.
- XML `.cue` ROM entries are ignored as metadata rather than payload targets.
- Existing plain hash rows and Redump disc URL / numeric-ID importing remain unchanged.

# DumpToolbox v0.7.87

- ISO2BIN Redump target box now accepts a full `redump.info/disc/<id>` URL or a bare numeric disc ID.
- Reuses the shared Redump importer used by FindCRCs and Audio.
- For multi-track/mixed-mode Redump entries, ISO2BIN uses the downloaded CUE to select the non-AUDIO/data track and imports that track's destination filename and hashes.
- Existing manually pasted single-target rows remain unchanged.

# DumpToolbox v0.7.86

- Audio tab hash input now accepts a full `redump.info/disc/<id>` URL or a bare numeric Redump disc ID.
- Reuses the shared Redump importer introduced for FindCRCs.
- When Redump CUE data is available, the Audio tab automatically filters the imported file/hash rows to tracks declared as `AUDIO` in the CUE, preserving Redump destination filenames.
- If CUE retrieval is unavailable, all payload rows are imported and the log explicitly warns that audio/data classification could not be performed.

# DumpToolbox v0.7.85

## FindCRCs mixed-mode Track 02 anchor priority + Redump page import

- Mixed-mode recovery now treats a verified Track 03 as the primary anchor for an unmatched/short Track 02. The backwards-from-Track-03 missing-start interpretation is tried before the weaker Track-01-forward interpretation; Track 01 is only a fallback when the Track 03 hypothesis does not verify.
- The FindCRCs Hash targets box now accepts either a full public Redump disc URL (for example `https://redump.info/disc/118856`) or just the numeric disc ID (`118856`).
- On Search, DumpToolbox downloads the public Redump disc page, imports the `.bin` payload filenames/sizes/CRC32/MD5/SHA-1 values, and uses those filenames as extraction destinations.
- DumpToolbox also requests Redump's public `/cue` representation and, when available, writes it to a temporary CUE file and feeds it through the existing CUE analysis path automatically. This preserves Redump track/pregap/index information without requiring the user to paste a CUE manually.
- If the CUE endpoint is unavailable, imported hashes remain usable and the log explicitly says the scan is continuing without imported CUE information.

# DumpToolbox v0.7.84.1

- Fixes the Track 2 long-pregap recovery temp-file sharing failure introduced in v0.7.84.
- The synthesized scrambled-sector candidate writer is now flushed and disposed before the candidate is reopened for CRC32/MD5 verification.
- Recovery logic and pregap interpretation are otherwise unchanged from v0.7.84.

# v0.7.84.1

- Corrects the long Track 02 pregap recovery introduced in v0.7.83. For `00:02:01`, `00:02:02`, etc., the prepended scrambled data sectors are no longer copied from the final Track 01 sector(s). DumpToolbox now synthesizes empty raw data sectors at the **next MSF address(es) after Track 01 ends**, preserving Track 01's data mode/XA framing where applicable, then applies the standard CD scrambling transform before hashing the Track 02 candidate.
- `00:02:01` therefore tests one synthesized sector at `Track 01 end MSF + 1`; `00:02:02` tests the next two consecutive MSFs, and so on.
- The candidate remains strictly CRC32/MD5-gated.

# v0.7.83

- SkeleTool Redumper DAT verification now formats Expected/Actual values with fixed-width labels so hash values align cleanly in the monospaced log.
- DIC tab now exposes the existing verbose diagnostic logging as a persisted `Verbose logging` checkbox (`[DIC] verbose=` in DumpToolbox.ini).
- FindCRCs adds CUE-directed mixed-mode pregap-length recovery under `Attempt to fix under-dumped Audio edges`:
  - Track 02 pregap below 00:02:00: if Track 01 is unmatched, retry Track 01 with the corresponding number of final 2352-byte sectors replaced by zeroes (00:01:74 = one sector, 00:01:73 = two, etc.).
  - Track 02 pregap above 00:02:00: when an ordinary Track 03 match proves Track 02 is short at its beginning and normal zero-silence recovery still fails, prepend scrambled copies of the corresponding final Track 01 raw data sectors, then enough zero bytes to make the exact target size (00:02:01 = one scrambled sector, 00:02:02 = two, etc.).
  - Every candidate remains CRC32/MD5 gated; no speculative result is accepted without the supplied target hash.

# v0.7.82 — EOF-slack seed cleanup + Easy CD Creator 5.3 (010)

- Added seeded external EOF-slack override for `EASY CD CREATOR 5.3 (010)` with blank PVD System ID: `DeltaSectors=10`, `Confidence=HIGH`.
- Removed all `Notes=` fields from the generated `EOFSlackRules.ini` seed template and documentation examples.
- Runtime rule matching remains unchanged: zero-filled EOF slack is the default, and external rules are residue-copy overrides only.
- Existing user `EOFSlackRules.ini` files are still never overwritten automatically.

# v0.7.81 — Additional Easy CD Creator EOF-slack seed rules

- Added three newly observed deterministic Easy CD Creator mastering signatures to the first-run `EOFSlackRules.ini` seed template.
- Easy CD Creator 5.0 (336) + `CD-RTOS CD-BRIDGE`: 2688-sector residue distance (121/121 searchable non-zero tails across 2 discs).
- Easy CD Creator 5.0 (352) + `CD-RTOS CD-BRIDGE`: 10-sector residue distance (412/412 searchable non-zero tails across 3 discs).
- Easy CD Creator 5.3 (158) + blank PVD System ID: 10-sector residue distance (1087/1087 searchable non-zero tails on 1 disc).
- Runtime rule parsing/reconstruction logic is unchanged from v0.7.80.
- Existing user-created `EOFSlackRules.ini` files are not overwritten; the updated rules appear automatically only when the file is first generated.

# v0.7.80 — EOF slack overrides + explicit DAT hash values

- Simplified `EOFSlackRules.ini`: zero-filled EOF slack is now the implicit/default resurrection behaviour.
- Removed `Mode` and all explicit zero-fill rules. Every valid external rule is a `DeltaSectors` residue-copy override.
- No match, disabled/invalid rule, or ambiguous matches leave normal zero-filled EOF slack untouched.
- Removed `EXPERIMENTAL` wording from EOF-slack runtime log messages.
- SkeleTool Redumper DAT verification now always prints Expected and Actual Size/CRC32/MD5/SHA-1 values, including successful matches.
- First-run generated rule template bumped to FormatVersion=2 and contains only observed residue-copy exceptions.

# v0.7.79 — external EOF slack mastering-rule database

- Removed the compiled Easy CD Creator / Roxio EOF-slack signature selectors introduced in the experimental v0.7.77 branch.
- Added runtime `EOFSlackRules.ini` support shared by SkeleTool and DIC resurrection.
- On first run DumpToolbox creates `EOFSlackRules.ini` beside the executable from an embedded default template.
- Pre-populates the external file with all currently observed residue and zero-fill mastering signatures.
- Rules are reloaded for every reconstruction, so edits take effect without rebuilding or restarting the application.
- `ApplicationContains` uses case-insensitive PVD Application-ID matching. `SystemIdMatch=*` matches any System ID; `SystemIdMatch=<blank>` explicitly requires a blank System ID; other values match the trimmed System ID exactly.
- Supported modes are `CopyPreviousSectorTail` and `ZeroFill`.
- Invalid rules are skipped with warnings. Multiple matching enabled rules are treated as ambiguous and none are applied.
- Retains v0.7.78 SkeleTool Redumper DAT final-image verification.

# DumpToolbox v0.7.78 — EXPERIMENTAL mastering EOF rules + SkeleTool Redumper DAT verification

- Continues the v0.7.77 experimental mastering EOF-residue branch.
- SkeleTool now searches for Redumper `.log` files beside the selected `.skeleton` and parses their `dat:` `<rom ... size/crc/md5/sha1>` entries.
- When a log contains multiple DAT entries/tracks, the expected image is selected by exact skeleton byte size; ambiguous equal-sized entries are disambiguated by skeleton/DAT basename when possible, otherwise verification is skipped rather than guessed.
- After resurrection completes, SkeleTool automatically calculates CRC32, MD5 and SHA-1 for the rebuilt image and compares them with the selected DAT entry.
- Verification reports `MATCH` or `FAILED` with per-hash expected/actual values. A failed verification deliberately leaves the rebuilt image in place for diagnosis.
- DIC recovery behaviour is unchanged by this DAT-verification addition.

# DumpToolbox v0.7.77 — EXPERIMENTAL mastering EOF-residue signatures

- Branches from the v0.7.75 active baseline. The rejected generic v0.7.76 Easy CD Creator 6 MiB rule is not used.
- Adds an experimental post-resurrection mastering pass shared by SkeleTool and the built-in DIC recovery path.
- Rules are gated by exact ISO9660 PVD application/version strings plus System ID where the corpus showed it mattered.
- Evidence-backed residue deltas currently encoded:
  - Easy CD Creator 4.2 (292) + `CD-RTOS CD-BRIDGE`: 24 sectors (LOW confidence; 8/9 tails on one disc).
  - Easy CD Creator 5.0 (306) + `CD-RTOS CD-BRIDGE`: 2688 sectors (LOW; 3/3 on one disc).
  - Easy CD Creator 5.0 (314) + blank System ID: 2976 sectors (MEDIUM; 26/26 on one disc).
  - Easy CD Creator 5.2 (056) + `CD-RTOS CD-BRIDGE`: 2592 sectors (HIGH; 105/105 across three discs).
  - Easy CD Creator 5.2 (056) + blank System ID: 3072 sectors (MEDIUM; 24/24 on one disc).
  - Easy CD Creator 5.3 (071) + blank System ID: 3072 sectors (MEDIUM; 21/21 on one disc).
  - Easy CD Creator 6.0 (171) + blank System ID: 3072 sectors (MEDIUM; 11/11 on the directly compared Winter Sports Extreme / Snow Extreme disc).
  - Easy CD Creator 6.1 (048) + blank System ID: 3072 sectors (MEDIUM; 124/124 on one disc).
  - Roxio Burn Engine 2.1: 5120 sectors (HIGH; dominant signature on two independent discs).
- Explicit zero-fill observations are logged but remain no-ops for Easy CD Creator 4.0 (140), 4.1 (202), 4.2 (285), 4.2 (310), and 4.5 (409).
- Only ordinary contiguous 2048-byte logical file extents are eligible. XA/Form2/interleaved layouts are skipped.
- Raw 2352 targets have EDC/ECC regenerated after tail insertion; DIC exact-sector/fill recipes are reapplied afterward and remain stronger evidence.
- Every selected rule and every modified EOF sector is logged with confidence, source LBA, delta, and byte count.

# DumpToolbox v0.7.75

- Zero-length ordinary files now display the same green satisfied tick (`✓0`) in the DIC and SkeleTool file explorers instead of the neutral empty-set symbol.
- DIC source scans now retain an actual zero-byte source file as pathname/Joliet identity evidence when a unique exact relative-path match exists. No hashing or payload copy is performed.
- Zero-byte identity matches are excluded from queued-payload and newly-added-payload counters, so source accounting continues to reflect only files that contain data.
- The existing conservative primary-name fallback remains available only when no real zero-byte source pathname is found.

# v0.7.74

- DIC Joliet reconstruction no longer fails solely because a genuine zero-length file has no extracted source pathname.
- For zero-length files only, a clean primary ISO9660 pathname with no `~` short-name alias may be used as a conservative Joliet-name fallback.
- The fallback is logged explicitly and does not weaken pathname requirements for ordinary non-empty files or short-name aliases.

# DumpToolbox v0.7.73

- DIC zero-length ordinary ISO9660 files are now metadata-only entries from import onward: `DataLength == 0` intrinsically marks an ordinary entry empty, `RequiresSource` is false, and they are excluded from required-source/hash counts while their directory records remain reconstructed.
- DIC import emits an explicit warning/count for zero-length ordinary files so cases such as `ALONE_CD1.DAT` are visible without being treated as missing payloads.
- Removed the visible **Verbose DIC logging** checkbox from the DIC tab. Diagnostic logging is now hidden and disabled by default. Enable it manually in `DumpToolbox.ini` with `verbose=1` under `[DIC]` (a bare top-level `verbose=1`/General key is also accepted).
- The obsolete `[DIC] VerboseLogging` checkbox setting is no longer read and is removed on the next settings save.

# DumpToolbox v0.7.72

- Compile fix for v0.7.71 supplementary/Joliet path-table geometry support.
- Reconstructs a `JolietDirectoryNode` full path from its `Name`/`Parent` chain instead of referencing a non-existent `Path` property.
- No intended behavioural change to the v0.7.71 geometry logic.

# DumpToolbox v0.7.71

- DIC volDesc parsing now captures supplementary/Joliet path-table records separately from the primary ISO9660 path table.
- Reconstructs full supplementary directory paths from directory-number/parent-number ordering and retains each original Joliet directory extent as explicit DIC evidence.
- Joliet synthesis now prefers a complete one-to-one volDesc supplementary path-table allocation ahead of CeQuadrat, translated, paired, or contiguous inferred layouts.
- A volDesc-proven layout is accepted only when every generated Joliet directory maps uniquely, the SVD root agrees, and all directory ranges avoid declared path tables and ordinary file extents.
- Verbose DIC logging lists every recovered supplementary path-table directory path, extent LBA, parent directory number, and directory number; it explicitly reports when no such evidence exists.

## v0.7.69

- DIC mainInfo exact-evidence fix: complete non-offset-check Main Channel sector dumps are no longer discarded merely because their LBAs are outside the primary ISO9660 metadata whitelist. This allows original-pressing supplementary/Joliet, slack, padding, or other non-file sectors captured by DIC to be preserved exactly. Raw/scrambled `Check Drive + CD offset` captures remain excluded here and continue through the dedicated descrambling evidence path.

## v0.7.68

- Fixed Joliet fallback allocation for discs such as ALFABE where the SVD anchors a small contiguous supplementary-directory area. Compact Joliet representations (unversioned identifiers and, when necessary, omission of primary-only System Use bytes) are now tested against the ordinary SVD-root contiguous layout instead of only against mastering-specific translated/paired layouts.
- The allocator remains conservative: it does not scatter directory records into guessed sectors, does not move DIC file extents, and leaves primary ISO9660 metadata untouched.

# DumpToolbox v0.7.65

- DIC source matching now consumes ISO Extractor private record payloads through `.dumptoolbox_iso_manifest.json` more robustly.
- Exact manifest identity (ISO path + record LBA + size + flags) remains preferred.
- If an exact-LBA manifest record is unavailable, DIC may use a single unambiguous manifest record with the same ISO path + size + flags.
- `.dumptoolbox_iso_records` remains excluded from loose recursive matching so duplicate and Associated records cannot be guessed by filename alone.

# DumpToolbox v0.7.64

- Fix SkeleTool raw resurrection of duplicate-path ISO records: the multi-extent sanity check now validates against the selected alternate record geometry rather than the original collapsed same-path entry length.
- This completes the v0.7.62/v0.7.63 duplicate-record geometry handoff for cases such as ALFABE `AWFLAS~2.HTM` (4,366-byte selected record vs 6,046-byte primary same-path record).

## v0.7.63

- Fixed duplicate-path SkeleTool geometry still being lost when the local SHA-1 history database preloaded an older match. Reusable history sightings are now resolved against alternate ISO-record geometry using the recorded source length.
- Freshly hashed exact-length source matches now replace reusable history-database matches for the same logical entry, so stale geometry cannot override current evidence.
- Direct ISO/BIN source-image SHA-1 matches also resolve duplicate-path geometry by exact source-record length before being accepted.

## v0.7.62

- Fixed SkeleTool resurrection for redumper skeletons containing duplicate same-path ISO9660 records with different extents/sizes. Inspection now retains alternate ISO record geometries instead of silently discarding them when collapsing the path for the hash manifest.
- SHA-1 source matching now uses the matched file length to select the unique same-path ISO record geometry. This prevents a valid hash for a shorter duplicate record from being attached to the previously retained larger record.
- Raw and cooked one-pass resurrection now use the geometry carried by the selected source match.
- Non-XA SHA-1 candidates whose length matches neither the primary nor a unique alternate same-path record are rejected during matching rather than failing later during resurrection.
- Version bumped to 0.7.62.

## v0.7.61

- Compile fix for the v0.7.60 image-backed resurrection change: `RawSequentialPlan.SourceStream` is now typed as `Stream` rather than `FileStream`, allowing both ordinary file streams and logical image-backed match streams without an invalid cast.

## v0.7.60

- Fixed raw skeleton resurrection for source matches backed by a cooked/raw ISO/BIN image. The fast sequential resurrection and overlap validator now use the logical matched-file stream at `SourceImageLba` instead of opening the whole source image at byte zero.
- Improved source EOF diagnostics to identify the affected entry, LBA, source path and logical source offset.

## v0.7.59

- DIC Joliet/source matching now handles symmetric underscore/separator elision, including CHY's Joliet `TRDUBL.a6e` matching primary ISO9660 `TR_DUBL.A6E`.
- The same symmetric comparison is used while validating source pathname evidence for Joliet metadata reconstruction.
- Separator-elided matches remain guarded by complete-path compatibility, exact file size and reverse-uniqueness checks.
- Version bumped to 0.7.59.

## v0.7.58

- DIC Joliet/source matching now recognises collision-dependent ISO9660 short aliases that use `_N` instead of `~N`, as seen on Cumhuriyet Bonus Disc (`3D_Modeller` -> `3D_MOD_1`, `Kurtulus Savasi Destani.avi` -> `KURTUL_1.AVI`, etc.).
- The same `_N` alias recognition is used when validating source pathname evidence for Joliet metadata reconstruction, so a successfully matched long-name source tree is not discarded later.
- Underscores are ignored only for the short-alias prefix comparison; exact file size, complete path compatibility and reverse-uniqueness checks remain mandatory.
- Version bumped to 0.7.58.

## v0.7.57

- Fixed live theme switching on the Settings page. Avalonia can raise the newly selected radio button's `Checked` event before the previously selected radio has finished unchecking; v0.7.56 therefore sometimes reapplied the previous theme. The handler now applies the theme indicated by the radio that raised the event and defers persistence until the radio group has settled.
- Version bumped to 0.7.57.

## v0.7.56

- Added a top-level **Settings** tab after **Other Tools**.
- Added global theme selection: System default, Light, or Dark, applied immediately and persisted.
- Moved SkeleTool's local SHA-1 history/database switch to Settings as **Build/use local SHA-1 database**; old INI values migrate automatically.
- Added **Remember last used paths**. When disabled, saved path keys are removed and future path fields are not persisted, while non-path operational preferences continue to be remembered.
- Added an **About...** button on Settings. The initial About dialog reports the running assembly version and is ready for more information later.
- Removed the FindCRCs search-alignment selector. FindCRCs now always searches at 1-byte alignment.
- Version bumped to 0.7.56.

## v0.7.55

- Fixed DIC final whole-image verification when the companion DAT names the raw image `.bin`; accepted DAT BIN hashes now populate the original CRC32/MD5/SHA-1 target.
- Fixed CeQuadrat/WinOnCD Joliet reconstruction changing directory extents on the rebuild pass after the first pass had synthesized the private directory-link table.
- Greatly reduced heavy-overlap rebuild time by replacing repeated per-pair skeleton rescans/per-sector comparisons with one payload-capacity prefix scan plus large contiguous source comparisons.
- Version bumped to 0.7.55.

# DumpToolbox v0.7.54

- DIC whole-image verification now runs after every successful resurrection whenever the DIC logs provide CRC32/MD5/SHA-1 anchors.
- Verification is no longer suppressed merely because the resurrection pass reports missing payloads; the generated image is still hashed and compared, with the missing count noted in the log.
- Whole-image hashes remain the final exactness authority for DIC recovery.

# DumpToolbox changelog

## 0.7.53
- Keep the version permanently in the main window title while transient tool status is appended after it.
- Make Skeletool and DIC filesystem/log panes equal width.
- Add DIC `Force rehash / clear cache` option. It removes the selected disc's persisted recovery-state JSON and donor cache before import so failed/stale runs start clean; there is no misleading per-file hash-cache behavior in DIC folder matching.
- Increase global frontend scrollbar thickness to 16 px for both vertical and horizontal scrollbars.

# DumpToolbox v0.7.52

- DIC: fixes CeQuadrat/WinOnCD Joliet directory placement. Directory bodies are packed from the SVD root in ascending mapped primary-directory extent order, while the Joliet path table and private link table retain primary path-table order.
- The rule is enabled only when the independently detected CeQuadrat bridge context covers every directory and all proposed ranges are structurally safe.

# DumpToolbox v0.7.50

- DIC: reconstructs the CeQuadrat/WinOnCD private Joliet directory link-table sector when it is fully derivable from DIC-preserved ISO/Joliet geometry.
- Requires a CeQuadrat primary formatter identifier, the private sector immediately after the volume-descriptor terminator and immediately before the primary Type-L path table, and a unique Joliet counterpart for every primary path-table directory.
- Emits the observed one-sector format: `CeQuadrat Joliet directory link table`, seven zero bytes, a little-endian directory count, then `{Joliet LBA, primary LBA}` DWORD pairs in primary path-table order; the rest of the 2048-byte payload remains zero.
- No title, filename, disc-hash, or Rebellion-specific rule is used. Existing exact metadata/donor evidence always wins over synthesis.

# DumpToolbox v0.7.49

- Skeletool SHA-1 history is now portable-only: `skeletool_sha1_history.json` is stored beside the executable via `AppContext.BaseDirectory`.
- Removed the LocalApplicationData location/fallback for the Skeletool SHA-1 database.
- Before Skeletool database-backed scans or successful-rebuild association writes, the UI probes the executable directory and logs a clear warning if it is not writable.
- A non-writable database never aborts source scanning and is never redirected elsewhere; existing readable history may still be consulted.

## 0.7.48
- Fixes CS0103 compile regression in `PatchRawUnknownLengthAsync` introduced by the v0.7.47 nullable cleanup: the helper correctly uses its `match` parameter when choosing 2048/2324-byte payload size.
- Retains all v0.7.47 left-anchored live-log behaviour and CS8602 cleanup.

## 0.7.47

- Fixed CS8602 nullable warning in the raw sequential resurrection-plan builder by resolving a non-null source match once per recoverable entry.
- All read-only multiline output/log panes are explicitly left-aligned.
- Log auto-follow now keeps the vertical view at the newest line while forcing horizontal offset back to column zero after every append, preventing long log lines from leaving subsequent output scrolled to the right.
- ISO Extractor now uses the same shared log append/scroll behaviour as the other tools.

## 0.7.46

- DIC: recognizes a generic Mode 1 `0x55 except 16-byte header` mastering pattern when an LBA is explicitly mapped as an unresolved EccEdc mismatch and the recovered 2048-byte user payload is entirely `0x55`.
- The inferred recipe is applied only if the sector still contains canonical Mode 1 EDC/ECC generated from that payload; stronger non-canonical raw evidence still present at final-recipe time is not replaced.
- No disc-name, image-hash, or MSF-specific selector is used.

## 0.7.45

- Skeletool SHA-1 history is now metadata/provenance only; source file payloads are never copied into a persistent cache.
- ISO/BIN sightings store the original image path, internal path, LBA, length and SHA-1. Reuse reopens the original image and reads the extent directly.
- Existing history JSON files remain readable; obsolete cached-payload properties are ignored on load and disappear on the next save.

# DumpToolbox 0.7.44

- DIC resurrection no longer treats overlapping recoverable file extents as automatically fatal.
- Overlapping DIC source payloads are validated byte-for-byte over the actual shared raw-sector user-data ranges before resurrection.
- Verified-identical overlaps are allowed and reported as `DIC OVERLAP: VERIFIED`.
- A real byte disagreement in the shared physical range remains fatal and reports the exact LBA, user-data byte offset, and both conflicting paths.
- The fast one-pass raw writer can now carry multiple simultaneously active, pre-validated DIC restore extents.
- No DIC recovery-state schema bump: this changes validation/resurrection behaviour only.

## 0.7.43

- Skeletool can scan a source ISO/BIN directly as an alternative to a source folder. ISO9660 file payloads are SHA-1 matched and matching payloads are cached locally for resurrection.
- Added optional persistent Skeletool SHA-1 history database with one-to-many source sightings and successful-rebuild provenance.
- Folder scans become cumulative and, when history is enabled, hash/index all scanned files for reuse by future skeletons.
- Missing-file reports list every previous source sighting and every successful rebuilt image associated with that SHA-1, including CRC32/MD5/SHA-1.
- Complete Skeletool rebuilds are hashed and linked to their contributing SHA-1 entries.

# DumpToolbox v0.7.42

- DIC log selection now uses a folder picker. DumpToolbox discovers the single matching DIC companion set in that folder and rejects ambiguous folders containing multiple sets instead of guessing.
- Core DIC discovery remains backward-compatible with callers/saved settings that still pass an individual companion-log path.
- DIC and Skeletool filesystem explorers now display every file size as an exact byte count, including zero-byte files, rather than automatically converting to KiB/MiB/GiB.
- Recovery-state schema remains v41 because these are UI/discovery changes and do not alter reconstruction semantics.

# Changelog

## 0.7.41

- Added DVD Physical Format Information (PFI) geometry recovery from DIC `disc.txt`.
- For single-layer DVD media with valid `StartingDataSector` and `EndDataSector`, derives the complete 2048-byte logical-sector count as `EndDataSector - StartingDataSector + 1`.
- Allows PFI-proven recorded data-zone length to extend beyond ISO9660 Volume Space Size, preserving post-volume sectors instead of misclassifying the smaller filesystem volume as whole-image geometry.
- DAT `.iso` size/hash evidence now independently confirms already-proven PFI geometry rather than being required to identify a cooked DVD image; disagreement is reported instead of changing PFI-proven physical capacity.
- Cross-checks DIC `LayerZeroSector`/DVD sector-length evidence against the independently derived PFI length and reports disagreement without silently shrinking the image.
- Recovery-state schema bumped to v41 because DVD target geometry can now be resolved from additional authoritative evidence.

## v0.7.40

- Fixed cooked/DVD DIC geometry when a sibling DAT proves a whole `.iso` image that is larger than the ISO9660 Volume Space Size.
- A unique 2048-aligned `.iso` DAT entry is now accepted when it covers every sector already proven by DIC filesystem/track evidence; exact equality with Volume Space Size is no longer required.
- DAT-proven cooked image length now extends the reconstruction sector count so post-volume sectors are retained and audited instead of being truncated.
- Raw `.img` handling remains conservative: non-ISO image entries still require exact known 2048/2352 geometry.
- Recovery-state schema bumped to 40 because target image geometry can change.
- Version bumped to 0.7.40.

## v0.7.39

- Refines Joliet reconstruction candidate selection when an SVD-proven directory layout cannot fit the historical `;1`-suffixed supplementary identifiers.
- Preserves the existing versioned+System-Use representation first for compatibility with already-proven byte-exact layouts, then retries with standards-style unversioned Joliet file identifiers while keeping System Use intact.
- Only after identifier-form retries fail may inherited primary System Use be omitted; both unversioned and final compatibility versioned variants are validated against the same SVD-proven geometry.
- Directory-record length calculation and output now use the selected identifier form consistently, so geometry validation and emitted bytes cannot disagree.
- DIC recovery state schema bumped to v39.

## v0.7.39

- Treats ISO9660 directory-record System Use as namespace-specific metadata rather than assuming primary-tree bytes must also appear in Joliet records.
- Joliet reconstruction still preserves inherited primary System Use by default for compatibility with previously byte-exact layouts.
- If inherited System Use makes an independently SVD-proven translated/paired Joliet geometry impossible, the builder now retries the supplementary tree without inherited primary System Use and accepts it only when every proven directory extent then fits in-volume without file/path-table/directory overlap.
- This specifically handles primary XA/System-Use payloads whose extra record bytes would otherwise grow supplementary directories by whole sectors because ISO directory records cannot cross 2048-byte boundaries.
- Recovery-state schema bumped to v38 because supplementary metadata bytes/geometry selection can change.
- Version bumped to 0.7.39.

## v0.7.37

- Added a guarded fixed-offset primary-to-Joliet directory allocator for mastering layouts where supplementary directory extents mirror the primary ISO9660 tree at a constant LBA translation.
- The translation is derived from the SVD-declared Joliet root versus the DIC-proven primary root; no fixed offset or disc-specific LBA is hard-coded.
- Every translated directory is rejected unless it remains in-volume and avoids ordinary file extents, declared Joliet path tables, and other generated directories.
- Existing paired-primary and contiguous Joliet placement strategies remain as fallbacks.
- Recovery-state schema bumped to v37 because supplementary metadata geometry can now differ from earlier builds.
- Version bumped to 0.7.37.

## v0.7.36

- Skeletool and DIC filesystem browsers now render successful status tokens (`✓`, `✓XA`, `✓R`, `✓0`) in green and missing-source `✗` in red.
- Only the status token is coloured; filenames, sizes and source-path text keep the normal theme foreground. Other state symbols (`○`, `?`, `!`, `∅`, `…`) remain theme-coloured.
- This is presentation-only; recovery-state schema remains v34.
- Version bumped to 0.7.36.

## v0.7.35

- Fixed generic DIC companion-file discovery so non-`.txt` companions such as `<basename>.dat` are found automatically when selecting any file from the same log set.
- `FindCompanion` now enumerates all top-level files and performs the existing case-insensitive exact filename comparison, instead of pre-filtering candidates to `*.txt`.
- Reconstruction logic and DIC recovery-state schema are unchanged from v0.7.34.
- Version bumped to 0.7.35.

## v0.7.34

- Preserves primary ISO9660 sibling ordering when assigning Joliet path-table directory numbers. This handles mastering layouts where primary and supplementary path tables share directory-number order even though long Joliet aliases sort differently from primary short names.
- Carries each matched directory's proven primary ISO9660 path into the Joliet model; the supplementary path table still writes the Joliet identifier, but parent/directory numbering follows the primary identifier order when evidence is available.
- Keeps Joliet-name ordering as the fallback when no primary mapping exists.
- DIC recovery-state schema bumped to v34.
- Version bumped to 0.7.34.

## v0.7.33

- Fixes generic primary-directory metadata extraction when an ISO9660 directory record contains a malformed/unparseable 7-byte recording timestamp. Structural evidence (extent, data length, flags, raw timestamp bytes and System Use) is now retained independently of timestamp parsing.
- This allows the SVD-validated paired primary/Joliet directory allocator introduced in v0.7.32 to operate on discs whose root timestamp is deliberately invalid/zeroed instead of incorrectly falling back to the contiguous Joliet allocator.
- A directory discovered only through its internal `.` record also retains the already-known walked extent and data length, so malformed timestamps cannot erase geometry at any level.
- Recovery-state schema bumped to v33 so Joliet metadata omitted by the v0.7.32 geometry-gating bug is not reused.
- Version bumped to 0.7.33.

## v0.7.32

- Adds SVD-validated scattered Joliet directory placement for mastering layouts where each supplementary directory immediately follows its corresponding primary ISO9660 directory allocation instead of occupying one contiguous metadata area.
- The paired layout is accepted only when the SVD-declared Joliet root independently confirms the same primary-end relationship and every proposed child range is non-overlapping with files, declared path tables, and other generated directories. Otherwise the existing conservative contiguous allocator is retained.
- Primary directory extent and data-length evidence is now carried alongside timestamps/flags/System Use metadata so supplementary placement can be derived from logged structure rather than guessed.
- Fixes Abe's Oddysee-style layouts where Type-M/Type-L path tables are near the volume descriptors but Joliet child directories are scattered next to their primary counterparts.
- Recovery-state schema bumped to v32.
- Version bumped to 0.7.32.

## v0.7.31

- Fixes legacy DIC `Recording Date and Time` parsing. Older DIC volDesc logs use `YYYY-MM-DD HH:MM:SS +HH:MM`, while newer evidence may use ISO `YYYY-MM-DDTHH:MM:SS+HH:MM`. Both are now accepted.
- This restores per-file recording-time evidence needed only as a secondary tie-breaker for otherwise ambiguous numeric `~N` short-name alias groups. Path/alias compatibility and exact byte size remain mandatory, and timestamps are accepted only when they produce a unique mapping.
- Abe's Oddysee regression: the equal-sized `ABESIZ~1..4.CUR` and `ABEREC~1..2.ICO` groups can be resolved from their distinct DIC recording times without any disc- or filename-specific rule.
- Recovery-state schema bumped to v31 so state created before legacy timestamp parsing was fixed is not silently reused.

## v0.7.30

- Fixes generic DOS/ISO numeric short-alias matching when the normalized long-name stem is exactly the same length as the alias prefix. Numeric aliases are not limited to long stems: a filename such as `Abe.Theme` may legitimately be represented as `ABE~1.THE` because the extension itself requires 8.3 conversion or because the authoring tool allocated a collision alias.
- Keeps numeric `~N` suffixes non-predictive. Full path, exact size, reverse uniqueness and (when required) unique DIC/source recording timestamp remain the evidence used to select an alias.
- Regression: `Desktop Theme -> DESKTO~1`, `Abe.Theme -> ABE~1.THE`, and the equal-sized `Abe Size *.cur -> ABESIZ~N.CUR` sibling set.
- Recovery state schema bumped to v30.

## v0.7.29

- Adds conservative timestamp disambiguation for groups of same-sized Joliet source files that all plausibly map to ISO9660 numeric short aliases such as `ABESIZ~1.CUR`, `ABESIZ~2.CUR`, etc.
- Recording time is used only after full relative-path projection and exact byte size already match, and only when it yields a unique one-to-one source/primary-record mapping. Ambiguous or non-matching timestamps remain unresolved rather than guessed.
- Source timestamp comparison accepts either the same absolute instant or the same wall-clock value to accommodate extraction tools that preserve ISO9660 timestamps while dropping the recorded GMT offset.
- Existing generic 3-character ISO extension truncation remains authoritative for cases such as `Abe.Theme -> ABE.THE`; no disc/file-specific aliases are hard-coded.
- Recovery-state schema bumped to v29.
- Version bumped to 0.7.29.

## v0.7.28

- Adds conservative support for DOS-style numeric primary ISO9660 aliases such as Joliet directory `Desktop Theme` -> primary `DESKTO~1`.
- The numeric `~N` suffix is never guessed. DumpToolbox only recognises the alias shape and normalized prefix; existing full-path, exact-size, forward-uniqueness and reverse-uniqueness checks remain authoritative. Ambiguous short aliases are rejected.
- Applies the same alias rule to source-payload matching and Joliet directory-metadata association so the two reconstruction stages cannot disagree.
- DIC recovery state schema bumped to v28 so prior unmatched source state is not silently reused.
- Version bumped to 0.7.28.

## v0.7.27

- Fixes Project Eden Joliet directory timestamps after v0.7.26. Primary directory traversal now keeps parent-visible directory-entry metadata separate from the internal `.` and `..` records instead of letting a child directory's zeroed `.` timestamp overwrite the real timestamp recorded in its parent.
- Joliet reconstruction now preserves separate raw 7-byte timestamps and System Use areas for the visible directory entry, `.` and `..`. This reproduces discs where those records intentionally differ.
- Project Eden regression: `/DIRECTX80A` remains `65 0A 03 0D 0B 38 E4` in the parent-visible Joliet entry while its internal `.`/`..` records retain the mastered `00 00 00 00 00 00 E4`.
- DIC recovery-state schema bumped to v27 so Joliet sectors generated under the v0.7.26 collapsed-directory-metadata model are not reused.
- Version bumped to 0.7.27.

## v0.7.26

- Preserves the exact ISO9660 System Use bytes from each matched primary directory record when synthesizing the corresponding Joliet directory record. This fixes CD-ROM XA discs such as Project Eden where every Joliet record carries a 14-byte XA System Use area.
- Joliet directory-size calculation and sector packing now include the preserved System Use length, preventing record offsets and subsequent directory extents from drifting.
- Preserves the raw seven-byte primary directory-record timestamp when available instead of normalizing malformed mastering values. Project Eden's root `.` / `..` records intentionally contain `00 00 00 00 00 00 E4`.
- Extends the primary-record metadata scan to ordinary files as well as directories so per-file System Use bytes and raw recording timestamps can be transferred exactly.
- Recovery-state schema bumped to v26 so Joliet metadata generated without System Use areas is not reused.
- Version bumped to 0.7.26.

## v0.7.25

- Unifies Joliet-to-primary ISO9660 pathname matching across source matching and Joliet metadata reconstruction; v0.7.24 had two independent projection implementations, so a path could match one stage and fail another.
- Adds an ISO9660 Level-2/Nero projection: 31-character identifiers, modelled as a 27-character file stem plus dot plus 3-character extension. Project Eden regression: `ArcadeInstallPROJECTEDEN108c.exe` maps to `ARCADEINSTALLPROJECTEDEN108.EXE`.
- Retains exact, Level-1, and punctuation-elision projections. Project Eden regression: `DirectX8.0a` maps to `DIRECTX80A`; `Andre copy.edi` maps to `ANDRE_COPY.EDI`.
- Joliet directory metadata now resolves through the same guarded matcher instead of independently forcing an 8-character Level-1 directory projection. Ambiguous directory aliases are rejected.
- Recovery-state schema bumped to v25.
- Version bumped to 0.7.25.

## v0.7.24

- Extends conservative Joliet-to-primary-ISO9660 source matching for mastering tools that elide punctuation instead of replacing it. Example: Joliet directory `DirectX8.0a` can match primary directory `DIRECTX80A`.
- Keeps the existing underscore/8.3-style projection for cases such as `Setup-1.bin -> SETUP_1.BIN`.
- The punctuation-elision fallback still requires the full relative path, exact file byte length, and bidirectional uniqueness; ambiguous projections remain unresolved.
- Recovery-state schema bumped to v24 so source matches made under the older projection rules are not silently reused.
- Version bumped to 0.7.24.

## v0.7.23

- Raw 2352-byte CD restorations now accept a 2048-byte cooked ISO as a **file payload source only**. The ISO may be scanned recursively and uniquely matched ISO9660 files are queued/restored normally.
- A cooked ISO is never promoted to raw-sector evidence for a raw-CD target, even when its PVD and volume identifier prove same-disc identity. It cannot overwrite primary metadata, system/slack/exactness regions, sync/MSF/mode/XA framing, EDC/ECC, audio, or Mode 2 Form 2 raw bytes.
- Mandatory raw/exact donor requirements no longer cause the cooked ISO scan itself to fail. They remain visibly unsatisfied so a matching 2352-byte BIN can be supplied separately while file matches from the ISO are retained.
- Cooked 2048-byte DVD targets retain the v0.7.22 full cooked-donor behaviour. Raw 2352-byte BIN donors retain full raw-CD donor semantics.
- DIC recovery state schema bumped to v23 so donor-source classification from earlier candidates is not silently reused.
- Version bumped to 0.7.23.

## v0.7.22

- Fixed DIC same-disc donor handling when the reconstruction target is a cooked 2048-byte DVD/ISO image. Donor identity checks, metadata insertion, mandatory donor regions and optional exactness regions now calculate target offsets from the target `SkeletonImageKind` instead of assuming every DIC skeleton is raw 2352-byte CD.
- Cooked targets write donor logical payload sectors directly at `(LBA - BaseLba) * 2048`; raw targets retain the existing framing-preserving 2352-byte path. A raw BIN may still donate to a cooked target, but only its logical 2048-byte payload is written.
- Same-disc PVD comparison now reads the target PVD using the target geometry. This fixes cooked ISO donors being inconsistently demoted to candidate-file-only donors even when they are the exact same DVD.
- DIC recovery-state schema bumped to v22 so donor state created by the broken cooked-target offset logic is not resumed.
- Version bumped to 0.7.22.

## v0.7.21

- DIC DVD import now discovers a sibling `<basename>.dat` and accepts a unique `.iso`/`.img` ROM entry whose byte size exactly matches the reconstructed sector count at either 2048 or 2352 bytes/sector. Its CRC32/MD5/SHA1 are promoted as the original whole-image verification anchor when `disc.txt` does not contain one.
- DVD cooked-image geometry no longer requires a whole-image ROM line in `disc.txt`. `BookType: DVD...` plus DIC `LayerZeroSector` matching the ISO Volume Space Size is sufficient to identify a 2048-byte/sector image safely.
- Pokémon DVD regression: `POKEMON_NP_DVD.dat` reports `POKEMON_NP_DVD.iso` size 1,215,299,584 bytes; this is exactly 593,408 × 2048 and agrees with both `LayerZeroSector: 593408` and ISO Volume Space Size 593408. Target hashes are CRC32 `5876636a`, MD5 `bdb64aafa61dff7be49c8a1efbba11a4`, SHA1 `2ed020beea16a112439d78250e87fe41ff93fa1c`.
- DIC UI now reports the discovered `.dat` companion. Recovery-state schema bumped to v21.
- Version bumped to 0.7.21.

## v0.7.20

- Adds initial DIC DVD/cooked-image support. When the original `disc.txt` IMG size exactly equals `sectorCount × 2048`, DIC resurrection now creates a cooked 2048-byte skeleton instead of inventing raw-CD 2352-byte sync/MSF/EDC/ECC framing.
- Fixes the `BCD value must be between 0 and 99` crash when opening DVD-sized DIC log sets. CD images whose IMG size is `sectorCount × 2352` continue through the existing raw-CD path unchanged.
- DVD/cooked detection is evidence-based from the original whole-image size; oversized images with ambiguous geometry are rejected rather than guessed.
- Recovery-state schema bumped to v20.

## v0.7.19

- DIC recovery exactness audit now finds every in-volume sector not claimed by the ISO9660 file extents or DIC-preserved metadata instead of silently assuming such gaps are zero.
- Optional exactness donor regions are created for those unclaimed sectors. This covers hybrid/mastering metadata that sits outside ordinary ISO file extents.
- Detects Apple Partition Map evidence recovered from DIC drive-offset captures and reports Apple_HFS hybrid partitions explicitly.
- Fixes the Ben 10: Kayıp Gezegen case where HFS metadata at the beginning/end of the Apple_HFS partition was omitted from the v0.7.18 coverage audit.
- Recovery-state schema bumped to v19 and application version bumped to 0.7.19.

## v0.7.18

- Fixed a Joliet source-evidence edge case for discs whose supplied user-visible/Joliet tree happens to use only names that are also valid primary ISO9660 names. Ordinary additional-source-folder matches made by exact ISO9660 relative path + filename + size now retain their original source-relative spelling as trustworthy Joliet evidence.
- This is especially visible on root-only discs such as Ben 10: all three root files matched the primary ISO tree exactly, so v0.7.17 never entered the Joliet-projection fallback and therefore refused to synthesize the Joliet tree despite having the complete user-visible source tree.
- Donor-image and ISO Extractor manifest matches are unchanged; their relative paths are not promoted by this rule.
- DIC recovery-state schema bumped to v18 so prior source-path interpretation is not silently carried forward.
- Version bumped to 0.7.18.

## v0.7.17

- Fixed Joliet directory-record flags reconstructed from a matched source tree. The primary tree walker no longer lets a child directory's `.` record overwrite the externally visible flags recorded in its parent directory.
- Preserves flags such as Hidden (0x01) together with Directory (0x02). Black Mirror's `LASERLOK` primary record is 0x03 and now produces Joliet `Laserlok` with 0x03 instead of 0x02.
- Black Mirror LBA 24 regression: the corrected flags restore the original Mode-1 EDC `4A FF 1A DD`; v0.7.16 produced `6E 6B 42 58`.
- DIC recovery-state schema bumped to 17 so metadata reconstructed with the v0.7.16 flag bug is not carried forward.
- Version bumped to 0.7.17.

## v0.7.16

- Fixes Joliet SVD/path-table fidelity introduced by v0.7.15. The Joliet builder now preserves the exact Type-L, optional Type-L, Type-M and optional Type-M path-table location fields already present in the DIC/mainInfo SVD instead of clearing bytes 144-155.
- Generates both little-endian Type-L and big-endian Type-M Joliet path-table copies at every non-zero location declared by the original SVD.
- Adds overlap/conflict guards so contradictory Type-L/Type-M locations are rejected rather than guessed.
- Black Mirror regression: LBA 17 keeps Type-L LBA 27 and Type-M LBA 28. Its Mode-1 EDC returns to `DF 65 D7 F6`; v0.7.15's cleared Type-M field produced `49 8E 6C 89`.
- DIC recovery state schema bumped to v16 so Joliet metadata generated by v0.7.15 is not carried forward.
- Version bumped to 0.7.16.

## v0.7.15

- DIC normal-folder source matching now accepts a **Joliet/user-visible tree** when primary ISO9660 filenames differ. Exact primary path+size is still tried first; otherwise each source component is conservatively projected to ISO9660 Level-1 (upper-case, invalid characters to `_`, 8.3 truncation) and accepted only when the full path + exact byte length identifies one DIC primary record uniquely in both directions. Examples: `BlackMirror.ico -> BLACKMIR.ICO`, `Setup-1.bin -> SETUP_1.BIN`.
- Persisted source matches now retain the validated relative Joliet pathname. DIC recovery state schema is v15.
- Re-enabled supplementary/Joliet metadata reconstruction, but only after every ordinary file has trustworthy long-name evidence from either a validated Joliet source path or an explicit DIC long-name alias. Primary ISO9660 metadata remains authoritative and is snapshotted/restored byte-for-byte.
- Joliet reconstruction now carries primary-record file flags/timestamps, primary directory timestamps/hidden flags, and case-sensitive UCS-2 record ordering. It merges files and subdirectories in identifier order instead of writing all directories first.
- Black Mirror regression: source names `BlackMirror.ico`, `Setup-1.bin`, `Laserlok`, etc. plus DIC primary records reproduce Joliet root LBA 24. Its generated Mode-1 EDC is `4A FF 1A DD`, matching the original sector supplied for comparison.
- A later Joliet scan may replace an older donor/primary-only source match for naming purposes, including for payloads already present in a cumulative image; payload identity remains exact-size/primary-record constrained.
- Version bumped to 0.7.15.

## v0.7.14

- Fixes final `0x55 except header` resurrection for complete post-repair `*EdcEcc_Track_*.txt` verification maps whose per-sector fill records omit `MSF[...]`.
- A complete verifier has already proven canonical physical-LBA order, so its `2336 bytes ... 0x55` records can use the physical LBA plus `disc.txt` track mode for deterministic header framing. Historical/incomplete EccEdc maps still require explicit logged header evidence.
- Prevents Mode-1 EDC/ECC regeneration from surviving in the final 320-byte protection tail of LaserLok placeholder sectors: bytes 16-2351 are reasserted as literal `0x55` after payload/protection regeneration.
- Black Mirror regression LBA 341925: header `00 FF FF FF FF FF FF FF FF FF FF 00 76 01 00 01`, followed by exactly 2,336 bytes of `0x55`.
- DIC recovery-state schema bumped to 14 so v0.7.13 Black Mirror output/state containing regenerated ECC tails is not reused.
- Version bumped to 0.7.14.

## v0.7.13

- Fixed a v0.7.12 regression in EccEdc summary-to-physical-sector mapping. Summary-only historical anomalies (notably Warcraft II's 68,736 proven Mode 2 Form 1 Q-ECC faults) can again map by exact physical/reported LBA when the per-sector stream does not repeat the ECC/EDC failure.
- Retains v0.7.12's SmartE/repeated-header protection: when anomaly-marked per-sector candidates exist for a repeated reported LBA, those physical sectors are consumed first and neighbouring normal sectors are never used as fallback matches.
- Regression checked against the supplied Warcraft II EccEdc log (68,736/68,736 mapped, 0 unmapped) and Zoo Tycoon 2 SmartE log (10 repeated LBA 192302 errors mapped to physical sectors 192303-192312, excluding normal physical sector 192302).
- DIC recovery state schema bumped to v13 so a failed v0.7.12 cumulative output/state cannot be carried into the corrected rebuild.
- Version bumped to 0.7.13.

## v0.7.12

- DIC EccEdc record parsing now validates the per-sector stream structurally. If a historical log line is malformed/corrupted, physical-ordinal tracking stops at that point instead of treating later-looking `LBA[...]` text as trustworthy; `disc.txt` track geometry supplies conservative fallback classification for uncovered sectors.
- Added generic per-sector support for EccEdc's explicit `2336 bytes have been already replaced at 0x55` evidence. The final recipe is keyed to physical sector position and fills bytes 16-2351 only; protection names such as SafeDisc/LaserLok/SmartE are not used as synthesis heuristics.
- Fixed summary-LBA fallback mapping so repeated reported/header LBAs are filtered by the anomaly being mapped. Ten physical SmartE sectors whose headers all report the previous LBA no longer risk mapping one summary occurrence onto the neighbouring normal sector.
- Added narrowly safe `mainError` handling for `All zero sector. Skip descrambling`: those sectors are exact final 2352-byte zero sectors. Generic `Read error. padding [...]` history and arbitrary raw `Main Channel` dumps remain non-authoritative.
- Added support for complete later `*EdcEcc_Track_*.txt` verification maps. When such a map covers the complete image in absolute-LBA order **and no original whole-image hash is present**, it may supersede an earlier DIC per-sector state for final repaired-image reconstruction. If DIC provides CRC/MD5/SHA-1, the hash-anchored original DIC image remains authoritative and later repair maps are not applied automatically.
- Added exact extensionless raw-sector recovery files: a decimal filename containing exactly 2352 bytes is accepted as an exact LBA sector override only when its sync/MSF/mode framing validates against that LBA and the final sector map. Exact recovered sectors outrank generated payloads, donor bytes and generic `0x55` recipes.
- Black Mirror / LaserLok regression: the supplied bundle's CloneCD log records 7,595 failed sectors; the old checker reports exactly the same 7,595 `0x55` sectors; 37 validated LBA-named 2352-byte recovery sectors account exactly for the reduction to 7,558 `0x55` sectors in the final checker/image. See `DIC_BLACK_MIRROR_LASERLOK.md`.
- DIC recovery-state schema bumped to v12.
- Version bumped to 0.7.12.

## v0.7.11

- Reworked DIC EccEdc parsing around physical IMG sector ordinals rather than blindly using the printed/header-derived LBA. This handles malformed MSF/sync and track/session LBA discontinuities safely.
- Preserves exact logged MSF bytes and exact invalid raw Mode bytes when available; logical Mode 1/2 payload handling now uses the low Mode bits so high-bit protection values are not normalized during payload replacement.
- Preserves both XA subheader copies independently when EccEdc logs an eight-byte mismatch.
- Added first-class Mode 0, audio and unknown/unsafe EccEdc sector classifications. Audio/unknown/raw-framing anomalies are exposed as exact raw-donor regions instead of being synthesized as ordinary data sectors.
- EccEdc summary lists now overlay the per-sector map for ECC/EDC mismatch, invalid Mode, bad MSF, invalid/zero sync, unequal subheaders and expected-all-zero mismatches. Summary mapping is anomaly-aware because historical EccEdc versions mix physical and header-derived LBA coordinates.
- The Warcraft II Q-ECC mastering fault is no longer applied generically to every Mode-2 Form-1 EccEdc error list. It is positively fingerprinted by the already-proven target SHA-1/error population. Unknown corruption remains unknown rather than receiving invented bytes.
- Added explicit support for EccEdc's logged `N unmatch sector is replaced at 0x55 except header` recipe, only when the recipe count agrees with the explicit error count.
- Raw donor copies now reapply exact DIC framing evidence and any proven final-image recipe afterwards. Added a `RequiresRawDonor` exactness flag so a cooked ISO cannot satisfy regions whose raw 2352-byte bytes are essential.
- Raw payload restoration no longer rejects a sector solely because its sync is non-canonical; exact invalid/zero sync from a raw donor can be retained while the logical payload is restored.
- DIC recovery-state schema bumped to v11 so older cumulative skeleton/state data cannot hide the new evidence model.
- Version bumped to 0.7.11.

## v0.7.10

- Removed the hard-coded version banner from the ISO Extractor activity log.
- The main window title now displays the running DumpToolbox assembly version dynamically (for example, `DumpToolbox 0.7.10`), so the displayed version automatically follows the project version on future builds.
- Version bumped to 0.7.10.

## v0.7.9

- FindCRCs singleton Audio edge recovery now handles a safely bounded source extent that is shorter than the target instead of requiring an exact-sized extent.
- If the sole/inferred audio extent is short by `N` bytes, DumpToolbox treats the missing bytes as candidate digital zero silence and first exhaustively tests all `N + 1` distributions between the start and end at 1-byte precision. For example, a 315-byte shortfall tests 315/0, 314/1, ... 0/315 zero bytes at start/end.
- If none of those direct padding splits verifies, the padded recovery is then combined with the existing signed silence-shift logic. DumpToolbox measures the short source extent's verified leading/trailing zero runs and exhaustively tests padding plus shifts in both directions. This covers tracks that are both under-dumped and displaced within their own digital silence.
- The combined search is constructed so that any source bytes it discards must fall inside the measured boundary-zero runs; non-zero PCM is never discarded or invented. Every accepted candidate must verify CRC32 and MD5 when supplied.
- The padding and padded+shifted scans work in both CUE-mapped singleton-audio recovery and the v0.7.8 no-CUE two-target inference path because they live in `DumpToolbox.Core.EdgeRecoveryService`.
- Exact-sized singleton extents retain the existing v0.7.7 signed silence-shift behavior.
- Version bumped to 0.7.9.

## v0.7.8

- FindCRCs Audio edge recovery no longer requires a CUE for the safe two-target singleton case.
- When exactly two hash targets are supplied and the ordinary scan verifies exactly one of them, `DumpToolbox.Core.EdgeRecoveryService` can infer the unmatched target as a singleton edge candidate. The edge-recovery service then requires safe physical boundaries and an exact target-sized extent before testing signed zero-silence shifts. This inference is in Core so a future CLI can use the same behavior.
- This covers the common `Track 01 matched at offset 0 + unmatched final Track 02 + source EOF` layout without a cuesheet.
- A CUE remains recommended for multi-track audio mapping and is still required for Track 02 pregap scrambling.
- FindCRCs now leaves `Attempt to fix under-dumped Audio edges` and `Save partial files for manual inspection` available when no CUE is selected.
- Clearing the CUE no longer clears those two generic edge-recovery options; it clears only the CUE-only pregap option.
- Version bumped to 0.7.8.

## v0.7.7

- FindCRCs CUE-aware audio edge recovery now handles a disc with only one mapped AUDIO track. When adjacent matched data/source boundaries establish an exact target-sized audio extent, DumpToolbox tests whether the PCM is shifted within its own zero-byte digital silence instead of refusing recovery solely because there is no second audio-track anchor.
- The singleton-audio scan measures the actual leading and trailing zero-byte runs and exhaustively tests both signed directions at 1-byte alignment: prepend zeros while dropping only verified trailing zeros, then remove only verified leading zeros while appending the same number of zeros. A candidate is accepted only when the target CRC32 (and MD5 when supplied) verifies.
- No arbitrary silence-padding limit is used; the search range is bounded by silence actually present at the corresponding edge, so non-zero audio is never discarded by this recovery mode.
- Singleton audio tracks can now save a bounded `.partial` candidate for inspection when repair fails and a safe physical extent is available.
- FindCRCs logging no longer prints the same track twice as the two 'extreme' tracks when only one AUDIO track is mapped.
- Version bumped to 0.7.7.

## v0.7.6

- Added a runtime-generated `DumpToolbox.ini` for user-interface preferences. The INI is created automatically on first launch and is not a release/source asset that needs bundling.
- The INI remembers normal window size/position/state, selected tabs, last-used path fields, and stable per-tool options such as modes and checkboxes. Transient logs, progress, target/hash text, Base64 text contents and DIC recovery state are deliberately not stored.
- By default the INI is created beside the executable. If that location is not writable, DumpToolbox falls back to the current user's local application-data folder.
- Added `Clear saved inputs` to every page that has INI-backed inputs. The button resets only that page's saved UI values; DIC recovery JSON/matches are not deleted.
- Added `Reset all settings` on FindCRCs to reset every INI-backed page and navigation preference. Deleting `DumpToolbox.ini` remains an equivalent manual reset.
- Settings are saved when the application loses focus and on normal close. Missing, malformed or unwritable settings never block DumpToolbox from starting or running.
- Version bumped to 0.7.6.

## v0.7.5

- Fixed DIC same-disc raw-donor exactness copying overwriting the logged Mode 2 Form 1 mastering fault. Raw donor sectors copied for system-area/slack/post-volume/other exactness regions now have the DIC-proven protection fields reapplied when their LBA is in the explicit EccEdc error set.
- Reproduced the v0.7.4 Warcraft II failure exactly: copying donor-raw LBA 19 and the post-volume region 70,064-70,365 over the otherwise-correct image yields CRC32 `61ab4dd1`, MD5 `1e890cbad893ea412858406e512e4f3a`, SHA-1 `ee963ec88206ae025151bc0713c2ffafce049cef`, matching the reported failed rebuild. Reapplying the logged DIC fault returns the exact target hashes `af37ee45` / `0141a4079c5b3c0f4ff371cb0ad1bc07` / `8fae1a878deb63850de4e5a83d5567e28c5ef78b`.
- Added an in-place Mode 2 Form 1 protection-field rebuild helper so donor payload/header/subheader bytes remain intact while EDC/P/Q are regenerated with the optional DIC logged fault.
- DIC recovery-state schema bumped to v10 so 0.7.4 cumulative skeleton/state files are not silently reused after this donor-ordering fix.
- Version bumped to 0.7.5.

## v0.7.4

- DIC rebuilding now parses the explicit `[ERROR] Number of sector(s) where user data doesn't match the expected ECC/EDC` `Sector:` list from `*_EccEdc.txt` and carries those LBAs through the complete recovery pipeline.
- For logged Mode 2 Form 1 error LBAs, reproduces the reverse-engineered mastering fault exactly: EDC and P ECC are generated normally, Q ECC is calculated with raw-sector byte `0x873` temporarily forced to `00`, then the correct P byte is restored. Ordinary sectors remain standards-compliant.
- The special fault is reapplied during synthetic-skeleton creation, source-file restoration, zero-system-area regeneration, Joliet metadata patching and same-disc donor insertion so later recovery passes cannot accidentally normalise the logged bad ECC.
- Fixed DIC `mode 2 no edc` parsing so an explicit XA Submode is authoritative. A Form-1 submode is no longer misclassified as Form 2 merely because DIC printed `no edc`; genuine Form-2/no-EDC sectors continue to keep their four-byte EDC/spare field zero.
- Fixed the v0.7.4 donor-metadata regeneration compile error by explicitly passing the DIC ECC/EDC error-LBA set into `ApplyMetadataAsync` instead of referencing an out-of-scope `inspection` variable.
- Clarified documentation that the full EccEdc per-sector log is still parsed for normal DIC reconstruction metadata; only the new mastering-fault LBA set is restricted to DIC's explicit ECC/EDC `[ERROR]` list.
- DIC EccEdc error-list counts are validated against the number of unique parsed LBAs before any special ECC fault is applied.
- DIC recovery-state schema bumped to v9 so a cumulative BIN produced by an older build cannot be silently resumed without the new ECC-error reproduction behaviour.
- Version bumped to 0.7.4.

## v0.7.3

- Fixed a C# compile error in the older-DIC `volDesc` compatibility parser: ISO9660 parent-directory identifier byte 0x01 now uses the valid C# character escape `\x01` instead of invalid `\1`.
- No recovery behaviour changed from v0.7.2.
- Version bumped to 0.7.3.

## v0.7.2

- DIC importer now supports older `_volDesc.txt` logs that contain `File Identifier` but omit `FullPath` for ordinary files.
- Missing paths are reconstructed from the primary ISO9660 path table and the directory-sector context, including multi-sector directory extents.
- The DIC log reports how many file paths were reconstructed from the older log format.
- Fixes `No recoverable file extents were found` on older DiscImageCreator logs such as the Warcraft II Expansion example.
- Version bumped to 0.7.2.

## v0.7.1

- Added a DIC recovery coverage audit that distinguishes exact mainInfo metadata, deterministic path-table synthesis, recovered source payload, bytes proven by drive-offset captures, and zero-assumed/unproven regions.
- DIC now parses the early `Check Drive + CD offset` raw/scrambled main-channel captures in `_mainInfo.txt`, stitches adjacent reads, descrambles them and uses the recovered system-area payload bytes. Partial final captures are retained as byte-level evidence while unobserved bytes remain explicitly zero-assumed.
- Added optional same-disc donor recovery for file-sector tail slack, unproven ISO system-area sectors, synthesized/missing ISO metadata sectors, and sectors beyond ISO9660 Volume Space Size. These exactness regions do not block a best-effort resurrection.
- Mandatory donor requirements (EARs and ambiguous duplicate ISO records) remain blocking; optional exactness donor regions are now tracked separately.
- DIC log output reports the byte count and LBA range of every coverage/assumption category and whether the assumed region can be improved by a same-disc donor.
- Complete DIC rebuilds now automatically calculate and compare the original whole-image CRC32/MD5/SHA-1 when DIC logged those hashes, reporting a byte-exact match or the remaining zero-assumed byte count.
- DIC recovery state schema bumped to v8 so older cumulative working images are not silently reused without the new exactness/evidence handling.
- Version bumped to 0.7.1.

## v0.7.0

- Removed the experimental compressed optical-image analyser/extractor from the Convert tab and Core library.
- Removed its third-party package dependency and dedicated UI/source/documentation.
- Convert now contains ISO2BIN and MDF2BIN only.
- Retains all FindCRCs/audio recovery behaviour from v0.6.40, including pregap rebalance, signed edge recovery, dual anchor partials and source-tail diagnostics.
- Version bumped to 0.7.0.

## v0.6.40

- FindCRCs Track 02 pregap repair now tests a sector-aligned pregap-boundary rebalance when the Track 01/Track 03 anchor shortfall plus the proven positive final-audio shift equals a whole number of 2352-byte sectors.
- The rebalance keeps the corrected/scrambled pregap data sector(s), removes only verified all-zero PCM bytes immediately after them, inserts the inferred silent pregap sectors at that boundary, and accepts the result only when the Track 02 CRC32/MD5 verifies.
- When the inferred silent-sector count plus the detected stored pregap data-sector count exactly equals the CUE pregap count, this is reported explicitly.
- With "Save partial files for manual inspection" enabled and both immediate anchors matched, the first audio track now saves both target-sized hypotheses as `.forward.partial` and `.backward.partial`.
- Existing ordinary end-padding/Find-Ends and signed-symmetry fallbacks remain available when the pregap rebalance does not verify.
- Version bumped to 0.6.40.

## v0.6.39

- Fixed `CS0136` compile errors in the combined Track 02 pregap + missing-end repair path by giving the nested output/status locals unique names.
- No recovery behaviour change from v0.6.38; this is a compile-fix release.
- Version bumped to 0.6.39.

## v0.6.38

- FindCRCs Track 02 recovery now combines pregap-sector scrambling with forward-anchored missing-end repair when matched Track 01 and Track 03 prove the Track 02 region is short.
- The combined candidate first tests the corrected/scrambled Track 02 prefix plus zero padding for the exact missing suffix.
- If zero padding fails and MD5 is available, Find-Ends now derives the missing-end CRC32 from the **corrected (scrambled)** Track 02 prefix and searches the complete source for that segment.
- This avoids calculating an unusable missing-segment CRC from the original unscrambled pregap bytes.
- Version bumped to 0.6.38.

## v0.6.37

- FindCRCs first-audio recovery now tests the immediately preceding matched target (normally Track 01) as a forward anchor before falling back to the next-audio/Track 03 backwards anchor. A short Track 01→Track 03 region is first treated as a possible missing-end case for Track 02; only a hash-verified reconstruction is accepted.
- **Save partial files for manual inspection** now preserves a short final audio-track tail instead of refusing to write it when the expected target-sized window extends past source EOF. The log reports the exact saved byte count and exact shortfall.
- Version bumped to 0.6.37.

## v0.6.32

- FindCRCs now reports the relationship between the source image EOF and the expected end of the final CUE track.
- If the source continues beyond the final track extent, the log reports the exact extra byte count, the expected end offset, and the equivalent number of complete 2352-byte raw sectors plus any remainder.
- If the final track itself is matched, its verified offset is used directly. If it is not matched, DumpToolbox can project the final extent forward from the nearest earlier matched CUE track using the expected sizes of the remaining track targets.
- Exact EOF and under-dumped EOF cases are also logged so the end-of-image geometry is explicit.
- Version bumped to 0.6.32.

## v0.6.30

- Added FindCRCs **Save partial files for manual inspection** for CUE-mapped extreme AUDIO tracks.
- When an unmatched first/last audio track has safe anchor boundaries but the geometry does not prove a repairable under-dump, DumpToolbox can now save the complete bounded source region as `<track>.partial` for manual comparison. The saved region may be shorter, exact-size, or longer than the expected target.
- The option is independent of automatic edge repair: it can save bounded partials without attempting reconstruction, or retain the available data when zero/Find-Ends repair fails.
- Track 02 partials use the next matched AUDIO track (normally Track 03) as the authoritative end boundary and the best safe lower boundary available from the preceding layout.
- Version bumped to 0.6.30.

## v0.6.28

- Fixed ISO Extractor completion on Windows. The `.dumptoolbox_iso_manifest.json.partial` stream is now disposed before the manifest is renamed into its final filename.
- This fixes `The process cannot access the file because it is being used by another process` after an extraction has otherwise completed successfully.
- The extracted payload-file finalisation path was already closing its stream correctly; the remaining open-handle bug was in `IsoExtractionManifestService.SaveAsync`.
- ISO Extractor version display updated to 0.6.28.

## v0.6.27

- Reorganised the main UI to: FindCRCs, Audio, Convert, SkeleTool, DIC, Other Tools.
- Added a top-level **Convert** tab containing **ISO2BIN** and **MDF2BIN**.
- Added **Use resulting BIN as FindCRCs source** to both converters; after successful conversion the completed BIN is assigned to FindCRCs and the UI switches there.
- Moved **Concatenate**, **HashCalc**, **Base64**, **Find-Ends** and **ISO Extractor** under **Other Tools**.
- Updated DIC's Associated-record workflow so its ISO Extractor redirect selects **Other Tools → ISO Extractor** correctly after the tab move.
- Version bumped to 0.6.27.

## v0.6.26

- Fixed MDF2BIN completion on Windows: `.bin.partial` / optional `.sub.partial` streams are now explicitly disposed before the final rename into the requested output filenames.
- The previous v0.6.25 conversion could reach 100% and then fail with `The process cannot access the file because it is being used by another process` because the output `FileStream` was still open with `FileShare.None` when `File.Move` ran.
- CUE generation and final renames now happen only after the MDF/BIN/SUB stream scope has closed.
- Version bumped to 0.6.26.

## v0.6.24

- FindCRCs Track 02 audio-edge symmetry is now signed rather than one-sided.
- A positive final-edge shift (+N, last audio short) mirrors by removing exactly N all-zero PCM bytes after the corrected Track 02 pregap sectors, then re-running 1-byte FindCRCs.
- A negative final-edge shift (-N, verified zero overage at the last audio edge) mirrors by inserting exactly N zero PCM bytes after the corrected Track 02 pregap sectors, then re-running 1-byte FindCRCs.
- The shift magnitude is derived from the actual final audio edge geometry; it is never hard-coded to 24 bytes or any sector/sample size.
- Last-audio edge recovery can now also trim a proven all-zero overage and accepts it only when CRC32/MD5 verifies.
- Version bumped to 0.6.24.

## v0.6.23

- Generalised the mixed-mode audio edge symmetry repair: the Track 02 pregap pass now derives an arbitrary `N`-byte shortfall from the last AUDIO track's adjacent-anchor geometry rather than assuming a fixed 24-byte shift.
- If ordinary Track 02 pregap scrambling does not verify, DumpToolbox tests removing exactly that same `N` bytes immediately after the final corrected pregap data sector(s), but only when all `N` bytes are digital PCM silence (`00`).
- The adjusted Track 02 candidate is searched at 1-byte alignment and is accepted only after the normal CRC32/MD5 verification. Non-4-byte-aligned values are allowed for diagnosis and remain hash-gated.
- Search windows include the mirrored trim allowance so larger proven edge shifts are not truncated from the retry window.
- Version bumped to 0.6.23.

## v0.6.22

- Fixed Track 02 pregap scrambling so the CUE INDEX 00 -> INDEX 01 length is treated as a physical pregap search window, not as proof that the complete pregap belongs at the start of the Redump Track 02 BIN.
- After correcting recognised empty raw data sectors in the complete pregap, FindCRCs now performs a normal 1-byte rolling CRC32/MD5 search across a window of `Track 02 target size + pregap size`. This handles the common case where the normal 150 pregap sectors are assigned to Track 01 and only extra mastering-error sectors remain at the start of Track 02.
- Pregap correction logs the sector positions it changed and the actual byte/sector displacement where the verified Track 02 target begins.
- Audio sectors are never blindly scrambled; only sectors passing the strict empty raw CD-ROM data-sector test are changed.
- Version bumped to 0.6.22.

## v0.6.21

- FindCRCs now accepts an optional CUE sheet for disc-layout classification.
- Renamed **Fix under-dumped edges** to **Attempt to fix under-dumped Audio edges** and restricted anchor-based repair to the first and last AUDIO tracks identified by the CUE.
- Added **Scramble Track 2 pregap data sectors if present** for mixed-mode discs. Empty, unscrambled raw CD-ROM sectors in Track 02's file-backed INDEX 00 pregap are scrambled with the ECMA-130 Annex B 15-bit LFSR and accepted only when the Track 02 CRC32/MD5 verifies.
- ISO2BIN has returned to the top-level tab bar. The temporary Convert/PSP/Wii/GameCube/Xbox placeholder tabs have been removed.
- Version bumped to 0.6.21.

# DumpToolbox changelog

## v0.6.20
- Added a new top-level **Convert** tab to group image-format conversion tools.
- Moved the existing ISO2BIN interface unchanged under **ISO2BIN**.
- Added system-oriented conversion tabs for **PSP** (ISO ↔ CSO), **Wii / GameCube** (ISO / RVZ / GCZ / NKit), and **Xbox** (ISO ↔ XISO).
- The PSP, Wii/GameCube and Xbox tabs are explicit placeholders in this build; their conversion engines are not yet implemented.
- Updated ISO2BIN window/dialog wording to identify its new **ISO2BIN** location.
- Version bumped to 0.6.20.

## v0.6.19
- Fixed window restore sizing: restoring a maximized/fullscreen main window now returns to the last dimensions it had while in Normal state instead of retaining the maximized resolution.
- Normal window dimensions continue to track user resizes, so each maximize/restore cycle returns to the most recent manually selected size.

# v0.6.18 — integrated ISO Extractor tab + compile fix

- Moved ISO Extractor into the main DumpToolbox window as a top-level tab; removed the separate `DumpToolbox.IsoExtractor` executable/project from the solution.
- DIC Associated-record warning now sends the user to the integrated ISO Extractor tab.
- Successful extraction automatically fills the DIC Source Folder with the manifest-aware extraction folder.
- Fixed CS0136 in `DicLogImportService.cs` by separating the path-table Extended Attribute length local from the directory-record `xarLength` local.
- Main application version bumped to 0.6.18. DIC recovery-state schema remains v7 because source identity/state semantics are unchanged.

# v0.6.17 — separate ISO Extractor + manifest-aware Associated records

- Added a separate `DumpToolbox.IsoExtractor` Avalonia application. It reads the primary ISO9660 filesystem directly from 2048-byte ISO or 2352-byte BIN images.
- Extractor output preserves the ordinary visible record at its normal path while storing Associated/same-path records under `.dumptoolbox_iso_records`.
- Added `.dumptoolbox_iso_manifest.json`, recording original ISO path, extent LBA, length, File Flags, XAR/interleave fields and multi-extent information.
- DIC recovery now exposes non-empty Associated File records as distinct source requirements instead of automatically forcing an attached donor image.
- DIC source matching recognises extractor manifests and matches ISO records by exact PVD identity + volume identifier + original path + extent + length + File Flags.
- Associated records are never guessed from ordinary same-name files; without a matching manifest they remain unresolved.
- Loading DIC logs containing Associated records now explicitly directs the user to run DumpToolbox ISO Extractor and select its output folder as the source folder.
- Exact donor-image scanning remains available as a fallback and can now match Associated source entries by exact record identity.
- Recovery state format bumped to v7.

# v0.6.16 — mounted-filesystem duplicate policy

- Corrected the duplicate-path recovery assumption: different file sizes no longer imply that two same-path ISO records are both available from a mounted/extracted filesystem.
- A normal + Associated ISO 9660 pair is handled explicitly: the non-associated (ordinary/PC-visible) record remains eligible for normal source matching, while any non-empty Associated payload still requires an exact donor.
- Multiple non-associated records that normalize to the same pathname now require an exact donor regardless of whether their byte lengths differ, unless they are a valid Multi-Extent chain.
- Recovery-state schema bumped to version 6 because donor-required record identity changed.

# v0.6.15 — minimise mandatory donors; native ISO interleaving

- Reduced mandatory donor use to byte regions that genuinely cannot be reconstructed from DIC logs plus ordinary extracted files.
- Non-empty ISO 9660 Associated File payloads still require an exact same-disc donor because a normal mounted filesystem does not reliably expose their separate payload. Zero-length Associated records no longer require a donor.
- Extended Attribute Records now require the donor only for the EAR blocks themselves. The normal file payload following the EAR remains source-folder recoverable.
- Record (`0x08`) and Protection (`0x10`) flags no longer require a donor by themselves when Extended Attribute Record Length is zero; the exact flag byte is retained from DIC.
- Reserved File Flag bits `0x20`/`0x40` now produce a non-standard-disc warning instead of forcing a donor.
- Added native ISO 9660 interleaved-file restoration. File Unit Size and Interleave Gap Size are converted into non-contiguous extent segments so ordinary source bytes are written only into assigned file units and gap sectors are left untouched for their own owners.
- Correctly handles the ISO interleaved+EAR rule: the EAR occupies the first file unit and file data starts in the following file unit after the interleave gap.
- Donor filesystem extraction now understands file EAR offsets, interleave units/gaps, and Multi-Extent sections together.
- Duplicate normalized ISO paths with different lengths no longer force a donor; they get distinct internal identities and are matched by original path + exact byte length. Only same-path/same-size duplicate records remain donor-required because they cannot be assigned safely from an ordinary extracted tree.
- Cooked resurrection now honours multi-segment extent maps as well as the raw DIC resurrection path.
- Recovery-state schema bumped to version 5 because entry identities and donor-required regions changed.

---

# v0.6.14 — ISO 9660 storage-semantics safety + Multi-Extent recovery

- Expanded DIC ISO 9660 directory-record parsing to retain Extended Attribute Record Length, File Unit Size, Interleave Gap Size and the complete File Flags byte.
- Renamed File Flags `0x01` internally to `Existence`; these entries remain normally recoverable, but DIC logs warn that a mounted filesystem may hide them.
- Added proper ISO 9660 Multi-Extent (`0x80`) support: consecutive same-path file sections are represented as one logical source file, matched by the combined byte length, then split back across the recorded extents during raw resurrection.
- Donor ISO parsing also combines ordinary Multi-Extent sections so an ISO/BIN donor can provide a normal logical source file for them.
- Generalised the v0.6.13 Associated-file donor gate into a mandatory exact-donor safety model. Associated (`0x04`), Record (`0x08`), Protection (`0x10`), reserved flag bits (`0x20`/`0x40`), non-zero Extended Attribute Record Length, and ISO interleaving now require an exact same-disc donor before Resurrect is enabled.
- Directory Extended Attribute Records are detected both from primary directory records and the primary ISO path table. Directory data LBAs are shifted by the XAR block count when parsing multi-sector directories.
- Ambiguous duplicate non-MultiExtent paths after ISO version normalization are no longer allowed into the normal one-path/one-source model; they require the exact donor instead of risking a duplicate-key crash or wrong extent selection.
- Mandatory donor regions are copied directly from the exact donor. With a 2352-byte BIN donor, required sectors are copied raw byte-for-byte. A cooked 2048-byte ISO remains usable for requirements that do not touch Mode 2 Form 2 sectors.
- DIC donor parsing now retains directory records and XAR/interleave fields so donor validation can distinguish special ISO records correctly.
- Recovery-state schema bumped to version 4 because the donor-satisfaction semantics and logical Multi-Extent entry model changed.

---

# v0.6.13 — Associated-file donor requirement

- ISO 9660 Associated File records (`File Flags` bit `0x04`) are detected and retained as a separate donor-only recovery requirement.
- Loading DIC logs no longer aborts solely because associated records exist. The DIC tab logs and displays a warning explaining that normal mounted/extracted folders cannot supply those payloads.
- The Resurrect action is disabled until a qualifying ISO/BIN donor is scanned successfully. A defensive runtime check also blocks resurrection if the requirement is not satisfied.
- Associated records are excluded from ordinary host-file matching, avoiding duplicate-path collisions with their normal-file counterpart.
- Donor parsing now retains the ISO directory-record flags byte, so ordinary and Associated records with the same visible pathname remain distinguishable.
- For discs containing Associated records, the donor must pass the existing exact same-disc PVD + volume-identifier identity check and contain exactly matching associated records by path, extent, length and Associated flag.
- Required associated extents are copied directly from the donor image into the DIC raw skeleton. Mode 1 and Mode 2 Form 1 can use 2048-byte ISO or 2352-byte BIN donors; a required Mode 2 Form 2 associated extent requires a 2352-byte BIN donor.
- Target DIC XA subheaders and the v0.6.9 per-LBA no-EDC policy are preserved while donor payload bytes are inserted.
- Donor satisfaction is persisted in DIC recovery state and is automatically re-established when a saved donor is reapplied after reloading logs.

---

# v0.6.12 — Abort DIC resurrection for ISO 9660 Associated Files

- DIC import now treats ISO 9660 Associated File records (`File Flags` bit `0x04`) as an unsupported recovery condition.
- If any associated-file record is present, recovery aborts immediately after parsing the primary ISO 9660 directory records, before a skeleton image is created or modified.
- The DIC log/error dialog explains that ordinary mounted/extracted files cannot reliably supply the separate associated payload and lists example affected paths/LBAs.
- This replaces v0.6.11's unsafe behaviour of silently excluding associated payloads, which could produce a plausible but non-byte-identical image.

# v0.6.11 — Model ISO 9660 directory-record File Flags

- DIC ISO 9660 directory records now retain the standard File Flags byte as a typed flag set rather than testing raw magic values.
- Recognised flags are Hidden (0x01), Directory (0x02), Associated (0x04), Record (0x08), Protection (0x10), and Multi-extent (0x80).
- Associated-file records are parsed and retained as valid ISO 9660 records, but are intentionally excluded from ordinary host-filesystem source matching. Their directory metadata remains preserved from DIC.
- Associated and ordinary records are kept distinct during extent/alias grouping so an associated record cannot replace or merge into its normal-file counterpart.
- This does not attempt Macintosh resource-fork extraction or restoration.

# v0.6.10 — Ignore ISO associated-file records in DIC recovery

- DIC import now skips ISO 9660 records with File Flags bit 0x04 (Associated File).
- Prevents classic-Mac resource/data fork pairs from colliding on the same normalized path.
- Keeps the DIC recovery model intentionally single-file-per-path; no Mac fork or Joliet-style alias model is introduced.
- Retains all v0.6.9 Mode 2 Form 2 / no-EDC handling and v0.6.8 multi-sector filesystem fixes.

# v0.6.8 — DIC multi-sector filesystem continuation fix

- Fixed primary ISO9660 path-table parsing so all sectors in a multi-sector path table are parsed, rather than only the first path-table LBA.
- Fixed primary ISO9660 directory parsing so the complete multi-sector directory extent is recognised after its directory record is read.
- Files whose directory records occur in continuation sectors are now included in the DIC recovery model instead of being omitted and left as valid but zero-filled raw sectors.
- This directly addresses late-disc regions where sector sync/MSF/EDC/ECC were present but the 2048-byte Mode 1/Form 1 user-data payload remained zero.

# v0.6.7 — DIC primary path-table reconstruction

- Fixed DIC synthetic images leaving primary ISO9660 path-table sectors as zero-filled user data when `_mainInfo.txt` did not contain their raw bytes.
- Parses the complete primary path-table record list from `_volDesc.txt` (directory identifier, extent and parent directory number).
- When the primary PVD is available from `_mainInfo.txt`, rebuilds both Type-L (little-endian) and Type-M (big-endian) path-table copies at the exact LBAs recorded in the original PVD, including optional copies when present.
- Falls back to rebuilding the DIC-reported primary Type-L location when a raw PVD is unavailable.
- Validates the rebuilt path-table byte length against the PVD/volDesc Path Table Size and warns if they disagree.
- The path-table sectors are then framed with the existing per-sector Mode/XA data and regenerated EDC/ECC just like other reconstructed metadata.

# v0.6.6 — strict DIC ISO9660 matching

- Simplified DIC source matching to exact primary ISO9660 relative path + exact filename + exact byte length only.
- Matching remains case-insensitive and normalises path separators/Unicode.
- Removed DOS/8.3 compatibility, 5/6-character prefix matching, timestamp tie-breaking, filename-only fallback and size-only fallback.
- Donor-image payload matching now follows the same strict rules; same-disc donors may still donate original primary ISO9660 metadata sectors.
- Bumped persistent DIC state format to v3 so matches created by older heuristic rules are not silently reused.

# v0.6.5 — DIC ISO9660-only matching

- DIC recovery now treats only the primary ISO9660 namespace as authoritative; supplementary/Joliet trees are no longer synthesized or used for source matching.
- Supplementary/Joliet descriptors recovered from DIC `mainInfo` are preserved as opaque metadata, but their trees are no longer synthesized, parsed or used for source matching.
- DIC source-folder matching now requires exact byte length and ranks exact relative path, ISO/DOS-compatible path, exact filename, compatible filename, then conservative 6/5-character stem fallbacks.
- DIC recording time versus source `LastWriteTime` is used only to break ambiguous same-strength matches within ±2 seconds.
- Removed the unsafe unique-size-only fallback.
- Donor ISO/BIN parsing now uses only the primary ISO9660 tree and copies only primary ISO9660 metadata from exact same-disc donors.
- Bumped DIC persistent-state format to v2 so old Joliet-era/size-only matches and cumulative working images are not silently reused under the stricter ISO9660 matcher.

# v0.6.4

- Find-ends full-file hashes are now supplied as one target line instead of separate length/CRC32/MD5 fields.
- Accepts a normal Redump track/file row or compact `SIZE CRC32 MD5` input.
- Exactly one target is required and MD5 remains mandatory for final reconstruction verification.

# v0.6.3

- Fixed responsive layout of progress controls across FindCRCs, Concatenate, Audio, iso2bin, Skeletool, DIC, HashCalc and Find-ends. Action buttons/options and progress/status now occupy separate rows so controls cannot render over the progress bar when the window is narrow.
- Progress status labels now have bounded widths, leaving the progress bar usable at lower horizontal resolutions.
- Fixed `CS8600` in `FindEndsService.cs` by treating the `Dictionary.TryGetValue` output as nullable until the successful lookup/null check establishes a non-null candidate array.

# v0.6.2

- **iso2bin** now accepts one optional Redump target row with expected filename, raw size, CRC32, MD5 and optional SHA-1.
- For ISO-only conversion, the target raw size defines the final 2352-byte sector count. A short 2048-byte ISO is extended virtually with empty cooked sectors; a long ISO has excess trailing cooked sectors ignored during conversion. The source ISO is never modified.
- iso2bin verifies the completed BIN against the supplied Redump size/CRC32/MD5 and optional SHA-1 and uses the supplied Redump BIN filename as the suggested output name when available.
- Added optional under-dumped-edge repair to **Audio**. If track 1 or the final target does not match but its adjacent target does, that match is used as an anchor to calculate the available partial bytes and exact missing prefix/suffix length.
- Audio edge repair always tests zero PCM first. If that fails, it uses the Find-ends CRC algebra to derive the missing segment CRC32, searches the entire `combined_cdda.bin` for the segment, and accepts reconstruction only after full-target CRC32+MD5 verification.
- If an anchored first/last Audio target still cannot be repaired, the available bytes are retained as a `.partial` file. Audio working-file cleanup preserves these `.partial` outputs.
- Added the same optional under-dumped first/last-target recovery to **FindCRCs**, using the adjacent matched target as the positional anchor, zero-fill first, then Find-ends against the complete source, with `.partial` output on failure.
- Redump target parsing now retains optional SHA-1 values for tools that can use them, while FindCRCs continues to use CRC32+MD5 for scanning.

# v0.6.1

- Added **Clear** beside the FindCRCs hash-target box.
- Renamed the Redumper **Resurrect** tab to **Skeletool**.
- Added a top-level **Other** tab after DIC and moved **HashCalc** and **Base64** into it as sub-tabs.
- Added **Other → Find-ends**, based on the previous missing-start/missing-end recovery workflow.
- Find-ends derives the exact missing byte count and CRC32 from a partial file plus the known complete length/CRC32. Auto mode calculates both missing-prefix and missing-suffix possibilities.
- An optional source file can be searched exhaustively at every byte offset with a rolling CRC32. CRC candidates are reconstructed virtually and accepted only when the supplied complete-file MD5 matches.
- A verified candidate can be prepended/appended to the partial file and written through a safe `.partial` output before final rename.
- The source search uses two buffered streams rather than retaining the complete search window in memory, allowing large missing segments without allocating a same-sized RAM buffer.

# v0.6

- Branched from the stashed **v0.5 stable rollback** baseline.
- Added a **HashCalc** tab for whole-file hash calculation. CRC32, MD5 and SHA-1 are enabled by default; SHA-256, SHA-384 and SHA-512 are optional. All selected hashes are calculated in one streamed pass.
- Added a **Base64** tab with UTF-8 string encode/decode and streaming file encode/decode. File operations use `.partial` output and atomically rename on success.
- Renamed the **Audio Recovery** tab to **Audio**.
- Added **Clear hashes** for the Audio Redump target box.
- Added **Delete working files after recovery**. When enabled, converted `.cdda.bin` files and `combined_cdda.bin` are removed after a successful run while matched recovered track BINs are retained.
- Moved long static UI explanations into hover tooltips (`ⓘ`) across the application, leaving dynamic inspection/status text visible.

# v0.4.4

- Renamed the entire application and source tree to **DumpToolbox**.
- Renamed the solution, executable project, core project, namespaces, assembly/product name and generated executable from the previous project name.
- Renamed application-owned cache/state/working paths to the `dumptoolbox` prefix, including the Resurrect hash cache, DIC persistent state, DIC donor cache and Audio Recovery search file/output folder.
- Renamed the optional ffmpeg configuration environment variable to `DUMPTOOLBOX_FFMPEG_DIR`.
- Updated documentation, build commands, window titles and dialog text to use the new project name.

# v0.4.3

- Added optional XA metadata input to the **iso2bin** tab.
- Accepts DiscImageCreator `*_EccEdc.txt` logs or raw 2352-byte Redumper `.skeleton` files.
- MODE2/2048 conversion now restores the original per-sector XA File Number, Channel Number, Submode and Coding Info when metadata is available.
- The four-byte XA subheader is duplicated into both subheader copies before the 2048-byte payload is inserted and EDC/ECC are regenerated.
- Missing metadata records fall back to the previous generic `00 00 08 00` Form 1 subheader.
- If metadata marks a cooked source LBA as Mode 2 Form 2, conversion stops rather than fabricating the missing 2324-byte Form 2 payload from a 2048-byte source.
- Auto mode can use a supplied XA metadata map as evidence for Mode 2 Form 1 even when the cooked ISO itself lacks a `CD-XA001` marker.
- CUE conversion also applies the metadata to cooked `MODE2/2048` tracks using absolute output LBA; Mode 1, audio and already-raw tracks are unchanged.
- The activity log reports how many exact XA subheaders were used and how many sectors required the generic fallback.

# v0.4.2a

- Fully fixed the remaining `CS8602` nullable-flow warnings in `DicTab.cs`.
- The DIC load path now keeps the loaded recovery state, previous output path and saved donor path in non-null local variables after validation instead of repeatedly dereferencing nullable/mutable fields.
- No functional recovery or Audio Recovery behaviour changed.

# v0.4.2

- Fixed the nullable-state warning in `DicTab.cs` (`CS8602`) when clearing stale persistent DIC applied-entry state.
- Audio Recovery now accepts multiple verified lossless source formats rather than FLAC only.
- Added built-in PCM WAV support alongside the existing built-in native FLAC decoder.
- Added ffmpeg/ffprobe-backed support for Monkey's Audio (APE), True Audio (TTA), Apple Lossless (ALAC), AIFF PCM, Ogg-FLAC and TAK.
- External sources are probed before decode; lossy codecs and any source that is not already 44,100 Hz / 16-bit / stereo are rejected rather than resampled.
- APE/TTA/etc. can use `ffmpeg.exe` + `ffprobe.exe` beside DumpToolbox, from PATH, or from `DUMPTOOLBOX_FFMPEG_DIR`.
- Playlists can contain any mixture of the supported lossless formats.
- Audio boundary reporting is now format-neutral (`source boundary` rather than `FLAC boundary`).
- WavPack remains deliberately disabled because `.wv` may be lossless, hybrid or lossy and the current probe cannot prove that property safely enough for checksum recovery.

# v0.4.1

- Audio Recovery no longer creates `recovered_cdda_tracks.bin`; successful recovery outputs only the individual matched track BIN files.
- The decoded `combined_cdda.bin` working stream is retained for cross-FLAC-boundary searching.

# 2026-08-08 — Audio Recovery tab

- Added a dedicated **Audio Recovery** tab for reconstructing Redump CD-DA track BINs from FLAC.
- Added a self-contained native FLAC decoder supporting normal FLAC constant, verbatim, fixed-predictor and LPC subframes with Rice/Rice2 residuals and stereo decorrelation.
- FLAC STREAMINFO is validated before decode; only 44,100 Hz / 16-bit / stereo sources are accepted for exact CD-DA recovery.
- Added ordered multi-FLAC input plus M3U/M3U8, PLS, CUE and one-path-per-line playlist loading.
- Decodes each FLAC to raw headerless Redump-style little-endian CDDA PCM and preserves each converted `.cdda.bin`.
- Concatenates decoded FLACs before hash searching, allowing the original Redump track boundaries to differ from the FLAC split points.
- Added 4-byte stereo-sample aligned FindCRCs searching for audio recovery.
- Added configurable leading/trailing digital-silence search padding and reports how much edge silence was required for each match.
- Reports each matched track's offset relative to the nearest supplied FLAC boundary.
- Recovery output is limited to the individual matched track BIN files; no `recovered_cdda_tracks.bin` is generated.
- Batched aligned FindCRCs input reads so small alignments such as 4 bytes do not issue a filesystem read for every stereo sample frame.

---

# v0.3 split DIC tab

- Restored the Resurrect tab to the stable Redumper-only `.skeleton` + `.hash` workflow.
- Moved all DiscImageCreator log import, source matching and synthetic BIN recovery controls to a separate tab named `DIC`.
- DIC and Redumper recovery now have independent tree/progress/log/cancellation state.
- `Allow missing files` defaults to off in both recovery workflows.

# DIC timestamp fix

- Fixed invalid `00/00/1900` timestamps in synthesized Joliet directory records.
- Parses and preserves each file's `Recording Date and Time` from DiscImageCreator `volDesc.txt`.
- Preserves the DIC timezone offset in ISO9660/Joliet seven-byte recording timestamps.
- Carries timestamps through source matching and matched-long-name Joliet rewrites.
- DIC sentinel timestamps such as `1900-00-00T00:00:00` are treated as unavailable; synthetic directory records use the valid volume creation timestamp instead.
- Primary ISO9660 metadata remains byte-for-byte preserved.

# DIC recursive filename matching

- DIC recovery now always searches the selected source folder and all descendants.
- Source directory paths are no longer required to match the ISO9660 or Joliet hierarchy.
- Matching is case-insensitive and uses filename aliases plus exact byte size, then 8.3 compatibility, then 6/5-character stem + exact extension + exact size.
- Ambiguous matches remain unresolved rather than choosing an arbitrary file.
- Redumper source scans still honour the existing **Search subfolders** checkbox.

# DumpToolbox changes

## 2026-08-07 — v0.3 DiscImageCreator log recovery
- Added a conservative DIC source matching fallback using the first 5 sanitized characters of the filename stem plus exact byte size; ambiguous candidates are never auto-selected.

- Added **Load DIC logs...** to the Resurrect tab alongside Redumper skeleton loading.
- Selecting any DIC companion text log auto-discovers same-basename `*_volDesc.txt`, `*_disc.txt`, `*_mainInfo.txt` and `*.img_EccEdc.txt` / `*.scm_EccEdc.txt`.
- Builds a synthetic raw 2352-byte `_DIC_skeleton.bin` through an atomic `.partial` file.
- `volDesc.txt` supplies filesystem paths, extents, data lengths, volume metadata, directories and path-table locations.
- `disc.txt` supplies track sector count/mode and retains the original whole-image CRC32/MD5/SHA1 for later validation.
- `mainInfo.txt` is mined for complete 2048-byte original metadata-sector dumps so volume descriptors/path tables/directory data can be pre-populated where DIC logged them.
- `EccEdc.txt` is parsed per LBA for Mode 1 / Mode 2 Form 1 / Mode 2 Form 2.
- Mode 2 reconstruction preserves DIC's exact XA file number, channel number, raw submode flags and coding-info byte, including differing EOF/EOR sectors.
- Source matching for DIC mode does not hash blindly: exact relative path+size, then unique filename+size, then unique exact size. Ambiguous candidates remain unresolved.
- DIC raw restoration uses the physical per-LBA sector map, inserting 2048-byte Form1/Mode1 payloads or 2324-byte Form2 payloads before the existing parallel EDC/ECC stage.
- ISO interleaved file-unit/gap records are detected and disabled for automatic restoration rather than making an unsafe assumption.
- Existing Redumper resurrection, hash cache, GAP handling, responsive worker pipeline and default `Allow missing files = OFF` behaviour are preserved.

## 2026-08-06 — Resurrect tab

- Added a `Resurrect` tab for Redumper `.skeleton` + `.hash` recovery workflows.
- Parses preserved ISO9660 metadata and shows the skeleton contents as a live directory tree.
- Recursively or non-recursively SHA-1 hashes a supplied source folder and matches files by content rather than filename.
- Displays matched, missing, XA/Form2, empty and restored status directly in the tree.
- Restores matched user payload into copied 2048-byte ISO skeletons or raw 2352-byte CD sectors.
- Preserves raw sync/MSF/mode/XA subheaders and regenerates Mode 1 / Mode 2 Form 1 EDC+ECC and Mode 2 Form 2 EDC.
- Recognises `SYSTEM_AREA`, `GAP_#######` and Redumper `.XA` alternate hashes.
- Reconstructs Redumper gap lengths from preserved ISO9660 area boundaries when available.
- Correctly regenerates EDC/ECC for a standard all-zero raw `SYSTEM_AREA`; Redumper skeleton creation clears those protection fields even when the payload itself is zero.
- Supports partial resurrection with unmatched areas left zeroed.
- Never modifies the source skeleton; output is created via an atomic `.partial` file.
- Implemented independently in C# rather than incorporating GPL ResurrectSkeleton source.

## 2026-08-06 — ISO2BIN mixed-mode CUE support

- ISO2BIN now accepts an optional CUE sheet for single-file mixed-mode images.
- The CUE, rather than whole-file divisibility, becomes authoritative for track boundaries and sector types.
- Added `MODE1/2048` -> `MODE1/2352` and `MODE2/2048` -> `MODE2/2352` conversion per track.
- Added byte-for-byte pass-through for `AUDIO`, `MODE1/2352` and `MODE2/2352` tracks.
- Track byte offsets are calculated sequentially using CUE frame boundaries and each source track's sector size, allowing cooked data and raw audio to coexist in one backing file.
- Generates a replacement all-2352 CUE while preserving INDEX/PREGAP/POSTGAP and other original CUE lines.
- Added CUE/backing-file validation and explicit rejection of unsupported/multi-file CUE layouts.
- CUE-mode output defaults to an `_2352.bin` name so the source CUE is not accidentally overwritten.

## 2026-08-06 — ISO2BIN tab

- Added an `iso2bin`/ISO2BIN frontend tab for converting 2048-byte cooked CD-ROM images to 2352-byte raw BIN sectors.
- Input size must be an exact multiple of 2048 bytes.
- Added Auto, Mode 1 and Mode 2 Form 1 output selection.
- Auto mode detects the `CD-XA001` marker in ISO9660 volume descriptors and selects Mode 2 Form 1 when present; otherwise it selects Mode 1.
- Added standard raw-sector sync and BCD MSF headers (`LBA 0 -> 00:02:00`).
- Added CD-ROM EDC generation and Reed-Solomon P/Q ECC generation.
- Mode 2 Form 1 uses duplicated generic XA data subheaders (`00 00 08 00`).
- Added streamed batch conversion, progress/throughput, cancellation and atomic `.partial` output.
- UI explicitly warns that a cooked ISO cannot preserve original XA file/channel/EOR/EOF subheader values.

---

## 2026-08-06 — DumpToolbox refactor

- Renamed the application, solution, projects and namespaces from FindCRCs to DumpToolbox.
- Moved the existing hash scanner into a `FindCRCs` frontend tab.
- Added a `Concatenate` tab for ordered multi-file binary concatenation.
- Added Add / Remove / Move Up / Move Down / Clear controls for concatenation sources.
- Added destination save picker, progress, throughput, cancellation and activity logging.
- Concatenation streams in 4 MiB blocks and uses an atomic `.partial` output.
- Removed the unused hidden Avalonia DataGrid dependency.
- Preserved the current high-speed FindCRCs engine, Redump parsing, sequential matching and match extraction fixes.

---

## Historical FindCRCs development notes

# Changes from recovered 2015 application

## Rewrite

- Replaced Windows Forms with Avalonia.
- Changed project target from Windows-only to cross-platform .NET 8.
- Removed all non-hash-matching features at the owner's request.
- Replaced the external `findcrcs.exe` process invocation with a managed embedded search engine.
- Made offsets explicitly 64-bit.
- Added cancellation and progress reporting.
- Added exact parsing errors for malformed target rows.
- Added grouped scanning when multiple targets share the same size.
- Added selectable 2352-byte or byte-by-byte alignment.

## Behaviour intentionally not retained

The old GUI extracted a matched region to `Track NN.bin`. This rewrite only reports matches/offsets because the requested scope is hash matching only.

## Performance pass 2
- Preserved locally proven build fixes: Avalonia packages pinned to 11.3.13, `MainWindow` is partial, and TextBox scrollbars use `ScrollViewer.*` attached properties.
- Replaced per-offset construction of CRC GF(2) matrices with precomputed shift operators.
- Added slicing-by-8 IEEE CRC32 implementation.
- 2352-byte aligned targets are now all searched together in a single sequential pass over the source file.
- Each source sector CRC is calculated once and reused for every target size.
- The rolling history stores one 32-bit CRC per sector instead of a full target-sized byte window.
- Replaced thousands of tiny asynchronous reads with buffered sequential synchronous I/O on a worker thread.
- MD5 is calculated only on a CRC candidate and only once for targets sharing the same size+CRC candidate.


## Live scan visibility

- Added a scrolling activity log.
- CRC32 candidates are reported immediately with decimal and hexadecimal offsets.
- MD5 verification failures are shown explicitly.
- Confirmed matches are shown immediately while the scan is still running.
- Matching result rows update live instead of waiting for the complete scan.
- Added scan throughput (MiB/s), periodic progress, candidate counts and final match count.
- Routine log messages are throttled to avoid materially slowing the scanner.

## Redump paste parsing / default alignment
- 1-byte exhaustive search is now the default; 2352-byte sector alignment is secondary.
- Target parser now accepts old Redump track-table rows and new filename-based Redump file rows.
- Parser locates SIZE + CRC32 + MD5 (+ optional SHA1) wherever it occurs and ignores superfluous columns.
- `.cue` rows in filename-based Redump lists are ignored automatically.
- New-format `.bin` filenames and old-format track numbers are retained as result labels.
- Existing simple `SIZE CRC32 [MD5]` input remains supported.

## Sequential target search
- Targets are now searched in pasted order.
- After a match, the next target begins at the byte immediately after the previous matched region.
- If no match is found before EOF, the search wraps once to offset 0 and continues only up to the original starting point.
- If a target is not found after the wrapped full-file search, the next target retains the end position of the last successful match.
- Applies to the default 1-byte mode and the secondary aligned mode.

## Sequential direct-probe + extraction

- Before rolling-scanning a target after a successful match, hash the exact expected consecutive offset once.
- Verified matches are automatically extracted beside the source image.
- New Redump rows retain their supplied `.bin` filename.
- Old Redump track-table rows use `Track_<number>_<md5>.bin`.
- Extraction uses a `.partial` temporary file and atomic final rename.

## Windows extraction lock fix
- Fixed Windows `The process cannot access the file because it is being used by another process` after a verified CRC/MD5 candidate.
- Root cause: the `.partial` extraction output was still open with `FileShare.None` when `File.Move` attempted to rename it.
- Input/output streams are now disposed before the atomic `.partial` -> final filename rename.
- Added an explicit `Hash verified ... extracting match...` log entry so verification and extraction failures are distinguishable.
## UI tidy
- Reduced the main grid row spacing from 12px to 6px.
- Made the results grid a compact 200px high so the activity log sits closer to the progress controls.
- Increased the activity log slightly to 170px.
- Removed the redundant footer text about CRC candidates / MD5 verification.


## Zero-padding aware concatenation
- Added optional zero padding between concatenated source files.
- Padding byte count is user-configurable (default UI value: 10240 bytes).
- Added boundary safety checking before padding is applied.
- The safety check probes up to 4096 bytes at each adjacent edge and requires at least 256 consecutive zero bytes at either the previous file's tail or the next file's head.
- If both adjacent edges contain data, padding is skipped for that boundary and the decision is written to the activity log.
- Boundary safety can be disabled to force padding between every source file.
- Progress totals include only padding that will actually be written.

## Multi-file CUE / ISO+WAV support
- ISO2BIN now accepts CUE sheets with multiple `FILE` entries.
- `FILE ... BINARY` sources can contain AUDIO, MODE1/2048, MODE1/2352, MODE2/2048 or MODE2/2352 tracks.
- `FILE ... WAVE` sources are parsed as RIFF/WAVE; the PCM `data` chunk is copied without the WAV container header.
- WAV audio is validated as 44,100 Hz, 16-bit, stereo PCM and must contain an exact whole number of 2352-byte CD audio frames.
- `.wav` AUDIO files are also recognised by extension even if an old CUE labels them BINARY.
- Multiple ISO/WAV/RAW/BIN source files are merged into one output BIN.
- Per-file CUE INDEX positions are shifted to cumulative positions in the generated single-file CUE.
- PREGAP, POSTGAP, FLAGS, ISRC, REM and other non-FILE/INDEX metadata lines are preserved.

## Fast resurrection pipeline

- Replaced the old copy-then-random-patch resurrection path with a one-pass sequential builder.
- Raw 2352 images are processed in 2048-sector (~4.8 MiB) blocks.
- All recoverable user data for a block is inserted before EDC/ECC regeneration.
- Only sectors whose payload/protection data needs rebuilding are regenerated.
- EDC/ECC work for dirty sectors is parallelised across available CPU cores.
- Removed sector-sized async read/seek/write operations from the active resurrection path.
- Cooked 2048 skeletons are also built sequentially without an initial full-file copy.
- Partial output remains atomic: `.partial` is renamed only after successful completion.

## v0.2 performance/UI follow-up
- Fixed CS8602 nullable warning in `StartRawPlan` by resolving the source match once before dereferencing it.
- Source matching now uses bounded parallel SHA-1 hashing (up to four workers / half the logical CPU count).
- When no Redumper `.XA` hashes are present, candidate files are safely pre-filtered by exact expected payload length before SHA-1 hashing.
- Stops doing expensive hashing once every required manifest entry has a match (already in-flight work may finish).
- Excludes the selected `.skeleton` and `.hash` files themselves from the candidate scan.
- Hash progress now distinguishes files actually hashed from files skipped by the pre-filter.
- Resurrect tab path selectors now share a single fixed-column grid, so all text boxes and Browse buttons line up.

## Persistent source hash cache
- Added `.dumptoolbox_hashcache.json` in the selected Resurrect source folder.
- Cache entries store relative path, file size, last-write UTC ticks, and SHA-1.
- Unchanged files reuse their cached SHA-1 on later scans instead of rereading file contents.
- Added **Force rehash (ignore cache)** to the Resurrect tab.
- Source hashing progress now reports freshly hashed, cached, and skipped files separately.
- Cache writes are atomic (`.tmp` then replace) and best-effort; cache corruption never prevents a scan.

## UI responsiveness update
- Source hashing now runs its entire setup/enumeration/cache/hash pipeline off the Avalonia UI thread.
- Routine source-scan progress is throttled to roughly 10 updates/second, with live match notifications capped to avoid dispatcher flooding.
- The final match tree is still refreshed from the complete result set, so throttling cannot hide a discovered match.
- Raw resurrection EDC/ECC parallelism now leaves one logical processor available for the UI/OS.
- Removed per-file "restoring started" UI/log events; completion events and block progress remain, greatly reducing dispatcher traffic on discs containing thousands of files.
## v0.2 responsive - default safety change
- Resurrect: **Allow missing files** now defaults to OFF.
- Resurrection only allows missing files when the checkbox is explicitly enabled.


## v0.3 DIC compile-fix
- Fixed CS0136 in the raw sequential resurrection path by separating the physical-map payload offset local from the normal restore-path offset local.
- Fixed nullable-analysis warnings in DIC metadata-buffer and sector-layout dictionary lookups.

## v0.3 DIC ISO/Joliet matching fix
- DIC source matching is case-insensitive on every platform.
- Treats ISO9660 and Joliet pathnames that resolve to the same physical extent and data length as aliases of one recovery entry.
- Tries every known ISO/Joliet alias for exact path+size and filename+size matching.
- Added conservative DOS 8.3-to-long-name matching for every path component, so paths such as `XTRAS/QUICKT~1/QTASSE~1.X32` can match long-name source trees.
- 8.3 matching always requires exact file size and a unique candidate; ambiguous candidates remain unresolved rather than being guessed.
- Unicode paths are normalised before comparison.

- Tightened DIC long/8.3 fallback matching: 6-char then 5-char stem prefix, exact extension (case-insensitive), exact byte size, unique candidate only.

## v0.3 DIC Joliet tree fix
- Fixed DIC synthetic skeletons advertising a Joliet SVD while leaving its path-table/directory sectors empty.
- When DIC mainInfo contains the SVD but not the supplementary tree, DumpToolbox now synthesizes a valid UCS-2BE Joliet path table and directory tree in the original SVD metadata area.
- Original file LBAs and byte lengths are preserved; only supplementary filesystem metadata is generated.
- Existing primary ISO9660 metadata from DIC remains untouched.

## v0.3 DIC matched Joliet long names
- After DIC source matching, the synthetic Joliet tree is regenerated using unambiguous matched source names.
- Path-based matches may contribute the full relative source path; weaker filename/prefix/size matches only contribute the long filename and keep the DIC directory path.
- Primary ISO9660 metadata, file extents, byte sizes, and recovered payloads are never changed by this naming pass.
- If the fully reconstructed long-name tree will not fit in the original Joliet metadata area, DumpToolbox retries with long filenames only; if that also will not fit, it leaves the already-valid synthetic Joliet tree unchanged rather than moving file data.
- Mode 2 XA filesystem sectors retain their original DIC File/Channel/Submode/CodingInfo bytes while EDC/ECC is regenerated.

### v0.3 DIC ISO/Joliet isolation fix
- Joliet long-name regeneration now explicitly discovers and snapshots the primary ISO9660 PVD/path-table/directory sectors before applying any supplementary-tree update.
- Any Joliet naming strategy that would overlap primary ISO9660 metadata is rejected.
- Primary ISO9660 metadata is restored byte-for-byte after the Joliet patch, guaranteeing that matched long names cannot leak into the ISO9660 tree.

## v0.3 DIC directory-source fix
- Joliet directory names are no longer taken from the recovery source-folder hierarchy.
- DIC pathname aliases are authoritative for directory naming when the DIC logs contain both ISO and Joliet forms.
- Matched source files may still contribute a long filename when DIC did not log a distinct long filename.
- If a DIC log set does not contain supplementary directory names, the short DIC directory names are retained rather than guessed.

## DIC donor image + cumulative recovery update

- Added DIC donor-image input for 2048-byte ISO and 2352-byte BIN/IMG images.
- Parses ISO9660/Joliet internally rather than requiring donor files to be extracted first.
- Exact PVD + volume-label match enables same-disc extent matching and copies original ISO/Joliet metadata into the DIC raw working image.
- Non-matching donors are still recursively searched for candidate files without copying their filesystem metadata.
- Raw 2352 donors can supply Mode 2 Form 2 payloads; cooked 2048 donors explicitly reject Form 2 entries.
- Added persistent `.dumptoolbox_dicstate.json` recovery state.
- Source-folder and donor matches accumulate across scans and application sessions.
- Successful partial/full rebuilds become cumulative working BINs, so already restored entries survive future sessions without requiring their original source files.
- Added `Clear saved matches` control on the DIC tab.

### v0.4.2b
- Fixed the final nullable-flow `CS8602` warning in the DIC recovery-state load path by normalizing `AppliedEntries` once into a non-null local collection.

## 0.7.52
- DIC CeQuadrat/WinOnCD Joliet geometry now takes precedence over the generic translated/paired Joliet allocators whenever the independently validated CeQuadrat link-table context is present.
- This ensures the final Joliet Type-L/Type-M path tables use the same ascending-primary-extent physical directory layout as the directory bodies and private CeQuadrat directory-link table.
- Fixes cases where v0.7.51 detected CeQuadrat correctly but a generic allocator succeeded first, leaving path-table extent fields on the old generic layout.

## v0.8.61
- Moved the IRD top-level tab to immediately after DIC.
- Removed the custom toolbox application/window icon and associated icon assets.
- Fixed nullable warnings in `MainWindow.DiscEvidence.cs` by guarding the optional settings store.
- Added optional PS3 IRD output encryption using a supplied 16-byte disc key.
- IRD accepts 16-byte binary `.key` files, 32-hex-character `.key`/`.txt` files, or a directly entered 32-character hex key (text entry takes precedence).
- Encryption follows the PS3 disc region map: plain regions are copied unchanged; encrypted regions use AES-128-CBC per 2048-byte sector with the sector LBA encoded as the IV.
- Encrypted output is retained only when all IRD region MD5s verify; otherwise it is deleted.
- Source-file verification is still reused when already valid, avoiding a second full source hash pass.

## v0.8.69
- Audio Recovery: Hail Mary exact-edge recovery is now opt-in with a dedicated checkbox and defaults off.
- Audio Recovery: `.bin` and `.iso` can be added as direct raw PCM/search byte-stream sources. They bypass decoding/conversion and are concatenated/searched exactly as supplied.
- Cleanup never deletes original direct BIN/ISO source files.
- Audio saved-input reset/persistence includes the Hail Mary preference.

## 0.8.70
- Audio Recovery Hail Mary now alternates forced-zero placement on both sides of the recovered missing segment.
- For each forced-zero length N, the physical outer-edge placement is tried first, then the inner boundary next to the anchored audio.
- The zero-length baseline is tried once; mixed inner+outer zero splits are intentionally not added.
- First-track recovery mirrors the final-track ordering.

## 0.8.71
- Added a global application-exit guard. If any long-running task is active in any tab, closing DumpToolbox now asks for confirmation and lists the active task(s).
- Choosing Cancel keeps DumpToolbox open; choosing Exit anyway closes the application.
- Included SHA-1 Database and developer Disc Evidence scans in the global busy-operation detection.

## v0.8.86

- DIC donor BIN/ISO matching now carries an unambiguously mapped Joliet pathname in each matched source record instead of discarding that namespace evidence after payload extraction.
- Donor Joliet names are treated strictly as pathname/casing evidence: primary DIC ISO9660 records remain authoritative for extent, size, flags and payload placement, and a non-exact donor still cannot copy filesystem metadata sectors.
- The donor-image scan now runs the same Joliet synthesis/update stage used after an extracted-folder source scan, allowing image-backed sources to reproduce long-name/casing metadata without first copying files out of the disc.
- Updated donor logging to distinguish pathname evidence from forbidden donor metadata copying.

## v0.8.85
- Fixed SkeleTool direct ISO/BIN source handling for ISO9660 multi-extent files. Consecutive 0x80 continuation records are now collapsed into one logical file and hashed as the concatenation of all extents.
- Image-backed source matches now retain the complete logical extent map. Resurrection reads the same extent list used for verification instead of assuming the file is one contiguous LBA range.
- Raw 2352-byte BIN image sources continue to expose only Mode 1 / Mode 2 Form 1 2048-byte logical filesystem payload bytes, with the exact file byte length respected at each extent end.
- SHA-1 catalogue schema bumped to v4 and now stores an optional image extent map. Existing ISO9660 catalogue units are invalidated once during migration so previously single-extent-indexed images are rescanned correctly.
- Audited DIC donor-image extraction: it already combines multi-extent ISO9660 records and streams all extents in order (including EAR/interleave handling), so no duplicate DIC-specific workaround was added.
