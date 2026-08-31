# FindCRCs

FindCRCs locates and extracts known files from a larger source image by CRC32, with MD5 verification when supplied.

## Inputs

- A source image or binary file.
- Hash targets pasted as Redump rows, `SIZE CRC32 [MD5] [SHA1]`, or XML DAT content.
- A Redump disc URL or numeric disc ID. DumpToolbox imports the public BIN rows and downloads the companion CUE when available.
- An optional CUE for track geometry and audio-edge recovery.

XML DAT `<rom>` entries require `size` and `crc`; `name`, `md5`, and `sha1` are retained. CUE entries in a DAT are ignored because they are metadata rather than payload targets.

## Search behaviour

- Searches every byte by default, or only 2352-byte CD-sector boundaries when alignment is enabled.
- Uses rolling CRC32 for candidate discovery and verifies MD5 before accepting a candidate when an MD5 is present.
- Supports 64-bit offsets and streams the source rather than loading it into memory.
- Tests the exact end of the previous match first for sequential targets.
- Extracts verified matches beside the source image using the supplied filename where possible.

## Audio-edge recovery

Enable **Attempt to fix under-dumped Audio edges** for incomplete first or final audio tracks. A CUE normally identifies audio tracks and supplies their physical boundaries. A no-CUE fallback is limited to the safe two-target case where exactly one target is already verified and the source boundary proves the other extent.

DumpToolbox may:

1. add the exact missing amount as digital zero silence;
2. shift an exact-sized track within zero bytes already verified at its edges;
3. derive the CRC32 of a missing prefix or suffix and search the source for it;
4. save bounded `.partial` candidates for manual inspection when requested.

Missing non-zero audio is never invented. Every automatic repair must reproduce the target CRC32 and MD5 when supplied.

## Mixed-mode Track 02 pregaps

With a mixed-mode CUE, DumpToolbox can account for Track 02 pregaps that differ from the usual 150 frames. It can correct recognised empty raw data sectors, test a Track 03-anchored Track 02 boundary, and evaluate whole-sector pregap rebalancing. Geometry alone is never accepted as proof; the complete target hashes must verify.

## Outputs and safety

- Matches are written as separate files.
- Optional partials are clearly suffixed and never treated as verified output.
- Source files are read-only.
- Cancellation stops searching without modifying the source.
