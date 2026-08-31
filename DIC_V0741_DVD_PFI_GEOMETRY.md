# DIC v0.7.41 — DVD PFI geometry

DIC `disc.txt` can preserve DVD Physical Format Information including `StartingDataSector` and `EndDataSector`. For a single-layer DVD data zone, these are absolute physical-sector addresses. DumpToolbox deliberately does not apply this subtraction to dual-layer media yet, because layer addressing/OTP geometry needs separate evidence-driven handling. DumpToolbox derives the complete logical 2048-byte image length as:

```
sectorCount = EndDataSector - StartingDataSector + 1
```

This is physical image geometry, distinct from ISO9660 Volume Space Size. A mastered DVD may contain valid post-volume sectors, so Volume Space Size must not be used as an implicit whole-image length.

The derived PFI length is accepted only when it is positive and does not contradict stronger DIC filesystem/track minimum-LBA evidence. It may extend the target image but never shrink below already-proven LBAs. A sibling DAT remains useful as an independent whole-image size/hash anchor but is no longer required to classify such a DVD as cooked 2048-byte geometry.

`LayerZeroSector` is retained as an independent DIC DVD-sector-length cross-check. Disagreement is reported rather than silently normalized.
