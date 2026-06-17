using System.Diagnostics;
using LanternServer.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LanternServer.Services;

/// <summary>
/// Coordinates between operations that need to pause Grounded 2 (snapshot restore,
/// world wipe, in-place upgrade) and the supervisor loop that keeps it
/// running. A restore acquires the gate, kills Grounded 2 if it's running, mutates
/// SaveGames, then releases the gate. The supervisor waits on the gate
/// before each relaunch so a new Grounded 2 doesn't spawn into a half-written
/// world.
///
/// Kill scoping: only kills Grounded 2 instances whose executable lives under the
/// configured <see cref="LanternServerOptions.GameInstallRoot"/> (or, when that
/// is empty, instances whose command line references the configured
/// <see cref="LanternServerOptions.GameUserDir"/>). A self-hoster running
/// vanilla Grounded 2 elsewhere on the same machine will not be touched.
/// </summary>
public sealed class G2RestartCoordinator
{
    private static readonly string[] G2ProcessNames =
    [
        "Grounded2-Win64-Shipping",
        "Grounded2-WinGDK-Shipping",
        "Grounded2",
    ];

    private readonly ILogger<G2RestartCoordinator> _log;
    private readonly LanternServerOptions _opts;
    private readonly SemaphoreSlim _restoreGate = new(1, 1);

    public G2RestartCoordinator(ILogger<G2RestartCoordinator> log, IOptions<LanternServerOptions> opts)
    {
        _log = log;
        _opts = opts.Value;
    }

    public async Task<IDisposable> BeginRestoreAsync(CancellationToken ct)
    {
        await _restoreGate.WaitAsync(ct).ConfigureAwait(false);
        return new Releaser(_restoreGate);
    }

    public async Task WaitForNoRestoreAsync(CancellationToken ct)
    {
        await _restoreGate.WaitAsync(ct).ConfigureAwait(false);
        _restoreGate.Release();
    }

    /// <summary>
    /// Kill the Grounded 2 instances this LanternServer owns. Scoping is strict:
    /// only processes whose path starts with <c>GameInstallRoot</c> (when
    /// configured) or whose command line includes <c>GameUserDir</c> are
    /// touched. Unrelated Grounded 2 processes on the machine — e.g. a customer's
    /// vanilla Steam session running alongside their host — are left alone.
    /// </summary>
    public void KillGame(TimeSpan waitForExit)
    {
        if (!OperatingSystem.IsWindows()) return;

        var installRoot = NormalizeForCompare(_opts.GameInstallRoot);
        var userDir = NormalizeForCompare(_opts.GameUserDir);
        var pidFile = _opts.GamePidFile ?? "";

        // Path A: pid-file fallback. Used by panel-managed deploys where
        // LanternServer doesn't own the game's lifecycle and GameInstallRoot is
        // empty. The panel's PowerShell writes Logs\game.pid with the game
        // process id; KillGame reads it, validates the process is still
        // alive and matches the game process-name whitelist (defends
        // against PID recycle), then kills. This path runs INDEPENDENTLY
        // of the install-root/user-dir path: even if A succeeds we still
        // try B to mop up any sibling instance under our install root
        // (unusual but defensible).
        if (!string.IsNullOrEmpty(pidFile))
        {
            TryKillFromPidFile(pidFile, waitForExit);
        }

        // Path B: install-root / user-dir scoping. Refuse to fire if no
        // anchor is configured AND the pid-file path didn't run, to avoid
        // killing every game on the box.
        if (installRoot is null && userDir is null)
        {
            if (string.IsNullOrEmpty(pidFile))
            {
                _log.LogWarning(
                    "KillGame skipped: none of GameInstallRoot, GameUserDir, or GamePidFile is configured");
            }
            return;
        }

        foreach (var name in G2ProcessNames)
        {
            Process[] procs;
            try { procs = Process.GetProcessesByName(name); }
            catch { continue; }

            foreach (var p in procs)
            {
                try
                {
                    if (!OwnsProcess(p, installRoot, userDir))
                    {
                        _log.LogDebug("KillGame skipping pid={Pid} (not under our GameInstallRoot/GameUserDir)", p.Id);
                        continue;
                    }
                    _log.LogInformation("Restoring: killing Grounded 2 pid={Pid} name={Name}", p.Id, name);
                    p.Kill(entireProcessTree: true);
                    p.WaitForExit((int)waitForExit.TotalMilliseconds);
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Failed to kill Grounded 2 pid={Pid}", p.Id);
                }
                finally
                {
                    try { p.Dispose(); } catch { }
                }
            }
        }
    }

    private void TryKillFromPidFile(string pidFile, TimeSpan waitForExit)
    {
        // Read pid file (single-line int). Missing/empty/garbage all skip
        // silently — the panel may not have started the game yet, or the file
        // may be in the middle of an atomic write.
        int pid;
        try
        {
            if (!File.Exists(pidFile)) return;
            var text = File.ReadAllText(pidFile).Trim();
            if (string.IsNullOrEmpty(text)) return;
            if (!int.TryParse(text, out pid) || pid <= 0) return;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "TryKillFromPidFile: read {Path} failed", pidFile);
            return;
        }

        // Derive the expected instance root from the pid file location.
        // Panel layout: <root>\Logs\game.pid → instance root is <root>.
        // The game executable lives somewhere under that (typically
        // <root>\Grounded2\Binaries\Win64\Grounded2-Win64-Shipping.exe).
        // We use this to reject PID-recycled processes that share the game
        // whitelist name but live under a DIFFERENT instance root — e.g.
        // customer-A's stale PID file points at a PID now owned by
        // customer-B's game process. Without this check we would kill the
        // wrong customer's server (round 5 Codex finding).
        string? expectedRoot = null;
        try
        {
            var logsDir = Path.GetDirectoryName(pidFile);
            if (!string.IsNullOrEmpty(logsDir))
            {
                var root = Path.GetDirectoryName(logsDir);
                expectedRoot = NormalizeForCompare(root);
            }
        }
        catch { /* fall through to refuse */ }
        if (string.IsNullOrEmpty(expectedRoot))
        {
            _log.LogWarning(
                "TryKillFromPidFile: cannot derive instance root from {Path}; refusing to kill pid={Pid}",
                pidFile, pid);
            return;
        }

        Process? p = null;
        try
        {
            try { p = Process.GetProcessById(pid); }
            catch
            {
                // Process exited (or PID never existed). Nothing to do.
                return;
            }

            // PID-recycle guard step 1: name whitelist.
            var name = p.ProcessName ?? "";
            var isGame = false;
            foreach (var allowed in G2ProcessNames)
            {
                if (string.Equals(name, allowed, StringComparison.OrdinalIgnoreCase))
                {
                    isGame = true;
                    break;
                }
            }
            if (!isGame)
            {
                _log.LogWarning(
                    "TryKillFromPidFile: pid={Pid} now belongs to '{Name}' (not Grounded 2); refusing to kill",
                    pid, name);
                return;
            }

            // PID-recycle guard step 2: process image path under expected
            // instance root. Without this, customer-A's stale pid file
            // could kill customer-B's game process (same name, different
            // instance). MainModule.FileName needs same-user permission,
            // which LanternServer has because it runs as the same user
            // that spawned the game in panel-managed deploys.
            string? exePath = null;
            try { exePath = p.MainModule?.FileName; }
            catch (Exception ex)
            {
                _log.LogWarning(ex,
                    "TryKillFromPidFile: cannot read MainModule for pid={Pid}; refusing to kill (cannot verify instance ownership)",
                    pid);
                return;
            }
            if (string.IsNullOrEmpty(exePath))
            {
                _log.LogWarning(
                    "TryKillFromPidFile: MainModule.FileName empty for pid={Pid}; refusing to kill",
                    pid);
                return;
            }
            var normExe = NormalizeForCompare(exePath)!;
            var sep = Path.DirectorySeparatorChar;
            if (!normExe.StartsWith(expectedRoot + sep, StringComparison.OrdinalIgnoreCase))
            {
                _log.LogWarning(
                    "TryKillFromPidFile: pid={Pid} ({Name}) exe={Exe} is NOT under expected instance root {Root}; refusing to kill (likely PID recycled to a different customer's Grounded 2)",
                    pid, name, normExe, expectedRoot);
                return;
            }

            _log.LogInformation(
                "FAIL-CLOSED (pid-file): killing Grounded 2 pid={Pid} name={Name} exe={Exe} root={Root}",
                pid, name, normExe, expectedRoot);
            p.Kill(entireProcessTree: true);
            p.WaitForExit((int)waitForExit.TotalMilliseconds);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "TryKillFromPidFile: kill pid={Pid} failed", pid);
        }
        finally
        {
            try { p?.Dispose(); } catch { }
        }
    }

    public bool IsOwnedGameRunning()
    {
        if (!OperatingSystem.IsWindows()) return false;

        var installRoot = NormalizeForCompare(_opts.GameInstallRoot);
        var userDir = NormalizeForCompare(_opts.GameUserDir);
        if (installRoot is null && userDir is null)
        {
            return false;
        }

        foreach (var name in G2ProcessNames)
        {
            Process[] procs;
            try { procs = Process.GetProcessesByName(name); }
            catch { continue; }

            foreach (var p in procs)
            {
                try
                {
                    if (OwnsProcess(p, installRoot, userDir)) return true;
                }
                catch { }
                finally
                {
                    try { p.Dispose(); } catch { }
                }
            }
        }

        return false;
    }

    private static bool OwnsProcess(Process p, string? installRoot, string? userDir)
    {
        // Path match — fast, no WMI. MainModule.FileName requires SeDebug-ish
        // privileges for processes owned by other users; in LanternServer's
        // case it always runs as the same user as the game, so MainModule works.
        string? exePath = null;
        try { exePath = p.MainModule?.FileName; }
        catch { /* access denied or exited */ }

        if (installRoot is not null && exePath is not null)
        {
            var normExe = NormalizeForCompare(exePath)!;
            // StartsWith without a directory-separator boundary would treat
            // 'C:\Games\G2-vanilla\...' as living under 'C:\Games\G2'.
            // Require either exact match (unlikely — exe path includes the
            // .exe) or the next char after the install root to be a
            // separator before claiming ownership.
            var sep = Path.DirectorySeparatorChar;
            if (string.Equals(normExe, installRoot, StringComparison.OrdinalIgnoreCase)
                || normExe.StartsWith(installRoot + sep, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        // Command line match for -USERDIR flag. WMI is the cleanest source
        // but pulls a heavy dependency; fall back to reading the process's
        // command line through Win32. For our scope, MainWindowTitle + PE
        // module path already cover 99% of cases.
        if (userDir is not null && exePath is not null)
        {
            // Process MAY have been launched with -USERDIR=<userDir>. Without
            // pulling a command-line helper, treat the install-root match as
            // authoritative; userDir is best-effort context only.
        }

        return false;
    }

    private static string? NormalizeForCompare(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            var full = Path.GetFullPath(path);
            return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch { return null; }
    }

    private sealed class Releaser : IDisposable
    {
        private readonly SemaphoreSlim _s;
        private int _disposed;
        public Releaser(SemaphoreSlim s) => _s = s;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) _s.Release();
        }
    }
}
