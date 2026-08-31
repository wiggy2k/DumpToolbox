# DIC v0.7.52 — CeQuadrat allocator precedence

v0.7.51 introduced the CeQuadrat/WinOnCD physical Joliet directory allocator, but the generic translated and paired allocators were attempted first. On discs where one of those generic geometries was technically non-overlapping, it was accepted before the CeQuadrat rule ran.

When the independently validated CeQuadrat directory-link-table context exists, v0.7.52 attempts the CeQuadrat allocator first in every Joliet geometry retry path. The allocator packs Joliet directory bodies from the SVD root in ascending primary-directory extent order; the path table itself remains in primary path-table order. BuildJolietPathTable then naturally serializes the final assigned extents into both Type-L and Type-M copies.

No disc title, hash, filename, or fixed directory LBA is used to select the rule.
