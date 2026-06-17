using System.Diagnostics;
using LanternServer.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LanternServer.Services;

/// <summary>
/// Watches plugin heartbeats. If we haven't heard from the plugin in
/// <see cref="LanternServerOptions.PluginHeartbeatTimeoutSeconds"/>, log a warning;
/// future work hooks process supervision into this to restart the Grounded 2 instance.
///
/// Password enforcement model:
///   Password enforcement lives in LanternAuth.lua's K2_PostLogin hook on
///   incoming remote clients. The native Lantern.dll ApproveLogin hook is
///   INTENTIONALLY disabled (see G2ProcessSupervisorService.EmitPluginConfig
///   for the Grounded 2 crash-loop rationale). EmitPluginConfig always emits an
///   empty ServerPassword to plugin-config.json, so the native gate has
///   nothing to enforce.
///
///   The legacy <see cref="CheckServerPasswordReady"/> path is kept as a
///   defensive bug-detector: if the plugin EVER reports
///   ServerPasswordConfigured=1, something has gone wrong upstream (a
///   stale plugin-config.json carrying a non-empty ServerPassword from an
///   earlier deploy, or a manually-edited plugin config). In that
///   case the native and Lua paths could race and reproduce the Grounded 2
///   crash loop, so we kill the game loudly with a CRITICAL log rather than
///   let a customer hit it. The expected steady-state is
///   Configured=0 + HookReady=0 across the entire fleet.
/// </summary>
public sealed class HeartbeatWatchdogService : BackgroundService
{
    private readonly ILogger<HeartbeatWatchdogService> _log;
    private readonly PipeServerState _state;
    private readonly G2RestartCoordinator _coordinator;
    private readonly LanternServerOptions _opts;
    private readonly TimeSpan _timeout;

    // Grace period after we first see the plugin connect before we'll
    // start fail-closing. The plugin's bootstrap thread installs the
    // ApproveLogin hook AFTER the pipe handshake, so the first few
    // heartbeats can legitimately report hook-not-ready while the
    // bootstrap is still racing.
    private static readonly TimeSpan AuthGraceWindow = TimeSpan.FromSeconds(20);

    // Throttle for the "configured but native gate down" warning so we
    // don't spam the log every ~10s while waiting on the supervisor.
    private DateTimeOffset _lastFailClosedWarnAt = DateTimeOffset.MinValue;

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
        _timeout = TimeSpan.FromSeconds(options.Value.PluginHeartbeatTimeoutSeconds);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var period = TimeSpan.FromSeconds(Math.Max(1, _timeout.TotalSeconds / 3));
        using var timer = new PeriodicTimer(period);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                CheckHeartbeatStale();
                CheckServerPasswordReady();
                CheckLanternAuthLuaReady();
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "HeartbeatWatchdog tick error");
            }
        }
    }

    private static readonly TimeSpan LuaStatusBootstrapGrace = TimeSpan.FromSeconds(45);
    // Wider grace for the no-connection-at-all case: covers the cold-
    // start window where the game process is launching but Lantern.dll hasn't
    // connected the pipe yet. Grounded 2 cold launches can take 30-60s on a
    // panel-managed Win Server box. After this window without a pipe,
    // LanternServer concludes UE4SS / LanternLoader / Lantern.dll never
    // loaded successfully and fail-closes.
    private static readonly TimeSpan NoConnectionFailClosedGrace = TimeSpan.FromSeconds(180);
    private DateTimeOffset _lastLuaFailClosedWarnAt = DateTimeOffset.MinValue;
    private readonly DateTimeOffset _serverStartedAt = DateTimeOffset.UtcNow;

    // The "no-connection-window-started-at" anchor. Reset whenever we observe
    // a live connection. Without this reset, a long-lived LanternServer that
    // briefly loses its pipe (e.g. game process crash + relaunch driven by
    // LanternServer's own supervisor, or by the panel's PowerShell) would
    // skip the cold-start grace on the relaunch and fail-closed immediately
    // even though the new game process needs its own 30-60s cold launch.
    // Initialized to _serverStartedAt so the very first game launch gets the
    // full grace window from LanternServer process startup.
    private DateTimeOffset _noConnSince = DateTimeOffset.UtcNow;

    private void CheckLanternAuthLuaReady()
    {
        // Fail-closed for the Lua-only password-enforcement model.
        // If LanternAuthPassword is configured but the Lua gate isn't
        // reporting ready, kill the game to prevent the server from
        // running open without a gate. LanternAuth.lua writes
        // LanternServer/.lantern-auth-status with key=value pairs:
        //   ready=0|1
        //   passwordConfigured=0|1
        //   updated=<unix>
        //   reason=<short string>
        //
        // Fail-closed paths:
        //   1. No pipe connection at all (UE4SS never loaded, LanternLoader
        //      failed, Lantern.dll never connected) past NoConnectionFailClosedGrace
        //      since LanternServer process start. Use path-scoped KillGame
        //      (only effective on standalone — panel deploys log loudly).
        //   2. Pipe connection exists but LanternAuth status is not ready
        //      past LuaStatusBootstrapGrace since handshake. Use BOTH
        //      path-scoped KillGame AND PluginPid-based kill.
        if (string.IsNullOrEmpty(_opts.LanternAuthPassword)) return;

        var conn = _state.Connection;
        if (conn is not null)
        {
            // Live connection observed. Reset the no-connection anchor so
            // that any FUTURE drop gets a fresh 180s grace window. This is
            // the fix for the case where the anchor was the DI construction
            // time, which meant a long-running LanternServer would skip grace
            // on a later game restart and immediately fail-closed during the
            // new game's legitimate cold-launch window.
            _noConnSince = DateTimeOffset.UtcNow;
        }
        if (conn is null)
        {
            // Grace window since we last had a connection (or since
            // LanternServer process startup if we've never had one).
            var sinceConnLost = DateTimeOffset.UtcNow - _noConnSince;
            if (sinceConnLost < NoConnectionFailClosedGrace) return;

            var noConnNow = DateTimeOffset.UtcNow;
            var noConnSinceWarn = noConnNow - _lastLuaFailClosedWarnAt;
            if (noConnSinceWarn > TimeSpan.FromSeconds(15))
            {
                _log.LogCritical(
                    "FAIL-CLOSED: LanternAuthPassword is configured but Lantern.dll has NEVER connected to LanternServer's IPC " +
                    "pipe past {Grace}s grace. UE4SS / LanternLoader / Lantern.dll likely did not load. Without the plugin and " +
                    "the Lua gate, the passworded server cannot enforce its password. Stopping Grounded 2 (path-scoped only — " +
                    "panel-managed deploys with empty GameInstallRoot will see this log but require panel-side recovery, " +
                    "PluginPid path is unavailable without a connection). " +
                    "Diagnose: check g2-stdout.log for UE4SS bootstrap, LanternAuth.log absent, LanternServerRuntime.log absent.",
                    NoConnectionFailClosedGrace.TotalSeconds);
                _lastLuaFailClosedWarnAt = noConnNow;
            }
            try
            {
                _coordinator.KillGame(TimeSpan.FromSeconds(10));
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "FAIL-CLOSED (no-conn): KillGame threw");
            }
            return;
        }
        var sinceConnect = DateTimeOffset.UtcNow - conn.ConnectedAt;
        if (sinceConnect < LuaStatusBootstrapGrace) return;

        // Status file lives next to appsettings.json, which sits in
        // LanternServer's working dir. We can derive it from the
        // running process. On panel deploys the panel starts
        // LanternServer.exe with `-WorkingDirectory $lanternDir` so the
        // exe directory IS the appsettings directory. On standalone
        // self-hosts the assumption may not hold; ContentRootPath
        // would be more correct in the future, but adding a host
        // dependency here is more risk than it's worth right now.
        string statusPath;
        try
        {
            var baseDir = AppContext.BaseDirectory;
            statusPath = Path.Combine(baseDir, ".lantern-auth-status");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "LanternAuth status path resolve failed");
            return;
        }

        var (ready, passwordConfigured, reason, exists) = ReadLuaStatus(statusPath);
        // Stale-file protection: LanternServerRuntime/main.lua atomically
        // overwrites .lantern-auth-status with ready=0,reason=runtime_init
        // BEFORE LanternAuth runs, on every game process start (see the
        // write_not_ready_at pass at the top of that script). If runtime
        // can't overwrite (file lock + all retries fail), it calls
        // error(...) to abort LanternServerRuntime entirely — LanternLoader
        // and LanternAuth never load, Lantern.dll never injects, no pipe
        // handshake happens, and the no-connection fail-closed path in
        // CheckLanternAuthLuaReady (above) takes the game down via pid-file.
        // So any .lantern-auth-status the watchdog reads here with
        // ready=1 + passwordConfigured=1 was written by THIS game
        // process's LanternAuth.lua post-handshake; no file mtime
        // freshness check needed.
        if (exists && passwordConfigured && ready) return;

        var now = DateTimeOffset.UtcNow;
        var sinceWarn = now - _lastLuaFailClosedWarnAt;
        if (sinceWarn > TimeSpan.FromSeconds(15))
        {
            _log.LogCritical(
                "FAIL-CLOSED: LanternAuthPassword is configured but LanternAuth.lua status is not ready " +
                "(exists={Exists} ready={Ready} passwordConfigured={PwConfigured} reason='{Reason}' path={Path}). " +
                "Stopping Grounded 2 to prevent the passworded server from running open without the Lua password gate. " +
                "Investigate UE4SS load failure / LanternAuth.lua install / mods.txt ordering; the supervisor will " +
                "relaunch the game on the next loop.",
                exists, ready, passwordConfigured, reason, statusPath);
            _lastLuaFailClosedWarnAt = now;
        }

        try
        {
            // Path-scoped KillGame is best-effort on panel-managed deploys
            // (GameInstallRoot is empty), so also try the PID-based kill
            // path via the active pipe connection. PluginPid is the game
            // process that loaded Lantern.dll (Lantern.dll calls
            // GetCurrentProcessId in its handshake), so if we have an
            // active pipe handshake, we know the exact PID to kill.
            _coordinator.KillGame(TimeSpan.FromSeconds(10));
            TryKillByPluginPid(conn.PluginPid);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "FAIL-CLOSED (Lua): KillGame / KillByPluginPid threw");
        }
    }

    private (bool ready, bool passwordConfigured, string reason, bool exists) ReadLuaStatus(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return (false, false, "file_missing", false);
            }
            var lines = File.ReadAllLines(path);
            bool ready = false;
            bool passwordConfigured = false;
            string reason = "";
            foreach (var line in lines)
            {
                var eq = line.IndexOf('=');
                if (eq <= 0) continue;
                var key = line.Substring(0, eq).Trim();
                var value = line.Substring(eq + 1).Trim();
                switch (key)
                {
                    case "ready": ready = value == "1"; break;
                    case "passwordConfigured": passwordConfigured = value == "1"; break;
                    case "reason": reason = value; break;
                }
            }
            return (ready, passwordConfigured, reason, true);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "LanternAuth status read failed: {Path}", path);
            return (false, false, "read_error", false);
        }
    }

    private static readonly string[] G2KillProcessNameWhitelist = new[]
    {
        // Process names are sometimes case-insensitive on Windows;
        // Process.ProcessName has no .exe suffix. The plugin is loaded
        // into one of these Grounded 2 binaries.
        "Grounded2-Win64-Shipping",
        "Grounded2-WinGDK-Shipping",
        "Grounded2",
    };

    private void TryKillByPluginPid(int pid)
    {
        if (pid <= 0) return;
        try
        {
            using var p = Process.GetProcessById(pid);
            if (p.HasExited) return;

            // PID-reuse guard. Lantern.dll is loaded into the game process,
            // so the PluginPid reported on handshake must belong to one
            // of the game binaries. If the PID was recycled between the
            // handshake and the kill, the new owner's process name will
            // not match the game whitelist; refusing to kill prevents us
            // from terminating an unrelated user process. Case-insensitive
            // because Windows process names round-trip case unpredictably.
            var name = p.ProcessName ?? "";
            var isWhitelistedG2 = false;
            foreach (var allowed in G2KillProcessNameWhitelist)
            {
                if (string.Equals(name, allowed, StringComparison.OrdinalIgnoreCase))
                {
                    isWhitelistedG2 = true;
                    break;
                }
            }
            if (!isWhitelistedG2)
            {
                _log.LogWarning(
                    "FAIL-CLOSED (Lua): PluginPid={Pid} now belongs to '{Name}' (not Grounded 2); refusing to kill",
                    pid, name);
                return;
            }

            _log.LogWarning("FAIL-CLOSED (Lua): killing Grounded 2 plugin host pid={Pid} name={Name} via PluginPid path", pid, name);
            p.Kill(entireProcessTree: false);
        }
        catch (ArgumentException)
        {
            // pid no longer exists — race with normal supervisor exit
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "FAIL-CLOSED (Lua): KillByPluginPid({Pid}) failed", pid);
        }
    }

    // Tracks the last staleness state we logged so we emit one line on each
    // transition (fresh -> stale, stale -> fresh) rather than spamming every tick.
    private bool _heartbeatStale;

    private void CheckHeartbeatStale()
    {
        var conn = _state.Connection;
        if (conn is null) return;
        var last = _state.LastHeartbeatAt;
        if (last is null) return;
        var age = DateTimeOffset.UtcNow - last.Value;
        var stale = age > _timeout;

        _log.LogDebug("Heartbeat: instance={Instance} pid={Pid} age={Age}s stale={Stale}",
            conn.InstanceId, conn.PluginPid, (int)age.TotalSeconds, stale);

        if (stale && !_heartbeatStale)
        {
            _log.LogWarning("Plugin heartbeat went STALE: instance={Instance} pid={Pid} age={Age}s",
                conn.InstanceId, conn.PluginPid, (int)age.TotalSeconds);
        }
        else if (!stale && _heartbeatStale)
        {
            _log.LogInformation("Plugin heartbeat RECOVERED: instance={Instance} pid={Pid} age={Age}s",
                conn.InstanceId, conn.PluginPid, (int)age.TotalSeconds);
        }
        else if (stale)
        {
            // Keep the original per-tick warning as a Debug heartbeat-still-stale
            // line so verbose logs retain the continuous signal.
            _log.LogDebug("Plugin heartbeat still stale: instance={Instance} pid={Pid} age={Age}s",
                conn.InstanceId, conn.PluginPid, (int)age.TotalSeconds);
        }

        _heartbeatStale = stale;
    }

    private void CheckServerPasswordReady()
    {
        // Steady-state under the Lua-only enforcement model is
        // ServerPasswordConfigured=0 across the entire fleet. The native
        // ApproveLogin hook is intentionally disabled (plugin-config.json
        // always has an empty ServerPassword), so if the plugin EVER
        // reports Configured=1, something upstream is broken and we
        // could be heading for the Grounded 2 crash loop that necessitated
        // the pivot to Lua-only enforcement in the first place. Treat that
        // as a fail-closed condition.
        var conn = _state.Connection;
        if (conn is null) return;

        // Wait for the bootstrap-grace window after first contact. The
        // plugin's bootstrap thread populates the heartbeat fields
        // shortly after the handshake; first heartbeat or two can race.
        var sinceConnect = DateTimeOffset.UtcNow - conn.ConnectedAt;
        if (sinceConnect < AuthGraceWindow) return;

        // Plugin reporting Configured=0 is the expected steady state.
        // Nothing to do.
        if (_state.LastServerPasswordConfigured == 0) return;

        // Plugin says "I see ServerPassword in plugin-config.json." Under
        // the Lua-only model this should be impossible — EmitPluginConfig
        // hardcodes ServerPassword="". A non-zero value here means a stale
        // config from a previous deploy, a manually-edited plugin-config.json,
        // or a Lantern update that regressed EmitPluginConfig. Stop the game
        // before native + Lua enforcement layers race into the crash.
        var now = DateTimeOffset.UtcNow;
        var sinceWarn = now - _lastFailClosedWarnAt;
        if (sinceWarn > TimeSpan.FromSeconds(15))
        {
            _log.LogCritical(
                "FAIL-CLOSED: native plugin reported ServerPasswordConfigured={Configured} " +
                "hookReady={Ready}, but Lantern enforces password in LanternAuth.lua only " +
                "and emits an empty ServerPassword in plugin-config.json. This usually means a " +
                "stale plugin-config.json from an earlier deploy or a manually-edited config. " +
                "Stopping Grounded 2 to prevent the native gate from racing with the Lua gate " +
                "(which reproduces the Grounded 2 fatal crash loop). Fix plugin-config.json " +
                "ServerPassword field to empty and restart.",
                _state.LastServerPasswordConfigured, _state.LastServerPasswordHookReady);
            _lastFailClosedWarnAt = now;
        }

        // Kill the game. Path-scoped KillGame covers standalone deploys (where
        // GameInstallRoot is configured); PluginPid-based kill covers
        // panel-managed deploys (where GameInstallRoot is intentionally
        // empty and KillGame logs "no anchor to scope by"). The IPC
        // handshake gives us the exact game PID — Lantern.dll calls
        // GetCurrentProcessId() inside the game process when sending
        // handshake.
        try
        {
            _coordinator.KillGame(TimeSpan.FromSeconds(10));
            TryKillByPluginPid(conn.PluginPid);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "FAIL-CLOSED: KillGame / KillByPluginPid threw");
        }
    }
}
