# XML DAT hash input (v0.7.88)

The shared `TargetParser` accepts XML DAT data in addition to the existing plain hash-row forms. This restores XML input for FindCRCs and Audio without replacing Redump URL / numeric disc-ID importing.

Supported XML can be a complete DAT, a `<game>`/`<machine>` fragment, or bare `<rom>` elements. Each usable `<rom>` requires `size` and `crc`; `name`, `md5`, and `sha1` are retained when present. Attribute order and single/double quoted values are accepted. `.cue` entries are ignored because they are metadata, not payload targets.
