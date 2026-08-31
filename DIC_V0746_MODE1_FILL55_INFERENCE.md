# DIC v0.7.46 Mode 1 all-0x55 mastering inference

A DIC EccEdc mismatch alone is not enough to invent parity bytes. v0.7.46 adds one narrow, evidence-driven exception for Mode 1 sectors:

1. DIC must map the physical LBA as an unresolved ECC/EDC mismatch.
2. After normal file restoration, all 2048 Mode 1 user-data bytes must be `0x55`.
3. The current EDC/reserved/ECC area must still equal the canonical fields DumpToolbox would generate for that exact LBA/header/payload. This prevents the inference from overwriting stronger non-canonical raw evidence that remains present at final-recipe time.
4. Only then is byte range 16..2351 replaced with `0x55`, yielding the mastered raw form `16-byte header + 2336 x 0x55`.

The rule is generic: it contains no title, whole-image hash, filename, or special MSF check. The Star Wars: Rebellion sample at LBA 45610 supplied the raw-sector evidence proving this pattern: its 2048-byte logical payload matches the resurrected payload exactly, while the stored EDC/reserved/ECC area is also all `0x55`.
