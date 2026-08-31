# DIC logged ECC/EDC regression — v0.7.5

Regression source: Warcraft II Expansion DIC `*.img_EccEdc.txt` supplied with the recovery case.

- Explicit `[ERROR]` ECC/EDC count: 68,736
- Unique parsed error LBAs: 68,736
- Parsed error LBAs classified as Mode 2 Form 1: 68,736
- Unsupported explicit error LBAs: 0
- Genuine Mode 2 Form 2 / no-EDC LBAs: 70,214 and 70,215 only

For the 68,736 explicit error LBAs, the reproduced mastering fault is:

1. Generate normal Mode 2 Form 1 EDC.
2. Generate and store normal P ECC.
3. Save raw-sector byte `0x873`.
4. Temporarily set raw-sector byte `0x873` to `00`.
5. Generate Q ECC.
6. Restore the saved, correct P byte at `0x873`.

Applied to the supplied alternate `Track 01.bin`, this produces the DIC target hashes exactly:

- CRC32: `af37ee45`
- MD5: `0141a4079c5b3c0f4ff371cb0ad1bc07`
- SHA-1: `8fae1a878deb63850de4e5a83d5567e28c5ef78b`

The regression output is byte-for-byte identical to the independently generated known-matching candidate used to reverse-engineer the fault.

## Raw-donor overwrite regression fixed in v0.7.5

The v0.7.4 same-disc raw-donor exactness path copied raw sectors verbatim after the DIC mastering fault had already been generated. In the Warcraft II case the surviving overwritten error sectors are LBA 19 and error-listed sectors within the post-volume region 70,064-70,365. Starting with the known-correct DIC-matching image and replacing those donor regions verbatim reproduces the reported 0.7.4 failed output exactly:

- CRC32: `61ab4dd1`
- MD5: `1e890cbad893ea412858406e512e4f3a`
- SHA-1: `ee963ec88206ae025151bc0713c2ffafce049cef`

v0.7.5 reapplies the DIC error-map Mode 2 Form 1 protection rebuild immediately after each raw donor sector is copied. The corrected result returns to:

- CRC32: `af37ee45`
- MD5: `0141a4079c5b3c0f4ff371cb0ad1bc07`
- SHA-1: `8fae1a878deb63850de4e5a83d5567e28c5ef78b`

The corrected regression output is byte-for-byte identical to the independently generated known-matching candidate.
