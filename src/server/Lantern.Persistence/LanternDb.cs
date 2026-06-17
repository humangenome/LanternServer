using Dapper;
using Microsoft.Data.Sqlite;

namespace Lantern.Persistence;

/// <summary>
/// SQLite WAL store for Lantern's operational state. Owns its connection string;
/// callers do not hold connections — every method opens a short-lived one. WAL
/// mode + autovacuum + sane synchronous settings are applied at first open.
///
/// Tables:
///   players        — known clients (lantern_user_id, display, first/last seen, total seconds)
///   bans           — temporary or permanent bans keyed by lantern_user_id
///   join_tickets   — one-shot join authorizations issued by the launcher API
///   save_snapshots — record of save backups taken
///   audit_log      — every RCON command, plugin restart, ban/unban
/// </summary>
public sealed class LanternDb
{
    private readonly string _connectionString;

    public LanternDb(string filePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(filePath))!);
        _connectionString = $"Data Source={filePath};Cache=Shared;Pooling=True";
        EnsureSchema();
    }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        return c;
    }

    private void EnsureSchema()
    {
        using var c = Open();
        c.Execute("PRAGMA journal_mode=WAL;");
        c.Execute("PRAGMA synchronous=NORMAL;");
        c.Execute("PRAGMA foreign_keys=ON;");
        c.Execute("""
            CREATE TABLE IF NOT EXISTS players (
                lantern_user_id  TEXT PRIMARY KEY,
                display_name     TEXT NOT NULL,
                first_seen_unix  INTEGER NOT NULL,
                last_seen_unix   INTEGER NOT NULL,
                total_seconds    INTEGER NOT NULL DEFAULT 0
            );
            """);
        c.Execute("""
            CREATE TABLE IF NOT EXISTS bans (
                lantern_user_id  TEXT PRIMARY KEY,
                reason           TEXT NOT NULL,
                expires_unix     INTEGER,
                added_by         TEXT NOT NULL,
                added_unix       INTEGER NOT NULL
            );
            """);
        c.Execute("""
            CREATE TABLE IF NOT EXISTS join_tickets (
                ticket_id        TEXT PRIMARY KEY,
                lantern_user_id  TEXT NOT NULL,
                display_name     TEXT NOT NULL,
                issued_unix      INTEGER NOT NULL,
                expires_unix     INTEGER NOT NULL,
                consumed_unix    INTEGER
            );
            """);
        c.Execute("""
            CREATE TABLE IF NOT EXISTS save_snapshots (
                snapshot_id      TEXT PRIMARY KEY,
                taken_unix       INTEGER NOT NULL,
                file_path        TEXT NOT NULL,
                size_bytes       INTEGER NOT NULL,
                sha256_hex       TEXT NOT NULL,
                retention_days   INTEGER NOT NULL
            );
            """);
        c.Execute("""
            CREATE TABLE IF NOT EXISTS audit_log (
                id               INTEGER PRIMARY KEY AUTOINCREMENT,
                ts_unix          INTEGER NOT NULL,
                actor            TEXT NOT NULL,
                action           TEXT NOT NULL,
                target           TEXT,
                detail_json      TEXT
            );
            CREATE INDEX IF NOT EXISTS audit_ts_idx ON audit_log (ts_unix DESC);
            """);
    }

    // ── Players ───────────────────────────────────────────────────────────

    public void UpsertPlayerSeen(string lanternUserId, string displayName, long unixSeconds)
    {
        using var c = Open();
        c.Execute("""
            INSERT INTO players (lantern_user_id, display_name, first_seen_unix, last_seen_unix)
            VALUES (@id, @name, @ts, @ts)
            ON CONFLICT(lantern_user_id) DO UPDATE SET
                display_name = excluded.display_name,
                last_seen_unix = excluded.last_seen_unix;
            """, new { id = lanternUserId, name = displayName, ts = unixSeconds });
    }

    public PlayerRecord? GetPlayer(string lanternUserId)
    {
        using var c = Open();
        return c.QueryFirstOrDefault<PlayerRecord>(
            "SELECT lantern_user_id AS LanternUserId, display_name AS DisplayName, " +
            "first_seen_unix AS FirstSeenUnix, last_seen_unix AS LastSeenUnix, " +
            "total_seconds AS TotalSeconds FROM players WHERE lantern_user_id = @id",
            new { id = lanternUserId });
    }

    public IReadOnlyList<PlayerRecord> ListPlayers(int limit = 100)
    {
        using var c = Open();
        return c.Query<PlayerRecord>(
            "SELECT lantern_user_id AS LanternUserId, display_name AS DisplayName, " +
            "first_seen_unix AS FirstSeenUnix, last_seen_unix AS LastSeenUnix, " +
            "total_seconds AS TotalSeconds FROM players ORDER BY last_seen_unix DESC LIMIT @lim",
            new { lim = limit }).ToList();
    }

    // ── Bans ──────────────────────────────────────────────────────────────

    public void AddBan(BanRecord ban)
    {
        using var c = Open();
        c.Execute("""
            INSERT INTO bans (lantern_user_id, reason, expires_unix, added_by, added_unix)
            VALUES (@LanternUserId, @Reason, @ExpiresUnix, @AddedBy, @AddedUnix)
            ON CONFLICT(lantern_user_id) DO UPDATE SET
                reason = excluded.reason,
                expires_unix = excluded.expires_unix,
                added_by = excluded.added_by,
                added_unix = excluded.added_unix;
            """, ban);
    }

    public void RemoveBan(string lanternUserId)
    {
        using var c = Open();
        c.Execute("DELETE FROM bans WHERE lantern_user_id = @id", new { id = lanternUserId });
    }

    public BanRecord? GetActiveBan(string lanternUserId, long nowUnix)
    {
        using var c = Open();
        return c.QueryFirstOrDefault<BanRecord>("""
            SELECT lantern_user_id AS LanternUserId, reason AS Reason,
                   expires_unix AS ExpiresUnix, added_by AS AddedBy, added_unix AS AddedUnix
            FROM bans
            WHERE lantern_user_id = @id
              AND (expires_unix IS NULL OR expires_unix > @now)
            """, new { id = lanternUserId, now = nowUnix });
    }

    // ── Join Tickets ──────────────────────────────────────────────────────

    public void IssueTicket(JoinTicketRecord t)
    {
        using var c = Open();
        c.Execute("""
            INSERT INTO join_tickets (ticket_id, lantern_user_id, display_name, issued_unix, expires_unix)
            VALUES (@TicketId, @LanternUserId, @DisplayName, @IssuedUnix, @ExpiresUnix);
            """, t);
    }

    public JoinTicketRecord? ConsumeTicket(string ticketId, long nowUnix)
    {
        // Atomic single-statement consume. UPDATE returns the affected row only
        // when the WHERE matches AND the row was unconsumed AND unexpired —
        // safe against concurrent ticket consumers.
        using var c = Open();
        return c.QueryFirstOrDefault<JoinTicketRecord>("""
            UPDATE join_tickets
               SET consumed_unix = @now
             WHERE ticket_id = @id
               AND consumed_unix IS NULL
               AND expires_unix > @now
            RETURNING ticket_id AS TicketId, lantern_user_id AS LanternUserId,
                      display_name AS DisplayName, issued_unix AS IssuedUnix,
                      expires_unix AS ExpiresUnix, consumed_unix AS ConsumedUnix
            """, new { id = ticketId, now = nowUnix });
    }

    // ── Save Snapshots ────────────────────────────────────────────────────

    public void RecordSnapshot(SnapshotRecord s)
    {
        using var c = Open();
        c.Execute("""
            INSERT INTO save_snapshots (snapshot_id, taken_unix, file_path, size_bytes, sha256_hex, retention_days)
            VALUES (@SnapshotId, @TakenUnix, @FilePath, @SizeBytes, @Sha256Hex, @RetentionDays);
            """, s);
    }

    public IReadOnlyList<SnapshotRecord> ListSnapshots(int limit = 30)
    {
        using var c = Open();
        return c.Query<SnapshotRecord>("""
            SELECT snapshot_id AS SnapshotId, taken_unix AS TakenUnix, file_path AS FilePath,
                   size_bytes AS SizeBytes, sha256_hex AS Sha256Hex, retention_days AS RetentionDays
            FROM save_snapshots ORDER BY taken_unix DESC LIMIT @lim
            """, new { lim = limit }).ToList();
    }

    // ── Audit ─────────────────────────────────────────────────────────────

    public void Audit(string actor, string action, string? target, string? detailJson, long unixSeconds)
    {
        using var c = Open();
        c.Execute("""
            INSERT INTO audit_log (ts_unix, actor, action, target, detail_json)
            VALUES (@ts, @actor, @action, @target, @detail);
            """, new { ts = unixSeconds, actor, action, target, detail = detailJson });
    }

    public IReadOnlyList<AuditEntry> RecentAudit(int limit = 50)
    {
        using var c = Open();
        return c.Query<AuditEntry>("""
            SELECT id AS Id, ts_unix AS TsUnix, actor AS Actor, action AS Action,
                   target AS Target, detail_json AS DetailJson
            FROM audit_log ORDER BY ts_unix DESC LIMIT @lim
            """, new { lim = limit }).ToList();
    }
}

public sealed record PlayerRecord(string LanternUserId, string DisplayName, long FirstSeenUnix, long LastSeenUnix, long TotalSeconds);

public sealed record BanRecord(string LanternUserId, string Reason, long? ExpiresUnix, string AddedBy, long AddedUnix);

public sealed record JoinTicketRecord(string TicketId, string LanternUserId, string DisplayName, long IssuedUnix, long ExpiresUnix, long? ConsumedUnix);

public sealed record SnapshotRecord(string SnapshotId, long TakenUnix, string FilePath, long SizeBytes, string Sha256Hex, long RetentionDays);

public sealed record AuditEntry(long Id, long TsUnix, string Actor, string Action, string? Target, string? DetailJson);
