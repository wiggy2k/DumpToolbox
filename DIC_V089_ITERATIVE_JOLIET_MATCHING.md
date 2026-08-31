# DumpToolbox v0.8.9 — iterative Joliet/source matching

This release ports the conservative rules mined from the existing DICSimulator PC W and PC XYZ candidate/oracle corpus into the DumpToolbox DIC reconstruction source matcher.

## Rule order

The existing strong/direct and formatter-specific selectors remain first. The new rules run inside the existing DICSimulator-style fixpoint so every newly proved mapping can expose new parent-directory evidence before the next pass.

1. **Windows NT hashed 8.3 disc profile** — requires an earlier proven checksum-form Windows alias on the same disc, exact deterministic hashed leaf, exact size, compatible recording timestamp and one unused candidate. Parent textual compatibility is intentionally not required once the naming profile is proved.
2. **Proven-parent rescan** — derive mutual-unique primary-parent ↔ source-parent pairs from already proved child mappings, then retry their unresolved children using exact size + compatible timestamp.
3. **Strict alias tie-break** — if a proved parent still has multiple equal size/time candidates, permit only the existing local `~N` family-prefix compatibility test; never infer generic lexical `~N` order.
4. **Residual mutual uniqueness** — only after stronger rules, exact size + compatible timestamp may select a source globally when both target→source and source→target are unique among the remaining evidence-eligible candidates.

These rules use no original-image payload bytes or oracle identity. The oracle corpus was used only offline to grade the proposed selectors before porting them.
