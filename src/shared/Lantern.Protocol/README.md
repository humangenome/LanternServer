# Lantern.Protocol

Wire protocol shared by LanternServer, the launcher, and the native plugin:
the frame codec and MessagePack message records.

PORT FROM BEACON: copy `Beacon.Protocol` (FrameCodec + Protocol records) and
rename the namespace to `Lantern.Protocol`. The wire format ports verbatim;
adjust message payloads only where the Grounded 2 surface differs from
Subnautica 2. The generated protocol code is gitignored (see `.gitignore` ->
`src/shared/Lantern.Protocol/Generated/`).
