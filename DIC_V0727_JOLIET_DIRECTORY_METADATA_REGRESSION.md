# DIC v0.7.27 Joliet directory metadata regression — Project Eden

Project Eden proves that an ISO9660 directory's parent-visible record and its internal `.` / `..` records can carry different raw recording timestamps.

Primary evidence from `PROJECTEDEN_mainInfo.txt`:

- parent-visible `DIRECTX80A`: `65 0A 03 0D 0B 38 E4`
- internal `DIRECTX80A/.`: `00 00 00 00 00 00 E4`
- internal `DIRECTX80A/..`: `00 00 00 00 00 00 E4`
- parent-visible `GAMESPY`: `65 0A 03 0D 0B 33 E4`
- parent-visible `IMAGES`: `65 0A 03 0D 0B 33 E4`
- parent-visible `LEVELS`: `65 0A 03 0D 0B 31 E4`
- parent-visible `MOVIES`: `65 0A 03 0D 0B 0F E4`

v0.7.26 stored one metadata object per directory path. When traversal later entered a child directory, its `.` record overwrote the parent-visible timestamp. v0.7.27 stores parent-visible, self (`.`), and parent-link (`..`) metadata independently and uses each in the corresponding Joliet record. System Use bytes are kept separate by the same mechanism.
