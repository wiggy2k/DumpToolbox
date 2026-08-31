## v0.6.17 Associated-file extraction workflow

When DIC reports ISO9660 Associated File records (`File Flags 0x04`), a normal mounted filesystem may expose only the non-associated record at that pathname. Use the **ISO Extractor** tab in DumpToolbox on a source ISO/BIN. Its output folder contains normal files plus `.dumptoolbox_iso_manifest.json` and `.dumptoolbox_iso_records`; select that folder as the DIC Source Folder. DumpToolbox then matches hidden records by original ISO pathname, extent, size and File Flags rather than guessing from host filenames.

# DiscImageCreator recovery

The **DIC** tab reconstructs a raw 2352-byte working image from historical DiscImageCreator logs and recovered file payloads. It is independent from the Redumper **Skeletool** workflow.

### Joliet Type-L / Type-M path tables

v0.7.16 preserves the exact path-table location fields from the DIC/mainInfo Supplementary Volume Descriptor. Joliet Type-L tables are generated with little-endian numeric fields and Type-M tables with big-endian numeric fields; optional copies are emitted when the original SVD declares them. The SVD locations are evidence and are not normalized or cleared. Black Mirror uses Type-L at LBA 27 and Type-M at LBA 28.


## Files used

| DIC file | Purpose |
| --- | --- |
| `*_volDesc.txt` | Primary ISO9660 paths, extents, sizes, recording timestamps and volume information. Required. |
| `*_disc.txt` | Track geometry and expected original image hashes when available. |
| `*.img_EccEdc.txt` / `*.scm_EccEdc.txt` | Per-sector Mode 1 / Mode 2 Form 1 / Mode 2 Form 2 layout and XA subheaders. The full log is parsed; the explicit DIC ECC/EDC `[ERROR]` sector list is additionally captured for exact mastering-fault reproduction. |
| `*_mainInfo.txt` | Original dumped ISO9660 metadata sectors where DIC preserved them. |

## ISO9660-only policy

The primary ISO9660 filesystem remains the authoritative DIC structure for extents, sizes and logged metadata. Historical DIC logs usually preserve the Joliet Supplementary Volume Descriptor but not its directory/path-table sectors. v0.7.16 can reconstruct that missing supplementary tree **only after a normal source folder supplies validated Joliet/user-visible paths**. The source names are correlated back to unique primary ISO records; primary metadata itself is never replaced by the inferred Joliet names.

The file matcher is deliberately strict and case-insensitive: the source must have the exact primary ISO9660 relative path, exact filename and exact logical byte length. There are no 8.3/prefix/timestamp/filename-only/size-only fallbacks. See `DIC_FILENAME_MATCHING.md`.

## Donor images

A 2048-byte ISO or 2352-byte BIN/IMG can be supplied as a donor. Only its primary ISO9660 tree is parsed. If its PVD and volume label exactly match the DIC working image, original primary ISO9660 metadata sectors may be donated. Donor file payloads are still accepted only when their primary ISO9660 relative path, filename and exact byte length match the DIC entry case-insensitively. If identity does not match, no donor metadata is copied; its ISO9660 files are searched using those same strict matching rules.


### `mode 2 no edc` and explicit ECC/EDC errors

DumpToolbox does not treat the text `mode 2 no edc` as synonymous with Mode 2 Form 2. The XA Submode Form bit from the per-sector log determines whether the sector is Form 1 or Form 2. Genuine Form 2/no-EDC sectors retain the Form-2 layout and omit the optional EDC.

The separate DIC `[ERROR] Number of sector(s) where user data doesn't match the expected ECC/EDC` `Sector:` list is parsed into an exact LBA set. Only Mode 2 Form 1 sectors in that explicit error set receive the reverse-engineered DIC mastering-fault Q-ECC calculation. Other sectors continue to use normal EDC/ECC generation.

A cooked 2048-byte donor is not used for entries requiring Mode 2 Form 2 payload bytes.

## Persistent recovery

DIC matches and applied entries are persisted beside the logs in `.dumptoolbox_dicstate.json`. Multiple source folders and donor images can therefore be supplied across separate sessions. Once a payload has been committed to a cumulative rebuilt BIN, it does not need to be found again on subsequent runs.

## Primary ISO9660 path tables

DIC `_volDesc.txt` records contain the parsed primary ISO9660 path-table entries even when `_mainInfo.txt` does not contain a raw dump of the path-table sector itself. DumpToolbox reconstructs those entries and writes both Type-L and Type-M copies using the original locations from the primary PVD when available. This prevents filesystem path-table sectors from being left as zero-filled payloads.


## v0.6.16 duplicate-path source policy

A normal ISO 9660 record and an Associated (`0x04`) record may legitimately share a visible pathname. DumpToolbox treats the non-associated record as the ordinary mounted/extracted source and requires a donor only for a non-empty Associated payload. If two or more non-associated records normalize to the same mounted pathname, DumpToolbox does not assume that differing byte sizes mean both were exposed by the host filesystem; those records require an exact donor unless they form a valid Multi-Extent chain.


### v0.7.1 exactness coverage

The importer distinguishes mandatory donor regions from optional exactness regions. Optional regions include unproven system-area payload, file tail slack, synthesized/missing metadata sectors and post-volume sectors. They remain zero-filled for best-effort resurrection when no donor is available, but a verified same-disc donor can overwrite them with exact sectors. The `_mainInfo.txt` drive-offset checks are also decoded and used as byte-level evidence for early system-area sectors.

### Older volDesc logs without FullPath

Older DIC versions can log complete ISO9660 directory records but omit `FullPath:`. The importer reconstructs the primary directory tree from the path table (`Directory Identifier`, parent directory number and extent), maps directory continuation sectors, and combines that context with each file record's `File Identifier`. Reconstructed paths are reported in the import warnings.


## v0.7.11 EccEdc evidence handling

DIC EccEdc sector records are now indexed by their physical order in the checked IMG. The printed `LBA[...]` is retained as header/reported evidence and is not assumed to be the file offset when headers are malformed or track/session coordinates jump.

Exact logged MSF bytes and XA subheader copies are retained. Mode 0 is a real sector class; audio is opaque raw content. Invalid/zero sync, unresolved Block Indicators, unequal XA copies, unknown corruption and other raw-only anomalies are surfaced as exactness regions requiring a 2352-byte donor for byte-perfect recovery.

An explicit ECC/EDC error list does not by itself choose a corruption algorithm. The proven Warcraft II Q-ECC recipe is fingerprinted narrowly; EccEdc's explicit `0x55 except header` recipe is count-checked before use; all other corruption is left unknown rather than guessed.

Generic `mainError` read-padding/history and raw `Main Channel` captures are not promoted to final IMG bytes in this version because sampled DIC versions show that they can represent transient retries or pre-descramble SCM-domain data.



## v0.7.13 EccEdc summary mapping hotfix

v0.7.13 fixes the v0.7.12 regression where summary-only historical anomalies could become unmapped if their per-sector lines did not repeat the anomaly text. The mapper now prefers positively identified anomaly records sharing a reported/header LBA (needed for SmartE and similar repeated-header protections), but falls back to exact physical/reported mapping when no such anomaly-marked candidate exists (needed for the Warcraft II summary-only Q-ECC list).

Regression fixtures from the supplied log batches:

- Warcraft II: 68,736 ECC/EDC summary occurrences -> 68,736 physical sectors, 0 unmapped.
- Zoo Tycoon 2 / SmartE: ten summary occurrences all reporting LBA 192302 -> physical sectors 192303-192312; normal physical sector 192302 excluded.

## v0.7.12 final-state and historical-log evidence

Historical recovery bundles can contain more than one sector-state view of a disc. The original `*.img_EccEdc.txt` can describe an early DIC dump with read substitutions or damaged framing, while a later complete `*EdcEcc_Track_*.txt` can describe the repaired image that was actually retained. DumpToolbox only promotes a later checker map when it covers the complete image in absolute physical-LBA order and the DIC logs do **not** already provide an original whole-image CRC/MD5/SHA-1 anchor. Hash-anchored DIC images always keep the original hash as the target.

EccEdc's per-sector statement `2336 bytes have been already replaced at 0x55` is treated as exact final-image evidence: preserve/generate the 16-byte sync+header and set raw bytes 16-2351 to `0x55`. This is generic evidence and is not tied to a protection name.

Some submitted recovery bundles also contain extensionless files named only by decimal LBA. A 2352-byte candidate is accepted only when its raw sync, canonical MSF and logical mode validate for the filename LBA and it does not conflict with the final map's explicit `0x55` state. Accepted files become exact raw-sector overrides and are reapplied after source payloads/donors, so later recovery passes cannot overwrite them.

If an EccEdc text record is malformed, DumpToolbox stops trusting physical-ordinal continuation at that point. Later records may have been shifted by lost/corrupted text, so uncovered ranges fall back to `disc.txt` track geometry rather than guessed per-sector evidence.

`mainError` remains an event/history log except for narrowly deterministic statements. `All zero sector. Skip descrambling` is accepted as proof of an exact all-zero 2352-byte final sector; generic read-padding events and arbitrary raw captures are not.

### v0.7.17 Joliet directory flags

When reconstructing a Joliet tree from validated source names, directory flags come from the corresponding primary ISO9660 entry in its **parent directory**. The `.` record inside a directory may supply that directory's own timestamp, but it must not replace the parent entry's flags. This matters for hidden directories: Black Mirror records `LASERLOK` as flags `0x03` (Hidden + Directory), while its internal `.` record is `0x02`.


### v0.7.18 root-only / primary-compatible Joliet source trees

When a DIC disc contains a Joliet SVD, the ordinary additional source folder is treated as the user-visible/Joliet namespace for naming evidence. A source file that matches a DIC primary ISO9660 record exactly by relative path, filename and byte length still contributes its original source-relative spelling to Joliet reconstruction. This matters when every Joliet name is also primary-ISO-compatible, because no file needs the long-name projection fallback to prove the namespace.

This promotion applies only to normal source-folder scans. Same-disc donor-image matches and ISO Extractor manifest matches remain separate evidence classes and are not treated as Joliet naming evidence merely because they match the primary ISO tree.
