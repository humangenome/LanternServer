# LanternServer

`LanternServer.exe`, the control service that runs next to one Grounded 2 server. It serves:

- RCON on gameplay port +3 (see `Lantern.Rcon`)
- Source A2S query on gameplay port +2 (see `Lantern.SourceQuery`)
- the HTTP API on gameplay port +4: public routes for health, players and the join password check, and
  signed admin routes for world snapshots, restore and import, used by the Lantern app's World window
- the world snapshots, and the pause around a restore that stops the game and holds the panel's restart

It does not start the game. The hosting panel installs the runtime, writes the game's settings and starts
Grounded 2; LanternServer reads the game's state from the host mod's heartbeat and roster files. That is why
self-hosting is not supported yet (see the repository README).

Settings live under the `Lantern` section of `appsettings.json`; the `Mods` settings nest under it.
