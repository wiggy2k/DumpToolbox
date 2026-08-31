# Black Mirror / LaserLok regression (v0.7.12)

Fixture: `BMIRROR_UK`, LaserLok Marathon 3.07.

The DIC bundle contains several chronological views of the same recovery:

- `BMIRROR_UK.img_EccEdc.txt` — original DIC dump state (2024-03-30).
- `CloneCD.log` and `image.cue_EdcEcc_Track_1 (old).txt` — later recovery attempt (2024-04-01).
- 37 extensionless files named only by decimal LBA, each exactly 2352 bytes — recovered raw sectors (2024-04-01/02).
- `image.cue_EdcEcc_Track_1.txt` — post-repair complete checker map (2024-04-02).

A known repaired raw tail covering original LBA 243900-351671 was supplied for regression:

- sectors: 107,772
- bytes: 253,479,744
- MD5: `0c8a40ae83049522b0c62f530cd11133`

## Original DIC state versus repaired target

Original `BMIRROR_UK.img_EccEdc.txt` contains 351,672 physical records and reports:

- 342,373 ordinary Mode-1 records
- 7,286 per-sector `2336 bytes ... 0x55` records
- 1,742 bad-MSF records
- 270 ECC/EDC mismatch records
- 1 invalid-sync record

Comparing those physical positions with the supplied repaired BIN tail:

| Original DIC class | Final `0x55` body | Final normal Mode 1 |
| --- | ---: | ---: |
| ECC/EDC mismatch | 229 | 41 |
| bad MSF | 1,542 | 200 |
| explicit `0x55` | 5,787 | 1,499 |
| invalid sync | 0 | 1 |

Therefore the original DIC EccEdc map alone is not the final repaired-image state.

## Exact `0x55` history

`CloneCD.log` records exactly 7,595 unique failed sectors. `image.cue_EdcEcc_Track_1 (old).txt` independently reports exactly 7,595 sectors whose raw bytes 16-2351 are all `0x55`.

The final repaired checker reports 7,558 such sectors. The difference is exactly 37 sectors.

There are exactly 37 extensionless LBA-named 2352-byte files in the bundle. Every one matches the corresponding sector of the supplied repaired BIN byte-for-byte, and the set of those 37 LBAs is exactly:

`CloneCD failed LBAs - final 0x55 LBAs`.

This proves the repair history:

1. failed sectors were represented with an `0x55` body;
2. 37 sectors were subsequently recovered exactly;
3. those 37 exact sectors replaced their `0x55` placeholders;
4. 7,558 placeholders remained in the final checked image.

The 37 exact replacement LBAs are:

343266,
344118-344121,
344236-344237,
344433-344438,
344816-344819,
345346-345350,
346106-346109,
346606-346607,
347152-347154,
347922,
348712-348713,
349912-349913,
351320.

## Final `0x55` ranges in the repaired image

The supplied repaired BIN and final checker agree exactly on 7,558 sectors in 22 runs:

- 341925-341932 (8)
- 341941-341956 (16)
- 341965-342028 (64)
- 342037-342166 (130)
- 342175-342184 (10)
- 342427-343265 (839)
- 343414-344117 (704)
- 344212-344235 (24)
- 344393-344402 (10)
- 344421-344432 (12)
- 344512-344815 (304)
- 344842-345345 (504)
- 345402-346105 (704)
- 346303-346450 (148)
- 346469-346605 (137)
- 346806-347151 (346)
- 347180-347921 (742)
- 348065-348711 (647)
- 348738-349911 (1,174)
- 350209-351086 (878)
- 351144-351193 (50)
- 351213-351319 (107)

For every one of these sectors, bytes 0-15 are the ordinary canonical Mode-1 sync/header and bytes 16-2351 are `0x55`.

## Reconstruction implications

When an original DIC whole-image hash is available, that hash remains the target and later repair evidence must not silently change the image.

When no original image hash exists, as in this fixture, a later complete checker map covering all sectors in absolute-LBA order is stronger final-state evidence than an earlier DIC read-state map.

Exact decimal-LBA 2352-byte replacement files outrank both generated payloads and `0x55` recipes. Final recipe precedence is therefore:

1. exact validated raw-sector replacement;
2. exact all-zero sector evidence;
3. explicit final `0x55` body evidence;
4. proven mastering-fault recipe;
5. ordinary payload/EDC/ECC synthesis.


## v0.7.14 resurrection-order regression

The complete post-repair verifier writes all-0x55 records without an `MSF[...]` field, for example:

```text
LBA[341925, 0x537a5], 2336 bytes have been already replaced at 0x55
```

Because the verifier is accepted only after proving a complete absolute physical-LBA map, the missing per-line MSF does not make the header ambiguous. LBA 341925 must reconstruct as the canonical Mode-1 header followed by 2,336 literal `0x55` bytes.

v0.7.13 built the skeleton correctly but did not retain those verifier-only fill records as final resurrection recipes, allowing normal Mode-1 EDC/ECC regeneration to overwrite the protection tail. v0.7.14 retains the fill recipe and reapplies bytes 16-2351 after all payload and EDC/ECC work.

Known-good LBA 341925 from the supplied repaired image:

```text
00 FF FF FF FF FF FF FF FF FF FF 00 76 01 00 01
55 55 55 ... (2336 bytes total) ... 55
```
