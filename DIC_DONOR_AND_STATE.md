# DIC donor images and persistent state

The DIC tab accepts a cooked 2048-byte ISO or raw 2352-byte BIN/IMG as an optional donor.

Only the donor's **primary ISO9660** filesystem is parsed. Supplementary/Joliet descriptors are ignored.

If both the Primary Volume Descriptor and volume label match the DIC reconstruction, the donor is treated as the same disc. DumpToolbox can then copy the donor's primary ISO9660 descriptor/path-table/directory sectors into the working raw image. Payload matching remains strict: exact primary ISO9660 relative path, exact filename and exact byte length, compared case-insensitively.

If the PVD or volume label differs, no filesystem metadata is copied. The donor's ISO9660 tree is searched using the same strict case-insensitive relative-path + filename + exact-length rules. No heuristic or timestamp fallback is used.

Recovery state is saved in `.dumptoolbox_dicstate.json`; donor-extracted payloads are cached in `.dumptoolbox_dic_donor_cache`.


## Mandatory donor conditions (v0.6.16)

DumpToolbox now requires an exact same-disc donor only for bytes that cannot be derived safely from the DIC logs plus ordinary extracted files.

A donor is mandatory for:

- a **non-empty Associated File** payload (`File Flags 0x04`);
- actual **Extended Attribute Record (EAR)** blocks (`Extended Attribute Record Length > 0`);
- **multiple non-associated ISO records that normalize to the same mounted pathname**, regardless of whether their byte lengths differ, because a mounted filesystem cannot be assumed to expose every colliding record.

A donor is **not** required solely for:

- zero-length Associated records;
- Record (`0x08`) or Protection (`0x10`) flags when no EAR blocks exist;
- reserved File Flag bits (`0x20`/`0x40`) — these are preserved and logged as non-standard;
- Multi-Extent (`0x80`) files;
- interleaved files with File Unit Size / Interleave Gap Size;
- a **normal + Associated** same-name pair: the normal/non-associated record remains recoverable from the ordinary mounted/extracted file; only a non-empty Associated payload requires donor data.

For files with an EAR, only the EAR region is copied from the donor; the actual file bytes remain recoverable from a normal source folder. Interleaved files are restored by splitting the source into the ISO-defined file units and leaving interleave-gap sectors untouched. If an EAR is present on an interleaved file, the EAR occupies the first file unit and file payload begins in the next file unit.

When the donor is a raw 2352-byte BIN, mandatory regions are copied byte-for-byte. A cooked 2048-byte ISO can satisfy a mandatory region only when that region does not require Mode 2 Form 2 payload bytes.

Recovery state is version 6 because the donor-region policy and duplicate-entry identities changed.



### v0.7.1 exactness coverage

The importer distinguishes mandatory donor regions from optional exactness regions. Optional regions include unproven system-area payload, file tail slack, synthesized/missing metadata sectors and post-volume sectors. They remain zero-filled for best-effort resurrection when no donor is available, but a verified same-disc donor can overwrite them with exact sectors. The `_mainInfo.txt` drive-offset checks are also decoded and used as byte-level evidence for early system-area sectors.

Recovery state schema is v10 as of DumpToolbox 0.7.5. Older state is intentionally discarded so cumulative BINs created before the current coverage, DIC ECC-fault and raw-donor ordering logic are not silently reused.

## DIC logged ECC faults and raw donors (v0.7.5)

A raw 2352-byte same-disc donor can provide bytes that ordinary files/logs do not prove, but its physical EDC/ECC representation is not allowed to override an error pattern explicitly proven by the target DIC EccEdc log. After a raw donor sector is copied for an exactness requirement, DumpToolbox reapplies the target DIC Mode 2 Form 1 protection-field fault when that LBA is in the explicit error set.

Recovery state schema v10 invalidates older state so a skeleton modified by the v0.7.4 donor-copy ordering cannot be resumed silently.
