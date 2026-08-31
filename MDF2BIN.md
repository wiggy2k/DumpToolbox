# MDF2BIN

DumpToolbox v0.6.27 includes a CD-focused converter for classic Alcohol 120% `.mds` + `.mdf` image pairs.

## Scope

The first implementation intentionally supports only classic **MDS major version 1** descriptors for CD/CD-R/CD-RW media. It converts the represented CD main channel to a single raw 2352-byte BIN and writes a companion CUE.

Supported MDS CD track mode codes:

- `0xA9` / `0xE9` — Audio
- `0xAA` / `0xEA` — Mode 1
- `0xAB` — Mode 2
- `0xEC` / `0xAC` — Mode 2 Form 1
- `0xED` / `0xAD` — Mode 2 Form 2

Supported subchannel modes:

- `0x00` — no stored subchannel
- `0x08` — 96-byte interleaved subchannel appended to each 2352-byte main-channel sector

The converter currently rejects MDS 2.x/MDX, DVD-family media, split/multi-file MDF sets and any CD track whose MDS sector record does not resolve to a 2352-byte main channel.

## Descriptor structures used

The parser reads the classic MDS structures directly in little-endian form:

- header: 88 bytes
- session record: 24 bytes
- track record: 80 bytes
- TrackExtra: 8 bytes (`pregap`, `sectors`)
- footer: 16 bytes

The header must begin with the 16-byte ASCII signature `MEDIA DESCRIPTOR`.

For each real track (`POINT` 01-99), the converter uses the track mode, subchannel mode, ADR/CONTROL byte, sector size, start LBA, MDF `startOffset`, file count and TrackExtra pointer. Non-track TOC descriptor records such as A0/A1/A2/B0/C0 are parsed as table members but are not copied as MDF data regions.

This implementation was written independently from the public format behaviour. The structure interpretation was cross-checked against the current Aaru Alcohol 120% image plugin, whose Alcohol component is published under LGPL-2.1-or-later.

## Pregap / MDF offset rule

For a non-first track in a session, classic MDS stores `startOffset` at INDEX 01 and the TrackExtra pregap sectors immediately before it in the MDF. DumpToolbox therefore locates the physical beginning as:

```text
physicalStart = startOffset - (pregap * storedSectorSize)
```

and writes the pregap followed by the track's data sectors.

The first track of a session is different: classic MDS commonly reports a 150-sector pregap as metadata while its `startOffset` already points to the first represented MDF sector. DumpToolbox does **not** synthesize those 150 sectors. The generated BIN begins with the bytes that are actually represented by the MDF.

For each output track the generated CUE uses:

- `INDEX 00` at the beginning of a physically stored pregap, when present;
- `INDEX 01` after that pregap;
- first-track/session metadata-only pregaps are not converted into fake sectors.

## Subchannel

When the MDS track record reports interleaved subchannel, its sector record is normally 2448 bytes:

```text
2352-byte CD main channel + 96-byte subchannel
```

The BIN receives the first 2352 bytes of every sector. If **Also save 96-byte interleaved subchannel as .sub** is selected, the exact 96-byte tails are written in sector order to a `.sub` file.

DumpToolbox only permits `.sub` output when every represented sector has stored subchannel. It does not create zero/fake subchannel sectors for tracks where the MDS says none exists.

## Multi-session discs

Main-channel sectors are extracted in MDS session/track order. A CUE sheet cannot fully represent all multi-session lead-in/lead-out information, so generated CUE files include `REM SESSION n` markers for reference and the UI logs a warning.

The BIN is still useful as the exact concatenation of the main-channel sectors referenced by the supported MDS track records, with their file-backed pregaps retained.

## Safety behaviour

MDF2BIN refuses conversion when:

- the MDS signature/version is unsupported;
- a track record or TrackExtra pointer is outside the descriptor;
- a track's required MDF byte range is beyond the MDF file;
- a track reports multiple MDF files;
- the CD main channel is not 2352 bytes/sector;
- an unknown track/subchannel mode is encountered;
- `.sub` was requested but subchannel coverage is incomplete.

Output is written through `.partial` files and renamed only after successful completion. Cancellation removes partial output.

Select **Use resulting BIN as FindCRCs source** to place the completed BIN into FindCRCs' source field and switch directly to the FindCRCs tab after a successful conversion.


## Windows completion fix

v0.6.26 closes the source/output streams before renaming `.partial` outputs into their final filenames. This is required on Windows because the temporary BIN/SUB files are opened with `FileShare.None`; attempting to rename them while those handles are still open results in a sharing-violation error after conversion reaches 100%.
