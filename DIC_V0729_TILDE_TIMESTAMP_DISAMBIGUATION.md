# DIC v0.7.29 numeric short-alias timestamp disambiguation

Some ISO9660 primary trees use collision-numbered short aliases for several Joliet names sharing the same normalized prefix, for example multiple long names mapping to `ABESIZ~1.CUR`, `ABESIZ~2.CUR`, and so on. The numeric suffix cannot safely be predicted from the long name alone.

v0.7.29 keeps the v0.7.28 generic `PREFIX~N` compatibility test, full relative-path requirement, exact-size requirement, and reverse-uniqueness guard. If several same-sized source files remain plausible for one primary record, the source filesystem modification timestamp may be used as an additional discriminator against the DIC/ISO9660 recording timestamp. It is accepted only when exactly one candidate matches and the reverse check likewise identifies exactly one primary record.

Timestamp evidence never broadens the name/path candidate set. If timestamps are absent, changed, duplicated, or otherwise ambiguous, DumpToolbox leaves the entries unmatched rather than assigning `~N` values heuristically.

No disc names, hashes, filenames, or fixed `~N` assignments are encoded by this rule.
