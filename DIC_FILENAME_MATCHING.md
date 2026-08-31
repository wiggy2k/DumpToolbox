# DIC source filename matching

The DIC workflow keeps the primary ISO9660 filesystem from `*_volDesc.txt` authoritative for extents, sizes, flags and short-name identity, but a normal source folder may be supplied using the **Joliet/user-visible names and directory tree**.

Matching order:

1. Exact primary ISO9660 relative path + filename + exact byte length (case-insensitive).
2. If that fails, conservative Joliet -> ISO9660 Level-1 projection of the **entire relative path**, still with exact byte length. Non-ISO characters become `_`; each directory/file stem is limited to 8 characters and file extensions to 3. The fallback is accepted only when the mapping is unique both from DIC-record-to-source and source-to-DIC-record.

Examples:

- `BlackMirror.ico` -> `BLACKMIR.ICO`
- `Setup-1.bin` -> `SETUP_1.BIN`
- `Laserlok/...` -> `LASERLOK/...`

A successful Joliet-tree scan also records the exact source-relative spelling/casing. Those names are later used to rebuild supplementary Joliet directory/path-table metadata that DIC did not log. They are **not** used to rewrite the logged primary ISO9660 metadata. Ambiguous mappings remain unmatched.


