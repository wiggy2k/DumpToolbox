# ISO Extractor

ISO Extractor creates a DIC-compatible source folder from a cooked 2048-byte ISO or raw 2352-byte BIN.

It reads the primary ISO9660 filesystem directly rather than mounting it through the operating system. This preserves Associated File records and same-path records that a normal filesystem view may hide or collapse.

## Output layout

- Ordinary records are written to their normal relative paths where possible.
- Additional or colliding records are stored under `.dumptoolbox_iso_records/`.
- `.dumptoolbox_iso_manifest.json` maps every extracted record to its original path, extent, length, flags and storage details.

Keep the manifest and private record directory with the extracted files. DIC uses them to match records by exact ISO identity rather than guessing from host filenames.

## Workflow

1. Open **Other Tools → ISO Extractor**.
2. Select an ISO/BIN and output folder.
3. Extract.
4. On success, the output folder is placed in the DIC Source Folder field automatically.
5. Open DIC and match sources.

The source is read-only and the manifest is finalized transactionally.
