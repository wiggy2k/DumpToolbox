# DIC v0.7.28 Joliet numeric short-alias matching

Some mastering tools store a DOS-style numeric short alias in the primary ISO9660 tree while keeping the long user-visible name in Joliet. One observed structural example is `Desktop Theme` in Joliet versus `DESKTO~1` in the primary tree.

The `~N` suffix is collision-dependent and therefore must not be predicted from a long name. v0.7.28 only recognises a primary component of the form `PREFIX~N` (and the corresponding short-extension form for files) when `PREFIX` matches the beginning of the normalized Joliet component. The existing complete-relative-path, exact-file-size, forward-uniqueness and reverse-uniqueness guards remain in force. Directory metadata lookup likewise requires a unique primary path match.

This is a generic namespace mapping rule; it contains no disc names, hashes, fixed aliases or fixed numeric suffixes.
