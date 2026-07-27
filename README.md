# LanternServer

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
[![.NET 8](https://img.shields.io/badge/.NET-8.0-blueviolet.svg)](https://dotnet.microsoft.com/en-us/download/dotnet/8.0)
[![Platform](https://img.shields.io/badge/Platform-Windows_x64-blue.svg)](#requirements)
[![Game](https://img.shields.io/badge/Game-Grounded_2-darkgreen.svg)](https://store.steampowered.com/app/2661300/)

LanternServer is the open-source host supervisor for [Lantern](https://github.com/HumanGenome/Lantern) multiplayer in **Grounded 2**. It starts and watches the hosted game process, takes and restores save snapshots, exposes an admin HTTP API, answers Source A2S query, and runs Source RCON.

Players join with the [Lantern desktop app](https://github.com/HumanGenome/Lantern). A playable host also needs Lantern's in-game runtime — a `ue4ss\` folder with the host mod and native runtime, plus the WARP redist — next to the `LanternServer\` folder. The **release zip bundles that runtime**, so a downloaded server is complete. Building from this source yourself produces the supervisor only.

## Features

### 🖥 Host supervision
Starts Grounded 2 with the Lantern runtime, watches the game process, tracks the runtime heartbeat, and coordinates restarts.

### 💾 Save snapshots
Snapshots the world on every game auto-save (when `SnapshotsEnabled` is on) and on admin trigger. Restore swaps the save directory atomically, so a failed restore does not leave a half-written world.

### 📡 Source query
Answers standard Source A2S query on the query port so server lists, monitoring tools, and bots can read server name, map, and player count.

### 🛠 Source RCON
Runs a Source-compatible RCON listener with `help`, `status`, `players`, `ping`, `save snapshot`, `save list`, `say`, `announce`, and `motd`.

### 🔐 Admin HTTP API
Exposes health, info, players, mod manifest, chat, map state, and the snapshot list/download/restore/import routes. Admin routes are HMAC-signed with a key derived from the RCON password.

### 🧩 Mod surface
Loads UE4SS Lua and native mods through the Lantern runtime layout, and publishes the server's mod manifest at `GET /api/v1/manifest` for the launcher to install on join.

## Requirements

- Windows 10/11 or Windows Server, x64
- Grounded 2 game files installed on the host machine (LanternServer launches them; it does not ship the game)
- Open/forwarded ports for gameplay, query, RCON, and admin HTTP

The server runs headless on the WARP software renderer, so no GPU is required. Release builds are self-contained; a separate .NET install is not needed for normal use.

## Installation

### Managed hosting
[SurvivalServers.com Grounded 2 hosting](https://www.survivalservers.com/services/game_servers/grounded_2/?utm_source=github&utm_medium=readme_install&utm_campaign=lanternserver) ships the complete Lantern server runtime already installed and handles ports, updates, and panel integration.

### Self-host
1. Download `LanternServer-v<version>.zip` from the [latest release](https://github.com/HumanGenome/LanternServer/releases/latest).
2. Extract it to a stable folder on the Windows host. It contains `LanternServer\` (the supervisor and `appsettings.json`), `ue4ss\` (the in-game runtime), `redist\` (the WARP renderer and the UE4SS proxy), `engine-ini\` (host and client templates), `host-instance.ps1`, and `steam_appid.txt`.
3. Install Grounded 2 on the host and keep Steam logged in.
4. Edit `LanternServer\appsettings.json`.
5. Open/forward the ports below. The gameplay UDP port also needs a Windows Defender inbound allow rule, or players cannot reach the listen socket.
6. Run `LanternServer\LanternServer.exe`.

Players connect with the Lantern app to `<host>:<GameplayPort>`.

## Ports

Every port is derived from the gameplay port, so a host only configures one.

| Offset | Default | Purpose | Proto |
|--------|---------|---------|-------|
| +0 | 7777 | Gameplay, the join port | UDP |
| +1 | 7778 | Control / IPC identifier, local only | — |
| +2 | 7779 | Server query (Source A2S) | UDP |
| +3 | 7780 | RCON (Source RCON) | TCP |
| +4 | 7781 | Admin HTTP API | TCP |

## Server settings

LanternServer reads `appsettings.json` next to `LanternServer.exe`, under the `Lantern` section. At minimum set `InstanceId`, `GameplayPort`, `RconPassword`, and `ServerName`. `Mods` settings nest under the `Lantern` block, not at the top level.

## Layout

- `src/server/LanternServer` — host supervisor, HTTP admin API, watchdog
- `src/server/Lantern.Rcon` — Source RCON server
- `src/server/Lantern.SourceQuery` — Source A2S query responder
- `src/server/Lantern.Persistence` — save/snapshot storage
- `src/shared/Lantern.Abstractions`, `src/shared/Lantern.Protocol` — shared contracts

## Build

```bash
dotnet build src/server/LanternServer/LanternServer.csproj -c Release
```

## License

See [LICENSE](LICENSE).
