# Audio-tab Redump import — v0.7.86

The Audio tab accepts either a Redump disc URL such as `https://redump.info/disc/118856` or the bare numeric disc ID `118856` in its hash target box.

It uses the same shared `RedumpDiscImportService` as FindCRCs. The Redump Files table supplies destination filenames, size, CRC32, MD5 and SHA-1. When the Redump CUE endpoint is available, the existing CUE parser classifies tracks and only rows corresponding to CUE `AUDIO` tracks are placed into the Audio hash box.

If CUE retrieval is unavailable, the hash rows still import, but the Audio log warns that it could not automatically distinguish data from audio payloads.
