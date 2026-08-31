# DIC v0.7.23 cooked ISO source for raw CD

For a raw 2352-byte CD reconstruction, a 2048-byte cooked ISO is useful evidence for ordinary ISO9660 file payloads but is not byte-exact sector evidence.

Policy matrix:

- Raw2352 target + Raw2352 BIN: full same-disc donor semantics.
- Raw2352 target + Cooked2048 ISO: filesystem/file-payload source only.
- Cooked2048 target + Cooked2048 ISO: full cooked same-disc donor semantics.
- Cooked2048 target + Raw2352 BIN: logical payload extraction where supported.

The payload-only raw-CD/cooked-ISO path deliberately skips metadata and exactness-region copying and does not satisfy raw-donor requirements. File matching/extraction still proceeds and the resulting source matches are retained.
