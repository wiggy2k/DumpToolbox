# FindCRCs Redump disc-page import (v0.7.85)

The FindCRCs **Hash targets** box accepts either a normal pasted target table, a full `https://redump.info/disc/<id>` URL, or the numeric disc ID alone.

For a Redump disc reference, DumpToolbox retrieves the public disc page and imports the BIN rows from its Files table, retaining each Redump filename as the FindCRCs extraction destination. CRC32 and MD5 are required; SHA-1 is retained when present. Duplicate `.img` aliases are not imported when `.bin` payload rows are available.

DumpToolbox also retrieves `https://redump.info/disc/<id>/cue`. When available, that CUE is written under the user's temporary DumpToolbox/Redump folder and analysed with the normal FindCRCs CUE code, so mixed-mode/audio edge and pregap rules receive the same track/index information as a manually selected CUE.

The Redump import happens only when the complete Hash targets box contains a Redump URL or a single positive integer. Ordinary hand-entered target rows are unchanged.
