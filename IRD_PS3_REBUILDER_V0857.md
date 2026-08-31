# DumpToolbox v0.8.57 — PS3 IRD rebuild tab

Adds a top-level **IRD** tab beside SkeleTool and DIC for rebuilding plain/decrypted PlayStation 3 ISO images from an extracted/JB source folder and a matching `.ird`.

## First implementation

- IRD versions 6, 7, 8 and 9 are parsed directly.
- Whole-file gzip-compressed IRDs and raw `3IRD` files are accepted.
- The embedded IRD ISO9660 header is parsed to recover paths, file extents and sizes.
- Source files are matched by their IRD relative path and verified with the per-file MD5 stored by the IRD.
- Rebuild is blocked if any required source is missing, wrong-sized or has the wrong MD5.
- The output is assembled from the IRD header + files at their recorded sectors + IRD footer.
- The completed ISO is checked against the IRD region MD5s. Failed region verification removes the output.
- Cancellation removes an incomplete output.

## Intentionally deferred

- PS3 ISO encryption/key handling. This will be added only after plain IRD reconstruction is validated on real examples.
- Direct archive-as-source support. The first validation target is a normal extracted/JB directory.
