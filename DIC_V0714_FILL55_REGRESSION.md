# DIC v0.7.14 complete-verifier 0x55 regression

## Black Mirror / LaserLok

The accepted complete post-repair `image.cue_EdcEcc_Track_1.txt` contains 7,558 final records of the form:

```text
LBA[341925, 0x537a5], 2336 bytes have been already replaced at 0x55
```

These records omit `MSF[...]`. Because the verifier was accepted only after proving all 351,672 physical records are present and each reported LBA equals its physical LBA, the header address is canonical and deterministic.

For LBA 341925 the supplied repaired BIN proves:

- raw header: `00 FF FF FF FF FF FF FF FF FF FF 00 76 01 00 01`
- bytes 16-2351: exactly 2,336 bytes of `0x55`
- EDC/ECC positions are therefore also literal `0x55`; they must not contain regenerated Mode-1 EDC/P/Q values.

v0.7.14 promotes complete-verifier fill records to final recipes even when the individual line omits MSF. Historical/incomplete EccEdc maps remain conservative and still require explicit header evidence.

The DIC state schema is version 14 so outputs created with the v0.7.13 bug are rebuilt from a fresh skeleton.

## Fast resurrection ordering

The fast block writer now executes final DIC recipes on every physical sector in the block. EDC/ECC regeneration remains conditional on a logical-payload modification, but exact raw overrides, exact-zero recipes, full-body `0x55` recipes and proven Q-ECC faults are always reasserted last.
