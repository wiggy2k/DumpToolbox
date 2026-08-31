# DIC v0.7.35 companion `.dat` discovery

DIC log sets can contain a sibling `<basename>.dat` file alongside `_disc.txt`, `_volDesc.txt`, `_mainInfo.txt`, and EccEdc logs.

The common companion lookup helper previously enumerated only `*.txt` files before comparing the requested filename. Therefore calls such as `FindCompanion(directory, baseName + ".dat")` could never discover the `.dat` automatically unless the `.dat` itself had been selected directly.

v0.7.35 removes the extension pre-filter and enumerates all top-level files, retaining the existing case-insensitive exact filename comparison. This is extension-agnostic and applies to any future companion type without disc-specific names or hashes.

No recovery-state schema bump is required because this changes only file discovery, not reconstructed metadata or persisted matching semantics.
