# DIC v0.7.16 Joliet Type-L/Type-M regression — Black Mirror

Black Mirror's exact SVD is present in `BMIRROR_UK_mainInfo.txt` at LBA 17. Relevant cooked-user-data offsets:

```text
132-139 Path Table Size: 54, both-endian
140-143 Type-L path table:          1B 00 00 00 = LBA 27
144-147 Optional Type-L:            00 00 00 00
148-151 Type-M path table:          00 00 00 1C = LBA 28
152-155 Optional Type-M:            00 00 00 00
156...   Root directory record:      extent 24
```

v0.7.15 incorrectly cleared cooked offsets 144-155 while reconstructing Joliet metadata. That removed the Type-M location. With the otherwise-exact LBA-17 payload this changes the Mode-1 EDC from the original:

```text
DF 65 D7 F6
```

to:

```text
49 8E 6C 89
```

which exactly matches the user-observed v0.7.15 resurrected sector.

v0.7.16 keeps all four location fields unchanged and writes:

- Type-L (little-endian numeric fields) at LBA 27;
- Type-M (big-endian numeric fields) at LBA 28.

For Black Mirror the Joliet directories are root (extent 24), `Laserlok` (25), and `Manual` (26), producing a 54-byte path table in either byte order. The corrected SVD therefore returns to the original `DF 65 D7 F6` EDC.
