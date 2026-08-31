# DumpToolbox ISO Extractor tab

> v0.6.28: manifest finalisation explicitly closes the `.partial` manifest stream before the Windows rename.

The **ISO Extractor** is built into the main DumpToolbox application under **Other Tools**. It creates a DIC-compatible source folder directly from a 2048-byte ISO or 2352-byte BIN.

It reads the primary ISO9660 filesystem itself rather than mounting the image through the host OS. This allows it to preserve ISO9660 Associated File records and other same-path directory records that Windows/Linux filesystem drivers may hide or collapse.

The output contains:

- normal ISO files at ordinary relative paths where possible;
- `.dumptoolbox_iso_records/` containing additional records that cannot share the same host pathname;
- `.dumptoolbox_iso_manifest.json` mapping every extracted file back to its original ISO path, extent LBA, byte length and File Flags.

Do not rename/delete the manifest or private record directory before using the folder with DIC recovery.

## Workflow

1. Open the **ISO Extractor** tab.
2. Choose the source ISO/BIN and an output folder.
3. Click **Extract**.
4. On completion DumpToolbox automatically sets the DIC Source Folder to that extraction folder.
5. Return to **DIC** and click **Match Sources**.
6. Associated records are matched only through the manifest's exact ISO record identity; they are never guessed from an ordinary same-name file.

There is no separate extractor executable or project. Publish the normal DumpToolbox application only.
