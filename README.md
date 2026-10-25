# LanternServer

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
[![.NET 8](https://img.shields.io/badge/.NET-8.0-blueviolet.svg)](https://dotnet.microsoft.com/en-us/download/dotnet/8.0)
[![Platform](https://img.shields.io/badge/Platform-Windows_x64-blue.svg)](#requirements)
[![Game](https://img.shields.io/badge/Game-Grounded_2-darkgreen.svg)](https://store.steampowered.com/app/2661300/)
[![Client App](https://img.shields.io/badge/Client_App-Lantern-brightgreen.svg)](https://github.com/HumanGenome/Lantern)

Grounded 2 has no dedicated server. LanternServer is the server side of [Lantern](https://github.com/HumanGenome/Lantern), which adds one: together with the Lantern runtime in the release zip it makes a Grounded 2 dedicated server that runs the game headless on a Windows machine with a public IP and port. LanternServer itself is the control service next to the game: world snapshots and restores, Source A2S query, Source RCON and the admin HTTP API the Lantern app uses.

Players join with the [Lantern app](https://github.com/HumanGenome/Lantern) and the Steam version of Grounded 2 on Windows. Stock Grounded 2 cannot connect to a Lantern server directly.

The **release zip is the complete server**: this .NET service plus the in-game runtime (a `ue4ss\` folder with the host mod and native runtime, and the WARP redist). Building this source produces the control service only.

## Features

### 💾 World snapshots
Takes a snapshot of the world every time the game autosaves (`SnapshotsEnabled`, on by default) and whenever an admin asks. A restore swaps the save folder in one step, so a failed restore never leaves a half-written world, and an uploaded Grounded 2 save can be restored the same way.

### 📡 Source query
Answers Source A2S query on the query port, so server lists, monitoring tools and bots can read the server name, map and player count.

### 🛠 Source RCON
A Source-compatible RCON listener with `help`, `status`, `players`, `ping`, `save snapshot` and `save list`. The Lantern app's Console tab uses it.

### 🔐 Admin HTTP API
Health, info, players, the mod manifest, the join-password check the Lantern app makes before it starts the game, map state, and the snapshot list, download, restore and import routes. Admin routes are HMAC-signed with a key derived from the RCON password.

### 🖥 Runtime watch
Reads the in-game runtime's heartbeat and player roster, reports `game=running|stopped runtime=ready|starting|stale` in RCON `status`, and coordinates save operations with the host's Grounded 2 launch wrapper.

### 🧩 Mods
The runtime loads UE4SS Lua and native mods from the server package's `ue4ss\Mods` folder, and the server lists them at `GET /api/v1/manifest`.

## Requirements

- Players: the Steam version of Grounded 2 on a Windows PC and the [Lantern app](https://github.com/HumanGenome/Lantern)
- Building this source: the .NET 8 SDK

## Installation

### Rent a server
[SurvivalServers.com Grounded 2 hosting](https://www.survivalservers.com/services/game_servers/grounded2/?utm_source=github&utm_medium=readme_install&utm_campaign=lanternserver) ships the complete Lantern server already installed and handles ports, updates and panel integration.

### Self-hosting
Not supported yet. The release zip is the server runtime that rented Lantern servers run, and their hosting panel does the setup around it for each server: it puts the runtime into the game folder, writes the game's launch settings, and starts and watches the game the way the runtime expects. The bundled `host-instance.ps1` does none of that. Started the way this page used to describe (2026-10-19 test), Grounded 2 never opened a server port and LanternServer reported the game as down, so nobody could join.

Players connect with the Lantern app to `<host>:<GameplayPort>` of a running server.

## Ports

The Lantern app finds the other ports from the gameplay port at these offsets, so keep the offsets when you change the gameplay port.

| Offset | Default | Purpose | Proto |
|--------|---------|---------|-------|
| +0 | 7777 | Gameplay, the join port | UDP |
| +1 | 7778 | Control / IPC identifier, local only | none |
| +2 | 7779 | Server query (Source A2S) | UDP |
| +3 | 7780 | RCON (Source RCON) | TCP |
| +4 | 7781 | Admin HTTP API | TCP |

## Server settings

LanternServer reads `appsettings.json` next to `LanternServer.exe`, under the `Lantern` section. At minimum set `InstanceId`, `GameplayPort` (and the other ports at their offsets), `RconPassword` and `ServerName`. `Mods` settings nest under the `Lantern` block, not at the top level.

## Layout

- `src/server/LanternServer`: control service, HTTP admin API, runtime watch
- `src/server/Lantern.Rcon`: Source RCON server
- `src/server/Lantern.SourceQuery`: Source A2S query responder
- `src/server/Lantern.Persistence`: save and snapshot storage
- `src/shared/Lantern.Abstractions`, `src/shared/Lantern.Protocol`: shared contracts

## Build

```bash
dotnet build src/server/LanternServer/LanternServer.csproj -c Release
```

## Contributing

Bug reports and pull requests are welcome in this repo's [issues](https://github.com/HumanGenome/LanternServer/issues). Security problems go through [private vulnerability reporting](https://github.com/HumanGenome/LanternServer/security/advisories/new), never a public issue.

## Community note

Lantern is an independent community project. It is not affiliated with, endorsed by, or supported by Obsidian Entertainment or Xbox Game Studios.

## License

See [LICENSE](LICENSE).
