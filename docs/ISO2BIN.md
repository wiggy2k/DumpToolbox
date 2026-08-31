# ISO2BIN

ISO2BIN expands cooked 2048-byte CD data into raw 2352-byte sectors. It supports a single ISO or a CUE-described mixed-mode layout.

## ISO-only mode

The complete input is treated as one cooked data track.

- **Auto** looks for ISO9660 descriptors and the `CD-XA001` marker. XA-marked images use Mode 2 Form 1; other or ambiguous images use Mode 1.
- Manual Mode 1 and Mode 2 Form 1 choices override Auto.
- Raw sectors receive sync, BCD MSF addressing, EDC, and Reed-Solomon P/Q ECC.
- An optional Redump target can set the filename, authoritative sector count, and expected hashes. Short inputs receive virtual zero-filled cooked sectors; extra cooked sectors are ignored without changing the source.

## Mixed-mode CUE mode

The CUE is authoritative for per-track source geometry. Supported declarations are:

| Source track | Output behaviour |
| --- | --- |
| `MODE1/2048` | Rebuilt as `MODE1/2352` |
| `MODE2/2048` | Rebuilt as Mode 2 Form 1 |
| `AUDIO` | Copied byte-for-byte |
| `MODE1/2352` | Copied byte-for-byte |
| `MODE2/2352` | Copied byte-for-byte |

Single- and multi-file CUEs are supported. The replacement CUE retains indices, pregaps, postgaps and other metadata while updating filenames and cooked track declarations.

## XA metadata

A cooked Mode 2 image normally loses its original XA File Number, Channel Number, Submode and Coding Information. These bytes can be recovered from a DIC `*_EccEdc.txt` log or raw 2352-byte Redumper skeleton. Missing entries use the generic Form 1 subheader `00 00 08 00`. A metadata entry identifying Form 2 is rejected because a 2048-byte source lacks the required 2324-byte payload.

## Validation and safety

- Input and CUE geometry must account for the source bytes exactly.
- Output target hashes are checked when supplied.
- Conversion streams in batches and writes through a `.partial` file.
- A completed BIN can be sent directly to FindCRCs.

Reference SHA-256 vectors for an all-zero cooked sector at LBA 0 are:

- Mode 1: `b4f18ab66709c9b3fdef2721cc323e031b6728f3ca6c57b7c435c96189222250`
- Mode 2 Form 1: `019d8734ffd82181a112f3b3c485312e8cfc1e37c1fc85374606669356ee35b8`
