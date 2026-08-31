# DumpToolbox v0.7.98 — PC XYZ Easy CD Creator + Joliet evidence

This build intentionally branches from v0.7.92 and therefore removes all Dataware-specific recovery, fingerprint and popup changes introduced in v0.7.93-v0.7.97.

## EOF slack seed updates
- Easy CD Creator 5.0 (352), blank System ID -> 2976 sectors (HIGH)
- Easy CD Creator 5.0 (352), CD-RTOS CD-BRIDGE -> 2688 sectors (HIGH)
- Easy CD Creator 5.3 (031), CD-RTOS CD-BRIDGE -> 2592 sectors (MEDIUM)
- Easy CD Creator 5.3 (034), blank System ID -> 10 sectors (HIGH)
- Easy CD Creator 5.3 (060), CD-RTOS CD-BRIDGE -> 2592 sectors (HIGH; strengthened)
- Easy CD Creator 6.1 (007), blank System ID -> 1920 sectors (HIGH)

Existing external EOFSlackRules.ini files are not overwritten. Delete/rename the external file to regenerate these defaults.

## Joliet/source naming
- Added deterministic WINDOWS_NT_HASHED_83_PATH_CHAIN_EXACT. It permits the exact Windows checksum alias at any component of a source path, so hashed parent directories no longer prevent an otherwise exact child mapping.
- Added deterministic PREFIX3_HEX_ORDINAL_PATH_CHAIN. It applies the existing PREFIX3_HEX_ORDINAL transform at directory and leaf levels, with sibling ordinal calculated from all source filesystem siblings.
- Both rules still require exact source size, compatible DIC timestamp where available, source availability and exactly one resulting source candidate.
- Existing v0.7.92 Wall Street Tycoon anti-cascade protections remain unchanged.
