# DIC v0.7.67 — ISO Extractor payload-only manifest mode

DIC normally verifies `.dumptoolbox_iso_manifest.json` by comparing the extractor Primary Volume Descriptor SHA-256 with the DIC skeleton PVD. A synthetic/reconstructed skeleton can legitimately have a different PVD even when the extractor payload files came from the same named volume.

When the volume identifier matches but the PVD fingerprint does not, v0.7.67 keeps the manifest only as a catalogue of private extracted payload files. It does **not** trust the extractor LBA or extent map.

A private manifest file is accepted only when ISO path + exact DataLength + FileFlags identify exactly one DIC recovery entry. Placement always uses the DIC entry's own LBA/extent geometry. Different volume identifiers remain rejected.

Verbose logging distinguishes `FULL PVD IDENTITY` from `PAYLOAD ONLY` and reports payload-only matches explicitly.
