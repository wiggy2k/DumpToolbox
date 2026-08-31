# DIC v0.7.32 scattered Joliet directory placement

Some ISO/Joliet mastering software does not reserve one contiguous area for all supplementary directory extents. Instead, each Joliet directory can be written immediately after the corresponding primary ISO9660 directory allocation.

The Abe's Oddysee regression demonstrates this layout. Its exact SVD preserves Type-L path table LBA 23, Type-M path table LBA 21, and Joliet root extent LBA 28. The primary root is LBA 24 with 8192 bytes (4 sectors), so the independently logged SVD proves `24 + 4 = 28` for the root.

The same relation holds for every primary/Joliet directory pair recovered from the validated source tree:

- primary root 24 + 4 sectors -> Joliet root 28
- primary DIRECTX 33 + 2 sectors -> Joliet directx 35
- primary DRIVERS 2711 + 1 sector -> Joliet drivers 2712
- primary USA 2713 + 5 sectors -> Joliet usa 2718
- primary DESKTO~1 8936 + 2 sectors -> Joliet Desktop Theme 8938
- primary DOS 326676 + 1 sector -> Joliet DOS 326677

v0.7.31 incorrectly allocated every generated Joliet directory contiguously from the SVD root extent. That required 19 sectors at LBA 28-46 and collided with existing disc content even though the original supplementary directories were distributed throughout the image.

v0.7.32 adds a generic, guarded paired-directory allocator. It is enabled only when the SVD root extent independently confirms that the Joliet root starts immediately after the primary root allocation. Every child must have proven primary extent/length metadata, and each proposed supplementary range must fit within the volume without overlapping a file extent, a declared path-table location, or another generated directory. If any guard fails, DumpToolbox retains the previous conservative contiguous allocator and refuses unsafe synthesis when that also does not fit.

No title, filename, hash, LBA list, or disc-specific branch is used by the implementation.
