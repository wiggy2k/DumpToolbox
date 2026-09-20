# SkeleTool

SkeleTool is an independent implementation of the Redumper skeleton/hash restoration workflow. It rebuilds a data-track `.skeleton` from its `.hash` manifest and verified source payloads.

## Workflow

1. Select a Redumper `.skeleton` or a compressed/archive-contained skeleton; the matching `.hash` is suggested automatically.
2. Load the pair and inspect the filesystem tree.
3. Scan a source folder or source ISO/BIN/IMG, including UDF-only images, and optionally query the SHA-1 catalogue.
4. Review found, missing, special and XA/Form 2 entries.
5. Resurrect to a new output image.

Source filenames do not need to match the image: normal files are matched by SHA-1 and logical size. `SYSTEM_AREA`, `GAP_#######`, and alternate `.XA` payload hashes are recognised.

Historical raw-LZMA skeletons are detected from their content even when they still use the `.skeleton` extension. Zstandard streams and skeletons stored in 7z, ZIP, RAR, gzip, bzip2, XZ, or TAR archives are also accepted. An archive must contain one `.skeleton` entry, or a uniquely matching entry when several are present. SkeleTool expands it beside the selected input, shows load progress, and leaves the compressed file unchanged. A compressed file already named `name.skeleton` uses `name.skeleton.temp`; a wrapper such as `name.skeleton.zst` uses `name.skeleton`. If that working filename already exists, a numbered `.temp` name is used without overwriting it.

When a skeleton contains Nero's hidden `!!MSxxxx.NRI` project record and a verified `NeroISO` payload, SkeleTool can regenerate the associated 32-byte record in sector 15 automatically. The filename, extent and length come from the hidden directory record; the four Nero-private bytes are recovered against the manifest's `SYSTEM_AREA` SHA-1. Resurrection displays a cancellable progress window with search rate and worst-case remaining time. The generated 32 KiB system area is accepted only after its SHA-1 matches the manifest.

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
