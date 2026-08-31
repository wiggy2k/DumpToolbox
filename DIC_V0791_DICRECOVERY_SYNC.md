# DICRecovery v0.3.5.4 sync

DumpToolbox v0.7.91 merges the accepted standalone DICRecovery v0.3.5.4 engine changes onto the current DumpToolbox branch using v0.7.75 as the common ancestor. The rejected v0.3.5.5 Easy CD Creator experiment is deliberately excluded.

Included areas: DicLogImportService, SkeletonResurrectionService, DicDonorImageService, DicRecoveryStateService, IsoExtractionManifest, the Mastering profile layer, HFS structural support/fixes, Joliet matcher improvements, and the newer ISO Extractor namespace/manifest behaviour. DumpToolbox-specific external EOF slack rules remain authoritative for EOF residue overrides.

Verbose DIC output is now asynchronous: producers enqueue, a background consumer coalesces bursts, and only bounded batch rendering is marshalled to the Avalonia UI thread.
