# DIC v0.7.33 invalid directory timestamp / geometry regression

ISO9660 directory geometry is structural evidence and must not depend on successful timestamp decoding. Some mastered discs contain invalid recording dates such as `1900-00-00 00:00:00 +00:00` in root and/or dot records while still carrying valid extent and data-length fields.

v0.7.32 populated `PrimaryExtentLba` and `PrimaryDataLength` for the PVD root only inside the successful timestamp-parse branch. A malformed root date therefore erased the geometry required by the guarded paired primary/Joliet allocator, causing an unnecessary fallback to contiguous supplementary placement.

v0.7.33 always preserves root extent, data length, flags and the raw seven timestamp bytes. If the timestamp cannot be represented as `DateTimeOffset`, a neutral internal fallback time is used only for APIs that require a date; the raw bytes remain authoritative when rewriting directory records. The same principle applies to directories first encountered through their internal `.` record.

This is a format-level fix: no disc name, hash, filename or known LBA is used to select the behavior.
