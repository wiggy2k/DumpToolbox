# DIC v0.7.34 Joliet path-table primary-order regression

Some ISO9660/Joliet mastering tools construct the primary and supplementary path tables with the same directory-number ordering, replacing only the directory identifier with the Joliet name. A long Joliet alias can therefore sort differently from its primary ISO9660 short name.

v0.7.33 flattened Joliet directories by the visible Joliet name. This can produce correct extents but different directory numbers and parent-directory numbers in the generated Type-L/Type-M path tables.

v0.7.34 carries the proven primary ISO9660 path for every matched Joliet directory and orders siblings by their primary ISO9660 identifier when assigning path-table directory numbers. The Joliet identifier itself is still written to the supplementary path table. If no primary mapping exists, the existing Joliet-name ordering remains the fallback.

This is structural and evidence-driven: there are no title-, hash-, LBA-, or filename-specific rules.
