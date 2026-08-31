# DIC v0.7.11 regression notes

Static/source regressions run while implementing the six-batch EccEdc evidence update:

## Warcraft II known mastering-fault fixture

- EccEdc per-sector records: 70,366
- Explicit ECC/EDC error occurrences: 68,736
- Safely mapped physical error sectors: 68,736
- Unmapped: 0
- Every error sector remains Mode 2 Form 1 for the known fixture.
- The source BIN's logged MSF and ordinary XA subheaders agree with the EccEdc records, so preserving logged framing does not alter the previously proven reconstruction.
- Previously generated DIC-matched image remains:
  - CRC32 `af37ee45`
  - MD5 `0141a4079c5b3c0f4ff371cb0ad1bc07`
  - SHA1 `8fae1a878deb63850de4e5a83d5567e28c5ef78b`

## Batch 6: 3D Aventyret Manniskan

The anomaly-aware summary mapper resolves the mixed coordinate systems correctly:

- bad-MSF summary physical sectors: 293816, 293817, 293818, 293819, 293820, 293821, 293823
- invalid-sync summary values `-36` and `545173` map to physical sectors 293815 and 293822
- zero-sync maps to physical 293824
- invalid Mode maps to physical 293813
- ECC/EDC mismatch maps to physical 293814

This is the regression that prevents corrupt header-derived LBAs from becoming reconstruction file offsets.

## Batch 6: Fuzzball

- EccEdc records: 159,493
- audio records without `MSF[...]`: 132,870
- unequal XA-subheader records: 75

The parser now advances the physical ordinal for every `LBA[...]` sector record, even when an audio line has no MSF field, preventing all following sectors from being shifted in the map.

## Batch 6: Team Factor

- EccEdc records: 152,200
- Mode 0 sectors: 11,250

Mode 0 is retained as a separate final sector class rather than being coerced into Mode 1/2.

## Compile environment

The container does not include a .NET SDK and outbound DNS/download access is blocked, so a real `dotnet build/publish` could not be executed here. Delimiter/source consistency checks were run across all C# files. v0.7.11 therefore remains a development candidate until compiled in the normal .NET 8 environment.
