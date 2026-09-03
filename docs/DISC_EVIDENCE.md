# Disc Evidence developer tool

Disc Evidence is a hidden developer workflow for collecting mastering observations from images already indexed by the SkeleTool SHA-1 catalogue.

Enable it by adding `devtools=1` under `[General]` or `[Settings]` in `DumpToolbox.ini`.

The scanner uses pending catalogue units and stores observations in `disc_mastering_evidence.sqlite`. It collects ISO9660/Joliet geometry, mastering identifiers, EOF slack relationships, earlier-sector candidate offsets and UDF presence. It also retains the raw evidence needed to distinguish Joliet ordering families:

- every volume descriptor's original LBA and sequence, volume-space size, escape sequence and path-table locations;
- SVD root geometry, exact record length and root System Use/XA bytes;
- ISO9660 and Joliet directory records with their parent, containing-directory extent, byte offset, record index and raw identifier bytes;
- mandatory and optional Type-L and Type-M path-table records with their original numbering, parent numbers, offsets and raw identifiers;
- explicit ISO9660-to-Joliet record correspondences, including the original position in both namespaces.

Evidence schema changes automatically mark older observations for refresh without changing ordinary catalogue scans. Ordering evidence is stored as raw on-disc facts; it does not automatically teach production reconstruction code from an isolated disc.

The tab can gather pending evidence, cancel a run, mark present units pending again, and export analysis CSV files. In addition to the name-pair and EOF reports, analysis exports descriptor observations, Joliet directory-record order, Joliet path-table order and explicit namespace record pairs. This database is research input; it is not used directly as unreviewed reconstruction authority.
