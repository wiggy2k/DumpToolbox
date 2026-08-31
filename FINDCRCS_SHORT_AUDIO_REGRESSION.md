# v0.7.9 short-audio regression

The short singleton-audio recovery was checked with byte-level synthetic fixtures.

1. Direct padding: a short extent with no displacement matches one of the `N + 1` ordinary start/end zero-padding splits.
2. Padded + shift, leading-silence case: no ordinary padding split matches; after adding the exact shortfall, the target is found only by discarding verified leading source zeros and adding the corresponding extra zero silence at the opposite edge.
3. Padded + shift, trailing-silence case: no ordinary padding split matches; the target is found only by discarding verified trailing source zeros and moving the equivalent silence to the opposite edge.

The combined search source is `zeros(N + trailingZeros) || source || zeros(N + leadingZeros)`. Every target-sized window can omit source bytes only from the measured leading/trailing zero runs, so the second-stage scan cannot discard non-zero PCM.
