# DIC v0.7.26 Joliet System Use regression — Project Eden

Project Eden is a CD-ROM XA / Joliet disc whose primary directory records are preserved by DIC. The primary records contain a 14-byte CD-ROM XA System Use area. Ordinary files commonly carry:

`00 00 00 00 0D 55 58 41 00 00 00 00 00 00`

and directories carry the corresponding directory attribute form containing `8D 55 58 41`.

The original Joliet records retain these System Use bytes. Previous Joliet synthesis emitted no System Use area, shortening each record by 14 bytes, changing directory packing and eventually changing generated Joliet directory extents.

v0.7.26 reads exact System Use bytes from the proven primary tree in the current skeleton and carries them to the uniquely correlated Joliet records. It also carries the raw seven-byte recording timestamp; Project Eden root records use the malformed-but-mastered value `00 00 00 00 00 00 E4`.

Representative root-sector record lengths before/after the fix:

- original root `.`: 0x30 bytes; old generated: 0x22 bytes
- original `AutoRun.exe;1`: 0x4A bytes; old generated: 0x3C bytes
- original `DirectX8.0a` directory: 0x46 bytes; old generated: 0x38 bytes

The extra 0x0E bytes in each case are the preserved XA System Use payload. Directory sizing/packing must include them before assigning Joliet extents.
