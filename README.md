## v0.7.11 DIC evidence-aware regeneration

DIC rebuilding now treats EccEdc as an evidence stream rather than a simple `LBA -> mode` table. Physical sector position is separated from the printed/header-derived LBA; exact logged MSF/mode and independent XA subheader bytes are preserved; Mode 0/audio/unknown sectors are distinct; summary-only anomalies are overlaid; and corruption recipes are applied only when positively supported by the log. Unknown ECC/protection anomalies are raw-donor-capable rather than guessed. See `DIC_ECCEDC_EVIDENCE_V0711.md`.

## v0.7.10 application version display

The application version is now shown in the main DumpToolbox window title and is read from the built assembly version. The ISO Extractor no longer writes a tool/version banner into its activity log.

## v0.7.9 short singleton-audio zero-padding recovery

FindCRCs now also handles a safely bounded singleton AUDIO extent that is shorter than the expected target. If the available extent is short by `N` bytes, DumpToolbox first builds a temporary zero-silence search space and tests every possible distribution of those `N` missing zero bytes between the beginning and end of the audio at 1-byte precision. A 315-byte shortfall therefore tests 316 direct possibilities, from 315 bytes prepended / 0 appended through to 0 prepended / 315 appended.

If direct padding still does not verify, DumpToolbox then combines that required padding with the signed silence-shift recovery. It measures the verified leading/trailing zero runs already present in the short source extent and searches every safe padded+shifted target-sized window. This catches a track that is both short and shifted within its own digital silence, while guaranteeing that any discarded source bytes are zeros.

This applies both with a CUE and to the safe no-CUE two-target singleton inference introduced in v0.7.8. No repair is accepted unless CRC32 and MD5 (when supplied) verify.

## v0.7.8 no-CUE two-target audio edge recovery

FindCRCs no longer requires a cuesheet for the safe singleton two-target edge case. If exactly two targets are supplied and the ordinary scan finds exactly one, enabling **Attempt to fix under-dumped Audio edges** lets DumpToolbox treat the unmatched target as a provisional singleton edge candidate. The Core edge-recovery logic still has to prove physical boundaries (for example, end of matched Track 01 through source EOF), and an exact-sized extent is required before bidirectional zero-silence shifts are tested at one-byte alignment. Every accepted result must match the target CRC32/MD5.

A CUE remains useful/necessary for identifying audio tracks in larger layouts and is still required for Track 02 file-backed pregap scrambling.

# DumpToolbox

## v0.7.7 singleton audio silence-shift recovery

FindCRCs no longer gives up merely because a CUE contains only one mapped AUDIO track. If the surrounding physical boundaries are still provable — for example a matched Track 01 ends exactly where the sole Track 02 begins and the source image ends at Track 02's expected end — DumpToolbox treats that exact-sized audio extent as a candidate and tests whether its PCM is shifted within its own digital zero silence.

The recovery is deliberately conservative. DumpToolbox counts the zero bytes actually present at both ends, then tests both signed directions at 1-byte alignment. One direction prepends silence while removing only verified trailing zero bytes; the other removes only verified leading zero bytes while appending the same amount of silence. Non-zero audio is never discarded by this mode, and a repaired track is accepted only when its target CRC32 and supplied MD5 verify.

## v0.7.6 user settings

DumpToolbox now creates `DumpToolbox.ini` automatically on first run. It is runtime state and does not need to be included in a release. The preferred location is beside the executable; if that folder is read-only, DumpToolbox uses the current user's local application-data folder instead.

The INI remembers window geometry/state, selected tabs, last-used path fields and stable tool options. Activity logs, progress/results, Redump target/hash text, Base64 string contents, and DIC per-disc recovery state are intentionally excluded. Each relevant page has a **Clear saved inputs** button that resets only that page. FindCRCs also exposes **Reset all settings** for a complete INI reset. DIC's **Clear saved matches** remains separate and continues to control the per-disc recovery JSON rather than the INI.


Cross-platform .NET 8 / Avalonia desktop toolbox for disc-dumping and recovery workflows.

## v0.7.5 DIC logged ECC/EDC error reproduction

When a DiscImageCreator `*_EccEdc.txt` log contains an explicit `[ERROR]` sector list for Mode 2 Form 1 sectors, DIC recovery now preserves that error map through the complete rebuild. For those listed LBAs only, DumpToolbox reproduces the known mastering fault discovered from the Warcraft II Expansion dump: normal EDC and P ECC are stored, but Q ECC is calculated with raw-sector byte `0x873` temporarily forced to `00` before the correct P byte is restored. This allows the rebuilt image to retain DIC's intentionally/non-standardly bad ECC instead of silently normalising it.

The XA Submode field is authoritative when interpreting DIC's `mode 2 no edc` text. This keeps logged Form-1 error sectors as Form 1 while still recognising genuine Form-2/no-EDC sectors.

When a 2352-byte same-disc donor is used for exactness regions, its raw sector is retained for the otherwise-unproven bytes, then any DIC-proven Mode 2 Form 1 mastering fault for that LBA is reapplied before the sector is committed. This prevents donor insertion from normalising or replacing the DIC error pattern after the synthetic skeleton has already generated it.

The application is built around separate tabs so additional utilities can be added without turning the FindCRCs tool into a monolithic window.

Current top-level layout:

```text
FindCRCs
Audio
Convert
  |- ISO2BIN
  |- MDF2BIN
  |- NRG2BIN
  |- CDI2BIN
SkeleTool
DIC
Other Tools
  |- Concatenate
  |- HashCalc
  |- Base64
  |- Find-Ends
  |- ISO Extractor
```

## FindCRCs tab

The existing high-speed embedded hash scanner is preserved.

Features include:

- 1-byte exhaustive search by default.
- Optional 2352-byte CD-sector alignment.
- Parses old Redump track-table rows.
- Parses newer Redump filename/hash rows.
- Accepts simple `SIZE CRC32 [MD5]` input too.
- Sequential-target optimisation: after a match, the next target is first tested at the exact end of the previous match.
- CRC32 candidate filtering with MD5 verification.
- 64-bit offsets.
- Live scan rate/activity logging.
- Verified matches are automatically extracted beside the source image.
- New Redump rows keep their supplied `.bin` filename.
- Old Redump track rows use `Track_<number>_<md5>.bin`.
- Optional **Attempt to fix under-dumped Audio edges** is CUE-aware. It only repairs the first and last AUDIO tracks identified by the supplied CUE (normally Track 02 and the final track on a mixed-mode disc). When the immediately preceding target (normally Track 01) is matched, the first AUDIO track first tests a forward-anchored missing-end interpretation from that verified boundary; if it does not verify, it falls back to working backwards from the verified start of the next AUDIO track (normally Track 03). Track 01 still does not have to hash-match for the backwards fallback to establish the lower boundary. The last AUDIO track is worked forwards from the previous matched audio track. It tries zero padding first, then Find-ends searches the complete source for the missing segment.
- Optional **Save partial files for manual inspection** saves bounded candidates for unmatched AUDIO tracks, not only the first/last track. Internal audio tracks use hash-matched immediate neighbours as hard boundaries; when a bounded extent is short, FindCRCs can also save forward/backward target-sized hypotheses for comparison. The automatic repair pass uses the same verified bounds to try exact-size signed silence shifts and exhaustive start/end zero-padding for short internal tracks before falling back to `.partial` inspection output. Extreme-track/source-EOF behaviour is unchanged.
- Optional **Scramble Track 2 pregap data sectors if present** handles the mixed-mode mastering case where empty raw data sectors sit in Track 02's file-backed INDEX 00 pregap. DumpToolbox corrects only recognised empty raw data sectors, then runs the normal 1-byte FindCRCs search across the full corrected pregap/Track-02 window instead of assuming the complete CUE pregap belongs at the start of the Track 02 BIN. This supports Redump-style splits where the normal 150-sector pregap is effectively attached to Track 01.
- With a CUE loaded, FindCRCs logs whether the source image ends exactly at, before, or after the expected extent of the final track. Extra tail data is reported in bytes and 2352-byte raw sectors; if the final track is unmatched, the extent may be projected from the nearest earlier matched track and the expected intervening track sizes.


## NRG2BIN

NRG2BIN converts supported Nero NRG CD images to a raw 2352-byte BIN plus CUE. It supports old `NERO` and newer `NER5` metadata, DAO/TAO track layouts, multisession track payloads, and 2448-byte sectors containing 96 bytes of stored subchannel data.

The companion `.sub` output is optional and disabled by default. When enabled, stored 96-byte subchannel data is preserved alongside the 2352-byte BIN; when disabled, only the main-channel 2352 bytes are written. Multisession boundaries and original track LBAs are retained as CUE `REM` metadata because standard CUE syntax cannot reproduce physical session lead-in/lead-out areas. Ambiguous layouts fail explicitly rather than being guessed. See `NRG2BIN.md` for the detailed format notes.

## CDI2BIN

CDI2BIN converts supported **Padus DiscJuggler `.cdi` images** (not Philips CD-i media) to a raw 2352-byte BIN plus CUE. It reads the trailing DiscJuggler v2.0/v3.0/v3.5 descriptor, preserves all parsed sessions and tracks, and supports the common 2048-, 2336- and 2352-byte storage forms. Later RAW-PQ/RAW-P-W images using 2368/2448-byte sectors are accepted when their stored sector size can be resolved unambiguously from the descriptor and payload geometry.

2048-byte Mode 1/Mode 2 sectors are expanded to raw 2352 sectors; 2336-byte Mode 2 bodies receive the correct sync/MSF/mode header; 2352-byte raw/audio data is copied unchanged. For 2368- and 2448-byte storage, the first 2352 bytes are retained as the BIN main channel. Full 96-byte P-W subchannel from 2448-byte sectors can optionally be written to a companion `.sub` file (off by default). 2368-byte images contain only 16 bytes of PQ subcode, so CDI2BIN will not fabricate the missing R-W channels; enable `.sub` only for images with full 2448-byte P-W storage.

Multisession tracks are retained in session order. Because standard CUE syntax cannot reproduce physical session lead-in/lead-out areas, session numbers and original track LBAs are written as CUE `REM` metadata instead of inventing sectors. Unknown or contradictory descriptor layouts fail explicitly. See `CDI2BIN.md` for technical notes.

## Other Tools tab

### HashCalc

Calculates hashes for a selected file in one streamed pass. CRC32, MD5 and SHA-1 are selected by default, with optional SHA-256, SHA-384 and SHA-512.

### Base64

Encodes or decodes Base64 strings and files. String mode uses UTF-8 text. File mode streams arbitrary files without loading the full input into memory and writes through a `.partial` file before the final rename.

### Find-Ends

Recovers a contiguous missing prefix or suffix from a partial file when the complete byte length, CRC32 and MD5 are known. It can calculate the missing segment's CRC32 without a source file, or exhaustively search an optional source file for that block. A CRC32 candidate is only accepted when prepending/appending it reconstructs the expected complete MD5. Auto mode tries both missing-start and missing-end interpretations.

### Concatenate

Combines multiple source files into one destination file in the displayed order.

- Add multiple files at once.
- Remove files.
- Move files up/down to control concatenation order.
- Choose or type the destination filename.
- 4 MiB streamed I/O; source files are not loaded into RAM.
- Live overall progress and throughput.
- Cancellation support.
- Writes to `<destination>.partial` and only renames it to the final filename after successful completion.
- Optional boundary-aware zero padding can be inserted between source files.

With padding disabled, the resulting file is exactly:

```
source1 + source2 + source3 + ...
```

No headers or separators are added.

## Audio tab

Recovers exact CD-DA track BINs from verified lossless audio sources.

- Accepts ordered FLAC, WAV, APE, TTA, ALAC, AIFF, Ogg-FLAC and TAK sources, including mixed-format M3U/M3U8/PLS/CUE playlists.
- Rejects sources that are not already 44,100 Hz, 16-bit, stereo; compressed bitrate is irrelevant and DumpToolbox never resamples/remixes for checksum recovery.
- Native FLAC and PCM WAV are decoded internally. APE/TTA/ALAC/AIFF/Ogg-FLAC/TAK use ffmpeg+ffprobe from beside the executable, PATH, or `DUMPTOOLBOX_FFMPEG_DIR`.
- Writes individual raw CDDA `.cdda.bin` files and `combined_cdda.bin`.
- Concatenates all decoded sources before searching, so source-file split points do not need to equal the original CD track boundaries.
- Runs the embedded FindCRCs engine at 4-byte stereo-sample alignment against pasted Redump SIZE/CRC32/MD5 rows.
- Can add a configurable temporary zero-PCM region before/after the source audio to recover missing digital silence at the outer edges.
- Exact matches are extracted automatically as individual track BIN files. No additional concatenated recovered-track image is written.
- Optional under-dumped edge recovery uses matched track 2 / penultimate-track anchors for a missing first/last track, tries zero PCM first, then derives the missing segment CRC32 and searches the whole combined audio stream. If reconstruction still fails, the available anchored track data is saved as `.partial`.

The Audio tab also includes a one-click Redump-hash clear button and an optional post-recovery cleanup that removes working `.cdda.bin`/combined files while keeping matched track outputs.

See `AUDIO_RECOVERY.md` for details.

## Convert tab

The **Convert** top-level tab groups image-format conversion tools. ISO2BIN and MDF2BIN can optionally send their completed BIN directly to the FindCRCs source field and switch to FindCRCs.

### ISO2BIN

Converts cooked 2048-byte CD-ROM data into raw 2352-byte sectors and can also rebuild **single- or multi-file mixed-mode images described by a CUE sheet**.

### ISO-only mode

When no CUE is supplied, the complete input is treated as one cooked 2048-byte data track.

- Verifies that the input size is an exact multiple of 2048 bytes before conversion.
- `Auto` scans ISO9660 volume descriptors for the `CD-XA001` marker: XA-marked images are emitted as Mode 2 Form 1; otherwise Auto uses Mode 1.
- Manual `Mode 1` and `Mode 2 Form 1 (XA)` overrides are available.
- Generates the 12-byte sync field, BCD MSF header, EDC and Reed-Solomon P/Q ECC for each raw sector.
- Mode 1 layout: 2048 user bytes, EDC, 8 reserved zero bytes, P/Q ECC.
- Mode 2 Form 1 layout: duplicated XA subheader, 2048 user bytes, EDC and P/Q ECC.
- Optional XA metadata can be supplied from a DIC `*_EccEdc.txt` log or a raw Redumper `.skeleton`; exact per-sector File Number, Channel Number, Submode and Coding Info replace the generic XA subheader before EDC/ECC are generated.
- An optional Redump target row can define the expected output filename, size and hashes. If the cooked ISO is short, empty 2048-byte sectors are appended virtually; if it is long, trailing cooked sectors beyond the target length are ignored. The resulting raw BIN is then checked against CRC32, MD5 and optional SHA-1.

### CUE-driven mixed-mode mode

Supply a CUE when the backing image contains mixed sector sizes, for example a cooked `MODE1/2048` data track followed by raw 2352-byte CD-DA audio tracks in the **same file**. The whole backing file therefore does not need to be divisible by 2048.

Supported CUE track types:

- `MODE1/2048` → converted to `MODE1/2352`.
- `MODE2/2048` → converted to `MODE2/2352` Form 1.
  - When XA metadata is supplied, the original per-sector XA subheaders are used by absolute output LBA.
- `AUDIO` → copied byte-for-byte as 2352-byte sectors.
- `MODE1/2352` → copied unchanged.
- `MODE2/2352` → copied unchanged.

The CUE is authoritative in this mode. Track boundaries are calculated from its `INDEX` positions, taking each track's source sector size into account. Conversion preserves the number of CD frames, so the original `INDEX`, `INDEX 00`, `PREGAP`, `POSTGAP`, FLAGS and other CUE metadata remain valid. DumpToolbox writes a replacement CUE beside the output BIN, changing only the `FILE` filename and cooked track declarations to their `/2352` equivalents.

### Important Mode 2 limitation

A 2048-byte cooked Mode 2 image contains the user-data payload but does **not** retain the original XA per-sector subheader metadata (file number, channel, EOR/EOF flags, etc.). DumpToolbox therefore uses the generic Form 1 data subheader `00 00 08 00` (duplicated). The resulting sectors have correct EDC/ECC and are structurally valid, but cannot be guaranteed to reproduce the original mastered raw-track hash when the original used different XA subheader values.

All conversions stream in batches, support cancellation, and write to `.partial` files before the final rename.


## SkeleTool tab

Rebuilds a Redumper data-track `.skeleton` using its companion `.hash` manifest and a folder of candidate source files.

Workflow:

- Choose the Redumper `.skeleton`; DumpToolbox automatically suggests the same-basename `.hash` when it exists.
- Load the pair to inspect the preserved ISO9660 metadata and display the filesystem as a directory tree.
- Choose a folder of candidate source files (recursive search is enabled by default).
- DumpToolbox SHA-1 hashes the candidate files and matches them against the Redumper manifest regardless of filename.
- Tree entries update live: found, XA/Form2 match, missing, empty, special or restored.
- `Resurrect` copies the skeleton to a new output and repopulates every matched extent. The source skeleton is never modified.
- Raw 2352-byte skeletons preserve their existing sync/MSF/mode/XA subheaders and regenerate EDC/ECC after user data is restored.
- Cooked 2048-byte skeletons are patched directly.
- `SYSTEM_AREA` and Redumper `GAP_#######` hash entries are recognised. Gap length is reconstructed from the preserved ISO9660 layout when possible.
- Redumper `.XA` alternate hashes are recognised for raw Mode 2 Form 2 payloads; these are restored using the preserved XA subheaders and Form-2 EDC.
- Partial resurrection can be allowed so unmatched payloads remain zeroed. Output is written through a `.partial` file and renamed only after successful completion.

The implementation is independent C# code based on the published Redumper skeleton/hash format and CD-ROM/ISO9660 structures; no ResurrectSkeleton source code is embedded.

### ISO Extractor

Reads the primary ISO9660 filesystem directly from a 2048-byte ISO or 2352-byte BIN without mounting it through the host OS. This preserves ISO9660 Associated File records and same-path records that a normal Windows/Linux filesystem view may hide.

- Ordinary visible records are extracted to their normal relative paths.
- Additional/Associated records are stored under `.dumptoolbox_iso_records/`.
- `.dumptoolbox_iso_manifest.json` records the original ISO path, extent, length, File Flags and storage fields for every extracted record.
- A successful extraction automatically fills the DIC **Source Folder** with the output directory.
- The manifest and private record directory must remain with the extracted files for DIC recovery.

### DiscImageCreator log recovery

The separate **DIC** tab handles DiscImageCreator log recovery and synthetic raw-image reconstruction.

- `*_volDesc.txt` is required and supplies the primary ISO9660 filesystem paths, extents, logical sizes, recording timestamps, directory records and volume information.
- Normal recovered source folders may use Joliet/user-visible names. DumpToolbox first tries the logged primary ISO path, then a unique conservative Joliet-to-ISO9660 8.3 projection with exact size; validated Joliet paths can reconstruct otherwise-unlogged supplementary directory/path-table sectors while the primary ISO metadata remains byte-authoritative.
- `*_disc.txt` is optional and supplies track geometry/mode plus the original whole-image CRC32/MD5/SHA1 when DIC recorded them.
- `*.img_EccEdc.txt` / `*.scm_EccEdc.txt` is optional but strongly recommended. The complete per-sector log is parsed to preserve the exact physical mode/form and, for Mode 2, the XA file number, channel number, submode byte and coding-info byte, including EOF/EOR/Form/Data/Audio/Video flags. The XA Submode Form bit is authoritative: DIC text such as `mode 2 no edc` does not by itself force a sector to Form 2. Genuine Form 2/no-EDC sectors retain their logged Form-2 layout and omit the optional EDC. Separately, DumpToolbox parses DIC's explicit `[ERROR] ... user data doesn't match the expected ECC/EDC` sector list and uses only those LBAs to reproduce the known DIC Mode 2 Form 1 mastering fault during EDC/ECC regeneration.
- `*_mainInfo.txt` is optional and is used to pre-populate any complete 2048-byte ISO metadata sectors that DIC dumped. Metadata not present there remains zero and is reported as a warning.

The generated `<basename>_DIC_skeleton.bin` has the original raw sector count where the logs provide it. File payload extents are zero-filled while available filesystem metadata is preserved. Mode 1, Mode 2 Form 1 and Mode 2 Form 2 sector framing is generated with valid EDC/ECC as appropriate. Mode 2 XA subheaders are recreated from the per-LBA DIC log instead of using a generic header.

Because DIC did not provide a Redumper-style per-file hash manifest, ordinary candidate files are matched conservatively by the primary ISO9660 relative path/filename and exact logical byte length. Multi-Extent (`0x80`) records are combined into one logical source entry and the matched file is split back across its recorded extents during resurrection. ISO interleaving is also reconstructed from the logged File Unit Size and Interleave Gap Size, so those files remain recoverable from ordinary extracted files. Non-empty Associated File payloads are exposed as separate source requirements and can be supplied by the manifest-aware folder created by the ISO Extractor tab; they are never guessed from an ordinary same-name host file. Exact same-disc ISO/BIN donors are therefore reserved mainly for Extended Attribute Record blocks and multiple non-associated ISO records that normalize to the same mounted pathname. Record/Protection or reserved flag bits do not force a donor by themselves. Form 2 payload restoration uses the physical DIC LBA map; mandatory donor regions that include Form 2 require a 2352-byte BIN donor.

The synthetic image is recovery scaffolding, not a promise of a byte-identical original before all payloads are restored: old logs may omit some system-area, slack, gap or supplementary filesystem bytes. When `disc.txt` contains original image hashes, DumpToolbox retains and displays them as the eventual byte-for-byte validation target.

Status markers used in the filesystem tree include `✓` (source matched), `✓XA` (Form2/XA source matched), `✗` (missing), `∅` (zero-length file), `✓0` (zero SYSTEM_AREA needs no source file), `…` (restoring), and `✓R` (restored).

## Requirements

- .NET 8 SDK to build.
- Avalonia dependencies are restored from NuGet.
- SkeleTool catalogue archive support is built in through the pure-managed SharpCompress library. No native 7-Zip DLL and no separately installed 7-Zip executable are required.

The project is currently pinned to Avalonia 11.3.13 for compatibility with the .NET 8 SDK generation used during recovery/testing.

## Build

From the repository root:

```bash
dotnet restore
dotnet build -c Release
```

Run it with:

```bash
dotnet run --project DumpToolbox -c Release
```

## Windows publish

Self-contained Windows x64:

```bash
dotnet publish DumpToolbox -c Release -r win-x64 --self-contained true
```

Framework-dependent Windows x64 (requires .NET 8 on the target):

```bash
dotnet publish DumpToolbox -c Release -r win-x64 --self-contained false
```

Framework-dependent single EXE:

```bash
dotnet publish DumpToolbox \
  -c Release \
  -r win-x64 \
  --self-contained false \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true \
  -p:DebugType=None \
  -p:DebugSymbols=false
```

The executable is named `DumpToolbox.exe`.

## Linux publish

```bash
dotnet publish DumpToolbox -c Release -r linux-x64 --self-contained true
```

## Project layout

```
DumpToolbox.sln
├── DumpToolbox.Core/
│   ├── HashSearchEngine.cs
│   ├── TargetParser.cs
│   ├── Crc32.cs
│   ├── ConcatenateService.cs
│   ├── Iso2BinService.cs
│   └── SkeletonResurrectionService.cs
└── DumpToolbox/
    ├── MainWindow.axaml
    ├── MainWindow.axaml.cs
    ├── SkeletonTreeNode.cs
    ├── App.axaml
    └── Program.cs
```

### Concatenate zero padding
The Concatenate tab can optionally insert a configurable number of zero bytes between source files. With **Skip unsafe boundaries** enabled, DumpToolbox probes up to 4096 bytes at the end of the previous file and the beginning of the next file. Padding is allowed when at least one of those exact edges contains a contiguous run of at least 256 zero bytes. If both edges contain data, padding is skipped and the reason is shown in the activity log. Disable the safety check if you explicitly want padding inserted between every file regardless of edge contents.

## ISO2BIN multi-file CUE support

The CD-ROM converter can use a CUE as the authoritative disc layout and merge all referenced source files into one 2352-byte-per-sector BIN.

Supported source combinations include:

- `data.iso` (`MODE1/2048` or `MODE2/2048`) + one or more WAV audio tracks
- `data.iso` + headerless `.raw` / `.bin` audio tracks
- multiple BINARY files
- a single BINARY file containing mixed cooked/raw tracks

For `FILE ... WAVE`, DumpToolbox parses the RIFF chunk structure rather than assuming a 44-byte header. It validates 44.1 kHz, 16-bit, stereo PCM, locates the actual `data` chunk, strips all WAV container/header bytes, and writes only the PCM payload. PCM bytes are copied unchanged (no implicit byte swap).

For BINARY/RAW audio, the input is treated as already-headerless 2352-byte CD audio frames and copied unchanged.

When several FILE entries are collapsed into one BIN, CUE INDEX times that were relative to each source file are rewritten as cumulative positions in the new single-file BIN. Cooked data tracks are changed from `/2048` to `/2352`; audio and already-raw data tracks keep their appropriate type. Other CUE metadata is preserved.

Each WAVE/raw audio payload must be an exact multiple of 2352 bytes. The converter fails explicitly instead of silently padding a partial audio sector.

## Resurrect hash cache

The Resurrect source scanner keeps a persistent `.dumptoolbox_hashcache.json` file in the selected source folder. Each cached SHA-1 is keyed by relative path and stored with the file size and UTC last-write timestamp. On later runs, unchanged files reuse their cached SHA-1 instead of rereading the file contents.

Use **Force rehash (ignore cache)** when you want to bypass cached hashes. The cache is an optimisation only: if it is missing, unreadable, or malformed, DumpToolbox hashes files normally and rebuilds it. Cache updates are written to a temporary file and atomically replaced when the scan finishes.

### UI responsiveness
Hashing and resurrection are performed off the Avalonia UI thread. High-frequency progress is deliberately coalesced/throttled so large directories and discs with many files do not overwhelm the UI dispatcher. Raw EDC/ECC regeneration also leaves one logical processor free for UI/OS work.

## Recovery tabs

`Resurrect` is reserved for the stable Redumper skeleton/hash workflow. DiscImageCreator recovery is intentionally separated into the `DIC` tab so experimental DIC filesystem reconstruction cannot change the Redumper UI/workflow.


## v0.7.1 DIC exactness audit

DIC recovery now reports which bytes are known exactly and which are assumptions. `_mainInfo.txt` drive-offset test captures are parsed as raw scrambled CD sectors, stitched across adjacent LBA reads and descrambled so early ISO system-area bytes can be recovered when DIC actually captured them.

The audit separately reports exact mainInfo metadata, deterministic ISO path-table synthesis, logical source-file payload, proven system-area bytes, and zero-assumed bytes such as file-sector slack or sectors after ISO9660 Volume Space Size. Assumed regions are **optional donor exactness regions**: they do not prevent a best-effort rebuild, but an exact same-disc ISO/BIN donor can replace those sectors before source-file payloads are applied. Mandatory donor requirements for genuinely unavailable ISO structures remain blocking.

## v0.7.0 cleanup

Version 0.7.0 is based on the v0.6.40 recovery code. The experimental compressed optical-image analyser/extractor and its external package dependency have been removed; Convert now contains ISO2BIN and MDF2BIN only. All v0.6.40 FindCRCs pregap rebalance, signed audio-edge recovery, source-tail diagnostics and dual-partial inspection behaviour is retained.

## FindCRCs Track 02 symmetric audio-edge correction (v0.6.23)

When Track 02 pregap scrambling is enabled on a mixed-mode CUE, DumpToolbox can use a proven shortfall of the final AUDIO track as a mirrored edge hint. If the final AUDIO track is `N` bytes short according to adjacent matched anchors, and ordinary pregap scrambling still does not match Track 02, DumpToolbox tests removing exactly `N` all-zero PCM bytes immediately after the corrected pregap data sectors. The value is not hard-coded and the adjusted Track 02 must still pass CRC32/MD5.

## FindCRCs signed audio-edge symmetry (v0.6.24)

The mixed-mode Track 02 pregap repair now mirrors the measured final-audio edge shift in either direction. A positive shift means the final audio track is short by `N` bytes; after correcting any recognised empty pregap data sectors, Track 02 tests deleting exactly `N` all-zero PCM bytes immediately after those sectors. A negative shift is only inferred when the final edge contains `N` verified zero bytes beyond the complete audio payload; Track 02 then tests inserting exactly `N` zero PCM bytes at the corresponding beginning-side boundary. Both cases are searched at 1-byte alignment and are accepted only when the requested CRC32/MD5 verifies. The value `N` may be any byte count.


### v0.6.40 Track 02 pregap rebalance + dual anchor partials

For mixed-mode Track 02 recovery, when both adjacent tracks are verified and the Track 02 region is short, DumpToolbox now checks whether `anchor shortfall + positive final-audio edge shift` is an exact multiple of 2352 bytes. If so it tests a pregap-boundary reconstruction: scrambled data pregap sector(s), removal of only verified zero PCM bytes immediately after them, and insertion of the inferred whole silent pregap sectors. The candidate must match the target CRC32/MD5.

When manual partial saving is enabled and both immediate anchors exist, FindCRCs saves both target-sized hypotheses: `*.forward.partial` begins at the end of the preceding matched track, while `*.backward.partial` ends at the start of the following matched audio track.

### v0.6.39 compile fix

Fixes C# `CS0136` local-variable shadowing errors introduced in the v0.6.38 combined Track 02 pregap + missing-end repair path. Recovery behaviour is unchanged.

### v0.6.38 Track 02 combined pregap/edge repair

When the FindCRCs CUE-aware pregap option corrects one or more empty data sectors in Track 02 and matched Track 01/Track 03 prove Track 02 is short at the end, edge recovery now works from the corrected bytes. DumpToolbox first tests the scrambled prefix plus a zero-filled missing suffix; if that does not verify, Find-Ends calculates the missing-end CRC32 from the scrambled partial before searching the source.

## v0.7.3 older DIC volDesc compatibility

Some older DiscImageCreator `_volDesc.txt` files do not contain `FullPath:` lines. DumpToolbox now reconstructs those paths from the primary ISO9660 path table and each directory record's `File Identifier`, including continuation sectors of multi-sector directories. This allows the DIC rebuilder to recover file extents from older logs instead of reporting that no recoverable extents were found.



## EOF slack rule database

DumpToolbox creates `EOFSlackRules.ini` beside the executable on first run. The external file contains mastering-specific post-EOF slack override rules used by both SkeleTool and DIC resurrection. Zero-filled EOF slack is the default when no rule matches. It is re-read on each reconstruction, allowing rules to be changed or added without recompiling DumpToolbox. See `EOF_SLACK_RULES.md`.

- NRG2BIN and CDI2BIN automatically keep CD media as BIN/CUE and emit native 2048-byte ISO for detected DVD media; no manual 2048/2352 selector is required.

## SkeleTool SHA-1 catalogue

SkeleTool's SHA-1 database is a collection catalogue, not reconstruction history. Register one or more folders under **Settings > SHA-1 Database**. DumpToolbox scans CD/DVD images in those folders, including `.iso`/`.bin` images and images stored in supported archives, and hashes every file visible in the disc filesystem. CUE sheets are honoured so only data tracks are filesystem-scanned.

The catalogue stores whole-source SHA-1 identities for direct images and archives as well as contained-file path, size, SHA-1 and scan date. This makes change scans incremental: unchanged sources are skipped, moved archives/images can be relinked by whole-source SHA-1 without rescanning their contents, new sources are added, and removed sources are retained historically as unavailable rather than deleted. A root must be enumerated successfully before anything beneath it is marked missing.

SkeleTool never populates this catalogue during a reconstruction. When a skeleton is loaded it may reuse hashes only from currently present catalogue sources. Explicit folders or ISO/BIN images selected in SkeleTool always take priority over catalogue matches; direct uncompressed catalogue images are preferred over archived sources. Archive-backed images are extracted on demand into the local catalogue cache only when needed.

ZIP/TorrentZip, solid 7z, Zstandard, RAR and other supported archive forms are handled in-process by the pure-managed SharpCompress library; no native 7-Zip DLL or separately installed 7-Zip executable is required.
