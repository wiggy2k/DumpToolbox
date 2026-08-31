# DumpToolbox user settings

## v0.7.56 Settings tab

DumpToolbox now exposes global settings in a dedicated top-level **Settings** tab after **Other Tools**. Theme can follow the system, force Light, or force Dark. SkeleTool SHA-1 database generation/use is controlled globally here. **Remember last used paths** controls path persistence independently from operational preferences; disabling it removes previously stored path keys. The Settings page also contains an **About...** dialog which currently reports the running assembly version.

# DumpToolbox user settings

DumpToolbox 0.7.6 creates `DumpToolbox.ini` automatically at runtime. The file is not required in source or release packages.

## Location

DumpToolbox first tries to use:

```text
<application folder>/DumpToolbox.ini
```

If the application folder is not writable, it falls back to the current user's local application-data folder:

```text
<LocalApplicationData>/DumpToolbox/DumpToolbox.ini
```

The INI is optional. Missing or malformed settings never prevent DumpToolbox from starting.

## Saved values

The INI stores:

- normal window width and height;
- window screen position and maximized/normal state;
- selected main/sub tabs;
- path fields on FindCRCs, Audio, ISO2BIN, MDF2BIN, SkeleTool, DIC, Concatenate, HashCalc, Base64 file mode, Find-Ends and ISO Extractor;
- stable per-tool choices such as modes, alignment and checkboxes.

The following are deliberately not stored:

- activity logs or progress/results;
- Redump target/hash text;
- Base64 string contents;
- Audio source queues;
- Concatenate source queues;
- DIC per-disc recovery state or saved source/donor matches.

DIC recovery state remains in the existing `*.dumptoolbox_dicstate.json` file and is independent from `DumpToolbox.ini`.

## Resetting

Each page with saved inputs has a **Clear saved inputs** button. It clears only that page's INI-backed fields/options.

The FindCRCs page also has **Reset all settings**. This resets every INI-backed page, tab selection and saved window geometry. It does not delete DIC recovery-state JSON.

Deleting `DumpToolbox.ini` while DumpToolbox is closed is also safe; a fresh file is generated on the next launch.


## Main menu layout (v0.8.26)

`Settings > General` now offers **Horizontal tabs** (default) or **Vertical tabs** for the main tool navigation. The choice is persisted as `MenuLayout=Horizontal|Vertical` in the `[Settings]` section of `DumpToolbox.ini`. Nested tab controls are deliberately unchanged. Resetting `DumpToolbox.ini` restores Horizontal.
