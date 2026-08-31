# IRD PS3 encryption — v0.8.61

The optional encryption stage consumes the already verified/rebuilt plain PS3 ISO and a 16-byte disc key.

Key input accepted:
- raw 16-byte `.key`
- text `.key`, `.dkey`, or `.txt` containing 32 hexadecimal characters
- 32 hexadecimal characters entered directly in the IRD tab (takes precedence over file input)

For each region described by PS3 sector 0 / the IRD region map:
- plain regions are copied unchanged
- encrypted regions are AES-128-CBC encrypted one 2048-byte sector at a time
- each sector resets the IV to 12 zero bytes followed by the big-endian 32-bit LBA

The encrypted output is then checked against every IRD region MD5. A mismatch removes the encrypted output.

Implementation reference behaviour: PS3Dec r5 and ps3netsrv region handling.
