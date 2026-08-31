# SkeleTool

SkeleTool is an independent implementation of the Redumper skeleton/hash restoration workflow. It rebuilds a data-track `.skeleton` from its `.hash` manifest and verified source payloads.

## Workflow

1. Select a Redumper `.skeleton`; the matching `.hash` is suggested automatically.
2. Load the pair and inspect the filesystem tree.
3. Scan a source folder or source ISO/BIN, and optionally query the SHA-1 catalogue.
4. Review found, missing, special and XA/Form 2 entries.
5. Resurrect to a new output image.

Source filenames do not need to match the image: normal files are matched by SHA-1 and logical size. `SYSTEM_AREA`, `GAP_#######`, and alternate `.XA` payload hashes are recognised.

## Sector handling

- Cooked 2048-byte skeletons are patched directly.
- Raw Mode 1 and Mode 2 Form 1 sectors retain their framing and have EDC/ECC rebuilt after payload insertion.
- Mode 2 Form 2 retains XA framing and receives its optional EDC unless evidence identifies a no-EDC sector.
- ISOCD/Pantaray FS/TM trademark payloads are restored only for recognised PVD records and only when the target range is still zero. Conflicting non-zero evidence is preserved.

The source skeleton is never modified. Output is written through a partial file and can optionally retain zero-filled missing regions for an explicitly allowed partial resurrection.

## SHA-1 catalogue

Collection roots are managed under **Settings → SHA-1 Database**. Direct ISO/BIN images and supported archives are indexed in `skeletool_sha1_catalogue.sqlite`.

- CUE geometry controls referenced BIN tracks; audio tracks are not offered to the filesystem scanner.
- Filesystem hashes and image locations are stored compactly; archive payloads are materialized only when a selected match is actually needed for resurrection.
- Direct local sources have priority over catalogue images, which have priority over archive-backed matches.
- Missing historical sources remain recorded but cannot satisfy a rebuild.
- Independent sources scan concurrently while SQLite writes remain serialized.
- Failed units remain retryable and are not used automatically.

Materialized archive/image data is temporary working state and is cleaned from the OS temporary directory. The SQLite catalogue remains beside the executable.
