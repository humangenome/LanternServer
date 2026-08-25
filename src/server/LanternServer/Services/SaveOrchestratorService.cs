using System.IO.Compression;
using System.Security.Cryptography;
using Lantern.Persistence;
using Lantern.Protocol;
using LanternServer.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LanternServer.Services;

/// <summary>
/// Save snapshot orchestrator. On a triggered snapshot, sends
/// <see cref="FrameType.SaveQuiesce"/> to the plugin, waits for the
/// game-side quiesce to complete, copies save files to the snapshot
/// directory under an atomic-rename pattern, hashes them, records a
/// row in the database, and rotates old snapshots past retention.
/// </summary>
public sealed class SaveOrchestratorService : IHostedService, IDisposable
{
    private readonly ILogger<SaveOrchestratorService> _log;
    private readonly LanternServerOptions _opts;
    private readonly PipeServerState _state;
    private readonly G2RestartCoordinator _coordinator;
    private readonly LanternDb _db;
    private readonly string _dbPath;
    private FileSystemWatcher? _watcher;
    private readonly System.Threading.SemaphoreSlim _autoLock = new(1, 1);
    private DateTime _lastAutoSnapshotUtc = DateTime.MinValue;
    private static readonly TimeSpan AutoSnapshotDebounce = TimeSpan.FromSeconds(45);
    private string GameSaveTree => Path.Combine(_opts.GameUserDir, "Saved", "Grounded2");

    public SaveOrchestratorService(
        ILogger<SaveOrchestratorService> log,
        IOptions<LanternServerOptions> opts,
        PipeServerState state,
        G2RestartCoordinator coordinator)
    {
        _log = log;
        _opts = opts.Value;
        _state = state;
        _coordinator = coordinator;
        _dbPath = Path.Combine(Path.GetDirectoryName(_opts.HmacKeyPath) ?? "data", "lantern.db");
        _db = new LanternDb(_dbPath);
    }

    public LanternDb Database => _db;

    public Task StartAsync(CancellationToken _)
    {
        _log.LogInformation("Save orchestrator ready; db={Path}, save_dir={Dir}, snapshots_enabled={Enabled}",
            _dbPath, _opts.SaveDir, _opts.SnapshotsEnabled);
        if (_opts.SnapshotsEnabled)
        {
            TryStartFileWatcher();
        }
        else
        {
            _log.LogInformation("Auto-snapshot FileSystemWatcher disabled by config (SnapshotsEnabled=false)");
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken _)
    {
        try { _watcher?.Dispose(); } catch { }
        _watcher = null;
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        try { _watcher?.Dispose(); } catch { }
        try { _autoLock.Dispose(); } catch { }
    }

    /// <summary>
    /// Watch the canonical per-instance Grounded2 tree for completed World.csav
    /// writes. Each save is a directory containing World.csav, HostPlayer.csav,
    /// player files, and SaveGameHeaderData.savheader.
    /// </summary>
    private void TryStartFileWatcher()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_opts.GameUserDir))
            {
                _log.LogInformation("FileSystemWatcher skipped: GameUserDir not configured");
                return;
            }
            var sourceDir = GameSaveTree;
            Directory.CreateDirectory(sourceDir);
            _watcher = new FileSystemWatcher(sourceDir, "World.csav")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
                IncludeSubdirectories = true,
                EnableRaisingEvents = true,
            };
            _watcher.Changed += (_, e) => _ = OnSaveFileChangedAsync(e.FullPath);
            _watcher.Created += (_, e) => _ = OnSaveFileChangedAsync(e.FullPath);
            _watcher.Renamed += (_, e) => _ = OnSaveFileChangedAsync(e.FullPath);
            _log.LogInformation("FileSystemWatcher armed on {Dir} (**/World.csav)", sourceDir);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "FileSystemWatcher failed to start; auto-snapshots disabled");
        }
    }

    private async Task OnSaveFileChangedAsync(string path)
    {
        // Debounce: Grounded 2 may emit multiple write events per save (size, mtime,
        // last access). Only one snapshot per AutoSnapshotDebounce window.
        if (!await _autoLock.WaitAsync(0).ConfigureAwait(false)) return;
        try
        {
            var now = DateTime.UtcNow;
            if (now - _lastAutoSnapshotUtc < AutoSnapshotDebounce) return;
            // Wait briefly for Grounded 2 to finish its write
            await Task.Delay(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            var rec = await SnapshotAsync(requestedBy: "auto:filewatcher", CancellationToken.None).ConfigureAwait(false);
            if (rec is not null)
            {
                _lastAutoSnapshotUtc = now;
                _log.LogInformation("Auto-snapshot {Id} fired from {Path}", rec.SnapshotId, path);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Auto-snapshot from FileSystemWatcher failed for {Path}", path);
        }
        finally
        {
            _autoLock.Release();
        }
    }

    /// <summary>
    /// Trigger a snapshot. Returns the snapshot record on success, null on failure.
    /// Invoked from RCON ("save snapshot"), the FileSystemWatcher when Grounded 2 auto-saves,
    /// or a scheduled timer.
    /// </summary>
    public async Task<SnapshotRecord?> SnapshotAsync(string requestedBy, CancellationToken ct = default)
    {
        var snapshotId = $"snap-{DateTimeOffset.UtcNow:yyyyMMddTHHmmssZ}-{Guid.NewGuid():N}".Substring(0, 36);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        try
        {
            // If the optional plugin is connected, request a quiesce. The shipping host
            // normally reaches this path from a completed World.csav file event; wait
            // briefly for the companion .csav/header writes to settle before archiving.
            var conn = _state.Connection;
            if (conn is not null)
            {
                await conn.SendAsync(FrameType.SaveQuiesce,
                    new SaveQuiesceMessage(snapshotId, TimeoutSeconds: 30),
                    ct).ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
            }

            Directory.CreateDirectory(_opts.SaveDir);
            var sourceDir = GameSaveTree;
            if (!Directory.Exists(sourceDir))
            {
                _log.LogWarning("Save source dir not found: {Dir}", sourceDir);
                return null;
            }

            if (!HasNonEmptyWorld(sourceDir))
            {
                _log.LogWarning("Save source contains no non-empty World.csav: {Dir}", sourceDir);
                return null;
            }

            var snapshotPath = Path.Combine(_opts.SaveDir, $"{snapshotId}.zip");
            var tmpPath = snapshotPath + ".tmp";
            CreateSaveTreeZip(sourceDir, tmpPath);
            File.Move(tmpPath, snapshotPath);

            var size = new FileInfo(snapshotPath).Length;
            var hash = await Sha256OfAsync(snapshotPath, ct).ConfigureAwait(false);

            var record = new SnapshotRecord(
                SnapshotId: snapshotId,
                TakenUnix: now,
                FilePath: snapshotPath,
                SizeBytes: size,
                Sha256Hex: hash,
                RetentionDays: 30);
            _db.RecordSnapshot(record);
            _db.Audit(requestedBy, "save.snapshot", target: snapshotId,
                detailJson: $"{{\"path\":\"{snapshotPath}\",\"bytes\":{size}}}", unixSeconds: now);

            _log.LogInformation("Snapshot {Id} written: {Bytes} bytes sha={Hash}", snapshotId, size, hash[..16]);
            RotateOldSnapshots();
            return record;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Snapshot {Id} failed", snapshotId);
            return null;
        }
    }

    private void RotateOldSnapshots()
    {
        try
        {
            var snapshots = _db.ListSnapshots(int.MaxValue);
            var nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            foreach (var s in snapshots)
            {
                var ageSeconds = nowUnix - s.TakenUnix;
                var maxSeconds = s.RetentionDays * 86_400;
                if (ageSeconds <= maxSeconds) continue;
                if (File.Exists(s.FilePath))
                {
                    try { File.Delete(s.FilePath); } catch { }
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Snapshot rotation pass failed");
        }
    }

    private static void CreateSaveTreeZip(string sourceDir, string destinationPath)
    {
        using var archive = System.IO.Compression.ZipFile.Open(
            destinationPath,
            System.IO.Compression.ZipArchiveMode.Create);

        foreach (var file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceDir, file).Replace('\\', '/');
            var info = new FileInfo(file);
            if (info.Length == 0 && IsWorldPath(relativePath))
            {
                continue;
            }

            archive.CreateEntryFromFile(file, relativePath, System.IO.Compression.CompressionLevel.Optimal);
        }
    }

    private static bool IsWorldPath(string relativePath) =>
        string.Equals(Path.GetFileName(relativePath), "World.csav", StringComparison.OrdinalIgnoreCase);

    internal static bool HasNonEmptyWorld(string sourceDir) =>
        Directory.Exists(sourceDir) &&
        Directory.EnumerateDirectories(sourceDir, "*", SearchOption.TopDirectoryOnly)
            .Any(saveDir =>
            {
                var world = Path.Combine(saveDir, "World.csav");
                return File.Exists(world) && new FileInfo(world).Length > 0;
            });

    internal static bool ZipHasValidSaveTree(string zipPath)
    {
        using var probe = ZipFile.OpenRead(zipPath);
        return probe.Entries.Any(entry =>
        {
            if (entry.Length <= 0) return false;
            var parts = entry.FullName.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length == 2
                && parts[0] is not "." and not ".."
                && string.Equals(parts[1], "World.csav", StringComparison.OrdinalIgnoreCase);
        });
    }

    private static async Task<string> Sha256OfAsync(string path, CancellationToken ct)
    {
        await using var f = File.OpenRead(path);
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(f, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }

    public IReadOnlyList<SnapshotRecord> ListSnapshots(int limit = 50) =>
        _db.ListSnapshots(limit);

    /// <summary>
    /// Restore a previously-taken snapshot by ID. Takes a pre-restore
    /// snapshot first (so the operation is reversible), kills the game, swaps
    /// the canonical Grounded2 save tree, then releases the gate
    /// so the supervisor relaunches Grounded 2 with the restored world.
    /// </summary>
    public async Task<bool> RestoreSnapshotAsync(string snapshotId, string requestedBy, CancellationToken ct = default)
    {
        var snap = _db.ListSnapshots(int.MaxValue).FirstOrDefault(s => s.SnapshotId == snapshotId);
        if (snap is null)
        {
            _log.LogWarning("Restore failed: snapshot {Id} not found", snapshotId);
            return false;
        }
        if (!File.Exists(snap.FilePath))
        {
            _log.LogWarning("Restore failed: snapshot file missing {Path}", snap.FilePath);
            return false;
        }
        return await RestoreFromZipPathAsync(snap.FilePath, requestedBy, $"restore:{snapshotId}", ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Restore from a freshly-uploaded zip on disk (e.g. an imported vanilla
    /// save or a cross-server transfer payload). The zip must contain at least
    /// one non-empty World.csav under a save directory.
    /// Takes a pre-restore snapshot, kills the game, swaps in the new files, then
    /// releases the supervisor gate.
    /// </summary>
    public async Task<bool> RestoreFromZipPathAsync(
        string zipPath, string requestedBy, string auditAction, CancellationToken ct = default)
    {
        if (!File.Exists(zipPath))
        {
            _log.LogWarning("Restore failed: source zip missing {Path}", zipPath);
            return false;
        }

        // Reject the wrong folder before taking the server down. The archive must
        // contain the contents of Saved\Grounded2: <save-folder>\World.csav.
        try
        {
            if (!ZipHasValidSaveTree(zipPath))
            {
                _log.LogWarning("Restore rejected: zip {Path} contains no direct non-empty save-folder/World.csav", zipPath);
                return false;
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Restore rejected: zip {Path} could not be opened", zipPath);
            return false;
        }

        using var _ = await _coordinator.BeginRestoreAsync(ct).ConfigureAwait(false);

        // 1. Stop Grounded 2 first. Taking a snapshot BEFORE the kill would
        // capture a mixed set of World/HostPlayer/player files. Kill, wait for
        // exact exit, then snapshot the stable tree.
        _coordinator.KillGame(TimeSpan.FromSeconds(20));
        if (_coordinator.IsOwnedGameRunning())
        {
            _log.LogWarning("Restore aborted: the owned Grounded 2 process did not stop");
            return false;
        }

        // 2. Snapshot the now-stable state so a bad restore is reversible.
        var pre = await SnapshotAsync($"pre-restore:{requestedBy}", ct).ConfigureAwait(false);
        if (pre is null)
        {
            _log.LogWarning("Restore aborted: pre-restore snapshot failed");
            return false;
        }

        // 3. Atomic Grounded2-tree swap. Extract the incoming zip into a temp
        // directory first; on success, rename the old tree out of the
        // way and rename the temp dir into place. If anything fails before
        // the rename, the live Grounded2 tree is untouched and the operation is
        // a no-op rather than a corruption.
        try
        {
            var saveGamesDir = GameSaveTree;
            Directory.CreateDirectory(Path.GetDirectoryName(saveGamesDir)!);

            var stagingDir = saveGamesDir + ".incoming-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            var prevDir = saveGamesDir + ".old-" + Guid.NewGuid().ToString("N").Substring(0, 8);

            bool savesMovedAside = false;
            try
            {
                Directory.CreateDirectory(stagingDir);
                // .NET 5+ ZipFile.ExtractToDirectory validates entries don't
                // escape the destination (zip-slip is rejected with an
                // IOException), so the staging dir is the canonical sandbox.
                System.IO.Compression.ZipFile.ExtractToDirectory(zipPath, stagingDir, overwriteFiles: true);
                if (!HasNonEmptyWorld(stagingDir))
                    throw new InvalidDataException("Extracted restore contains no non-empty World.csav");

                if (Directory.Exists(saveGamesDir))
                {
                    Directory.Move(saveGamesDir, prevDir);
                    savesMovedAside = true;
                }
                Directory.Move(stagingDir, saveGamesDir);
                if (Directory.Exists(prevDir))
                {
                    try { Directory.Delete(prevDir, recursive: true); } catch { /* best effort */ }
                }
            }
            catch (Exception ex)
            {
                // Rollback. If we already renamed the live Grounded2 tree out to
                // prevDir but couldn't get staging into place, restore the
                // original so the customer doesn't end up with NO save dir.
                if (savesMovedAside && Directory.Exists(prevDir) && !Directory.Exists(saveGamesDir))
                {
                    try { Directory.Move(prevDir, saveGamesDir); }
                    catch (Exception rollbackEx)
                    {
                        _log.LogError(rollbackEx,
                            "CRITICAL: rollback of Grounded2 saves from {Prev} to {Live} failed after restore error; " +
                            "manual recovery may be required",
                            prevDir, saveGamesDir);
                    }
                }
                if (Directory.Exists(stagingDir))
                {
                    try { Directory.Delete(stagingDir, recursive: true); } catch { }
                }
                _log.LogError(ex, "Restore failed; Grounded2 saves rolled back from {Prev}", prevDir);
                throw;
            }

            var nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            _db.Audit(requestedBy, auditAction, target: pre.SnapshotId,
                detailJson: $"{{\"pre_snapshot\":\"{pre.SnapshotId}\",\"source\":\"{Path.GetFileName(zipPath)}\"}}",
                unixSeconds: nowUnix);

            // The FileSystemWatcher's directory handle was opened against
            // the original Grounded2 tree; Directory.Move above replaced
            // that inode. Tear it down and re-arm against the new dir or
            // future auto-snapshots stop firing until LanternServer restart.
            try { _watcher?.Dispose(); } catch { }
            _watcher = null;
            TryStartFileWatcher();

            _log.LogInformation("Restore complete from {Zip}; pre-snapshot {Id}", zipPath, pre.SnapshotId);
            return true;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Restore failed while swapping Grounded2 saves");
            return false;
        }
        // gate released by `using` — supervisor will relaunch Grounded 2 on its
        // next loop iteration.
    }
}
