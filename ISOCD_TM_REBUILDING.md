# ISOCD/Pantaray FS/TM rebuilding (v0.8.96)

ISOCD 1.04 by Pantaray can store a CDTV/CD32 trademark payload outside ordinary ISO9660 files. The payload is described by an `FS/TM` record in the PVD Application Use field. Ordinary extracted source files therefore cannot restore it.

DumpToolbox now handles this in the shared resurrection path used by both DIC and SkeleTool. It scans the ISO9660 PVD, requires the Data Preparer field to identify ISOCD/Pantaray, parses the FS/TM length and LBA, and selects an embedded payload only for the two proven sizes:

- `CDTV.TM`: 22,152 bytes, SHA1 `fd3e764e6393974dea05612909e25ddb2124eb8b`
- `CD32.TM`: 2,048 bytes, SHA1 `c5ffcef2a5e33d2df606185823cd95d1c174d65f`

The repair is conservative: it only writes when the exact TM byte range is still all zero. If the range already equals the standard payload it is left untouched. If it contains other non-zero bytes, those bytes are treated as stronger evidence and are preserved. For raw CD images, the logical payload is inserted and EDC/ECC is regenerated while preserving existing sync/MSF/mode framing.

Confirmed examples:
- Astro Revisited: FS/TM length 22,152, LBA 21; exact expected CRC32/MD5/SHA1 after restoration.
- ZGR3D: same CDTV.TM geometry; exact expected CRC32/MD5 after restoration.
- Ultimate Body Blows: FS/TM length 2,048 at LBA 6021; the sector payload is byte-identical to the embedded `CD32.TM`.
