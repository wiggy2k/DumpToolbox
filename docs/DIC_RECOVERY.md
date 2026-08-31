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
2. Select one or more ordinary source folders, ISO Extractor outputs, or donor images.
3. Match sources and review mandatory/optional exactness requirements.
4. Rebuild. Matches and cumulative work are saved in `.dumptoolbox_dicstate.json` beside the logs.
5. Verify against supplied target hashes whenever available.

## Matching and filesystem policy

The logged primary ISO9660 structure remains authoritative. Source matching requires exact logical size and a conservative identity:

- exact primary path/name first;
- validated Joliet-to-primary projection when it is unique;
- ISO Extractor manifest identity for associated or colliding records;
- donor primary/Joliet evidence only under its stricter donor rules.

The matcher does not accept size-only or arbitrary filename guesses. Validated user-visible names can rebuild otherwise missing Joliet directories and path tables without replacing primary extents or metadata.

## Donors and exactness

A cooked ISO or raw BIN/IMG can supply payloads. Same-disc primary metadata is copied only when PVD identity and volume label match. Mandatory donor regions include non-empty Associated File payloads, Extended Attribute Records, and ambiguous colliding non-associated records that a mounted filesystem cannot prove.

Optional exactness regions—such as unproven system area, file-tail slack or missing metadata—remain zero-filled in a best-effort rebuild unless an exact same-disc donor supplies them. Raw-only anomalies require a 2352-byte donor; a cooked donor cannot provide Mode 2 Form 2 payload bytes or malformed raw framing.

## Sector evidence

EccEdc records are indexed by physical order while their printed/header LBA is retained as separate evidence. Logged MSF, mode, XA copies, Mode 0, audio, fill recipes and known protection faults are preserved when positively identified. Unknown corruption is not guessed.

Exact raw-sector overrides and deterministic fill recipes take precedence over inferred content. Final output is always verified when target hashes are available.

## Runtime reconstruction rules

- `EOFSlackRules.ini` contains mastering-specific post-EOF residue-copy rules. No match means normal zero-filled slack. Ambiguous matching rules require a user decision or hash-based selection.
- `JolietNamingRules.ini` contains mastering-specific Joliet-to-primary naming profiles; generic conservative projection remains the fallback.
- Deleting either file causes current defaults to be recreated on next use. Existing files are not overwritten automatically.
- ISOCD/Pantaray FS/TM payload repair is shared with SkeleTool and never overwrites conflicting non-zero bytes.
