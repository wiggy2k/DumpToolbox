# ISO Extractor

ISO Extractor creates a DIC-compatible source folder from a cooked 2048-byte ISO/IMG or raw 2352-byte BIN/IMG. It supports primary ISO9660, Joliet, and UDF-only images.

ISO9660/Joliet filesystems are read directly rather than mounted through the operating system. This preserves Associated File records and same-path records that a normal filesystem view may hide or collapse.

For a UDF-only image, the extractor reads the UDF tree directly, including write-once virtual partitions that use a Virtual Allocation Table. UDF files are ordinary payload sources only: they do not acquire invented ISO9660 extents, flags, PVD identity, or Joliet authority.

## Output layout

- Ordinary records are written to their normal relative paths where possible.
- Additional or colliding records are stored under `.dumptoolbox_iso_records/`.
- `.dumptoolbox_iso_manifest.json` maps every extracted record to its original path and length. ISO/Joliet entries also retain their extent, flags and storage details; UDF-only entries retain their UDF pathname explicitly.

Keep the manifest and private record directory with the extracted files. DIC uses ISO/Joliet records by exact identity and UDF records conservatively as payload-only pathname-and-size evidence rather than guessing from host filenames.

## Workflow

1. Open **Other Tools → ISO Extractor**.
2. Select an ISO/BIN and output folder.
3. Extract.
4. On success, the output folder is placed in the DIC Source Folder field automatically.
5. Open DIC and match sources.

The source is read-only and the manifest is finalized transactionally.
