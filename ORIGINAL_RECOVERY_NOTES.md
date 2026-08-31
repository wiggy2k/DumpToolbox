# FindCRCsGUI source recovery

Recovered from the supplied `FindCRCsGUI.exe` only. No original source/PDB was available.

## Identification

- C# / Windows Forms
- Managed PE32 (x86-capable) .NET assembly
- CLR metadata version: `v4.0.30319`
- Original PE timestamp: 2015-07-13 06:40:52
- Original assembly version: 1.0.0.0
- Original PDB path embedded in EXE: `e:\FindCRCsGUI\FindCRCsGUI\obj\Debug\FindCRCsGUI.pdb`
- Original window title: `Findcrcs GUI Plus Tools (Mark I ver 4)`
- SHA-256 of supplied executable: `37b89ddbd2a6590cc16cf3f4b32200a921d46cb861401813383b3fb25643342d`

## What was recovered

The managed metadata retained all application method and field names. Core routines were reconstructed directly from IL, including:

- `FillEDCECCLuts`
- track search/extraction using companion `findcrcs.exe`
- dummy creator
- data/audio splitter
- `CheckSync`
- `NullEDC`
- `CalculateECCP`
- `CalculateECCQ`
- PlayStation image detection / serial search
- redump quick-search helper

The UI has been rebuilt from the surviving control names, labels, strings and event names. The supplied designer is intentionally clean/maintainable rather than a byte-for-byte reproduction of Visual Studio's 2015 generated designer code.

## Known original quirks preserved/noted

1. `GetID("saturn")` contains no Saturn-ID implementation in the executable. Saturn is detected by `FoundImageType`, but `GetID` returns an empty string for it.
2. `FoundImageType` returns the last 11-byte header string if neither Saturn nor PlayStation matches, instead of returning an empty/unknown marker.
3. The dummy creator uses `textBox3.Text` both as the requested byte count and as the output filename. This odd behaviour is present in the IL.
4. `button4_Click` exists in the assembly but there is no surviving `button4` field in the final form, suggesting this was an older/orphaned tool method.
5. The old redump helper uses plain HTTP and an old `/discs/quicksearch/` endpoint. It is preserved for fidelity and may need updating.

## Modernisation

The reconstructed project targets `net8.0-windows` so it can be worked on in a current Visual Studio. The original was a .NET 4.x-era WinForms program. If exact legacy build compatibility is preferred, retargeting to .NET Framework 4.8 should be straightforward.

## Companion executable

The main FindCRCs function expects `findcrcs.exe` to be available in the program's working directory/PATH. That executable was not embedded in FindCRCsGUI.exe and was not supplied, so it is not part of this recovery.
