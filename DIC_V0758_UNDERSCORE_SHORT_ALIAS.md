# v0.7.58 — underscore numeric short aliases

Some ISO authoring software uses a collision short-name form ending in `_N` rather than the usual DOS `~N`. Cumhuriyet Bonus Disc demonstrates this in both directories and files, for example:

- `3D_Modeller` -> `3D_MOD_1`
- `ataturk_albumu` -> `ATATUR_1`
- `genclige hitabesi` -> `GENCLI_1`
- `Ata'nin Genclige Hitabesi.jpg` -> `ATA_NI_1.JPG`
- `Kurtulus Savasi Destani.avi` -> `KURTUL_1.AVI`
- `10.yil_marsi.mp3` -> `10_YIL_1.MP3`

DumpToolbox now recognises both `~N` and `_N` collision suffixes. Punctuation/underscore differences in the short alias prefix are tolerated only inside this alias test. A candidate still needs the same byte length, full component-by-component path compatibility, and reverse uniqueness before it can be accepted.

The rule is implemented both in source matching and in the subsequent Joliet-source-path validation so the same evidence is retained during metadata synthesis.
