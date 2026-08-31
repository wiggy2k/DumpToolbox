# DumpToolbox v0.8.51 — developer disc evidence

- Adds a schema-v3 migration to `skeletool_sha1_catalogue.sqlite` with `units.evidence_gathered`, `evidence_gathered_utc`, and `evidence_schema`.
- Normal SHA-1 catalogue scans do not collect mastering evidence. The new columns only schedule developer evidence work.
- Adds `disc_mastering_evidence.sqlite` alongside the existing catalogue.
- Adds a top-level **Disc Evidence** tab which is hidden unless `devtools=1` is present in `[General]` (or `[Settings]`) in `DumpToolbox.ini`.
- The developer scanner consumes pending units from the existing SHA-1 catalogue and reuses catalogue image records/materialisation.
- CD evidence: ISO9660/Joliet records, structural extent/length mappings, mastering/oracle volume identifiers, EOF slack, and exact earlier-sector candidate deltas.
- DVD evidence is back in scope: DVD-sized/UDF-bearing images are tagged as DVD; ISO9660/Joliet evidence is collected when present and UDF presence is recorded. No VIDEO_TS-specific logic exists.
- EOF non-zero-tail searching is a single earlier-sector pass per image rather than one full rescan per tail.
- Evidence schema revisions automatically make older completed units pending again via `evidence_schema`, without changing ordinary catalogue scan behaviour.
- Developer tab can gather pending evidence, cancel, mark all present units pending, and export raw corpus CSVs.
