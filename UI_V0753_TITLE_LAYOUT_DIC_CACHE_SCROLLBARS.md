# v0.7.53 UI and DIC clean-run changes

- The assembly version is retained in the window caption at all times. Tool progress is appended to the versioned base title rather than replacing it.
- Skeletool and DIC tree/log panes use a 1:1 split.
- DIC adds a persistent `Force rehash / clear cache` checkbox. On Load DIC logs it removes `<basename>.dumptoolbox_dicstate.json` and the `.dumptoolbox_dic_donor_cache` directory before importing. DIC source-folder matching is path/size based, so no unrelated Skeletool SHA-1 cache is deleted.
- Application-wide Avalonia ScrollBars are 16 px thick in both orientations.
