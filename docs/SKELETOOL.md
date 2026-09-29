# SkeleTool

SkeleTool is an independent implementation of the Redumper skeleton/hash restoration workflow. It rebuilds a data-track `.skeleton` from its `.hash` manifest and verified source payloads.

## Workflow

1. Select a Redumper `.skeleton` or a compressed/archive-contained skeleton; the matching `.hash` is suggested automatically.
2. Load the pair and inspect the filesystem tree.
3. Scan a source folder or source ISO/BIN/IMG, including UDF-only images, and optionally query the SHA-1 catalogue.
4. Review found, missing, special and XA/Form 2 entries.
5. Resurrect to a new output image.

Source filenames do not need to match the image: normal files are matched by SHA-1 and logical size. A source ISO/BIN scan also hashes the target `SYSTEM_AREA` and `GAP_#######` logical regions at their recorded LBAs, so matching regions from an alternate pressing can be restored directly. Alternate `.XA` payload hashes are recognised from suitable raw source files.

An ordinary ISO file whose skeleton-declared logical length is exactly zero needs no source payload. If Redumper omitted it from the `.hash` manifest, SkeleTool assigns the canonical empty-stream SHA-1 (`da39a3ee5e6b4b0d3255bfef95601890afd80709`), marks it satisfied, and does not report it as a missing manifest entry. This inference is not applied to GAP or `SYSTEM_AREA` records.

Raw Redumper GAPs are also checked for the repeatable zero/`0x55` layout left by Redumper's skeleton creator. SkeleTool follows Redumper's exact hashing rules: recognised Mode 1/Form 1 and Form 2 payloads participate in their corresponding hashes, while invalid-mode or invalid-sync sectors remain untouched and are omitted. A generated pattern is offered only after every available GAP SHA-1 matches. During resurrection, normal zero sectors receive regenerated protection fields, preserved invalid sectors are copied unchanged, and Redumper-generated `0x55` sectors retain their deliberately filled EDC/ECC bytes.

When a same-named `.sub`, `.subcode`, or `.subchannel` companion is present, SkeleTool analyses it alongside each GAP. Zstandard-compressed companions such as `.subcode.zst` are streamed directly. The activity log reports the covering track/index, a boundary at the GAP start, P-channel assertions, Q CRC/time faults near the trailing boundary, lead-out position, and non-zero raw R-W bytes. This is supporting evidence only: subchannels never supply or prove missing main-channel payload bytes, and cannot make an otherwise-unverified GAP recoverable.

Historical raw-LZMA skeletons are detected from their content even when they still use the `.skeleton` extension. Zstandard streams and skeletons stored in 7z, ZIP, RAR, gzip, bzip2, XZ, or TAR archives are also accepted. An archive must contain one `.skeleton` entry, or a uniquely matching entry when several are present. SkeleTool expands it beside the selected input, shows load progress, and leaves the compressed file unchanged. A compressed file already named `name.skeleton` uses `name.skeleton.temp`; a wrapper such as `name.skeleton.zst` uses `name.skeleton`. If that working filename already exists, a numbered `.temp` name is used without overwriting it.

When a skeleton contains Nero's hidden `!!MSxxxx.NRI` project record and a verified `NeroISO` payload, SkeleTool can regenerate the associated 32-byte record in sector 15 automatically. The filename, extent and length come from the hidden directory record. When a matching Redumper `.log` supplies the final whole-image CRC32 and every other required payload is available, SkeleTool rebuilds the image with a zero private value, measures the 32 individual bit effects (including raw-sector EDC/ECC), and solves the four bytes directly. The generated 32 KiB system area is accepted only after its SHA-1 matches the manifest. If a suitable whole-image CRC32 is unavailable or its candidate fails that check, SkeleTool automatically falls back to the cancellable vectorized SHA-1 search with progress, rate and worst-case remaining time.

SkeleTool also recognises repeatable mastering layouts that do not require an external `SYSTEM_AREA` file. These include known Personal RomMaker sector strings, `Dummy Apple Volume` records, three-entry Apple partition maps whose geometry can be recovered from the surviving classic-HFS header, and the repeated Riven HFS bitmap layout containing all-`FF` sectors. The complete generated 32 KiB area must match the manifest SHA-1; a partial signature or merely non-zero system area is never accepted as proof.

If direct Nero evidence references an NRI payload that is missing or lacks a valid length-prefixed `NeroISO` signature, SkeleTool loads with a visible warning explaining that the NRI payload cannot be generated and an exact source image or NRI file is required. Direct evidence means an embedded `!!MSxxxx.NRI` directory record or a structurally valid Nero sector-15 record; an arbitrary non-zero `SYSTEM_AREA` never triggers the warning.

UDF source files are hashed and streamed as logical file contents. UDF cannot supply an alternate raw 2324-byte Mode 2 Form 2 `.XA` payload, because that physical-sector information is outside the filesystem file stream.

The SHA-1 catalogue deliberately hashes UDF file payloads independently of the UDF partition and VAT layout. Disc Evidence records descriptor/VAT fingerprints and previous generations separately, so two discs with identical files can still be distinguished by their mastering structures without preventing payload reuse.

## Sector handling

- Cooked 2048-byte skeletons are patched directly.
- Raw Mode 1 and Mode 2 Form 1 sectors retain their framing and have EDC/ECC rebuilt after payload insertion.
- Mode 2 Form 2 retains XA framing and receives its optional EDC unless evidence identifies a no-EDC sector.
- ISOCD/Pantaray FS/TM trademark payloads are restored only for recognised PVD records and only when the target range is still zero. Conflicting non-zero evidence is preserved.

The source skeleton is never modified. Output is written through a partial file and can optionally retain zero-filled missing regions for an explicitly allowed partial resurrection.

## SHA-1 catalogue

Collection roots are managed under **Settings → SHA-1 Database**. Direct ISO/BIN/IMG images, standalone Nero NRI files and supported archives are indexed in `skeletool_sha1_catalogue.sqlite`.

- CUE geometry controls referenced BIN tracks; audio tracks are not offered to the filesystem scanner.
- ISO9660 and UDF-only data images are indexed directly. Selected UDF catalogue matches are materialized only when resurrection starts.
- Verified hidden `!!MSxxxx.NRI` payloads are indexed with their exact image LBA and length. Standalone `.NRI` files with a valid `NeroISO` signature are indexed as direct payloads.
- Filesystem hashes and image locations are stored compactly; archive payloads are materialized only when a selected match is actually needed for resurrection.
- Direct local sources have priority over catalogue images, which have priority over archive-backed matches.
- Missing historical sources remain recorded but cannot satisfy a rebuild.
- Independent sources scan concurrently while SQLite writes remain serialized.
- Failed units remain retryable and are not used automatically.

Materialized archive/image data is temporary working state and is cleaned from the OS temporary directory. The SQLite catalogue remains beside the executable.
