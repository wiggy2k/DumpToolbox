# Other Tools

## Concatenate

Combines files in the displayed order with streamed I/O. No headers or separators are added. Optional boundary-aware zero padding can be inserted between files. Output is written through `<destination>.partial` and finalized only after successful completion.

## HashCalc

Calculates selected hashes in one streamed pass. CRC32, MD5 and SHA-1 are selected by default; SHA-256, SHA-384 and SHA-512 are also available.

## Base64

Encodes or decodes UTF-8 strings and arbitrary files. File mode streams data and writes through a partial file so a failed operation does not leave a misleading final output.

## Find-Ends

Rebuilds a file missing one contiguous prefix or suffix when the complete size, CRC32 and MD5 are known.

1. Select the partial file and choose Auto, Missing start or Missing end.
2. Enter the complete length, CRC32 and MD5.
3. Optionally select a source file containing the missing block.

CRC32 combination/inversion derives the required missing-block CRC without knowing its bytes. A rolling search locates candidates in the optional source, and the virtual reconstructed file must match the complete MD5 before output is accepted. The partial and source files are never overwritten.

## Sub2TXT

Opens either a DiscImageCreator/CloneCD 96-byte-per-sector de-interleaved `.sub` file or a Redumper multiplexed `.subcode`/`.subchannel` file and displays its P, Q, and R–W data in DiscImageCreator's `_subReadable.txt` layout. The input format is detected automatically. Redumper's full-file sector positions, beginning at LBA -45150, are retained. Q-channel control, address, track/index, relative time, absolute time, catalogue, and ISRC fields are decoded where present. Dedicated MCN and ISRC boxes summarize valid, non-blank ADR 2 and ADR 3 values after checking their Q-channel CRC and field format. Conversion runs in the background, and the viewer pages through 500 sectors at a time so large subchannel files remain responsive. **Jump to LBA 0** opens and scrolls directly to the LBA 0 entry. **Save as...** writes the complete output, not just the visible page; the source file is never modified.

## XML DAT input

FindCRCs and Audio accept complete DAT files, `<game>`/`<machine>` fragments, or bare `<rom>` elements. Each usable ROM needs `size` and `crc`; optional name, MD5 and SHA-1 values are retained.
