using Lantern.SourceQuery;
using LanternServer.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LanternServer.Services;

/// <summary>
/// Runs the Source A2S query responder. Pulls live snapshot data from the plugin
/// (via <see cref="PipeServerState"/>) on every query, so external tools like
/// GameTracker and ServerMonkey see real player counts.
/// </summary>
public sealed class SourceQueryHostedService : IHostedService, IAsyncDisposable
{
    private readonly ILogger<SourceQueryHostedService> _log;
    private readonly LanternServerOptions _opts;
    private readonly PipeServerState _state;
    private readonly TimeSpan _heartbeatTimeout;
    private SourceQueryServer? _server;

    public SourceQueryHostedService(
        ILogger<SourceQueryHostedService> log,
        IOptions<LanternServerOptions> opts,
        PipeServerState state)
    {
        _log = log;
        _opts = opts.Value;
        _state = state;
        _heartbeatTimeout = TimeSpan.FromSeconds(Math.Max(1, _opts.PluginHeartbeatTimeoutSeconds));
    }

    public Task StartAsync(CancellationToken ct)
    {
        _server = new SourceQueryServer(
            _opts.QueryPort,
            BuildInfo,
            BuildPlayers,
            BuildRules,
            IsGameOnline,
            queryObserver: (type, remote) => _log.LogDebug("A2S query {Type} from {Remote}", type, remote));
        _log.LogInformation("Source A2S query listening on UDP {Port}", _server.BoundPort);
        return _server.StartAsync(ct);
    }

    public async Task StopAsync(CancellationToken _)
    {
        if (_server is not null) await _server.StopAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_server is not null) await _server.StopAsync().ConfigureAwait(false);
    }

    private ServerInfoSnapshot BuildInfo() => new(
        Name: string.IsNullOrWhiteSpace(_opts.ServerName) ? $"Lantern — {_opts.InstanceId}" : _opts.ServerName.Trim(),
        Map: "Augusta",
        Folder: "grounded2",
        Game: "Grounded 2",
        SteamAppId: 2661300,                          // real Steam AppID — written as 64-bit GameID in EDF
        PlayerCount: _state.EffectivePlayerCount,
        MaxPlayers: _opts.MaxPlayers,
        // Reflect the Lua-side LanternAuth gate's configured password.
        // LanternAuthPassword is the field the panel populates and that
        // LanternAuth.lua actually enforces. The legacy ServerPassword
        // field is intentionally ignored here for consistency with
        // LanternAuth.lua, which dropped the legacy fallback (reading
        // ServerPassword would only matter for the native-enforcement
        // crash path we're pivoting away from).
        PasswordRequired: !string.IsNullOrEmpty(_opts.LanternAuthPassword),
        VacSecured: false,
        Version: $"lantern-{LanternVersionInfo.LanternVersion}/g2-{LanternVersionInfo.G2Build}",
        GameplayPort: _opts.GameplayPort,
        Keywords: $"lantern,g2,lantern={LanternVersionInfo.LanternVersion},g2build={LanternVersionInfo.G2Build}");

    private IReadOnlyList<PlayerInfoEntry> BuildPlayers()
    {
        // Source A2S player list. We populate from cached
        // PlayerListSnapshot frames the plugin ships over IPC. Each
        // entry maps to (DisplayName, Score=0, ConnectionSeconds since
        // ConnectedAtUnixMs). If the plugin hasn't sent a snapshot yet
        // (or no players are connected) the list is empty — gametracker
        // / panel tools show 'no players online' rather than a faked
        // count.
        var snap = _state.Players;
        if (snap.Count == 0) return Array.Empty<PlayerInfoEntry>();

        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var result = new List<PlayerInfoEntry>(snap.Count);
        foreach (var p in snap)
        {
            var ageMs = nowMs - p.ConnectedAtUnixMs;
            var ageSec = ageMs > 0 ? (float)(ageMs / 1000.0) : 0f;
            result.Add(new PlayerInfoEntry(
                Name: string.IsNullOrEmpty(p.DisplayName) ? p.LanternUserId : p.DisplayName,
                Score: 0,
                ConnectSeconds: ageSec));
        }
        return result;
    }

    private IReadOnlyList<KeyValuePair<string, string>> BuildRules() => new[]
    {
        new KeyValuePair<string, string>("instance", _opts.InstanceId),
        new KeyValuePair<string, string>("gameplay_port", _opts.GameplayPort.ToString()),
        new KeyValuePair<string, string>("lantern_version", LanternVersionInfo.LanternVersion),
        new KeyValuePair<string, string>("g2_build", LanternVersionInfo.G2Build),
    };

    // Online when the in-game plugin is pinging OR the Grounded 2 process is alive.
    // The lobby-parked case (no fresh plugin heartbeat, but the game IS running and
    // joinable) must still answer A2S so the launcher shows the server online.
    private bool IsGameOnline()
    {
        var freshHeartbeat = _state.HasFreshHeartbeat(_heartbeatTimeout);
        // Only probe the pidfile if the heartbeat path didn't already say online,
        // matching the original short-circuit (no behavior change).
        var processAlive = !freshHeartbeat && GameProcessProbe.IsAlive(_opts.GamePidFile);
        var online = freshHeartbeat || processAlive;
        _log.LogDebug(
            "A2S IsGameOnline={Online} (source={Source}, freshHeartbeat={Heartbeat}, gameProcessAlive={Process})",
            online,
            online ? (freshHeartbeat ? "fresh-heartbeat" : "game-process-probe") : "none",
            freshHeartbeat, processAlive);
        return online;
    }
}
