# DIC v0.7.17 Joliet directory-flags regression — Black Mirror

Black Mirror's primary ISO9660 root directory records `LASERLOK` with file flags `0x03`:

- `0x01` Hidden
- `0x02` Directory

The `.` record inside the LASERLOK directory carries only `0x02`. v0.7.16's primary-directory metadata walker first learned `0x03` from the parent root record and then incorrectly overwrote it with `0x02` while walking the child's `.` record. The generated Joliet `Laserlok` record therefore lost the Hidden bit.

v0.7.17 retains externally visible flags from the parent entry and uses the `.` record only to refresh the directory timestamp when available. Dot-record flags are used only when no parent entry was recovered.

Black Mirror LBA 24 regression:

- Original Joliet `Laserlok` flags byte: `03`
- v0.7.16 reconstructed flags byte: `02`
- Corrected v0.7.17 flags byte: `03`
- Original Mode-1 EDC: `4A FF 1A DD`
- v0.7.16 EDC after the one-byte metadata error: `6E 6B 42 58`

All later ECC differences are downstream consequences of that one user-data byte.
