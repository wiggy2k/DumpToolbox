# DumpToolbox v0.8.60 — IRD rebuild verification correction

- Reuses a successful IRD/JB source verification when Rebuild is clicked, avoiding a second full MD5 pass.
- Falls back to verification if the selected IRD or source folder differs from the verified pair.
- Plain/decrypted PS3 ISO rebuilds no longer fail or get deleted because IRD region hashes do not match at this intermediate stage.
- IRD region hashes are retained for the later encrypted-ISO verification stage.
- Corrected PS3 region boundary parsing (alternating inclusive plain boundary / encrypted end-before-next-plain semantics).
- Rebuild still validates source lengths while streaming and preserves all existing per-file IRD MD5 gating before rebuild.
