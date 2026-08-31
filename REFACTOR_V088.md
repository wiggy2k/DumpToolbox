# DumpToolbox v0.8.8 - DIC UI modularisation

This is a no-intended-behaviour-change refactor based on the confirmed-working v0.8.7 tree.

`DicTab.cs` had remained a large feature code-behind file even after the main window was split by feature. It is now divided by DIC workflow responsibility:

- `DicTab.cs` - DIC state, initialization, and path browse handlers.
- `DicTab.Load.cs` - log loading/import and initial inspection workflow.
- `DicTab.Match.cs` - source matching and donor scan workflow.
- `DicTab.Resurrect.cs` - resurrection, state clearing, match merging, and persistence.
- `DicTab.Tree.cs` - tree construction, status management, and action-button state.
- `DicTab.Logging.cs` - verbose inspection output and asynchronous DIC log streaming.

All methods remain members of the same `MainWindow` partial class. Existing XAML handler names and signatures are unchanged. No recovery rule, Joliet mapping rule, EOF mastering rule, source matching criterion, or output verification behavior is intentionally changed.
