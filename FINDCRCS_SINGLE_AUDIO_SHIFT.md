# FindCRCs single-audio silence-shift recovery (v0.7.7)

When a CUE maps only one AUDIO target, a second audio-track anchor does not exist. If non-audio/source boundaries nevertheless prove an exact target-sized physical extent, DumpToolbox can now test whether the audio is shifted within its own zero-byte digital silence.

For a bounded source extent `S` of target length `N`:

- if `S` has `T` verified trailing zero bytes, FindCRCs searches `zeros(T) || S`; a match at offset `p` represents `k = T - p` zero bytes inserted at the start and the same `k` verified trailing zeros removed;
- if `S` has `L` verified leading zero bytes, FindCRCs searches `S || zeros(L)`; a match at offset `k` represents those `k` verified leading zeros removed and `k` zeros appended at the end.

Both scans use 1-byte alignment. The search range is inherently limited to the measured boundary-zero run, so the recovery cannot discard non-zero PCM. The reconstructed target is accepted only when CRC32 and the supplied MD5 verify.

The common two-track mixed-mode case is therefore safe: a matched Track 01 supplies the lower boundary for sole AUDIO Track 02 and source EOF supplies the upper boundary.


## Short singleton extent (v0.7.9)

If the same safe boundaries establish an extent shorter than the target by `N` bytes, the exact-size shift scan cannot run. DumpToolbox now assumes only that the missing bytes *might* be digital zero silence and tests that hypothesis exhaustively. It searches all `N + 1` ways to distribute the missing zeros between the start and end of the available audio.

The implementation constructs `zeros(N) || available_audio || zeros(N)` and performs a normal 1-byte FindCRCs search for the target-sized window. A match at offset `p` means `N - p` zeros belong at the start and `p` zeros belong at the end. This tests both pure prepend/append repairs and every split between them without guessing a direction.

Only a verified target hash is accepted. Non-zero missing PCM is never synthesized by this mode.
