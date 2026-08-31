# Resurrect implementation notes / validation checklist

This DumpToolbox feature is an independent implementation of the Redumper skeleton/hash restoration workflow.

## Expected Redumper inputs

A `.hash` line is parsed as a 40-character SHA-1 followed by whitespace and the logical ISO path. Special names such as `SYSTEM_AREA` and `GAP_#######` are retained. A trailing `.XA` is treated as Redumper's alternate Mode 2 Form 2 hash only when there is no real ISO filename with that exact `.XA` suffix.

A skeleton is accepted as either:

- cooked 2048-byte sectors, or
- raw 2352-byte sectors with the standard data-sector sync at the first sector.

For raw images the first sector MSF is used to establish Redumper's base LBA, matching the way its image reader addresses multisession/raw track images. ISO9660 extents are then mapped through that base to physical sectors in the skeleton file.

## Raw restoration rules

- Mode 1 user data: bytes 16..2063; EDC is regenerated at 2064 and P/Q ECC at 2076/2248.
- Mode 2 Form 1 user data: bytes 24..2071; XA subheader bytes 16..23 are preserved; EDC and P/Q ECC are regenerated.
- Mode 2 Form 2 user data: bytes 24..2347; XA subheader is preserved; there is no P/Q ECC. Normal Form-2 sectors regenerate the optional EDC at 2348. When the XA Submode identifies a genuine Form-2 sector and DIC logs that sector without EDC, bytes 2348..2351 remain zero. The text `mode 2 no edc` alone does not force a Form-1 sector into this path.
- The standard all-zero `SYSTEM_AREA` still has its EDC/ECC rebuilt because Redumper clears those fields when making a raw skeleton.

## Recommended first real-world test

Use a Redumper dump for which you still have the original intact data-track BIN/ISO as a reference:

1. Load its `.skeleton` and `.hash`.
2. Extract/copy all normal filesystem files from the known-good image into a source directory (plus any special gap/system payloads if applicable).
3. Hash the source directory and confirm the tree shows the expected matches.
4. Resurrect to a new image.
5. Compare the whole output SHA-1/MD5/CRC32 against the known-good original data track.
6. If the track contains XA Form2 sectors, repeat with the matching `.XA` payload source and verify the whole-track hash.

The build environment used to prepare this source snapshot does not contain the .NET SDK, so the first compile must be run on a machine with .NET 8 (`dotnet build -c Release`).
