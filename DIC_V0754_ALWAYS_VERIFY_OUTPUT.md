# DIC v0.7.54 — always verify rebuilt image

Earlier versions only calculated the DIC target whole-image hashes when `SkeletonResurrectionResult.MissingEntries == 0`. This could suppress verification on cumulative or partial recovery passes even though a rebuilt BIN had been produced.

v0.7.54 always calculates every available original DIC whole-image hash (CRC32, MD5, SHA-1) after a successful rebuild. If the pass still reports missing payloads, the log states that fact before hashing; the hash comparison still runs because it is the authoritative exactness test.
