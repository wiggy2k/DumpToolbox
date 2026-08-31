# FindCRCs short singleton-audio padding and shift recovery (v0.7.9)

When a safely bounded singleton AUDIO extent is shorter than its target by `N` bytes, DumpToolbox first tries all `N + 1` ways of inserting exactly `N` zero bytes between the start and end.

If no direct padding split verifies, recovery continues rather than stopping. DumpToolbox measures the existing leading (`L`) and trailing (`T`) zero-byte runs in the short source extent and constructs one search stream:

```text
zeros(N + T) || source extent || zeros(N + L)
```

Every target-sized 1-byte-aligned window in this stream is safe: the central `N + 1` positions are the direct padding splits, earlier positions can discard only verified trailing zeros, and later positions can discard only verified leading zeros. This therefore exhaustively covers a track which is both short and shifted in its own digital silence without ever discarding non-zero PCM.

Every accepted result must verify the target CRC32 and MD5 when supplied. The same Core behavior is used by CUE-mapped singleton audio and the no-CUE two-target inference path.
