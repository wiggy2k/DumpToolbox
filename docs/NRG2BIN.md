# NRG2BIN

NRG2BIN converts Nero NRG images using the `NERO` and `NER5` metadata formats.

## CD images

- DAO and TAO track layouts are supported.
- Raw 2352-byte Mode 1, Mode 2 and audio sectors are copied unchanged.
- Cooked 2048-byte Mode 1 and Mode 2 Form 1 tracks are expanded to raw sectors.
- 2448-byte sectors contribute 2352 main-channel bytes and can optionally preserve their 96-byte subchannel in a `.sub` file.
- Tracks from multiple sessions are emitted in session order.

CUE cannot reproduce physical multisession lead-in/lead-out areas, so the generated CUE records session and original-LBA information with `REM` lines and does not invent missing sectors. Ambiguous chunk/session mappings are rejected.

## DVD images

Nero media metadata is used to identify DVD images, with a conservative capacity fallback for a single 2048-byte data track. DVD payloads are written directly as 2048-byte ISO files; CD framing is never synthesized for DVD output.

## Subchannel and safety

Optional SUB output is disabled by default. When an image mixes subchannel and non-subchannel tracks, aligned zero placeholders preserve sector numbering and the analyser reports that condition. Outputs are written transactionally and incomplete files are removed on cancellation or failure.
