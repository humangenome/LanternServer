# LanternServer

The server side of [Lantern](https://github.com/HumanGenome/Lantern) — the host supervisor for **Grounded 2** dedicated, always-on multiplayer worlds. LanternServer launches and watches the Grounded 2 host process, serves Source RCON and A2S query, exposes an HTTP admin API, and manages save snapshots and world tools. Players join with the [Lantern desktop app](https://github.com/HumanGenome/Lantern); this repo is the server source.

## Layout

- `src/server/LanternServer` — host supervisor, HTTP admin API, watchdog
- `src/server/Lantern.Rcon` — Source RCON server
- `src/server/Lantern.SourceQuery` — Source A2S query responder
- `src/server/Lantern.Persistence` — save/snapshot storage
- `src/shared/Lantern.Abstractions`, `src/shared/Lantern.Protocol` — shared contracts

## Ports

Derived from the gameplay port (base): gameplay UDP (base), control (+1), query UDP (+2), RCON TCP (+3), admin HTTP TCP (+4).

## Build

```bash
dotnet build src/server/LanternServer/LanternServer.csproj -c Release
```

The server runs headless — no GPU is required. Configure it via `src/server/LanternServer/appsettings.json`.

## License

See [LICENSE](LICENSE).
