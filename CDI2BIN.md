# CDI2BIN

CDI2BIN converts Padus DiscJuggler `.cdi` CD images to a conventional raw 2352-byte BIN plus CUE. The `.cdi` extension here refers to the DiscJuggler image container, not the Philips CD-i filesystem/platform.

## Container handling

DiscJuggler stores track payload bytes from the beginning of the image and a variable-length descriptor near the end. CDI2BIN recognises the documented footer version values for CDI 2.0 (`0x80000004`), 3.0 (`0x80000005`) and 3.5 (`0x80000006`) and walks the descriptor's session/track records with bounds checking.

The common read modes map directly to 2048, 2336 and 2352 stored bytes per sector. DiscJuggler can also store RAW-PQ (2368) and RAW-P-W (2448). For unknown read-mode values CDI2BIN does not assume a numeric mapping: it tests 2368/2448 against the complete payload length and accepts the image only when one assignment is unique.

## Sector conversion

- 2048-byte Mode 1: rebuilt as raw Mode 1 with sync, MSF, EDC and ECC.
- 2048-byte Mode 2: rebuilt as Mode 2 Form 1 using DumpToolbox's existing raw-sector builder.
- 2336-byte Mode 2: preserved byte-for-byte as the Mode 2 body and prefixed with the raw 16-byte sync/MSF/mode header.
- 2352-byte audio/data: copied unchanged.
- 2368-byte RAW-PQ: first 2352 bytes copied to BIN; 16-byte PQ-only tail is not expanded to `.sub`.
- 2448-byte RAW-P-W: first 2352 bytes copied to BIN; optional final 96 bytes written to `.sub`.

The `.sub` option defaults off, matching MDF2BIN and NRG2BIN. If enabled, sectors without full P-W data receive aligned zero placeholders, but an image containing 2368-byte PQ-only tracks is rejected in `.sub` mode rather than inventing R-W subchannels.

## Sessions and CUE

All parsed sessions/tracks are emitted in stored order. CUE cannot encode the full physical multisession lead-in/lead-out structure, so CDI2BIN records `REM SESSION xx` and `REM ORIGINAL_LBA TRACK xx ...` and does not synthesise missing session areas. Stored pregap/index-0 payload is retained and represented with `INDEX 00`/`INDEX 01`.

## Conservative failures

Conversion stops if descriptor markers, counts, lengths, sector-size inference, or total payload geometry are contradictory. This is intentional: CDI2BIN should not silently reinterpret an unfamiliar DiscJuggler variant. Real failing images are useful regression samples for extending support safely.

## CD vs DVD output

CDI2BIN keeps CD images as 2352-byte BIN + CUE (with optional SUB). A DiscJuggler image is treated as DVD only when the parsed layout is a single native 2048-byte Mode 1 data track and its capacity is beyond normal CD range; that payload is written directly as a 2048-byte ISO. This deliberately conservative rule avoids misclassifying ordinary single-track 2048-byte CD images until additional DiscJuggler DVD samples establish a reliable explicit medium marker.
