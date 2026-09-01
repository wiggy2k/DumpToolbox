# DumpToolbox

DumpToolbox is a cross-platform .NET 8 and Avalonia desktop application for disc-image conversion, checksum-based recovery, and reconstruction from Redumper, DiscImageCreator, and PlayStation 3 IRD metadata.

Current version: **0.8.99**

## Tools

| Area | Purpose | Guide |
| --- | --- | --- |
| FindCRCs | Locate and extract known files from larger binary images | [FindCRCs](docs/FINDCRCS.md) |
| Audio | Recover Redump-compatible CD-DA tracks from lossless or raw sources | [Audio](docs/AUDIO.md) |
| ISO2BIN | Convert cooked ISO/CUE data to raw CD sectors | [ISO2BIN](docs/ISO2BIN.md) |
| MDF2BIN | Convert Alcohol 120% MDS/MDF images | [MDF2BIN](docs/MDF2BIN.md) |
| NRG2BIN | Convert Nero NRG images | [NRG2BIN](docs/NRG2BIN.md) |
| CDI2BIN | Convert DiscJuggler CDI images | [CDI2BIN](docs/CDI2BIN.md) |
| SkeleTool | Rebuild Redumper skeleton images from verified payloads | [SkeleTool](docs/SKELETOOL.md) |
| DIC | Reconstruct raw images from DiscImageCreator evidence | [DIC recovery](docs/DIC_RECOVERY.md) |
| IRD | Rebuild and optionally encrypt PlayStation 3 ISOs | [IRD rebuilder](docs/IRD_REBUILDER.md) |
| Other Tools | Concatenate, HashCalc, Base64, Find-Ends and ISO Extractor | [Other Tools](docs/OTHER_TOOLS.md) |

The complete function-oriented documentation is in the [documentation index](docs/README.md). Historical changes are recorded in [CHANGELOG.md](CHANGELOG.md).

## Quick start

Install the .NET 8 SDK, then run:

```powershell
dotnet restore DumpToolbox.sln
dotnet run --project DumpToolbox/DumpToolbox.csproj
```

To verify the repository:

```powershell
dotnet build DumpToolbox.sln --configuration Release
dotnet test DumpToolbox.sln --configuration Release --no-build
```

See the [development guide](docs/DEVELOPMENT.md) for publishing and project-layout details.

## Design and safety

DumpToolbox is intentionally conservative around recovery work:

- Source images and recovered inputs are not modified.
- Long-running conversions stream data instead of loading complete images into memory.
- New outputs are generally written to a partial file and promoted only after successful completion.
- Hashes are used as acceptance criteria whenever target values are available.
- Ambiguous layouts or unproven recovery rules are reported rather than silently guessed.
- Persistent per-disc or catalogue state is kept separate from ordinary UI preferences.

## Application layout

```text
FindCRCs
Audio
Convert
  ISO2BIN
  MDF2BIN
  NRG2BIN
  CDI2BIN
SkeleTool
DIC
IRD
Other Tools
  Concatenate
  HashCalc
  Base64
  Find-Ends
  ISO Extractor
Settings
  General
  SHA-1 Database
  Heads and Tails
```

## Runtime files

Depending on the functions used, DumpToolbox may create:

- `DumpToolbox.ini` for UI preferences and remembered paths;
- `EOFSlackRules.ini` and `JolietNamingRules.ini` for editable recovery rules;
- `skeletool_sha1_catalogue.sqlite` for collection SHA-1 metadata;
- `audio_heads_tails.sqlite` plus a user-selected Heads and Tails corpus;
- per-disc DIC state and donor-cache files beside the selected recovery logs.

Build output, runtime settings, databases, caches, temporary files, and recovery state are excluded from Git by the repository's `.gitignore`.
