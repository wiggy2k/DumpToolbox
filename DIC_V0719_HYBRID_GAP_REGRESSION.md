# DIC v0.7.19 hybrid/unclaimed in-volume coverage regression — Ben 10

The Ben 10: Kayıp Gezegen disc is an ISO9660/Joliet + Apple HFS hybrid. DIC drive-offset evidence preserves an Apple Partition Map in LBA 0. The Apple_HFS partition is named `DiscRecording 5.0.5d3`, begins at 512-byte block 1116 (CD LBA 279), and spans 415524 512-byte blocks.

v0.7.18 accounted for the ISO system area, ISO metadata, ordinary ISO file payloads, file tail slack, and post-volume sectors, but did not account for ordinary in-volume sectors between those structures. On this disc that omitted the hybrid HFS metadata sectors around the HFS partition boundaries. Those bytes were silently zero-filled and did not appear in the 335,850-byte exactness warning.

v0.7.19 computes the complement of all claimed sectors inside the ISO Volume Space Size. Every such unclaimed region is now reported as ASSUMED ZERO and receives an optional exactness donor requirement. Apple Partition Map evidence is also recognized so the log explains why an Apple_HFS hybrid can legitimately place non-ISO metadata in these regions.

This change deliberately does not fabricate HFS MDB/catalog/bitmap bytes from ISO filenames. Classic HFS metadata can contain mastering-time/generated values that DIC does not preserve, so exact same-disc donor evidence remains the authority for those bytes.
