# DIC v0.7.21 DVD geometry / DAT regression

Regression source: `POKEMON_NP_DVD` DIC log set.

The DVD log has no EccEdc file and `disc.txt` does not contain the CD-style whole-image `<rom ...img>` line used by older DIC imports. v0.7.20 therefore knew the volume had 593,408 sectors but refused to choose 2048-byte cooked geometry.

The bundle contains three independent pieces of geometry evidence:

- `volDesc.txt`: Volume Space Size = 593,408; Logical Block Size = 2048.
- `disc.txt`: BookType = DVD-ROM; SectorLength / LayerZeroSector = 593,408.
- `POKEMON_NP_DVD.dat`: `POKEMON_NP_DVD.iso`, size 1,215,299,584 bytes.

`1,215,299,584 / 2048 = 593,408` exactly.

v0.7.21 discovers the sibling `.dat`, accepts only a unique ISO/IMG ROM whose size agrees exactly with the resolved sector count, and promotes its hashes as the original whole-image target:

- CRC32 `5876636a`
- MD5 `bdb64aafa61dff7be49c8a1efbba11a4`
- SHA1 `2ed020beea16a112439d78250e87fe41ff93fa1c`

Even without a DAT, explicit DVD BookType plus a matching DIC LayerZeroSector count is sufficient to select cooked 2048-byte geometry. CD raw framing is never generated for that branch.
