using Lantern.Protocol;
using Lantern.Rcon;
using LanternServer.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LanternServer.Services;

/// <summary>
/// Source RCON server. Translates RCON commands into <see cref="FrameType.RconCommand"/>
/// frames sent to the plugin and awaits the matching response, with a fallback
/// for purely server-side commands (status, players, save).
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
            // The game host auto-saves to <userdir>/Saved/SaveGames every ~60s.
            // We snapshot the on-disk save file directly; the plugin SaveQuiesce
            // ack is not required because the game's own save writer is atomic
            // (writes to temp then renames). The FileSystemWatcher in
            // SaveOrchestratorService handles auto-snapshots; this RCON path is
            // for admin-triggered.
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
        var conn = _state.Connection;
        return conn is null
            ? $"instance={_opts.InstanceId} plugin=disconnected"
            : $"instance={_opts.InstanceId} plugin=connected pid={conn.PluginPid} version={conn.PluginVersion} players={_state.EffectivePlayerCount}";
    }

    private string BuildPlayers()
    {
        var n = _state.EffectivePlayerCount;
        return n == 0 ? "no players online" : $"{n} player(s) online (per-player names land in Phase 2)";
    }
}
