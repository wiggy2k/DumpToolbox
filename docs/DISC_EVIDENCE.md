# Disc Evidence developer tool

Disc Evidence is a hidden developer workflow for collecting mastering observations from images already indexed by the SkeleTool SHA-1 catalogue.

Enable it by adding `devtools=1` under `[General]` or `[Settings]` in `DumpToolbox.ini`.

The scanner uses pending catalogue units and stores observations in `disc_mastering_evidence.sqlite`. It collects ISO9660/Joliet geometry, mastering identifiers, EOF slack relationships, earlier-sector candidate offsets and full UDF structural evidence. It also retains the raw evidence needed to distinguish Joliet ordering families:

- every volume descriptor's original LBA and sequence, volume-space size, escape sequence and path-table locations;
- SVD root geometry, exact record length and root System Use/XA bytes;
- ISO9660 and Joliet directory records with their parent, containing-directory extent, byte offset, record index and raw identifier bytes;
- mandatory and optional Type-L and Type-M path-table records with their original numbering, parent numbers, offsets and raw identifiers;
- explicit ISO9660-to-Joliet record correspondences, including the original position in both namespaces.

For UDF images it records:

- every discovered anchor and the main and reserve volume descriptor sequences, including descriptor tag serials, checksum/CRC validity, original locations, exact descriptor bytes and raw SHA-1 fingerprints;
- physical, virtual and other partition maps, their partition numbers, implementation identifiers and required-zero reserved fields;
- the latest VAT and every reachable previous VAT generation, with placement, allocation type, exact VAT and implementation-use bytes, implementation identifier, revision and file/directory counts, mapped and `FFFFFFFF` unused entries, out-of-range mappings and raw VAT fingerprints;
- required-zero violations separately from implementation-use data. Implementation/application-use bytes, unused VAT entries and previous-generation structures are retained as mastering evidence rather than misreported merely because they are non-zero.

Evidence schema changes automatically mark older observations for refresh without changing ordinary catalogue scans. Ordering evidence is stored as raw on-disc facts; it does not automatically teach production reconstruction code from an isolated disc.

The tab can gather pending evidence, cancel a run, mark present units pending again, and export analysis CSV files. In addition to the name-pair and EOF reports, analysis exports descriptor observations, Joliet directory-record order, Joliet path-table order, explicit namespace record pairs, UDF descriptors, UDF partition maps and VAT generations. This database is research input; it is not used directly as unreviewed reconstruction authority.
