using Lantern.Protocol;
using Lantern.Rcon;
using LanternServer.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LanternServer.Services;

/// <summary>
/// Source RCON server. Runtime status and player data come from the game process
/// and g2_sshost roster heartbeat; the legacy native plugin pipe is optional.
/// </summary>
public sealed class RconHostedService : IHostedService
{
    private readonly ILogger<RconHostedService> _log;
    private readonly LanternServerOptions _opts;
    private readonly PipeServerState _state;
    private readonly SaveOrchestratorService _saves;
    private readonly ChatService _chat;
    private RconServer? _server;

    public RconHostedService(
        ILogger<RconHostedService> log,
        IOptions<LanternServerOptions> opts,
        PipeServerState state,
        SaveOrchestratorService saves,
        ChatService chat)
    {
        _log = log;
        _opts = opts.Value;
        _state = state;
        _saves = saves;
        _chat = chat;
    }

    public Task StartAsync(CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_opts.RconPassword))
        {
            _log.LogWarning("RCON disabled (no password set in Lantern:RconPassword)");
            return Task.CompletedTask;
        }
        _server = new RconServer(_opts.RconPort, _opts.RconPassword, ExecuteAsync, _log);
        _server.Start(ct);
        _log.LogInformation("RCON listening on TCP {Port}", _server.BoundPort);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken _)
    {
        if (_server is not null) await _server.StopAsync().ConfigureAwait(false);
    }

    private async Task<string> ExecuteAsync(string command)
    {
        var trimmed = command.Trim();
        if (string.IsNullOrEmpty(trimmed)) return "";

        var parts = trimmed.Split(' ', 2);
        var head = parts[0].ToLowerInvariant();
        var rest = parts.Length > 1 ? parts[1] : "";
        // Log the verb + arg length only — never the raw arg, which may carry
        // chat text or operator-entered content.
        _log.LogDebug("RCON dispatch: cmd={Cmd} argLen={ArgLen}", head, rest.Length);
        return head switch
        {
            "help"     => "commands: status, players, ping, save snapshot, save list, say <msg>, announce <msg>, motd [msg]",
            "status"   => BuildStatus(),
            "players"  => BuildPlayers(),
            "ping"     => "pong",
            "save"     => await HandleSaveAsync(rest).ConfigureAwait(false),
            "snapshot" => await HandleSaveAsync("snapshot").ConfigureAwait(false),
            "say"      => HandleChat(rest, "system"),
            "announce" => HandleChat(rest, "admin"),
            "motd"     => HandleMotd(rest),
            _          => $"unknown rcon command: {head} (try: help)",
        };
    }

    private string HandleChat(string msg, string channel)
    {
        var clean = (msg ?? "").Trim();
        if (string.IsNullOrEmpty(clean)) return $"usage: {(channel == "admin" ? "announce" : "say")} <message>";
        var entry = _chat.BroadcastFromServer(clean, channel: channel, sender: "Server");
        return $"chat ok ({channel}): {entry.Msg}";
    }

    private string HandleMotd(string sub)
    {
        var trimmed = (sub ?? "").Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            var cur = _chat.GetMotd();
            return string.IsNullOrEmpty(cur) ? "motd is empty" : $"motd: {cur}";
        }
        return _chat.SetMotd(trimmed) ? "motd updated" : "motd update failed (check log)";
    }

    private async Task<string> HandleSaveAsync(string sub)
    {
        var arg = sub.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(arg) || arg == "snapshot")
        {
            // WARNING: this snapshots whatever is already on disk. It does NOT ask the game to
            // save, and on Grounded 2 there may be nothing on disk to snapshot.
            //
            // This comment used to claim "the game host auto-saves to <userdir>/Saved/SaveGames
            // every ~60s". That was inherited from Beacon and is wrong twice over, and it is part
            // of why a data-loss defect hid for months behind a reassuring sentence:
            //   * the interval is not 60. MaineGameUserSettings carries AutosaveInterval=5.0 and
            //     AutosavesNumber=3.0, so the game is configured to autosave every 5 keeping 3;
            //   * and it does not happen at all. Across 27 Active instances, not one has ever
            //     produced an (AUTOSAVE-...) directory. The config is on and nothing calls it.
            // Grounded 2 commits its world on player LOGOUT ((LOGOUT-SAVE) is ESaveGameType::Logout
            // rendered into a directory name), so between logouts a snapshot here can capture an
            // empty directory and report success.
            //
            // The fix under way is a host-side caller for SaveLoadManager:RequestAutoSave; when that
            // lands this path becomes trustworthy. Until then, treat a snapshot as "whatever was on
            // disk", never as "the current world".
            // See goals/2026-08-15-graceful-stop-presave/design.md.
            var rec = await _saves.SnapshotAsync("rcon").ConfigureAwait(false);
            return rec is null
                ? "snapshot failed (check lantern log; save dir likely missing)"
                : $"snapshot ok: {rec.SnapshotId} ({rec.SizeBytes} bytes, sha={rec.Sha256Hex[..16]})";
        }
        if (arg == "list")
        {
            var snaps = _saves.Database.ListSnapshots(20);
            if (snaps.Count == 0) return "no snapshots yet";
            var sb = new System.Text.StringBuilder();
            foreach (var s in snaps)
                sb.AppendLine($"{s.SnapshotId}  {s.SizeBytes}B  age={(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - s.TakenUnix)}s  sha={s.Sha256Hex[..16]}");
            return sb.ToString().TrimEnd();
        }
        return "usage: save snapshot | save list";
    }

    private string BuildStatus()
    {
        var gameAlive = GameProcessProbe.IsAlive(_opts.GamePidFile);
        var heartbeatWindow = TimeSpan.FromSeconds(Math.Max(1, _opts.PluginHeartbeatTimeoutSeconds));
        var runtimeReady = _state.HasFreshRoster(heartbeatWindow);
        var online = gameAlive || runtimeReady || _state.HasFreshHeartbeat(heartbeatWindow);
        var runtime = !online
            ? "stopped"
            : runtimeReady
                ? "ready"
                : _state.LastRosterAt is null ? "starting" : "stale";
        return $"instance={_opts.InstanceId} game={(online ? "running" : "stopped")} runtime={runtime} players={_state.EffectivePlayerCount}";
    }

    private string BuildPlayers()
    {
        var n = _state.EffectivePlayerCount;
        return n == 0 ? "no players online" : $"{n} player(s) online (per-player names land in Phase 2)";
    }
}
