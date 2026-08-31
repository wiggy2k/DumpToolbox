# DIC v0.7.50 — CeQuadrat / WinOnCD Joliet directory link table

CeQuadrat/WinOnCD discs can contain a private sector between the ISO volume descriptor set and the primary path table. It bridges the supplementary Joliet directory extents to the corresponding primary ISO9660 directory extents.

DumpToolbox synthesizes this sector only when all geometry is independently proven:

- the primary descriptor identifies a CeQuadrat formatter;
- a Joliet SVD has already been safely reconstructed;
- the candidate sector is exactly one sector after the volume-descriptor terminator and one sector before the primary Type-L path table;
- every primary path-table directory has exactly one synthesized Joliet directory carrying the same primary extent identity;
- the sector does not overlap known file/directory content;
- no stronger exact metadata already occupies the sector.

Observed logical-sector format:

```text
0x000  37 bytes  "CeQuadrat Joliet directory link table"
0x025   7 bytes  00
0x02c   4 bytes  little-endian directory count
0x030   8*N      repeated little-endian {Joliet LBA, Primary LBA}
rest             00 to 2048 bytes
```

For Star Wars: Rebellion the nine derived pairs are:

```text
24 <-> 47
37 <-> 58
36 <-> 57
25 <-> 48
40 <-> 60
30 <-> 52
28 <-> 50
26 <-> 49
41 <-> 61
```

The resulting Mode 1 sector at LBA 19 has EDC `FF B8 14 2B`, matching the supplied original sector evidence. This regression is used only as validation; the implementation contains no title-specific constants.
