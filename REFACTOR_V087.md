# DumpToolbox v0.8.8 refactor

No intended behavioural changes.

The remaining legacy `UtilityTabs.cs` bundle was split by feature:

- `MainWindow.HashCalc.cs`
- `MainWindow.Base64.cs`
- `MainWindow.FindEnds.cs`
- `MainWindow.Utilities.cs` (shared utility-tab initialization only)

Already-separated UI features remain in their existing partials:

- `Mdf2BinTab.cs`
- `AudioRecoveryTab.cs`
- `IsoExtractorTab.cs`
- `DicTab.cs`

The split preserves field names and XAML event-handler signatures.
