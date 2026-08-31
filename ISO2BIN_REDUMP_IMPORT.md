# ISO2BIN Redump import

The ISO2BIN Redump target field accepts either an ordinary single target row, a Redump disc URL such as `https://redump.info/disc/118856`, or a bare disc ID such as `118856`.

For URL/ID input DumpToolbox downloads the public Redump Files table and CUE. If the disc has multiple payload tracks, the first non-AUDIO CUE track is matched by filename to the imported Redump target rows and used as the ISO2BIN verification target. The target box is replaced with that resolved row so the selected filename and hashes remain visible.
