# Audio recovery

The Audio tab reconstructs Redump-compatible CD-DA track BINs from verified lossless audio or raw byte sources.

## Supported sources

Audio must decode to 44,100 Hz, 16-bit stereo without resampling or other sample processing.

- FLAC and PCM WAV use built-in readers.
- APE, TTA, ALAC, AIFF, Ogg-FLAC and TAK use `ffmpeg` and `ffprobe`.
- BIN and ISO inputs are treated as raw bytes and are not decoded.
- M3U/M3U8, PLS, CUE and one-path-per-line lists can define an ordered source set.

Place external codec tools beside the executable, on `PATH`, or set `DUMPTOOLBOX_FFMPEG_DIR`. Lossy or unproven formats are rejected.

## Recovery workflow

1. Add sources in their intended order.
2. Choose an output directory.
3. Paste Redump rows, XML DAT content, or a Redump disc reference.
4. Adjust edge-silence and recovery options if required.
5. Convert and search.

Decoded files are joined into one CD-DA byte stream so source-file boundaries do not constrain the original track boundaries. Searches run at four-byte stereo-sample alignment, with CRC32 discovery and MD5 verification.

## Edge recovery

The optional edge-silence area allows missing all-zero PCM at the outer ends. When an adjacent target proves the boundary of an under-dumped first or final track, DumpToolbox first tries exact zero padding, then derives and searches for a missing segment. Manual `.partial` output can be retained when the complete track cannot be verified.

No repair is accepted unless the complete target hashes match. Non-zero samples are never synthesized from geometry alone.

## Heads and Tails

Heads and Tails is an optional final recovery source managed under **Settings → Heads and Tails**. Collection scans read loose or archived CUE-described images and append short non-zero samples from the start and end of audio tracks to a configured corpus file.

- Collection metadata is stored in `audio_heads_tails.sqlite` beside the executable.
- The corpus path is user-configurable and stored in `DumpToolbox.ini`.
- Scans are incremental and can use 1–64 workers.
- Archive contents are streamed where possible; complete archives are not extracted.
- All-zero tracks add no synthetic corpus bytes.
- Heads and Tails runs only after normal zero-fill and missing-segment recovery fail.

## Outputs

Working conversions use names such as `01_source.cdda.bin` and `combined_cdda.bin`. Verified targets are written separately using their target names. **Delete working files** removes conversion intermediates after success but retains verified targets and inspection partials.
