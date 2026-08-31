# v0.7.49 — portable Skeletool SHA-1 database

The Skeletool history database lives only at:

`<executable directory>/skeletool_sha1_history.json`

There is deliberately no LocalApplicationData fallback. The UI tests writability before database-backed operations. If the executable directory cannot be written, it logs a warning and continues without persisting new history. An existing readable database can still supply reuse/provenance information.
