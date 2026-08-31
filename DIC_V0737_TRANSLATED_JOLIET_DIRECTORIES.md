# DIC v0.7.37 translated Joliet directory layout

Some ISO/Joliet mastering software stores the supplementary directory tree as a fixed-sector translation of the primary ISO9660 directory tree rather than contiguously or immediately after each primary directory.

v0.7.37 adds a guarded allocator for this layout. The SVD-declared Joliet root and the DIC-proven primary root establish the translation delta. The same delta is applied to every mapped primary directory only when every generated Joliet directory range:

- is within the declared volume;
- does not overlap an ordinary file extent;
- does not overlap any declared Type-L/Type-M path-table sector; and
- does not overlap another generated supplementary directory.

If those checks fail, the existing paired-primary and contiguous allocation strategies remain available. No title, hash, fixed LBA, or fixed offset is hard-coded.
