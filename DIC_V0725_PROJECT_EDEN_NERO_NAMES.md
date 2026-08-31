# DIC v0.7.25 Project Eden / Nero ISO9660 naming regression

Source: supplied Project Eden exact extracted Joliet tree plus PROJECTEDEN DIC logs.

The disc was authored by `NERO - BURNING ROM` / `Nero - Burning ROM`. Its primary ISO9660 tree is not restricted to Level 1. The DIC logs prove several naming behaviours that must be handled consistently by every matching stage:

- Joliet directory `DirectX8.0a` -> primary `DIRECTX80A` (punctuation elision, no 8-character truncation).
- Joliet file `GameSpy/ArcadeInstallPROJECTEDEN108c.exe` -> primary `GAMESPY/ARCADEINSTALLPROJECTEDEN108.EXE` (ISO9660 31-character identifier: 27-character stem + `.` + 3-character extension).
- Joliet file `Images/Andre copy.edi` -> primary `IMAGES/ANDRE_COPY.EDI` (space replacement with underscore).
- Joliet file `Images/L1_9 copy.edi` -> primary `IMAGES/L1_9_COPY.EDI`.

v0.7.24 applied punctuation elision only in SkeletonResurrectionService. DicLogImportService still used an independent Level-1-only projection, and BuildJolietDirectoryTree independently shortened directory lookup paths to Level 1. This could allow a source path to match payload restoration but fail supplementary-directory reconstruction.

v0.7.25 uses the same deterministic candidate rules at all of those decision points: exact spelling (case-insensitive), Level-1 projection, Level-2/Nero projection, and punctuation elision. Existing exact-size and bidirectional uniqueness guards remain in force; directory metadata aliases must also resolve uniquely.
