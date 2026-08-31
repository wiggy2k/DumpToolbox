# FindCRCs no-CUE singleton edge recovery (v0.7.8)

A CUE is not required when all of the following are true:

1. exactly two hash targets were supplied;
2. the ordinary FindCRCs scan verifies exactly one target;
3. **Attempt to fix under-dumped Audio edges** is enabled; and
4. the matched target plus source boundary/EOF establish a safe physical extent for the unmatched target.

The common case is Track 01 verified at offset 0 and Track 02 occupying the remainder of the source file. If the inferred Track 02 extent is exactly the target size but does not hash-match, DumpToolbox measures zero-byte silence at both ends and tests both signed shifts at one-byte alignment. If the safely bounded extent is shorter than the target, v0.7.9 instead tests every possible distribution of the exact missing byte count as zero silence between the start and end. It accepts a repair only after the target hash verifies.

No-CUE inference is deliberately limited to the two-target/one-match case. Larger or ambiguous layouts still require a CUE for audio-track identification. Track 02 pregap scrambling remains CUE-only.
