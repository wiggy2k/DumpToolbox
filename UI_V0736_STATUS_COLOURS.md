# v0.7.36 filesystem status colours

The Skeletool and DIC filesystem TreeViews share `SkeletonTreeNode`. The item templates now render the status token separately from the entry text so success/failure can be coloured without colouring filenames.

- `✓`, `✓XA`, `✓R`, `✓0`: green
- `✗`: red
- `○`, `?`, `!`, `∅`, `…`: normal theme foreground

No recovery semantics or state schema changed.
