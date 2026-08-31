# DumpToolbox v0.8.59 — PS3 IRD ISO9660 multi-extent files

The IRD parser now recognises ISO9660 file records with flag 0x80 (multi-extent).
Consecutive continuation records with the same identifier are grouped into one logical file.

Effects:
- the IRD tree reports the summed logical size rather than only the first extent;
- multi-extent files show their extent count in the tree;
- JB/source validation compares the source file against the summed logical size and the IRD MD5 for the first extent/LBA;
- rebuilding streams the logical source file sequentially across every recorded extent instead of writing it contiguously from the first LBA.

Confirmed against BLUS30983 / 007 Legends:
- FILELIST.000 = 2 extents, 2,143,633,408 bytes
- FILELIST.001 = 2 extents, 2,064,734,208 bytes
- FILELIST.002 = 2 extents, 2,104,360,960 bytes
- FILELIST.003 = 1 extent, 349,558,784 bytes
