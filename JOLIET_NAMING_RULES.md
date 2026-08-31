# Joliet naming rules

`JolietNamingRules.ini` is generated beside DumpToolbox.exe on first run. It lets DIC source and donor matching select mastering-specific Joliet -> primary ISO9660 projection behaviour without recompiling DumpToolbox.

Profiles match the target disc PVD using `ApplicationContains`, `DataPreparerContains`, and optional `SystemIdMatch`. `Methods` is a comma-separated list of projection families. If no profile matches, DumpToolbox falls back to the full historic generic rule set.

The Disc Evidence `joliet_iso9660_observations.csv` already contains SystemId, ApplicationId and DataPreparerId with every mapping, so new mastering-specific rules can be learned from evidence and added directly to this INI.
