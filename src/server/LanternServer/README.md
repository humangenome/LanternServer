# LanternServer

The per-instance sidecar — `LanternServer.exe`. Runs Source RCON, Source A2S
query, and the loopback HTTP admin API for one Grounded 2 host. It does NOT own
the game-process lifecycle and there is NO display head / menu-drive: the host
control plane's PowerShell launches the per-instance exe directly
(CREATE_SUSPENDED), pins CPU affinity before resume, and stops it; `GameInstallRoot`
stays empty (same as SN2/Beacon). The game hosts headless via WARP + the Engine.ini
`LocalMapOptions=?listen` config — no GPU, no virtual display.

Same lifecycle model as Subnautica 2: the host control plane's PowerShell
owns the game-process lifecycle and
LanternServer's internal supervisor stays idle on an EMPTY `GameInstallRoot`. The
control plane launches the per-instance exe directly (CREATE_SUSPENDED with the WARP no-GPU
args), pins CPU affinity before resume, reaps `CrashReportClient`/`WerFault`, and
restarts. LanternServer only serves RCON / Source A2S query / the loopback HTTP
admin API for that instance — it does NOT launch, pin, or relaunch the game.

PORT FROM BEACON: copy `BeaconServer` (Program + Configuration + Services +
Static), rename the namespace and the `Beacon` config section to `Lantern`, and
swap the SN2 launch/lifecycle for the Grounded 2 recipe in `docs/RUNTIME.md` and
`scripts/host-instance.ps1`.

The `Mods` settings nest UNDER the `Lantern` config section, not at the top
level (same binding quirk Beacon has).
