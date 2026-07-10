using Lantern.Protocol;
using Microsoft.Extensions.Logging;

namespace LanternServer.Services;

/// <summary>
/// Shared state for the active plugin connection. Single-writer, multi-reader.
/// Lantern's design is one plugin connection per LanternServer instance.
/// </summary>
public sealed class PipeServerState
{
    private readonly ILogger<PipeServerState> _log;
    private readonly object _gate = new();

    private PipeConnection? _connection;

    public PipeServerState(ILogger<PipeServerState> log) => _log = log;

    public PipeConnection? Connection
    {
        get { lock (_gate) return _connection; }
    }

    public void SetConnection(PipeConnection? connection)
    {
        lock (_gate) _connection = connection;
    }

    public DateTimeOffset? LastHeartbeatAt { get; set; }

    // g2_sshost rewrites roster.json from inside the game every few seconds.
    // This is the shipping runtime heartbeat; the native pipe plugin is optional.
    public DateTimeOffset? LastRosterAt { get; set; }

    public int LastReportedPlayerCount { get; set; }

    public int EffectivePlayerCount => Players.Count;

    public bool HasFreshHeartbeat(TimeSpan maxAge)
    {
        var conn = Connection;
        var last = LastHeartbeatAt;
        if (conn is null || last is null) return false;
        return DateTimeOffset.UtcNow - last.Value <= maxAge;
    }

    public bool HasFreshRoster(TimeSpan maxAge)
    {
        var last = LastRosterAt;
        return last is not null && DateTimeOffset.UtcNow - last.Value <= maxAge;
    }

    // The optional native plugin reports auth state on every pipe heartbeat.
    // Password enforcement belongs to g2_sshost and the supervisor emits
    // ServerPassword="" to plugin-config.json, so the expected steady-state
    // on every endpoint is Configured=0/Ready=0.
    // HeartbeatWatchdogService fail-closes if
    // it ever sees Configured=1 — that indicates a stale plugin config or a
    // manually-edited plugin-config.json, both of which can reproduce the
    // game crash loop if they race with the Lua gate. The legacy-plugin case
    // (both fields stay 0) is now the normal case.
    public int LastServerPasswordConfigured { get; set; }
    public int LastServerPasswordHookReady { get; set; }

    // Cached player list populated by roster.json (shipping path) or the
    // optional IPC PlayerListSnapshot frame. SourceQueryHostedService and the
    // launcher's HTTP /players endpoint read from this.
    private List<PlayerSnapshot> _players = new();
    private readonly Dictionary<string, PlayerSnapshot> _logPlayers = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _playersGate = new();
    public IReadOnlyList<PlayerSnapshot> Players
    {
        get
        {
            lock (_playersGate)
            {
                var merged = new List<PlayerSnapshot>(_players.Count + _logPlayers.Count);
                var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var player in _players.Concat(_logPlayers.Values))
                {
                    var id = player.LanternUserId ?? "";
                    var name = player.DisplayName ?? "";
                    if (id.Length > 0 && !seenIds.Add(id)) continue;
                    if (name.Length > 0 && !seenNames.Add(name)) continue;
                    merged.Add(player);
                }
                return merged;
            }
        }
    }
    public void SetPlayers(IEnumerable<PlayerSnapshot> players)
    {
        lock (_playersGate) _players = players?.ToList() ?? new();
    }

    public void UpsertLogPlayer(string lanternUserId, string displayName, int pingMs = 0)
    {
        if (string.IsNullOrWhiteSpace(displayName)) return;
        var id = string.IsNullOrWhiteSpace(lanternUserId) ? $"g2:{displayName}" : lanternUserId;
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        lock (_playersGate)
        {
            var connectedAt = _logPlayers.TryGetValue(id, out var existing)
                ? existing.ConnectedAtUnixMs
                : now;
            _logPlayers[id] = new PlayerSnapshot(id, displayName, connectedAt, now, pingMs);
        }
    }

    public void RemoveLogPlayer(string lanternUserId)
    {
        if (string.IsNullOrWhiteSpace(lanternUserId)) return;
        lock (_playersGate)
        {
            _logPlayers.Remove(lanternUserId);
            _players = _players
                .Where(player => !string.Equals(player.LanternUserId, lanternUserId, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
    }

    public void RemoveLogPlayerByDisplayName(string displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName)) return;
        lock (_playersGate)
        {
            foreach (var key in _logPlayers
                         .Where(kv => string.Equals(kv.Value.DisplayName, displayName, StringComparison.OrdinalIgnoreCase))
                         .Select(kv => kv.Key)
                         .ToList())
            {
                _logPlayers.Remove(key);
            }
            _players = _players
                .Where(player => !string.Equals(player.DisplayName, displayName, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
    }

    public void ClearLogPlayers()
    {
        lock (_playersGate) _logPlayers.Clear();
    }

    internal void SetLogPlayerForTest(PlayerSnapshot player)
    {
        lock (_playersGate) _logPlayers[player.LanternUserId] = player;
    }

    public void ClearLogPlayersIfOnlyOne()
    {
        lock (_playersGate)
        {
            var mergedCount = _players
                .Concat(_logPlayers.Values)
                .GroupBy(player => !string.IsNullOrWhiteSpace(player.DisplayName)
                    ? $"name:{player.DisplayName}"
                    : $"id:{player.LanternUserId}", StringComparer.OrdinalIgnoreCase)
                .Count();
            if (mergedCount <= 1)
            {
                _logPlayers.Clear();
                _players.Clear();
            }
        }
    }

}

/// <summary>
/// Wraps the per-plugin connection: send queue, sequence counter, codec.
/// </summary>
public sealed class PipeConnection
{
    private readonly FrameCodec _codec;
    private readonly Func<byte[], CancellationToken, Task> _write;
    private uint _sequence;

    public PipeConnection(string instanceId, int pluginPid, string pluginVersion, FrameCodec codec, Func<byte[], CancellationToken, Task> write)
    {
        InstanceId = instanceId;
        PluginPid = pluginPid;
        PluginVersion = pluginVersion;
        _codec = codec;
        _write = write;
    }

    public string InstanceId { get; }
    public int PluginPid { get; }
    public string PluginVersion { get; }
    public DateTimeOffset ConnectedAt { get; } = DateTimeOffset.UtcNow;

    public Task SendAsync<T>(FrameType type, T payload, CancellationToken ct = default)
    {
        var seq = Interlocked.Increment(ref _sequence);
        var bytes = _codec.Encode(type, FrameFlags.None, seq, payload);
        return _write(bytes, ct);
    }
}
