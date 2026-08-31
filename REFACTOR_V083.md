# v0.8.3 Iso2BinService modularisation

No intended behavioural changes.

The former monolithic `Iso2BinService` has been divided into partial-class modules:

- `Iso2BinService.cs` — public inspection/conversion orchestration and shared state/types.
- `Iso2BinService.Cue.cs` — CUE parsing, layout calculation, WAVE inspection, and output CUE generation.
- `Iso2BinService.RawSectors.cs` — raw 2352-byte sector framing, MSF, EDC, and ECC generation.
- `Iso2BinService.XaMetadata.cs` — DIC/redumper XA metadata loading and reporting.
- `Iso2BinService.IO.cs` — file/path/partial-output helpers.

This pass moves existing methods intact rather than rewriting algorithms.
