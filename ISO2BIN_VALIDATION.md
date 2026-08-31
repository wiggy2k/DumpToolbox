# ISO2BIN implementation / regression notes

The ISO2BIN sector generator follows the standard CD-ROM raw sector layouts and the classic ECC/EDC algorithm used by ECM/eccedc implementations.

## Sector layouts

### Mode 1

- 0..11: sync `00 FF FF FF FF FF FF FF FF FF FF 00`
- 12..14: BCD MSF address (`LBA + 150` frames)
- 15: mode `01`
- 16..2063: 2048 input bytes
- 2064..2067: EDC over bytes 0..2063
- 2068..2075: eight zero reserved bytes
- 2076..2247: P parity
- 2248..2351: Q parity

### Mode 2 Form 1

- 0..11: sync
- 12..14: BCD MSF address
- 15: mode `02`
- 16..23: duplicated XA subheader; DumpToolbox uses `00 00 08 00 00 00 08 00`
- 24..2071: 2048 input bytes
- 2072..2075: EDC over bytes 16..2071
- 2076..2247: P parity
- 2248..2351: Q parity
- Address/mode bytes 12..15 are temporarily zeroed while computing Mode 2 Form 1 ECC, then restored.

## Regression vectors

These vectors use a 2048-byte all-zero input sector at LBA 0, therefore an MSF address of `00:02:00`.

- Mode 1 raw-sector SHA-256: `b4f18ab66709c9b3fdef2721cc323e031b6728f3ca6c57b7c435c96189222250`
- Mode 2 Form 1 raw-sector SHA-256: `019d8734ffd82181a112f3b3c485312e8cfc1e37c1fc85374606669356ee35b8`

The values were cross-checked against an independent C implementation of the same published ECC/EDC algorithm during the DumpToolbox conversion work.

## Auto mode

A cooked ISO does not store the original physical sector mode. DumpToolbox therefore treats Auto as a filesystem hint:

1. Inspect ISO9660 volume descriptors from sector 16 onward.
2. If `CD-XA001` is present at descriptor offset 1024, suggest/use Mode 2 Form 1.
3. If ISO9660 descriptors are present without the XA marker, use Mode 1.
4. If no useful descriptor is found, use Mode 1 and report the ambiguity.

Manual Mode 1 / Mode 2 Form 1 selection overrides Auto.

## Mixed-mode CUE validation

For a single-file mixed CUE, validation is performed per track rather than requiring the whole backing file to be divisible by 2048.

Example logical source layout:

- Track 01 `MODE1/2048`, 100 sectors => 204800 source bytes.
- Track 02 `AUDIO`, 50 sectors => 117600 source bytes.

The backing file must therefore be exactly 322400 bytes. The converted output contains 150 × 2352 = 352800 bytes. The replacement CUE keeps the same INDEX frame positions, changes Track 01 to `MODE1/2352`, and leaves Track 02 as `AUDIO`.

The mixed-mode converter rejects a CUE if the calculated per-track byte layout does not account for the backing file exactly.

## Optional Redump target length / hash validation

For ISO-only conversion, one Redump target row may be supplied. Its raw byte size must be an exact multiple of 2352 and becomes the authoritative output sector count.

- If the input contains fewer 2048-byte sectors, DumpToolbox supplies zero-filled cooked sectors after EOF before generating their raw headers/EDC/ECC.
- If the input contains more sectors, only the number required by the Redump target is converted; the source ISO itself is not altered.
- A supplied Redump `.bin` filename is used as the preferred output filename.
- After conversion, size and CRC32 are checked, MD5 is checked when present, and SHA-1 is checked when present.

This length correction is deliberately limited to ISO-only mode. A CUE is an explicit multi-track layout and therefore cannot be combined with a single Redump target-length override.

## FindCRCs hand-off

The ISO2BIN UI includes **Use resulting BIN as FindCRCs source**. After a successful conversion the completed BIN is assigned to the FindCRCs source field and DumpToolbox switches to FindCRCs.
