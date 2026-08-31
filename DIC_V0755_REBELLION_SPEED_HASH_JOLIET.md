# DIC v0.7.55 — Rebellion rebuild fixes

- Accept `.bin` entries in the DIC companion `.dat` as whole-image size/hash evidence when their length proves 2048- or 2352-byte sector geometry. This restores final CRC32/MD5/SHA-1 comparison for raw-CD DATs such as REBELLION.
- Preserve CeQuadrat/WinOnCD Joliet allocation across the second metadata-preparation pass. An already-present private `CeQuadrat Joliet directory link table` is parsed as structural evidence instead of causing the CeQuadrat detector to turn itself off after the first pass. Existing bridge pairs directly preserve the proven Joliet↔primary directory LBA mapping.
- Accelerate DIC overlapping-extent validation by building the raw payload-capacity prefix map once and comparing the corresponding source slices in large chunks. This removes repeated raw-skeleton rescans and per-sector source reads for every overlap pair while retaining byte-conflict detection and LBA/user-byte diagnostics.
