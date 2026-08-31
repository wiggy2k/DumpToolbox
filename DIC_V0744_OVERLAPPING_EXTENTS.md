# DIC overlapping recoverable extents — v0.7.44

DIC filesystem records may legitimately reference shared or partially overlapping physical extents. Previous builds rejected a second restore plan as soon as it began before the first plan ended.

v0.7.44 treats overlap as evidence requiring validation rather than as a structural error. During raw resurrection, every overlapping DIC plan pair is compared over the exact source bytes that map to the shared physical sectors. Mode 1 and Mode 2 Form 1 consume 2048 logical bytes per sector; Mode 2 Form 2 consumes 2324 bytes.

If all bytes constrained by both files are identical, reconstruction continues and logs `DIC OVERLAP: VERIFIED`. If any byte differs, resurrection stops and identifies the conflicting LBA, user-data byte offset, and both file paths.

Non-DIC overlaps, generated-only plans, or overlaps that cannot be mapped to source bytes remain conservative errors.
