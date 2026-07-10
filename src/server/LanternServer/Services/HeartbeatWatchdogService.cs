using System.Diagnostics;
using LanternServer.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LanternServer.Services;

/// <summary>
/// Watches the shipping g2_sshost runtime heartbeat. The native named-pipe
/// plugin is reserved for future use and is not required for a healthy host.
/// </summary>
public sealed class HeartbeatWatchdogService : BackgroundService
{
    private static readonly TimeSpan NativeAuthGrace = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan RuntimeBootstrapGrace = TimeSpan.FromSeconds(180);
    private static readonly TimeSpan FailClosedRetry = TimeSpan.FromSeconds(30);

    private static readonly string[] G2KillProcessNameWhitelist =
    [
        "Grounded2Steam-Win64-Shipping",
        "Grounded2-Win64-Shipping",
        "Grounded2-WinGDK-Shipping",
        "Grounded2",
    ];

    private readonly ILogger<HeartbeatWatchdogService> _log;
    private readonly PipeServerState _state;
    private readonly G2RestartCoordinator _coordinator;
    private readonly LanternServerOptions _opts;
    private readonly TimeSpan _timeout;

    private DateTimeOffset _runtimeUnhealthySince = DateTimeOffset.UtcNow;
    private DateTimeOffset _lastRuntimeFailClosedAt = DateTimeOffset.MinValue;
    private DateTimeOffset _lastNativeFailClosedAt = DateTimeOffset.MinValue;
    private bool _runtimeEverReady;
    private bool _runtimeStale;
    private bool _pluginHeartbeatStale;

    public HeartbeatWatchdogService(
        ILogger<HeartbeatWatchdogService> log,
        PipeServerState state,
        G2RestartCoordinator coordinator,
        IOptions<LanternServerOptions> options)
    {
        _log = log;
        _state = state;
        _coordinator = coordinator;
        _opts = options.Value;
        _timeout = TimeSpan.FromSeconds(Math.Max(1, options.Value.PluginHeartbeatTimeoutSeconds));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var period = TimeSpan.FromSeconds(Math.Max(1, _timeout.TotalSeconds / 3));
        using var timer = new PeriodicTimer(period);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                CheckHostRuntimeReady();
                CheckOptionalPluginHeartbeat();
                CheckUnexpectedNativePasswordGate();
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Heartbeat watchdog tick error");
            }
        }
    }

    private void CheckHostRuntimeReady()
    {
        // A missing runtime on an open server is a visibility/feature problem,
        // but on a passworded server it would also bypass the Lua join-key gate.
        if (string.IsNullOrEmpty(_opts.LanternAuthPassword)) return;

        var now = DateTimeOffset.UtcNow;
        if (!GameProcessProbe.IsAlive(_opts.GamePidFile))
        {
            _runtimeUnhealthySince = now;
            _runtimeStale = false;
            return;
        }

        if (_state.HasFreshRoster(_timeout))
        {
            if (_runtimeStale)
            {
                _log.LogInformation("g2_sshost runtime heartbeat recovered");
            }
            _runtimeEverReady = true;
            _runtimeStale = false;
            _runtimeUnhealthySince = now;
            return;
        }

        var grace = _runtimeEverReady ? _timeout : RuntimeBootstrapGrace;
        if (now - _runtimeUnhealthySince < grace) return;
        if (now - _lastRuntimeFailClosedAt < FailClosedRetry) return;

        _runtimeStale = true;
        _lastRuntimeFailClosedAt = now;
        var age = _state.LastRosterAt is DateTimeOffset last
            ? Math.Max(0, (int)(now - last).TotalSeconds)
            : -1;
        _log.LogCritical(
            "FAIL-CLOSED: Grounded 2 is running but the g2_sshost runtime heartbeat is missing or stale " +
            "(age={Age}s). Stopping this game instance so a passworded host cannot run without its join gate.",
            age);

        try
        {
            _coordinator.KillGame(TimeSpan.FromSeconds(10));
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "FAIL-CLOSED: runtime heartbeat kill failed");
        }
    }

    private void CheckOptionalPluginHeartbeat()
    {
        var conn = _state.Connection;
        var last = _state.LastHeartbeatAt;
        if (conn is null || last is null) return;

        var age = DateTimeOffset.UtcNow - last.Value;
        var stale = age > _timeout;
        if (stale && !_pluginHeartbeatStale)
        {
            _log.LogWarning(
                "Optional native plugin heartbeat went stale: instance={Instance} pid={Pid} age={Age}s",
                conn.InstanceId, conn.PluginPid, (int)age.TotalSeconds);
        }
        else if (!stale && _pluginHeartbeatStale)
        {
            _log.LogInformation(
                "Optional native plugin heartbeat recovered: instance={Instance} pid={Pid}",
                conn.InstanceId, conn.PluginPid);
        }
        _pluginHeartbeatStale = stale;
    }

    private void CheckUnexpectedNativePasswordGate()
    {
        var conn = _state.Connection;
        if (conn is null) return;
        if (DateTimeOffset.UtcNow - conn.ConnectedAt < NativeAuthGrace) return;
        if (_state.LastServerPasswordConfigured == 0) return;

        var now = DateTimeOffset.UtcNow;
        if (now - _lastNativeFailClosedAt < FailClosedRetry) return;
        _lastNativeFailClosedAt = now;

        _log.LogCritical(
            "FAIL-CLOSED: the optional native plugin reported a configured password gate. " +
            "Lantern uses the g2_sshost join-key gate; running both gates can crash player admission. " +
            "Stopping this game instance.");
        try
        {
            _coordinator.KillGame(TimeSpan.FromSeconds(10));
            TryKillByPluginPid(conn.PluginPid);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "FAIL-CLOSED: native password-gate kill failed");
        }
    }

    private void TryKillByPluginPid(int pid)
    {
        if (pid <= 0) return;
        try
        {
            using var process = Process.GetProcessById(pid);
            if (process.HasExited) return;

            var name = process.ProcessName ?? "";
            var allowed = G2KillProcessNameWhitelist.Any(candidate =>
                string.Equals(name, candidate, StringComparison.OrdinalIgnoreCase));
            if (!allowed)
            {
                _log.LogWarning(
                    "PluginPid={Pid} now belongs to '{Name}', not Grounded 2; refusing to kill",
                    pid, name);
                return;
            }

            _log.LogWarning("Stopping Grounded 2 plugin host pid={Pid} name={Name}", pid, name);
            process.Kill(entireProcessTree: false);
        }
        catch (ArgumentException)
        {
            // Process already exited.
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "KillByPluginPid({Pid}) failed", pid);
        }
    }
}
