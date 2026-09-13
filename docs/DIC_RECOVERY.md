# DiscImageCreator recovery

The DIC tab reconstructs a raw 2352-byte working image from DiscImageCreator logs, recovered payloads, and optional donors. It is separate from the Redumper SkeleTool workflow.

## Evidence files

| File | Purpose |
| --- | --- |
| `*_volDesc.txt` | Required primary ISO9660 paths, extents, sizes, timestamps and volume metadata |
| `*_disc.txt` | Track geometry and expected whole-image hashes when available |
| `*.img_EccEdc.txt` / `*.scm_EccEdc.txt` | Physical sector modes, framing, XA subheaders and explicit error evidence |
| `*_mainInfo.txt` | Preserved metadata sectors and drive-offset evidence |

Choose any companion log and DumpToolbox discovers the related files.

## Recovery workflow

1. Load the log set and create the synthetic raw skeleton.
2. Select one or more ordinary source folders, ISO Extractor outputs, or ISO9660/Joliet/UDF donor images.
3. Match sources and review mandatory/optional exactness requirements.
4. Rebuild. Matches and cumulative work are saved in `.dumptoolbox_dicstate.json` beside the logs.
5. Verify against supplied target hashes whenever available.

## Matching and filesystem policy

The logged primary ISO9660 structure remains authoritative. Source matching requires exact logical size and a conservative identity:

- exact primary path/name first;
- validated Joliet-to-primary projection when it is unique;
- ISO Extractor manifest identity for associated or colliding records;
- donor primary/Joliet evidence only under its stricter donor rules.
- UDF-only extractor/donor files by an unambiguous exact or conservatively projected pathname plus exact logical size.

The matcher does not accept size-only or arbitrary filename guesses. Validated user-visible names can rebuild otherwise missing Joliet directories and path tables without replacing primary extents or metadata.

## Donors and exactness

A cooked ISO or raw BIN/IMG can supply payloads. Same-disc primary metadata is copied only when PVD identity and volume label match. A UDF-only image has no comparable ISO9660 PVD, so it is always payload-only and never supplies filesystem metadata, Joliet identity, or raw-sector exactness. Mandatory donor regions include non-empty Associated File payloads, Extended Attribute Records, and ambiguous colliding non-associated records that a mounted filesystem cannot prove.

**Force matched Joliet names** treats every saved source-relative pathname on an accepted match as authoritative Joliet name/casing evidence, without projecting it back to the target's primary ISO9660 alias. This is useful when a mastering tool generated numbered collision aliases that cannot be reversed reliably. It does not bypass payload matching, file-size/extent checks, donor identity checks, or the requirement that every ordinary file have a saved pathname. The option is off by default.

UDF VAT/VDS history is intentionally not folded into logical file SHA-1 matching. Disc Evidence can preserve and compare those structures separately, but DIC cannot synthesize a byte-exact UDF mastering from a logical file tree or ordinary `*_volDesc.txt` evidence. Exact UDF reconstruction requires the original structural sectors or equivalent raw descriptor/VAT evidence.

Optional exactness regions—such as unproven system area, file-tail slack or missing metadata—remain zero-filled in a best-effort rebuild unless an exact same-disc donor supplies them. Raw-only anomalies require a 2352-byte donor; a cooked donor cannot provide Mode 2 Form 2 payload bytes or malformed raw framing.

## Sector evidence

EccEdc records are indexed by physical order while their printed/header LBA is retained as separate evidence. Logged MSF, mode, XA copies, Mode 0, audio, fill recipes and known protection faults are preserved when positively identified. Unknown corruption is not guessed.

Exact raw-sector overrides and deterministic fill recipes take precedence over inferred content. Final output is always verified when target hashes are available.

## Runtime reconstruction rules

- `EOFSlackRules.ini` contains mastering-specific post-EOF residue-copy rules. `ApplyMode=Direct` is reserved for fully reproduced observations; `ApplyMode=HashTrial` tests the zero-filled baseline and matching residue candidates only when an expected whole-image hash is available. No verified match leaves normal zero-filled slack.
- `JolietNamingRules.ini` contains mastering-specific Joliet-to-primary naming profiles. Profiles can also select file versioning (`Version1` or `None`), directory-record ordering, path-table ordering, and the safety-gated `OpaqueTildeAlias`/`HexOrdinalAlias` families; generic conservative projection remains the fallback.
- Deleting either file causes current defaults to be recreated on next use. Existing files are not overwritten automatically.
- ISOCD/Pantaray FS/TM payload repair is shared with SkeleTool and never overwrites conflicting non-zero bytes.
