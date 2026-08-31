# UI v0.7.47 left-anchored output logs

Avalonia TextBox caret auto-follow can horizontally scroll a no-wrap log to the end of a long line. v0.7.47 retains vertical auto-follow but resets the embedded ScrollViewer horizontal offset to zero after each append. All read-only multiline output panes are also explicitly TextAlignment=Left.
