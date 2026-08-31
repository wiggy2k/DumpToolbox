# DumpToolbox v0.7.59 — symmetric Joliet/primary separator elision

CHY contains name pairs where the Joliet spelling omits an underscore that is present in the primary ISO9660 spelling, for example `TRDUBL.a6e` versus `TR_DUBL.A6E`.

Earlier matching only projected punctuation from the Joliet/source side toward the primary name, so this reverse form was rejected. v0.7.59 compares a strictly alphanumeric projection of both path components as an additional conservative spelling test.

This does not by itself select a source: the existing complete-path, exact-size and reverse-uniqueness requirements remain in force. The same rule is used for initial source matching and later Joliet source-path validation.
