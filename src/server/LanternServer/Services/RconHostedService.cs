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
    private RconServer? _server;

    public RconHostedService(
        ILogger<RconHostedService> log,
        IOptions<LanternServerOptions> opts,
        PipeServerState state,
        SaveOrchestratorService saves)
    {
        _log = log;
        _opts = opts.Value;
        _state = state;
        _saves = saves;
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

    internal async Task<string> ExecuteAsync(string command)
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
            "help"     => HelpText,
            "status"   => BuildStatus(),
            "players"  => BuildPlayers(),
            "ping"     => "pong",
            "save"     => await HandleSaveAsync(rest).ConfigureAwait(false),
            "snapshot" => await HandleSaveAsync("snapshot").ConfigureAwait(false),
            // No Lantern piece shows chat or a message of the day to players (the chat plane came over
            // from Beacon, where the client overlay reads it; nothing in Grounded 2 does). Say so
            // instead of answering "chat ok" for a message nobody will ever see.
            "say"      => NoChatReply,
            "announce" => NoChatReply,
            "motd"     => NoMotdReply,
            _          => $"unknown rcon command: {head} (try: help)",
        };
    }

    internal const string HelpText = "commands: status, players, ping, save snapshot, save list";
    internal const string NoChatReply = "Grounded 2 servers have no chat yet: players would not see this, so nothing was sent.";
    internal const string NoMotdReply = "Grounded 2 servers have no message of the day yet: players never see one.";

    private async Task<string> HandleSaveAsync(string sub)
    {
        var arg = sub.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(arg) || arg == "snapshot")
        {
            // Archives the files already on disk; this does not request a fresh
            // game save. A successful snapshot is not proof of current progress.
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

    private string BuildPlayers() =>
        FormatPlayers(_state.Players, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    // One line per player: name and minutes since they joined (the same list A2S reports).
    // Control characters are dropped so a name cannot break the console's lines.
    internal static string FormatPlayers(IReadOnlyList<PlayerSnapshot> players, long nowUnixMs)
    {
        if (players.Count == 0) return "no players online";
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(players.Count == 1 ? "1 player online" : $"{players.Count} players online");
        foreach (var p in players)
        {
            var name = new string((p.DisplayName ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
            if (name.Length == 0) name = "(no name yet)";
            var minutes = p.ConnectedAtUnixMs > 0 && nowUnixMs > p.ConnectedAtUnixMs
                ? (nowUnixMs - p.ConnectedAtUnixMs) / 60000
                : 0;
            sb.AppendLine($"  {name}  {minutes} min");
        }
        return sb.ToString().TrimEnd();
    }
}
