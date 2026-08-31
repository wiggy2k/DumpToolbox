# DumpToolbox v0.8.6 refactor

No intended behavior changes.

The 2,326-line `MainWindow.axaml.cs` code-behind was split by UI workflow into partial classes:

- `MainWindow.axaml.cs` — window lifecycle, shared state, common log/dialog helpers.
- `MainWindow.FindCrcs.cs` — FindCRCs UI workflow.
- `MainWindow.Concatenate.cs` — concatenate UI workflow.
- `MainWindow.Iso2Bin.cs` — ISO2BIN UI workflow.
- `MainWindow.Skeleton.cs` — SkeleTool inspection, scanning and resurrection UI workflow.

Existing `MainWindow.Settings.cs` and tab-specific partials remain separate. XAML event-handler names and signatures are unchanged.
