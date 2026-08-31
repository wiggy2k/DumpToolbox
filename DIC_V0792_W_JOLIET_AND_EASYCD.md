# DumpToolbox v0.7.92 — PC W evidence merge

This build incorporates the reconstruction-visible lessons from the PC W DICSimulator batch.

## Joliet safeguards

- Numeric alias timestamp matching no longer becomes "unique" solely because previous sibling mappings consumed the other candidates.
- `LEXICAL_ALIAS_RANK_FROM_PROVEN_FAMILY` now requires proven anchors on both sides of the unresolved alias index. It is an interpolation rule, not an extrapolation rule.
- `ZERO_BASED_TERMINAL_ORDINAL_FAMILY` resolves a closed local family only when `~1..~N` exactly corresponds to source terminal numbers `0..N-1`, with exact parent, extension, size and recording-time compatibility for every member.

These constraints target the Wall Street Tycoon false cascade while allowing the repeated World War II Frontline Command `HIGHLI~1/~2` -> `highlight0/highlight1` families.

## EOF slack

The first-run `EOFSlackRules.ini` seed now contains all currently accepted Easy CD Creator fixed-delta cohorts known to this DumpToolbox line. Existing external INI files are not overwritten.
