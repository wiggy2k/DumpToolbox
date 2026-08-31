# Find-ends

`Other -> Find-ends` rebuilds a file that is missing bytes only from its beginning or end.

Inputs:

- partial file
- expected complete length in bytes
- expected complete CRC32
- expected complete MD5
- missing side: Auto, Missing start, or Missing end
- optional source file to search
- optional recovered output path

The missing length is `complete length - partial length`. DumpToolbox uses CRC32 combination/inversion math to derive the CRC32 of the missing block without knowing its bytes. In Auto mode it derives both possible CRCs.

When a source file is supplied, every byte offset is searched with a rolling CRC32 window of exactly the missing length. CRC32 candidates are tested by calculating the MD5 of the virtual reconstructed file in the correct order. Only a candidate matching the supplied complete MD5 is written.

Recovered output is written to `<output>.partial` first and renamed only after the written file is re-read and its MD5 is confirmed. The partial input and source search file are never overwritten.
