# Skeletool v0.7.43 source-image and SHA-1 history

Skeletool source matching can now use either ordinary files from folders or ISO9660 files read directly from a 2048-byte ISO / 2352-byte BIN. A persistent local SHA-1 history database records one-to-many sightings and successful complete-image provenance. History never replaces SHA-1 verification: it only reuses a still-valid source file/cached payload or reports prior provenance. XA/Form2 alternate payloads are not claimed from the generic direct-image file reader.
