# DIC v0.7.40 — DAT-proven cooked post-volume geometry

Older DIC log sets can expose ISO9660 Volume Space Size as the strongest sector-count evidence even when the complete cooked image contains additional sectors after that volume. A sibling Logiqx DAT may preserve the exact whole-image `.iso` size and hashes.

v0.7.40 no longer requires a `.iso` DAT size to equal `VolumeSpaceSize * 2048`. A unique `.iso` entry is accepted as cooked geometry when its size is an exact multiple of 2048 bytes and its sector count is at least the minimum count independently established by DIC filesystem/track/layout evidence.

When the DAT-proven sector count is larger, the reconstruction target is extended to that exact whole-image length. The additional LBAs are treated as post-volume sectors and remain donor-capable/zero-assumed unless other DIC evidence proves their bytes.

This is intentionally asymmetric with generic `.img` entries: `.iso` is explicit cooked-image evidence, while ambiguous `.img` files retain the previous exact-geometry requirement unless other DVD evidence independently proves cooked layout.
