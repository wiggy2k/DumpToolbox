# NRG2BIN

## v0.8.32 — UI cleanup

- Removed the long capability paragraph from the NRG2BIN converter panel.
- Capability/support details now live in `README.md` and this document instead of consuming converter workspace width.

## v0.8.31 — optional SUB output

- Added **Also save 96-byte subchannel as .sub**, default off, matching MDF2BIN.
- 2448-byte source sectors always contribute their first 2352 main-channel bytes to the BIN.
- The final 96 subchannel bytes are written only when SUB output is enabled.
- The preference is saved as `NRG2BIN.SaveSubchannel` and reset/clear returns it to off.


## v0.8.30 — raw 2448 and multisession support

NRG2BIN parses Nero NRG v1 (`NERO`) and v2 (`NER5`) footer/chunk metadata and converts supported CD track payloads to a 2352-byte BIN plus CUE.

### 2448-byte sectors

DAO/TAO tracks that store 2448 bytes per sector are supported. Nero subchannel modes 0x0F (raw data + subchannel), 0x10 (audio + subchannel), and 0x11 (raw Mode 2/Form 1 + subchannel) are recognised.

For each stored 2448-byte sector:
- bytes 0..2351 are written to the BIN unchanged;
- bytes 2352..2447 are written to a companion `.sub` file unchanged.

The `.sub` therefore has exactly 96 bytes per BIN sector. If an NRG mixes 2448-byte and non-subchannel tracks, sectors whose source track does not contain stored subchannel data receive a zero 96-byte placeholder so BIN/SUB sector numbering stays aligned. The analyser/log warns when this occurs.

### Multisession

Repeated `SINF` session records and corresponding DAO (`DAOI`/`DAOX`) or TAO (`ETNF`/`ETN2`) geometry chunks are parsed in sequence. All stored track payloads from all sessions are emitted to the BIN in session order.

A conventional CUE cannot model physical multisession lead-in/lead-out areas. NRG2BIN therefore does not invent those missing sectors. Instead it records session boundaries and each track's original disc LBA as `REM SESSION` and `REM ORIGINAL_LBA` lines in the generated CUE.

The SINF declared track count is checked against the paired DAO/TAO geometry chunk. Ambiguous session/geometry mappings are rejected rather than guessed.

### Other behaviour

- Raw 2352 Mode 1, Mode 2 and audio are copied unchanged.
- Cooked 2048 Mode 1 and Mode 2 Form 1 tracks are expanded with DumpToolbox's existing raw-sector EDC/ECC builder.
- CD-TEXT is detected but not yet emitted.

## CD vs DVD output

NRG2BIN inspects Nero MTYP media metadata. CD images are converted to 2352-byte BIN + CUE (with optional SUB); DVD images are written directly as 2048-byte ISO images. Known Nero DVD MTYP values are recognized, with a conservative capacity-based fallback for single-track 2048-byte images when MTYP is missing or unknown. DVD output never synthesizes CD sync/header/EDC/ECC framing.
