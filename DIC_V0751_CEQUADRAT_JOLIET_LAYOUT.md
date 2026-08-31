# DIC v0.7.52 — CeQuadrat / WinOnCD Joliet directory placement

v0.7.50 correctly decoded the private `CeQuadrat Joliet directory link table`, but it exposed a second CeQuadrat mastering rule: the link table is written in primary Type-L path-table order while the actual Joliet directory bodies are packed in a different order.

For a fully proven CeQuadrat context, v0.7.52 packs Joliet directory bodies contiguously from the SVD-declared Joliet root in ascending corresponding primary-directory extent LBA. The Joliet path table itself keeps the normal/primary path-table directory order and directory numbers.

This is intentionally evidence-driven. The allocator is considered only when:

- the CeQuadrat private-link-table context has already been independently detected;
- every generated Joliet directory has a unique mapped primary extent;
- that mapped primary-extent set exactly equals the extents read from the primary Type-L path table;
- the first primary extent maps to the SVD-anchored Joliet root;
- every proposed directory range stays within the volume and avoids declared path tables and ordinary file extents.

The private link-table writer then maps each primary path-table entry to the extent assigned to that exact directory identity, preserving primary path-table order in the bridge sector.

Rebellion regression geometry:

```
primary extent order / body allocation:
47 root      -> Joliet 24
48 REBELL~1  -> Joliet 25
49 MDATA     -> Joliet 26
50 GDATA     -> Joliet 28
52 EDATA     -> Joliet 30
57 INSTALL   -> Joliet 36
58 DIRECTX   -> Joliet 37
60 DRIVERS   -> Joliet 40
61 USA       -> Joliet 41

bridge output remains in primary path-table order:
24,47
37,58
36,57
25,48
40,60
30,52
28,50
26,49
41,61
```

No disc name, target hash, fixed LBA pair, or filename-specific exception participates in the allocator.
