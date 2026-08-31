# v0.7.42 — exact byte sizes and DIC folder selection

## Explorer sizes
The shared `SkeletonTreeNode` used by the DIC and Skeletool filesystem trees now renders exact byte counts (`N0` formatting) for every file, including zero-length entries. No automatic binary-unit conversion is performed in those explorers.

## DIC log selection
The DIC GUI now asks for the directory containing one DIC log set rather than asking the user to choose an arbitrary companion log. The importer derives basenames only from recognized DIC companion suffixes and requires a unique basename. If multiple DIC sets are present in the same folder, import stops with an ambiguity error rather than selecting one implicitly.

The core `Discover` method still accepts a companion-file path for compatibility with older saved settings or external callers.
