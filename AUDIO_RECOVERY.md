# Audio

The **Audio** tab reconstructs Redump-compatible CD-DA track BINs from lossless audio sources.

## Source validation

Every source must decode exactly to:

- 44,100 Hz sample rate
- 16 bits per sample
- 2 channels (stereo)

Compressed bitrate is deliberately not used as a validity test. For checksum recovery the important properties are that the codec is lossless and that the original decoded PCM is already CD-DA format. DumpToolbox does not resample, normalize, remix, apply ReplayGain, dither or otherwise alter the samples.

Supported sources:

- native FLAC — built-in decoder; no external tool required
- uncompressed PCM WAV — built-in reader; no external tool required
- Monkey's Audio (`.ape`) — ffmpeg/ffprobe
- True Audio (`.tta`) — ffmpeg/ffprobe
- Apple Lossless / ALAC (`.m4a` / `.mp4`) — ffmpeg/ffprobe; lossy AAC in the same containers is rejected
- AIFF PCM (`.aif` / `.aiff`) — ffmpeg/ffprobe
- Ogg-FLAC (`.oga` / `.ogg`) — ffmpeg/ffprobe
- TAK (`.tak`) — ffmpeg/ffprobe

For external-codec formats, place `ffmpeg.exe` and `ffprobe.exe` beside `DumpToolbox.exe`, put them on `PATH`, or set `DUMPTOOLBOX_FFMPEG_DIR` to their directory. The codec is inspected before decode and must be on the verified-lossless list.

WavPack (`.wv`) is intentionally not accepted yet because a `.wv` can be lossless, hybrid or lossy and the current probe does not reliably prove which source mode was used. This is safer for hash reconstruction than accepting a potentially lossy file.

## Input ordering

Sources may be selected directly or loaded from:

- M3U / M3U8
- PLS
- CUE
- a simple one-path-per-line text list

The displayed order is the concatenation order and may be changed with **Move up** / **Move down**. Mixed supported lossless formats are allowed in one recovery run.

## Conversion

Each source is decoded to raw stereo CD-DA PCM:

- signed 16-bit samples
- 44,100 sample frames/second
- left then right channel
- little-endian sample byte order used by the existing DumpToolbox CDDA/BIN workflow
- no container/header

Individual conversions are retained as `NN_<source>.cdda.bin` in the selected output folder.

All converted files are concatenated into `combined_cdda.bin`. This deliberately removes the supplied source split points as a constraint: Redump track boundaries may occur before or after boundaries chosen by the source files.

## Hash recovery

Paste Redump audio rows in the same format accepted by **FindCRCs**. DumpToolbox searches the concatenated audio using the existing rolling CRC32 scanner and verifies MD5 only when CRC32 matches.

Audio search alignment is **4 bytes**, one stereo sample frame. It is not restricted to 2352-byte sector boundaries, allowing sample-offset and differently-split lossless source sets to be recovered.

After one track matches, the next target is first tested immediately after it, so a run of correctly ordered Redump tracks normally turns into direct verification rather than a fresh full scan for every track.

## Leading/trailing digital silence

The **Edge silence search** setting adds an expendable all-zero search area before the first decoded sample and after the last decoded sample. This permits a target to match when the supplied lossless set is missing some all-zero PCM at the beginning or end.

Only genuine digital silence can be reconstructed this way. Missing non-zero samples cannot be inferred from a checksum.

The padded search image is temporary and deleted after matching. Recovered track files contain the exact matched bytes, including any zero samples required to satisfy their hashes.

Recovered targets are written only as individual track BIN files. DumpToolbox does not create an additional concatenated recovered-track image.


## Under-dumped first / last track recovery

Enable **Attempt to fix under-dumped segments with FindCRCs** when the supplied lossless set may be physically short at its outer edge. Normal FindCRCs matching runs first. If the first Redump target is missing but target 2 matches, target 2 is used as an anchor to calculate exactly how many bytes are absent before byte 0. The equivalent rule is used at the end when the final target is missing but the previous target matches.

For an anchored partial target DumpToolbox tries, in order:

1. prepend/append the exact missing length as zero PCM and verify the complete target CRC32+MD5;
2. if zero PCM fails, derive the missing segment CRC32 using the Find-ends CRC relationship;
3. search the complete `combined_cdda.bin` byte-for-byte for that missing segment;
4. reconstruct the full target and accept it only if the complete CRC32+MD5 matches.

If no verified missing segment is found, the bytes that are definitely part of the first/last target are still saved with a `.partial` extension. These `.partial` outputs are retained even when **Delete working files** is enabled. Without an adjacent matched target, DumpToolbox does not guess where an edge target begins or ends.

## Outputs

Typical output folder:

```text
01_track01.cdda.bin
02_track02.cdda.bin
03_track03.cdda.bin
combined_cdda.bin
Track_02_<md5>.bin
Track_03_<md5>.bin
Track_04_<md5>.bin
```

The activity log reports, for each recovered target:

- exact combined-stream offset
- leading zero padding used
- trailing zero padding used
- offset from the nearest supplied source boundary, in bytes and stereo sample frames

This makes it possible to see how the supplied file boundaries differ from the original CD track boundaries.

## Working-file cleanup

Enable **Delete working files** to remove the per-source converted `.cdda.bin` files and `combined_cdda.bin` after a successful recovery. Exact matched track BIN outputs and saved edge `.partial` files are excluded from cleanup and are always retained.


## Combined Track 02 pregap + missing-end repair (0.6.38)

For mixed-mode Track 02, pregap scrambling and edge repair are no longer independent when matched Track 01 and Track 03 prove the available Track 02 region is shorter than the target. The physical pregap sector(s) are corrected first; the resulting corrected prefix is then tested with the exact missing suffix zero-padded. If that fails, Find-Ends derives/searches the missing suffix from the corrected prefix so an unscrambled mastering-error sector cannot poison the missing-segment CRC calculation.

## v0.6.40 Track 02 pregap rebalance

When Track 01 and Track 03 are both matched, Track 02 has two exact anchor hypotheses. If the Track 02 region between those anchors is short and the final audio track proves a positive signed edge shift of `N` bytes, DumpToolbox tests whether `Track 02 shortfall + N` is a whole number of 2352-byte sectors. If it is, the pregap repair can test a boundary rebalance: scramble the detected empty data sector(s), remove only verified zero PCM bytes immediately after the corrected sector(s), insert the inferred silent pregap sectors at that boundary, and verify the complete Track 02 CRC32/MD5.

With **Save partial files for manual inspection** enabled and both immediate anchors available, both target-sized Track 02 candidates are saved: `.forward.partial` begins at the end of the preceding matched track and `.backward.partial` ends at the start of the following matched audio track.
## v0.7.7 single mapped audio track: zero-silence shift search

A CUE may contain only one AUDIO target, for example a two-track mixed-mode disc with Track 01 data and Track 02 audio. Previously the CUE-aware edge pass refused that case because there was no second audio-track anchor to determine shift polarity.

When non-audio/source boundaries still prove an exact target-sized extent, DumpToolbox now tests the bounded audio region for a pure shift within its own zero-byte PCM silence. For a final Track 02 this can be the end of a matched Track 01 through source EOF; an equivalent first/middle-track case can use source zero or a matched following target as the other boundary.

The scan measures the actual zero run at each edge and tests:

1. `zeros(k) + source[0..N-k]` for every `k` permitted by verified trailing zero bytes;
2. `source[k..N] + zeros(k)` for every `k` permitted by verified leading zero bytes.

The candidates are searched at 1-byte alignment with the normal FindCRCs verifier. A result is accepted only if the full target CRC32 and, when present, MD5 match. This makes the operation exhaustive for a pure zero-silence shift while preventing the recovery pass from throwing away non-zero PCM.



## v0.7.9 short singleton Audio extent

FindCRCs edge recovery no longer stops merely because the sole safely bounded AUDIO extent is shorter than its target. The exact shortfall is tested as digital zero silence at every possible start/end split, using 1-byte FindCRCs verification. This is hash-proven recovery only; DumpToolbox does not synthesize non-zero PCM.
