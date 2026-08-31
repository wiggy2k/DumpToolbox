# DumpToolbox v0.8.10 — five-set parent-rescan validation

The v0.8.9 source matcher already implemented the requested iterative parent-directory rescan. v0.8.10 keeps that algorithm unchanged and records validation against the supplied PC A, U, V, W and XYZ DICSimulator oracle corpora.

## Production rule retained

1. Apply existing strong/direct source matches.
2. Rebuild mutual-unique primary ISO9660 parent -> source/Joliet parent correspondences from proven children.
3. For unresolved children of a proven parent, restrict candidates to the proven source parent.
4. Require exact size, compatible DIC recording timestamp, source availability and uniqueness.
5. If still ambiguous, allow only the existing strict local ~N family-prefix tie-break.
6. Run residual mutual uniqueness only after stronger rules.
7. Repeat while any new mapping is proved.

No generic lexical or reverse-lexical ordering is used by this production rule.

## Five-set FINAL-graph recheck

The supplied archives already come from a simulator pipeline containing equivalent parent-oriented elimination, so most deterministic collapses have occurred before FINAL. Reapplying proven-parent restriction, already-claimed-source removal and singleton elimination to the FINAL graphs produced:

| Corpus | FINAL unresolved targets | Additional deterministic mappings | Oracle-correct |
|---|---:|---:|---:|
| PC A | 1908 | 0 | 0/0 |
| PC U | 1588 | 4 | 4/4 |
| PC V | 961 | 1 | 1/1 |
| PC W | 931 | 0 | 0/0 |
| PC XYZ | 982 | 0 | 0/0 |
| **Total** | **7370** | **5** | **5/5** |

This indicates convergence rather than repeated speculative matching. The residual ambiguous families still need a separate conservative ordering classifier; they should not be consumed by a global lexical/reverse-lexical rule.
