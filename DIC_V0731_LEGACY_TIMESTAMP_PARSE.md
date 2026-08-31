# DIC v0.7.31 legacy recording-time parser regression

Some older DiscImageCreator `volDesc.txt` logs render ISO9660 directory timestamps as:

`1997-09-05 23:51:16 +00:00`

whereas the previous parser accepted only an ISO-8601-like `T` form. Consequently `SkeletonContentEntry.RecordingTime` was null and v0.7.29's timestamp tie-breaker could not resolve otherwise ambiguous numeric short aliases.

v0.7.31 accepts either `T` or whitespace between date and time and optional whitespace before the UTC offset. Timestamp evidence remains secondary: a candidate must already satisfy complete Joliet-to-primary path compatibility and exact byte length, and only a unique timestamp result is accepted.

Regression evidence from Abe's Oddysee:

- `ABEREC~1.ICO;1` — 1997-09-05 18:51:48 +00:00
- `ABEREC~2.ICO;1` — 1997-09-05 18:39:42 +00:00
- `ABESIZ~3.CUR;1` — 1997-09-05 23:51:16 +00:00
- `ABESIZ~4.CUR;1` — 1997-09-05 23:51:52 +00:00
- `ABESIZ~2.CUR;1` — 1997-09-05 23:52:48 +00:00
- `ABESIZ~1.CUR;1` — 1997-09-05 23:53:06 +00:00

The extracted source files have the corresponding distinct timestamps, so the groups resolve one-to-one without predicting the `~N` suffix.
