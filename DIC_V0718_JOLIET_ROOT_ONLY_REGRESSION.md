# DIC v0.7.18 Joliet root-only source regression — Ben 10

The Ben 10 test disc contains a Joliet supplementary volume descriptor and only three ordinary files in the root directory. The supplied user-visible/Joliet source tree also contains those three files at the root, and all three names happen to be valid primary ISO9660 names.

In v0.7.17 all three source files therefore matched through `ISO9660 exact relative path+filename+size`. The scan-level `scanIsJolietTree` flag only became true when at least one file needed the Joliet-to-ISO projection fallback, so none of the three exact matches were considered trustworthy Joliet pathname evidence. Joliet synthesis was consequently skipped with `3 ordinary file(s) do not yet have a trustworthy Joliet pathname`.

v0.7.18 treats the relative pathname from an ordinary source-folder exact match as valid Joliet naming evidence too. The source path still has to pass `SourceJolietPathMatchesPrimaryEntry`, so it must either equal the primary component case-insensitively or project conservatively to it. The payload match itself remains exact-path+size and ambiguity protections are unchanged.

Donor-image and ISO Extractor manifest matches are deliberately not promoted by this rule.
