# CDI2BIN

CDI2BIN converts Padus DiscJuggler `.cdi` images. It does not refer to the Philips CD-i platform or filesystem.

## Supported containers and sectors

DiscJuggler 2.0, 3.0 and 3.5 descriptors are parsed with bounds checking.

| Stored sector | Output behaviour |
| --- | --- |
| 2048-byte Mode 1 | Rebuilt as raw Mode 1 |
| 2048-byte Mode 2 | Rebuilt as Mode 2 Form 1 |
| 2336-byte Mode 2 | Preserved body with a generated 16-byte raw header |
| 2352-byte audio/data | Copied unchanged |
| 2368-byte RAW-PQ | First 2352 bytes copied; PQ tail is not expanded |
| 2448-byte RAW-P-W | First 2352 bytes copied; optional 96-byte SUB tail |

Unknown RAW-PQ/P-W mode values are accepted only when complete payload geometry determines one unique sector size.

## Sessions, pregaps and DVD images

Sessions and tracks are emitted in stored order. Stored pregaps are retained with `INDEX 00`/`INDEX 01`; session and original-LBA details that CUE cannot express are written as `REM` metadata.

A CDI is treated as DVD only when it is a single native 2048-byte Mode 1 track beyond normal CD capacity. That payload is written as ISO. This conservative rule avoids misclassifying ordinary data CDs.

## Conservative failures

Contradictory markers, counts, sector sizes or payload lengths stop conversion. SUB mode rejects PQ-only sources rather than inventing missing R-W channels. Partial outputs are not promoted to final files.
