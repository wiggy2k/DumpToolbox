# DIC v0.7.30 numeric short-alias regression

The numeric-short-alias matcher must not require the source stem to be longer than the primary alias prefix. An 8.3 alias can be created because the extension is over three characters or because of a collision even when the stem itself fits.

Observed generic cases:

- `Desktop Theme` -> `DESKTO~1`
- `Abe.Theme` -> `ABE~1.THE` (stem `ABE` equals alias prefix `ABE`; `.Theme` truncates to `.THE`)
- `Abe Size NESW.cur`, `Abe Size NS.cur`, `Abe Size NWSE.cur`, `Abe Size WE.cur` -> `ABESIZ~N.CUR`; these equal-size siblings require recording timestamps to resolve the collision group, and the numeric suffix itself is never predicted.

The matcher therefore accepts `normalizedSourceStem.Length >= aliasPrefix.Length` when the prefix matches. Existing full-path, exact-size, reverse-ambiguity and optional unique timestamp checks remain mandatory.
