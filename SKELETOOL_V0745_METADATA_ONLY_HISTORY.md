# Skeletool v0.7.45 metadata-only SHA-1 history

The global Skeletool SHA-1 history database stores provenance only. It never stores copies of recovered file payloads.

Folder sightings retain the source path, length, timestamp and SHA-1. Image sightings retain the original ISO/BIN path, internal ISO path, LBA, length, timestamp and SHA-1. When an image sighting is reused, Skeletool reopens the original image and reads the file extent directly. If that image no longer exists or changed, the sighting remains useful for reporting history but cannot automatically satisfy a rebuild.
