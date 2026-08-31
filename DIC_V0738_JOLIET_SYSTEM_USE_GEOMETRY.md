# DIC v0.7.39 Joliet System-Use geometry

Primary ISO9660 and supplementary/Joliet directory records describe the same logical files but their per-record System Use areas are independent metadata. A primary XA/System-Use payload must therefore not be assumed to exist in the supplementary record.

v0.7.39 keeps the existing preserve-first behaviour. When inherited primary System Use causes a directory generated for an independently SVD-proven translated or paired Joliet layout to exceed its safe allocation, the builder constructs a second candidate without inherited primary System Use. That candidate is used only if the already-proven Joliet extents then validate completely against volume bounds, file extents, declared path tables, and all other generated directories.

This is evidence-driven: System Use is not globally stripped, no mastering application or disc name is special-cased, and ambiguous layouts continue to be rejected.
