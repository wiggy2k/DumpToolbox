# SkeleTool SHA-1 catalogue schema v2 (v0.8.47)

The catalogue now normalizes file hashes into a dedicated `hashes` table. SHA-1 digests are stored as 20-byte BLOB values and filesystem file rows retain only `image_id`, relative path, `hash_id`, and optional image LBA. File scan timestamps are inherited from their source/image scan rather than duplicated on every row.

A schema-v1 database is upgraded automatically on first open. The migration reuses the hashes already present in SQLite, does not reread collection images, and performs a final VACUUM so the old text/hash-index pages are physically reclaimed.

# SkeleTool SHA-1 catalogue (v0.8.40)

The SHA-1 database is populated only from collection folders registered in Settings and is independent from SkeleTool reconstruction runs.

Registered roots keep added/last-scan/last-success/error state. Each direct disc image or parent archive stores its current path, relative path, source kind, byte size, modification timestamp, whole-source SHA-1, present/missing state, first/last seen and last-scanned timestamps. Archive entries record the internal image path. Files inside a disc filesystem record relative path, size, SHA-1, optional ISO image LBA and scan timestamp.

Deleted/moved sources are never destructively removed from the catalogue. After a successful root enumeration an unseen source becomes unavailable; its image/file hashes remain. If a matching whole-source SHA-1 is later found, the existing source is made available at the new path without rescanning its filesystem.

Uncompressed `.iso` and `.bin` images are scanned. When a CUE is present, its referenced BINs are treated according to the CUE and only data tracks are scanned. CUE INDEX geometry is used to isolate data-track extents from single-file mixed-mode images.

Archives are SHA-1 hashed before their contents are parsed. ZIP/TorrentZip, 7z/solid archives, Zstandard, RAR and the other archive extensions recognised by the scanner all use the same bundled in-process 7-Zip engine.

The built-in scanner preserves ISO9660 LBAs so SkeleTool can later read matching payloads directly from an image. If the built-in ISO9660 scanner cannot enumerate a disc image, the bundled in-process 7-Zip engine is used as a filesystem fallback; those files are extracted from the image on demand when SkeleTool needs them.

Lookup priority is: explicit local SkeleTool folder/image sources; present direct/uncompressed catalogue images; present archive-backed catalogue images. Missing historical sources never satisfy a reconstruction.


## Archive engine (v0.8.40)

Archive access is entirely in-process and pure managed. DumpToolbox uses SharpCompress for supported ZIP/TorrentZip, 7z/solid 7z, RAR, TAR and gzip/bzip2/xz/zstd-family archive inputs. There is no bundled native `7z.dll`, no temporary native-library extraction, no `7z.exe`/`7zz.exe`/`7za.exe`, no PATH probing and no separately installed copy of 7-Zip.

The same backend is used for ordinary archive scanning, solid archives, on-demand extraction of an image from an archive, and the ISO/UDF fallback path used when the built-in filesystem scanner cannot enumerate an image.

## v0.8.42 scanning behaviour

CUE sheets are authoritative for BIN files they reference. Referenced AUDIO tracks are never offered to the filesystem scanner; referenced data tracks are scanned only over the extent described by their CUE INDEX geometry. A lightweight FILE-reference pass also prevents unusual CUE-controlled BINs from falling back to standalone probing if the full CUE analyser rejects the sheet.

Catalogue source scanning is parallel across independent direct images and archives. The Settings > SHA-1 Database tab exposes a 1-64 worker setting (remembered in DumpToolbox.ini); expensive hashing, extraction and filesystem parsing can run concurrently while SQLite catalogue operations remain serialized.

## Scan activity and fault isolation

The Settings > SHA-1 Database tab includes a live scan log which can be undocked into a separate window. Independent archive/image sources are fault-isolated: a read, extraction, CUE, or filesystem error is logged and the remaining scan queue continues. Only explicit cancellation or a root/global catalogue failure stops the scan. A source that could not be processed is not treated as deleted, and an incomplete scan unit is never used automatically by SkeleTool; it remains eligible for retry on the next change check.
