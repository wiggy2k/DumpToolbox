# EOF slack mastering rules (v0.7.92)

DumpToolbox does not hard-code Easy CD Creator / Roxio EOF-slack mastering signatures. Both SkeleTool resurrection and the built-in DIC recovery path load runtime overrides from `EOFSlackRules.ini`.

The file is created beside `DumpToolbox.exe` on first application start from the embedded default template. Deleting the external file causes the current shipped defaults to be recreated on the next start/use. Editing the external file changes the next reconstruction without recompiling or restarting DumpToolbox.

## Default behaviour

Post-EOF slack remains zero-filled when no rule matches. This is the normal/default reconstruction behaviour and requires no rule. A rule exists only to override that default with a known mastering residue pattern.

## File format

```ini
[EOF-SlackData-Fix]
Enabled=true
FormatVersion=2

[Rule1]
Enabled=true
Name=Easy CD Creator 5.2 (056), CD-RTOS CD-BRIDGE
ApplicationContains=EASY CD CREATOR 5.2 (056)
SystemIdMatch=CD-RTOS CD-BRIDGE
DeltaSectors=2592
Confidence=HIGH
```

`ApplicationContains` is a case-insensitive substring match against the ISO9660 PVD Application Identifier.

`SystemIdMatch` supports:

- `*` — any System ID
- `<blank>` — only a blank/empty System ID
- any other text — exact trimmed, case-insensitive System ID match

Every valid rule means: for each eligible partial final file sector, copy only the bytes after logical EOF from the same byte offsets at `LBA - DeltaSectors`. There is no `Mode` field.

Invalid rules are ignored with a log warning. If more than one enabled rule matches the same disc, no EOF-slack override is applied and the ambiguity is logged. A disabled global section, no match, invalid rule, or ambiguous match therefore all leave the normal zero-filled EOF slack untouched.

## Evidence precedence

In the DIC path, stronger exact DIC sector recipes are re-applied after EOF-slack inference so exact raw-sector, fill and protection evidence wins.

## Default rule database

The first-run file is pre-populated only with currently observed residue-copy exceptions. Known zero-fill cohorts are deliberately absent because zero-fill is already the default. The external rule file is authoritative at runtime; adding or changing a rule does not require rebuilding DumpToolbox.


## v0.7.81 seed additions

The generated first-run seed also includes:

- Easy CD Creator 5.0 (336), `CD-RTOS CD-BRIDGE` -> 2688 sectors.
- Easy CD Creator 5.0 (352), `CD-RTOS CD-BRIDGE` -> 10 sectors.
- Easy CD Creator 5.3 (158), blank System ID -> 10 sectors.

Existing external rule files are intentionally never overwritten. Delete/rename an existing `EOFSlackRules.ini` if you want DumpToolbox to regenerate the current seed template.

## v0.7.92 seed additions

- Easy CD Creator 5.0 (310), `CD-RTOS CD-BRIDGE` -> 2688 sectors.
- Easy CD Creator 5.0 (314), `CD-RTOS CD-BRIDGE` -> 2688 sectors.
- Easy CD Creator 5.3 (034), `CD-RTOS CD-BRIDGE` -> 2592 sectors.
- Easy CD Creator 5.3 (060), `CD-RTOS CD-BRIDGE` -> 2592 sectors.
- Easy CD Creator 5.3 (158), `CD-RTOS CD-BRIDGE` -> 10 sectors.
- Easy CD Creator 6.0 (210), blank System ID -> 10 sectors.

The 5.3 (158) blank-System-ID -> 10-sector rule remains present, so both currently observed 5.3 (158) environments are covered explicitly.

## v0.8.11 validated C-cohort additions/corrections

- Easy CD Creator 5.0 (306), `CD-RTOS CD-BRIDGE` -> 2688 sectors (HIGH; 106/106 unique EOF targets).
- Easy CD Creator 5.0 (314), blank System ID -> 2976 sectors (HIGH; 14/14).
- Easy CD Creator 5.0 (352), blank System ID -> 2976 sectors (HIGH; 861/861).
- Easy CD Creator 5.3 (010), blank System ID -> 3072 sectors (HIGH; 17/17; supersedes the older 10-sector seed).
- Easy CD Creator 5.3 (034), blank System ID -> 3072 sectors (HIGH; 15/15; supersedes the older 10-sector seed).
- Easy CD Creator 6.2 (134), blank System ID -> 3072 sectors (HIGH; 20/20).

The counts above are by unique EOF target. If the same tail bytes occur at several source locations, the mastering-specific expected offset only needs to be present among the exact matches; duplicate hits are not counted as separate EOF targets.


## v0.8.12 C-cohort additions

The rule format now also supports `DataPreparerContains=`. A rule may match on `ApplicationContains`, `DataPreparerContains`, or both, plus the existing `SystemIdMatch`. This is required for CD-Producer and QuickTopix, which identify themselves in the ISO9660 PVD Data Preparer field rather than Application ID.

New/corrected seeded rules:

- Easy CD Creator 5.0 (352), CD-RTOS CD-BRIDGE: 10 sectors (corrected from 2688).
- Easy CD Creator 5.3 (010), CD-RTOS CD-BRIDGE: 2592 sectors.
- CD-Producer v1.4: 31 sectors.
- CD-Producer v1.7: 31 sectors.
- CD-Producer v1.8: 31 sectors.
- OMI QuickTopix 2.0.3: 128-sector observation retained but disabled by default (LOW confidence; Set V shows the signature is not sufficient for a fixed rule).
- OMI QuickTopix 2.20: 128 sectors.
- Roxio Burn Engine 3.0: 5120 sectors.


## Ambiguous observed mastering modes (v0.8.13)

FormatVersion 4 deliberately allows multiple enabled rules to match the same PVD mastering signature. This models cases where apparently identical Easy CD Creator signatures have been observed with different deterministic EOF history distances, potentially due to project settings, writer firmware, or the burn pipeline. DumpToolbox does not silently choose the first rule. It asks the user which observation to apply; when expected final-image hashes are known it can try all matching observations and keep only the one reproducing those hashes.

## v0.8.25 additions

- Tempra CD-Producer 1.2b -> 31 sectors.
- Easy CD Creator 5.3 (158), `CD-RTOS CD-BRIDGE` now has two enabled observed modes: 10 and 2592 sectors. Multiple matching observations are intentionally handled by the ambiguity/hash-verification workflow.
