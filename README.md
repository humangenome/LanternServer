# LanternServer

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-blueviolet.svg)](https://dotnet.microsoft.com/en-us/download/dotnet/10.0)
[![Platform](https://img.shields.io/badge/Platform-Windows_x64-blue.svg)](#requirements)
[![Game](https://img.shields.io/badge/Game-Grounded_2-darkgreen.svg)](https://store.steampowered.com/app/2661300/)
[![Client App](https://img.shields.io/badge/Client_App-Lantern-brightgreen.svg)](https://github.com/HumanGenome/Lantern)

Grounded 2 has no dedicated server. [Lantern](https://github.com/HumanGenome/Lantern) adds one, and this repo is its server side.

Every player needs the [Lantern app](https://github.com/HumanGenome/Lantern) and the Steam version of Grounded 2 on Windows.

The **release zip is the complete server**. Building this source gives you `LanternServer.exe` alone, without the parts that run inside the game.

## Features

### 💾 World backups
Backs up the world whenever an admin asks, and before every restore or upload. A restore that fails never leaves a half-written world, and an uploaded Grounded 2 save restores the same way.

### 📡 Shows up in server lists
Server lists, monitoring tools and Discord bots can read the server name, map and player count.

### 🛠 Server console
RCON with `help`, `status`, `players`, `ping`, `save snapshot` and `save list`. The Lantern app's **Console** tab uses it.

### 🔐 Admin API
Health, info, players, the mod list, the password check the Lantern app makes before it starts the game, map state, and backup list, download, restore and upload. Admin routes are signed with a key made from the RCON password.

### 🧩 Mods
Loads UE4SS Lua and native mods from the server's `ue4ss\Mods` folder and lists them for the Lantern app.

## Requirements

- Players: the Steam version of Grounded 2 on a Windows PC and the [Lantern app](https://github.com/HumanGenome/Lantern)
- Building this source: the .NET 10 SDK

## Install

### Rent a server
[SurvivalServers.com](https://www.survivalservers.com/services/game_servers/grounded2/?utm_source=github&utm_medium=readme_install&utm_campaign=lanternserver) runs Grounded 2 servers with LanternServer installed.

### Host your own
Not supported yet. The release zip needs the setup a hosting panel does for each server: it puts the runtime into the game folder, writes the game's launch settings, and starts and watches the game. The bundled `host-instance.ps1` does none of that, and started on its own, Grounded 2 never opens a server port.

Players connect with the Lantern app to `<host>:<GameplayPort>` of a running server.

## Ports

The Lantern app finds the other ports from the gameplay port at these offsets, so keep the offsets when you change the gameplay port.

| Offset | Default | Purpose | Proto |
|--------|---------|---------|-------|
| +0 | 7777 | Gameplay, the join port | UDP |
| +1 | 7778 | Reserved, unused | none |
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

Bugs: open an [issue](https://github.com/HumanGenome/LanternServer/issues). Pull requests are welcome. Security problems go through [private reporting](https://github.com/HumanGenome/LanternServer/security/advisories/new).

## Community note

Lantern is an independent community project. It is not affiliated with, endorsed by, or supported by Obsidian Entertainment or Xbox Game Studios.

## License

See [LICENSE](LICENSE).
