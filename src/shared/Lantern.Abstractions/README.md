# Lantern.Abstractions

Shared service interfaces (`ILanternService` and friends) implemented by
LanternServer and consumed by the tools/launcher.

PORT FROM BEACON: copy `Beacon.Abstractions` (the `IBeaconService` interfaces)
and rename to `Lantern.Abstractions`. These are interface-only and port
verbatim.
