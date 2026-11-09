# Lantern.Rcon

Source RCON listener for LanternServer, on TCP gameplay port +3 (7780 with the default 7777). It handles the
Source RCON framing and the login; LanternServer's `RconHostedService` answers the commands: `help`, `status`,
`players`, `ping`, `save snapshot` and `save list`. With no `RconPassword` set, LanternServer does not open
the port.

The framing is the same as Beacon's `Beacon.Rcon`.
