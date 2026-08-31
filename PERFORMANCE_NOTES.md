# Performance pass: exhaustive byte scan

This version adds a dedicated `alignment == 1` scanner. The previous generic loop was doing work that is extremely expensive when repeated once per byte:

- `Stream.Read` for a single incoming byte
- `Crc32.Compute` twice per candidate position
- GF(2) matrix bit-walking (`ShiftOperator.Apply`) per byte
- LINQ `Where(...).ToArray()` per byte
- generic ring-copy and modulo helpers per byte

The new byte scanner:

- reads 4 MiB blocks sequentially
- performs direct CRC->target dictionary lookup with no per-byte allocations
- precomputes the 256 outgoing-byte CRC contributions
- precomputes a four-table implementation of the one-byte CRC shift transform
- updates the byte ring directly
- throttles ordinary UI progress reporting to 256 MiB

Matching semantics are unchanged: CRC32 selects candidates, optional MD5 verifies them, and matching stops when all requested targets are found.
