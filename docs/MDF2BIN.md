# MDF2BIN

MDF2BIN converts classic Alcohol 120% MDS/MDF CD images into a raw 2352-byte BIN and companion CUE.

## Supported images

- MDS major version 1 CD/CD-R/CD-RW descriptors.
- Audio, Mode 1, Mode 2, Mode 2 Form 1 and Mode 2 Form 2 tracks.
- 2352-byte main-channel sectors with either no subchannel or an appended 96-byte interleaved subchannel.
- Multiple sessions represented in one MDF.

MDS 2.x/MDX, DVD media, split MDF sets, unknown modes, and layouts without a resolvable 2352-byte main channel are rejected.

## Pregaps and sessions

Stored pregaps for later tracks are copied and represented by `INDEX 00`/`INDEX 01`. A first-track metadata-only 150-sector pregap is not synthesized. If an audio pregap physically overlaps the preceding data region, its main channel is emitted as zero CD-DA silence instead of duplicating data-sector bytes.

All represented sessions are emitted in descriptor order. Because CUE cannot encode physical multisession lead-in and lead-out regions, session boundaries are recorded as `REM` metadata rather than invented sectors.

## Subchannel

The BIN always receives the 2352-byte main channel. Optional `.sub` output preserves exact 96-byte tails and is allowed only when every represented sector has stored subchannel data.

## Safety

Descriptor pointers, lengths, modes and MDF ranges are bounds-checked. BIN, CUE and SUB output use temporary partial files and are finalized only after successful conversion. The resulting BIN can optionally be sent to FindCRCs.
