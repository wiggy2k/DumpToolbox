# DIC EccEdc evidence model — v0.7.11

This update is based on comparative inspection of six large batches of DiscImageCreator logs spanning multiple DIC/EccEdc generations.

## Physical sector vs reported LBA

EccEdc's `LBA[...]` value is not always the physical sector index in the generated IMG. With malformed MSF/sync, the printed LBA can be header-derived garbage; track/session gaps can also make it discontinuous.

v0.7.11 therefore keys the EccEdc map by the ordinal of each per-sector record (physical position in the IMG) and stores the printed/reported LBA separately as evidence. Summary lists are mapped anomaly-aware: a matching physical sector is preferred when its per-sector record exhibits that summary anomaly; otherwise the reported-LBA lookup is used.

## Exact final framing retained from EccEdc

For data sectors, v0.7.11 retains:

- the three exact logged MSF/header bytes;
- an exact invalid raw Mode byte when EccEdc prints `Invalid mode: [xx]`;
- the logical mode from the low Mode bits when high bits are non-standard;
- all eight XA subheader bytes when EccEdc logs unequal subheader copies.

`mode 1 with Block Indicators` is recognized, but EccEdc does not expose the exact upper Mode bits in that label. Such a sector is therefore flagged as requiring an exact raw donor/capture for byte-perfect recovery.

## Final sector classes

The per-sector map now distinguishes Mode 0, Mode 1, Mode 2 Form 1, Mode 2 Form 2, audio, and unknown/unsafe sectors. Audio is opaque 2352-byte content and is not treated as a data sector with a 2048/2324-byte regeneratable payload.

Invalid/zero sync is not normalized after an exact raw donor supplies it. Payload replacement can operate on non-canonical sync while preserving the raw framing.

## EccEdc summary overlays

The parser overlays summary lists onto the per-sector stream, including:

- ECC/EDC mismatch;
- invalid Mode;
- bad MSF;
- invalid sync;
- zero sync;
- unequal Mode-2 No-EDC subheaders;
- expected-all-zero mismatches.

This matters for historical EccEdc versions where a per-sector line can look ordinary even though the final summary identifies an anomaly.

## Error reproduction recipes

An EccEdc error list alone no longer selects a corruption algorithm.

- The proven Warcraft II Q-ECC fault remains supported, but is positively fingerprinted by its known final IMG SHA-1 plus the exact 68,736 mapped Mode-2 Form-1 error population.
- `0x55 except header` is used only when EccEdc explicitly prints the recipe and its recipe count equals the explicit ECC/EDC error count.
- Other ECC/EDC mismatch sectors are marked as known abnormal and raw-donor-capable; DumpToolbox does not invent a Warcraft/0x55 fault for them.

Both final-image recipes are reapplied after donor/source writes so later recovery stages cannot normalize them away.

## Raw donor precedence

Exact framing from DIC remains stronger evidence than donor framing. After a raw donor sector is copied, v0.7.11 reapplies exact logged MSF/mode and XA subheader bytes where DIC supplied them, then applies any proven final-image recipe.

Regions whose exactness inherently requires 2352-byte raw bytes (audio, invalid/zero sync, unknown mode, unresolved Block Indicators, unknown ECC corruption, etc.) are marked `RequiresRawDonor`; a cooked 2048-byte ISO donor cannot falsely satisfy them.

## mainError/mainInfo caution

This release deliberately does **not** turn generic `mainError` `Main Channel` dumps or `Read error. padding [...]` messages into final IMG overrides. Across the sampled logs those can be pre-descramble SCM captures or transient failed-read history later replaced by a successful read. They need processing-stage/context evidence before being promoted to final bytes.

`mainInfo` offset-test parsing remains context-limited as before.
