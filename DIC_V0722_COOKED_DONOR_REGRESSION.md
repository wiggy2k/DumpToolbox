# DIC v0.7.22 cooked donor regression

The v0.7.20/v0.7.21 DVD work introduced cooked 2048-byte DIC skeletons, but `DicDonorImageService` still treated the *target* as raw 2352 bytes in several paths. This caused exact cooked ISO donors to work inconsistently depending on whether the operation was PVD identity, metadata copy, a file extraction, or an exactness-region copy.

## Fixed paths

- target PVD identity reads use the target image kind;
- metadata copy writes directly to 2048-byte target LBAs for cooked images;
- mandatory/optional donor-region bounds use the target sector size;
- cooked target donor-region writes use logical payloads directly rather than reading a nonexistent raw target header;
- raw BIN donors to cooked targets are accepted only by extracting a 2048-byte logical payload.

Raw-CD target behavior is unchanged.
