# Audio Recovery Hail-Mary edge recovery (v0.8.66)

This is a last-resort extension of the existing Audio Recovery edge-fix workflow.

It runs only for an unmatched first/final target that already has the exact expected physical extent, as proven by the adjacent matched anchor plus source start/EOF, and whose physical outside edge contains zero-valued 16-bit PCM silence.

For the final track, trailing silence is removed back to the nearest non-zero PCM sample. The remaining anchored prefix is kept fixed and Find Ends searches the complete combined CDDA block for the missing suffix. If that fails, the missing source segment is shortened one byte at a time while 0x00 bytes are forced onto the physical end, preserving the exact target size on every attempt.

The first-track case is mirrored: leading silence is removed, the anchored suffix remains fixed, and progressively more 0x00 bytes are forced at the physical start.

No reconstruction is accepted without the expected CRC32 and MD5.
