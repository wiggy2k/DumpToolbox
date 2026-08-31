# PS3 IRD rebuilder

The IRD tab rebuilds a plain/decrypted PlayStation 3 ISO from an extracted JB folder and a matching IRD, then optionally produces an encrypted ISO.

## Supported inputs

- Raw `3IRD` files and whole-file gzip-compressed IRDs.
- IRD format versions 6–9.
- An extracted source directory matching the paths embedded in the IRD header.
- Optional disc key as a raw 16-byte `.key`, a text `.key`/`.dkey`/`.txt` containing 32 hexadecimal characters, or 32 hex characters entered directly. Typed keys are never saved.

## Verification and rebuild

The IRD ISO9660 header supplies paths, extents and logical sizes. Every required source must match its IRD path, size and per-file MD5 before rebuilding. ISO9660 multi-extent files are treated as one logical file and streamed across all recorded extents.

The plain image is assembled from the IRD header, verified source files at their recorded sectors, and IRD footer. A previously successful verification is reused when the IRD/source pair is unchanged. Output is written to a uniquely named partial file and replaces the requested destination only after rebuild validation succeeds; an existing destination is preserved if the operation fails.

## Optional encryption

Encrypted regions use AES-128-CBC one 2048-byte sector at a time. Each sector resets the IV to 12 zero bytes followed by the big-endian 32-bit LBA. Plain regions are copied unchanged.

The encrypted result is checked against every IRD region MD5 using the IRD region boundaries. A failed encrypted-region check removes only the incomplete partial output.

Direct archive-as-source rebuilding is not supported; use an extracted directory.
