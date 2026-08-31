# DIC v0.7.15 Joliet-source regression — Black Mirror

Black Mirror (`BMIRROR_UK`) demonstrates why normal source files should be supplied using the Joliet/user-visible namespace. DIC logs the primary ISO9660 records, including:

- `BLACKMIR.ICO;1`
- `SETUP_1.BIN;1`
- `LASERLOK`

The mounted/Joliet tree exposes:

- `BlackMirror.ico`
- `setup-1.bin`
- `Laserlok`

v0.7.15 maps the latter back to the former only when the complete relative path and exact byte length produce a unique primary-record match.

The DIC Supplementary Volume Descriptor says Joliet root extent = LBA 24 and path table = LBA 27, but the logs do not preserve the actual LBA 24 directory sector. Rebuilding the Joliet root from the supplied names while retaining DIC primary extents, sizes, timestamps and flags produces the supplied original LBA 24 directory bytes. In particular the generated Mode-1 EDC is:

```text
4A FF 1A DD
```

matching the original sector. The hidden primary `LASERLOK` directory flag is retained in the generated Joliet child record, and directory/file records are ordered by case-sensitive Joliet identifier spelling rather than grouped by type.
