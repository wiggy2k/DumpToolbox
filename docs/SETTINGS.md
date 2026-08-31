# Settings

DumpToolbox stores application preferences in `DumpToolbox.ini`. It first tries the application directory and falls back to `<LocalApplicationData>/DumpToolbox/` when that directory is not writable.

## What is saved

- Window geometry, state and selected tabs.
- Horizontal or vertical main navigation.
- Last-used paths when **Remember last used paths** is enabled.
- Stable per-tool choices such as modes, alignment, worker counts and checkboxes.
- IRD paths and encryption preference, but never a directly typed disc key.

Activity logs, progress/results, pasted hash targets, Base64 string contents, source queues and DIC per-disc state are not stored in the INI.

## Reset behaviour

Each tool's **Clear saved inputs** resets only that page. The global reset clears all INI-backed settings and window state without deleting DIC recovery JSON or catalogue databases. Deleting `DumpToolbox.ini` while the application is closed is also safe.

## SHA-1 Database

Register collection folders, enable or disable catalogue lookup, choose 1–64 scan workers, scan for changes, and inspect the activity log. The persistent database is `skeletool_sha1_catalogue.sqlite` beside the executable.

## Heads and Tails

Register audio-disc collection folders, select the `AudioHeadsandTails.bin` corpus location, configure workers and scan for changes. The corpus must exist at its configured path before the Audio recovery option becomes available.

## Rule files

`EOFSlackRules.ini` and `JolietNamingRules.ini` are seeded on first use and can be reset from Settings. User-edited existing files are not silently overwritten.
