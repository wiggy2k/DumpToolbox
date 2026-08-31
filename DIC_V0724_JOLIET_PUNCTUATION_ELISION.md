# DIC v0.7.24 Joliet punctuation-elision regression

Some ISO authoring software creates a primary ISO9660 identifier by removing punctuation from the Joliet/user-visible component rather than replacing it with `_` or applying strict 8.3 truncation.

Regression example:

- Joliet directory: `DirectX8.0a`
- Primary ISO9660 directory: `DIRECTX80A`

The source matcher now tries three component comparisons, in order:

1. case-insensitive exact spelling;
2. the existing conservative Level-1-style projection (invalid characters -> `_`, 8.3 limits);
3. punctuation elision while retaining ISO d-characters (`A-Z`, `0-9`, `_`) without imposing an 8.3 limit.

The third rule maps `DirectX8.0a` to `DIRECTX80A`. It is only evidence for a match when the complete relative path and exact byte length identify one DIC record, and the same source path is not compatible with another same-sized required record. Ambiguity is rejected rather than guessed.
