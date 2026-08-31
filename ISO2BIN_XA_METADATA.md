# iso2bin XA metadata

The iso2bin tab can optionally recover the four XA subheader bytes that are lost when a Mode 2 Form 1 disc is stored as a cooked 2048-byte-per-sector ISO.

Supported metadata sources:

- DiscImageCreator `*_EccEdc.txt` / `.img_EccEdc.txt` / `.scm_EccEdc.txt` logs.
- Raw 2352-byte Redumper `.skeleton` images. The skeleton must contain normal raw CD sync/header sectors; a cooked 2048-byte skeleton cannot supply XA subheaders.

For each MODE2/2048 sector the converter uses the source metadata to restore:

- File Number
- Channel Number
- Submode, including EOF/EOR/data flags
- Coding Information

The four bytes are duplicated as required by Mode 2 XA, then the 2048-byte cooked payload is inserted and EDC/P-Q ECC are regenerated.

If no metadata entry exists for a Mode 2 Form 1 LBA, iso2bin falls back to `00 00 08 00`, preserving the previous behaviour. If metadata says the corresponding sector is Mode 2 Form 2, conversion fails explicitly because a 2048-byte source does not contain the required 2324-byte Form 2 payload.

When a CUE is used, XA metadata is applied only to cooked `MODE2/2048` tracks. `MODE1/2048`, AUDIO, `MODE1/2352` and `MODE2/2352` tracks keep their normal conversion/copy behaviour.
