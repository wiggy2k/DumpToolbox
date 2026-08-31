# DIC v0.7.13 summary mapping regression

## Why v0.7.13 exists

v0.7.12 made reported-LBA fallback require the per-sector record to visibly contain the same anomaly as the EccEdc summary. That was correct for repeated-header protections such as SmartE, but too strict for older summary-only logs.

### Warcraft II

The supplied `WARCRAFT II Expansion.img_EccEdc.txt` contains 70,366 valid per-sector records. Its 68,736 proven Q-ECC mastering faults are listed only in the final ECC/EDC error summary; the corresponding per-sector lines are ordinary-looking Mode 2 Form 1 records.

Expected v0.7.13 mapping:

- summary occurrences: 68,736
- physical sectors mapped: 68,736
- unmapped: 0

This restores selection of the known Warcraft Q-ECC recipe and therefore preserves the v0.7.11 byte-exact regression target:

- CRC32 `af37ee45`
- MD5 `0141a4079c5b3c0f4ff371cb0ad1bc07`
- SHA1 `8fae1a878deb63850de4e5a83d5567e28c5ef78b`

### Zoo Tycoon 2 / SmartE

The supplied SmartE EccEdc log contains ten physical ECC/EDC failures whose headers all report LBA 192302. Physical sector 192302 itself is a normal sector; the errors occupy physical sectors 192303 through 192312.

Expected v0.7.13 mapping:

- summary occurrences: 10 (`192302` repeated ten times)
- mapped physical sectors: 192303-192312
- normal physical sector 192302: excluded
- unmapped: 0

The mapping priority is therefore:

1. exact physical sector visibly carrying the anomaly;
2. anomaly-marked physical records sharing the reported/header LBA;
3. only when no anomaly-marked candidate exists for that reported value, allow summary-only exact physical/reported fallback;
4. finally, old header-LBA fallback for summary-only logs.

## Recovery state

The DIC recovery state schema is bumped to 13. This intentionally prevents a cumulative output/state produced by the broken v0.7.12 Warcraft mapping from being treated as a valid carry-forward image after upgrading. Source/donor recovery can be repeated safely against a fresh v0.7.13 skeleton.
