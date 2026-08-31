# Audio Hail Mary bidirectional zero placement — v0.8.70

The opt-in Audio Recovery Hail Mary fallback now tries forced zero padding on both boundaries of the missing source segment.

For a missing final edge:

    [anchored audio][source]
    [anchored audio][shorter source][00]
    [anchored audio][00][shorter source]
    [anchored audio][shorter source][00 00]
    [anchored audio][00 00][shorter source]
    ...

For a missing first edge the order is mirrored around the anchored audio.

For each N > 0, all N zero bytes are placed either at the physical outer edge or at the inner edge next to the anchor. Mixed splits such as one zero at each side are not included. CRC32 is solved for each layout and every candidate still requires full MD5 verification.
