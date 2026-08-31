# Disc Evidence developer tool

Disc Evidence is a hidden developer workflow for collecting mastering observations from images already indexed by the SkeleTool SHA-1 catalogue.

Enable it by adding `devtools=1` under `[General]` or `[Settings]` in `DumpToolbox.ini`.

The scanner uses pending catalogue units and stores observations in `disc_mastering_evidence.sqlite`. It collects ISO9660/Joliet geometry, mastering identifiers, EOF slack relationships, earlier-sector candidate offsets and UDF presence. Evidence schema changes automatically mark older observations for refresh without changing ordinary catalogue scans.

The tab can gather pending evidence, cancel a run, mark present units pending again, and export analysis CSV files. This database is research input; it is not used directly as unreviewed reconstruction authority.
