# FindCRCs mixed-mode Track 02 pregap-length boundary recovery (v0.7.84.1)

This pass is enabled by **Attempt to fix under-dumped Audio edges** and requires a supplied mixed-mode CUE with Track 01 data and Track 02 audio.

The normal Track 02 pregap baseline is 150 frames (`00:02:00`). The CUE's file-backed INDEX 00→INDEX 01 duration is preferred; an explicit `PREGAP` value is used only when no file-backed pregap exists.

## Pregap shorter than 00:02:00

If Track 01 did not hash-match, retry its source-offset-zero target-sized extent after replacing the final `150 - pregapFrames` whole 2352-byte raw sectors with zeroes.

Examples:

- `00:01:74` → zero the final 1 raw sector.
- `00:01:73` → zero the final 2 raw sectors.

The candidate is accepted only if target CRC32 and MD5 (when supplied) verify.

## Pregap longer than 00:02:00

This retry runs only after ordinary audio zero-silence edge recovery failed and an **ordinary, unreconstructed Track 03 match** proves that Track 02 is short at its beginning.

For `pregapFrames - 150 = N`:

1. take the final `N` raw 2352-byte sectors from Track 01;
2. require them to look like raw Mode 1/Mode 2 data sectors;
3. apply the standard CD-ROM scrambling XOR to each sector;
4. prepend those `N` scrambled sectors to the available Track 02 source extent;
5. insert zero bytes after the scrambled sectors for any remaining exact shortfall;
6. verify the complete target CRC32 and MD5.

Examples:

- `00:02:01` → synthesize the next MSF data sector after Track 01, scramble it, then add the remaining zero shortfall.
- `00:02:02` → synthesize the next two consecutive MSF data sectors after Track 01, scramble them, then add the remaining zero shortfall.

No result is accepted from CUE geometry alone; a target hash match is mandatory.

## Mixed-mode Track 02 anchor priority (v0.7.85)

When Track 01 is data, Track 02 is the first audio track, and ordinary FindCRCs has verified Track 03, the Track 03 boundary is authoritative for a missing/short Track 02. The backwards-from-Track-03 interpretation (missing bytes at Track 02's beginning) is attempted first. A Track-01-forward interpretation is retained only as a fallback if the Track-03-anchored candidate fails verification.
