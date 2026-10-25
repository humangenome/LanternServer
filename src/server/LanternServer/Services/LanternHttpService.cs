using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LanternServer.Configuration;
using LanternServer.Static;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LanternServer.Services;

/// <summary>
/// Authenticated HTTP API for launcher-side snapshot management. Listens on
/// <see cref="LanternServerOptions.HttpPort"/>, validates an HMAC-SHA256
/// signature against the per-instance HMAC key, then dispatches to the save
/// orchestrator.
///
/// Routes (all under <c>/api/v1</c>):
///   GET  /health                       — public, no auth
///   POST /join/pass                    — public; the player's app proves the join password before the game
///                                        connects (HMAC, never the password) and gets a pass token its
///                                        join mod presents over the game connection
///   POST /join/identity                — source-bound character choice; join-password HMAC when configured
///   GET  /info                         — instance + version
///   GET  /snapshots                    — list snapshots (auth)
///   GET  /snapshots/{id}/download      — stream zip (auth)
///   POST /snapshots                    — upload zip body, store as snapshot (auth)
///   POST /snapshots/{id}/restore       — restore previously-stored snapshot (auth)
///   POST /snapshots/import-restore     — upload + restore in one shot (auth)
///
/// Auth: client computes
///   signature = HMAC_SHA256(key, method + "\n" + path + "\n" + ts + "\n" + body_sha256_hex)
/// and sends headers:
///   X-Lantern-Timestamp: &lt;unix seconds&gt;
///   X-Lantern-Signature: &lt;hex&gt;
///   X-Lantern-Content-Sha256: &lt;body_sha256_hex&gt; (required for bodies over 64 KB)
/// Server rejects timestamps older than 5 minutes (replay window). Signed requests are
/// authorized from their headers before any body is read (see <see cref="HttpAuthGate"/>);
/// public and signed routes run on separate request slots, and one large upload runs at a time.
/// </summary>
public sealed class LanternHttpService : BackgroundService
{
    private const int ReplayWindowSeconds = 300;
    // Separate request slots: public routes (players' apps: health, player list, join
    // password), signed routes, and large uploads inside the signed slots. A client that
    // holds every signed slot can no longer stop players joining.
    private const int PublicConcurrentRequests = 16;
    private const int SignedConcurrentRequests = 6;
    private const int ConcurrentLargeUploads = 1;

    private readonly ILogger<LanternHttpService> _log;
    private readonly LanternServerOptions _opts;
    private readonly IConfiguration _config;
    private readonly SaveOrchestratorService _saves;
    private readonly PipeServerState _pipeState;
    private readonly InstanceIdentityProvider _identity;
    private readonly ChatService _chat;
    private readonly IdentityChoiceService _identityChoices;
    private readonly byte[] _authKey;
    private readonly SemaphoreSlim _publicLimiter = new(PublicConcurrentRequests, PublicConcurrentRequests);
    private readonly SemaphoreSlim _signedLimiter = new(SignedConcurrentRequests, SignedConcurrentRequests);
    private readonly SemaphoreSlim _uploadLimiter = new(ConcurrentLargeUploads, ConcurrentLargeUploads);
    // Sliding window of signatures we've already accepted, so a captured
    // valid request inside the 5-minute replay window can't be re-sent to
    // double-trigger a restore or pile up snapshots.
    private readonly SignatureReplayCache _replay = new(ReplayWindowSeconds);
    private static readonly string UploadTempDir = Path.Combine(Path.GetTempPath(), "lantern-uploads");
    private HttpListener? _listener;

    public LanternHttpService(
        ILogger<LanternHttpService> log,
        IOptions<LanternServerOptions> opts,
        IConfiguration config,
        SaveOrchestratorService saves,
        PipeServerState pipeState,
        InstanceIdentityProvider identity,
        ChatService chat,
        IdentityChoiceService identityChoices)
    {
        _log = log;
        _opts = opts.Value;
        _config = config;
        _saves = saves;
        _pipeState = pipeState;
        _identity = identity;
        _chat = chat;
        _identityChoices = identityChoices;
        // The HTTP API auth secret is SHA256(RconPassword). Same trust tier
        // as RCON — if the customer has set an RCON password, they already
        // expose admin control of the world. Deriving from RconPassword
        // means there's no second secret to manage. If RconPassword is
        // empty, only the public routes start; admin routes fail closed.
        _authKey = string.IsNullOrEmpty(_opts.RconPassword)
            ? Array.Empty<byte>()
            : SHA256.HashData(Encoding.UTF8.GetBytes(_opts.RconPassword));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_opts.HttpPort <= 0)
        {
            _log.LogInformation("HTTP API disabled (HttpPort <= 0)");
            return;
        }
        if (_authKey.Length == 0)
            _log.LogWarning("HTTP admin routes disabled: RconPassword is empty; public routes remain available");

        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://+:{_opts.HttpPort}/api/v1/");
        _listener.Prefixes.Add($"http://+:{_opts.HttpPort}/map/");

        try
        {
            _listener.Start();
            _log.LogInformation("HTTP API bound to all interfaces on port {Port}", _opts.HttpPort);
        }
        catch (HttpListenerException ex) when (ex.ErrorCode == 5 /* access denied */)
        {
            // Wildcard bind requires either Administrator or a one-time
            // urlacl registration. Try once to add the urlacl ourselves
            // (will succeed if we happen to be elevated). If that fails,
            // fall back to localhost — but log a clear error so customers
            // know remote launcher transfers will fail until urlacl is
            // provisioned.
            _log.LogWarning(ex,
                "HttpListener bind +:{Port} denied. Attempting netsh urlacl auto-registration",
                _opts.HttpPort);
            TryRegisterUrlAcl(_opts.HttpPort);

            try
            {
                _listener = new HttpListener();
                _listener.Prefixes.Add($"http://+:{_opts.HttpPort}/api/v1/");
                _listener.Prefixes.Add($"http://+:{_opts.HttpPort}/map/");
                _listener.Start();
                _log.LogInformation("HTTP API bound to all interfaces on port {Port} after urlacl fix", _opts.HttpPort);
            }
            catch (Exception retryEx)
            {
                _log.LogError(retryEx,
                    "HTTP API DEGRADED: bound to LOCALHOST ONLY on port {Port}. " +
                    "Remote launcher world transfers WILL FAIL. " +
                    "Fix: run as Administrator once, or pre-register with " +
                    "'netsh http add urlacl url=http://+:{Port}/ user=Everyone'",
                    _opts.HttpPort, _opts.HttpPort);
                _listener = new HttpListener();
                _listener.Prefixes.Add($"http://localhost:{_opts.HttpPort}/api/v1/");
                _listener.Prefixes.Add($"http://127.0.0.1:{_opts.HttpPort}/api/v1/");
                _listener.Prefixes.Add($"http://localhost:{_opts.HttpPort}/map/");
                _listener.Prefixes.Add($"http://127.0.0.1:{_opts.HttpPort}/map/");
                try { _listener.Start(); }
                catch (Exception innerEx)
                {
                    _log.LogError(innerEx, "HttpListener could not bind even to localhost:{Port}; HTTP API disabled", _opts.HttpPort);
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "HttpListener failed to start on port {Port}; HTTP API disabled", _opts.HttpPort);
            return;
        }

        _log.LogInformation("HTTP API listening on {Prefixes}", string.Join(", ", _listener.Prefixes));
        ApplyListenerTimeouts(_listener);
        var swept = HttpBodyReader.SweepStale(UploadTempDir, TimeSpan.FromHours(6), DateTime.UtcNow);
        if (swept > 0) _log.LogInformation("Removed {Count} upload temp file(s) left by an earlier run", swept);

        // Outer loop: if the listener crashes for any reason other than
        // shutdown, log it and restart after a short backoff so a transient
        // OS-level error doesn't permanently disable the API.
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                while (!stoppingToken.IsCancellationRequested && _listener.IsListening)
                {
                    HttpListenerContext ctx;
                    try
                    {
                        var getCtx = _listener.GetContextAsync();
                        var done = await Task.WhenAny(getCtx, Task.Delay(Timeout.Infinite, stoppingToken)).ConfigureAwait(false);
                        if (done != getCtx) break;
                        ctx = await getCtx.ConfigureAwait(false);
                    }
                    catch (ObjectDisposedException) { break; }
                    catch (OperationCanceledException) { break; }

                    // Bounded concurrency, per lane: refuse new requests if that
                    // lane is full rather than spawning unbounded Task.Run
                    // instances. The lane comes from the request line alone.
                    var limiter = HttpAuthGate.IsPublicRoute(
                        ctx.Request.HttpMethod, ctx.Request.Url?.AbsolutePath ?? "")
                        ? _publicLimiter : _signedLimiter;
                    if (!await limiter.WaitAsync(0).ConfigureAwait(false))
                    {
                        try
                        {
                            ctx.Response.StatusCode = 503;
                            ctx.Response.Headers["Retry-After"] = "5";
                            ctx.Response.KeepAlive = false;
                            ctx.Response.Close();
                        }
                        catch { }
                        continue;
                    }

                    _ = Task.Run(async () =>
                    {
                        try { await HandleRequestAsync(ctx, stoppingToken).ConfigureAwait(false); }
                        finally { limiter.Release(); }
                    }, stoppingToken);
                }
            }
            catch (HttpListenerException ex)
            {
                _log.LogWarning(ex, "HttpListener accept loop crashed; restarting in 5s");
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "HttpListener accept loop hit unexpected error; restarting in 5s");
            }

            if (stoppingToken.IsCancellationRequested) break;
            try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            try
            {
                if (!_listener.IsListening) _listener.Start();
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "HttpListener restart failed; will retry");
            }
        }

        try { _listener?.Stop(); } catch { }
        _log.LogInformation("HTTP API stopped");
    }

    private async Task HandleRequestAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        var req = ctx.Request;
        var res = ctx.Response;
        var path = req.Url?.AbsolutePath ?? "";
        var method = req.HttpMethod.ToUpperInvariant();
        var remote = req.RemoteEndPoint?.ToString() ?? "?";

        _log.LogDebug("HTTP request {Method} {Path} from {Remote}", method, path, remote);

        try
        {
            res.Headers["X-Lantern-Instance"] = _identity.InstanceId;

            // Public endpoints
            if (method == "GET" && path == "/api/v1/health")
            {
                var heartbeatTimeout = TimeSpan.FromSeconds(Math.Max(1, _opts.PluginHeartbeatTimeoutSeconds));
                var pluginOnline = _pipeState.HasFreshHeartbeat(heartbeatTimeout);
                var runtimeReady = _pipeState.HasFreshRoster(heartbeatTimeout);
                // The shipping host uses g2_sshost + roster.json, not the optional
                // native plugin pipe. Keep all three signals for standalone/backward
                // compatibility; runtimeReady is authoritative for current packages.
                var gameProcessAlive = GameProcessProbe.IsAlive(_opts.GamePidFile);
                var online = runtimeReady || gameProcessAlive || pluginOnline;
                int? heartbeatAgeSeconds = null;
                if (_pipeState.LastHeartbeatAt is DateTimeOffset lastHeartbeat)
                    heartbeatAgeSeconds = Math.Max(0, (int)(DateTimeOffset.UtcNow - lastHeartbeat).TotalSeconds);
                int? runtimeHeartbeatAgeSeconds = null;
                if (_pipeState.LastRosterAt is DateTimeOffset lastRoster)
                    runtimeHeartbeatAgeSeconds = Math.Max(0, (int)(DateTimeOffset.UtcNow - lastRoster).TotalSeconds);

                _log.LogDebug(
                    "/health decision: online={Online} (runtimeReady={Runtime}, gameProcessAlive={Process}, runtimeAge={RuntimeAge}s, legacyPluginOnline={Plugin})",
                    online, runtimeReady, gameProcessAlive, runtimeHeartbeatAgeSeconds, pluginOnline);

                await WriteJsonAsync(res, online ? 200 : 503, new
                {
                    ok = online,
                    instance = _identity.InstanceId,
                    server_name = string.IsNullOrWhiteSpace(_opts.ServerName) ? $"Lantern - {_identity.InstanceId}" : _opts.ServerName.Trim(),
                    lantern_version = LanternVersionInfo.LanternVersion,
                    g2_build = LanternVersionInfo.G2Build,
                    gameplay_port = _opts.GameplayPort,
                    query_port = _opts.QueryPort,
                    max_players = _opts.MaxPlayers,
                    player_count = _pipeState.EffectivePlayerCount,
                    game_process_alive = gameProcessAlive,
                    runtime_ready = runtimeReady,
                    last_runtime_heartbeat_age_seconds = runtimeHeartbeatAgeSeconds,
                    // Backward-compatible diagnostic for the reserved legacy IPC path.
                    plugin_connected = _pipeState.Connection is not null,
                    last_heartbeat_age_seconds = heartbeatAgeSeconds,
                });
                return;
            }

            if (method == "GET" && path == "/api/v1/players")
            {
                // Public — same info Source A2S exposes, just as JSON so
                // the launcher hero panel doesn't have to parse A2S to
                // show 'who is online'. No auth required.
                var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var snap = _pipeState.Players;
                await WriteJsonAsync(res, 200, new
                {
                    instance = _identity.InstanceId,
                    count = snap.Count,
                    players = snap.Select(p => new
                    {
                        name = string.IsNullOrEmpty(p.DisplayName) ? p.LanternUserId : p.DisplayName,
                        connected_seconds = Math.Max(0, (nowMs - p.ConnectedAtUnixMs) / 1000),
                        ping_ms = p.PingMs > 0 ? p.PingMs : (int?)null,
                    }),
                });
                return;
            }

            if (method == "GET" && path == "/api/v1/manifest")
            {
                // Public — players need this before they have RconPassword,
                // so HMAC is intentionally not enforced. Integrity rests on
                // per-mod sha256 pins in the payload. Spec:
                // protocol/manifest-v1.md.
                //
                // Read the Mods section fresh from IConfiguration so a
                // hoster editing appsettings.json never needs a restart.
                // ASP.NET Core's default appsettings provider has
                // reloadOnChange=true so this picks up edits within seconds.
                var mods = _config.GetSection("Lantern:Mods").Get<ModsOptions>() ?? new ModsOptions();
                await WriteJsonAsync(res, 200, new
                {
                    manifest_version = 1,
                    instance = _identity.InstanceId,
                    server_name = string.IsNullOrWhiteSpace(_opts.ServerName) ? $"Lantern - {_identity.InstanceId}" : _opts.ServerName.Trim(),
                    lantern_version = LanternVersionInfo.LanternVersion,
                    generated_unix = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    required = (mods.Required ?? new()).Select(SerializeEntry),
                    recommended = (mods.Recommended ?? new()).Select(SerializeEntry),
                    blocked = (mods.Blocked ?? new()).Select(b => new
                    {
                        id = b.Id ?? "",
                        reason = b.Reason ?? "",
                    }),
                });
                return;
            }

            if (method == "POST" && path == "/api/v1/" + Lantern.Protocol.JoinPass.Route)
            {
                string rawBody;
                try { rawBody = await ReadShortBodyAsync(req, maxBytes: 1024, ct).ConfigureAwait(false); }
                catch (BodyTooLargeException)
                {
                    await WriteJsonAsync(res, 413, new { error = "payload too large" });
                    return;
                }
                var pass = _identityChoices.AcceptPass(
                    rawBody,
                    req.Headers[Lantern.Protocol.JoinPass.ProofHeader],
                    req.RemoteEndPoint?.Address);
                if (pass.Ok)
                    await WriteJsonAsync(res, 200, new { ok = true, open = pass.OpenServer, expires_in = pass.ExpiresInSeconds, pass = pass.Token ?? "" });
                else
                    await WriteJsonAsync(res, pass.StatusCode, new { ok = false, error = pass.Error });
                return;
            }

            if (method == "POST" && path == "/api/v1/join/identity")
            {
                string rawBody;
                try { rawBody = await ReadShortBodyAsync(req, maxBytes: 2048, ct).ConfigureAwait(false); }
                catch (BodyTooLargeException)
                {
                    await WriteJsonAsync(res, 413, new { error = "payload too large" });
                    return;
                }
                var result = _identityChoices.Accept(
                    rawBody,
                    req.Headers["X-Lantern-Join-Proof"],
                    req.RemoteEndPoint?.Address);
                await WriteJsonAsync(res, result.StatusCode,
                    result.Ok ? new { ok = true } : new { ok = false, error = result.Error });
                return;
            }

            // ----- Live web map (protocol/map-v1.md) -----
            //
            // LanternRoster Lua mod writes roster.json (player list +
            // positions) every 5s and on K2_PostLogin/Logout.
            // /api/v1/map/state projects that into the SPA's expected
            // shape. Public when MapOptions.Public=true (community
            // dashboards) or HMAC-authed (launcher / private use) —
            // fall-through to the authed handler at the bottom of this
            // method covers the private path. The /map/ SPA HTML is
            // always public; if the state endpoint refuses the SPA's
            // poll the page renders "map is private" rather than failing
            // silently.

            if (method == "GET" && (path == "/map/" || path == "/map" || path == "/map/index.html"))
            {
                if (!_opts.Map.Enabled)
                {
                    await WriteJsonAsync(res, 404, new { error = "map disabled" });
                    return;
                }
                var html = MapSpa.Render();
                var bytes = Encoding.UTF8.GetBytes(html);
                res.StatusCode = 200;
                res.ContentType = "text/html; charset=utf-8";
                res.Headers["Cache-Control"] = "no-store";
                res.ContentLength64 = bytes.Length;
                await res.OutputStream.WriteAsync(bytes, ct).ConfigureAwait(false);
                res.OutputStream.Close();
                return;
            }

            if (method == "GET" && path == "/api/v1/map/state" && _opts.Map.Enabled && _opts.Map.Public)
            {
                await ServeMapStateAsync(res, ct).ConfigureAwait(false);
                return;
            }

            if (method == "GET" && path == "/api/v1/map/state" && !_opts.Map.Enabled)
            {
                await WriteJsonAsync(res, 404, new { error = "map disabled" });
                return;
            }

            // ----- Chat plane reads (protocol/chat-v1.md) -----
            //
            // /chat/motd stays public: it is the server's own message of the
            // day. /chat/recent moved behind the signature (below): nothing in
            // the game shows that stream, so a public copy of it only served
            // strangers. The player chat ingest (/chat/player) is gone: no
            // overlay or host mod ever read what it accepted, and it took a
            // sender name from the request.
            if (method == "GET" && path == "/api/v1/chat/motd")
            {
                await WriteJsonAsync(res, 200, new
                {
                    instance = _identity.InstanceId,
                    msg = _chat.GetMotd(),
                });
                return;
            }

            // Auth required for everything below. Body (if any) is streamed
            // to a temp file once and the resulting BufferedBody is passed
            // through to the route handler — no full-body MemoryStream and
            // no base64-via-header round trip (used to triple the memory
            // footprint per upload).
            var (body, status, error) = await AuthorizeAndReadBodyAsync(req, ct).ConfigureAwait(false);
            if (body is null)
            {
                // Refused before (or instead of) reading the rest of the body:
                // close the connection rather than let it drain.
                res.KeepAlive = false;
                if (status == 503) res.Headers["Retry-After"] = "30";
                await WriteJsonAsync(res, status, new { error });
                return;
            }

            if (method == "GET" && path == "/api/v1/info")
            {
                body.Dispose();
                await WriteJsonAsync(res, 200, new
                {
                    instance = _identity.InstanceId,
                    lantern_version = LanternVersionInfo.LanternVersion,
                    g2_build = LanternVersionInfo.G2Build,
                    gameplay_port = _opts.GameplayPort,
                    query_port = _opts.QueryPort,
                    max_players = _opts.MaxPlayers,
                });
                return;
            }

            // Authed entry for /api/v1/map/state. If MapOptions.Public is
            // set the public block above returned early; this branch handles
            // launcher + dashboard clients that authenticate via HMAC.
            if (method == "GET" && path == "/api/v1/map/state")
            {
                body.Dispose();
                if (!_opts.Map.Enabled)
                {
                    await WriteJsonAsync(res, 404, new { error = "map disabled" });
                    return;
                }
                await ServeMapStateAsync(res, ct).ConfigureAwait(false);
                return;
            }

            if (method == "GET" && path == "/api/v1/snapshots")
            {
                body.Dispose();
                var list = _saves.ListSnapshots();
                await WriteJsonAsync(res, 200, new
                {
                    snapshots = list.Select(s => new
                    {
                        id = s.SnapshotId,
                        taken_unix = s.TakenUnix,
                        size_bytes = s.SizeBytes,
                        sha256 = s.Sha256Hex,
                        retention_days = s.RetentionDays,
                    }),
                });
                return;
            }

            if (method == "GET" && path.StartsWith("/api/v1/snapshots/") && path.EndsWith("/download"))
            {
                body.Dispose();
                var id = path["/api/v1/snapshots/".Length..^"/download".Length];
                var record = _saves.ListSnapshots(int.MaxValue).FirstOrDefault(s => s.SnapshotId == id);
                if (record is null || !File.Exists(record.FilePath))
                {
                    await WriteJsonAsync(res, 404, new { error = "snapshot not found" });
                    return;
                }
                res.StatusCode = 200;
                res.ContentType = "application/zip";
                res.Headers["X-Lantern-Sha256"] = record.Sha256Hex;
                res.Headers["Content-Disposition"] = $"attachment; filename=\"{id}.zip\"";
                res.ContentLength64 = record.SizeBytes;
                await using (var fs = File.OpenRead(record.FilePath))
                {
                    await fs.CopyToAsync(res.OutputStream, ct).ConfigureAwait(false);
                }
                res.OutputStream.Close();
                return;
            }

            if (method == "POST" && path == "/api/v1/snapshots")
            {
                if (body.SizeBytes == 0)
                {
                    body.Dispose();
                    await WriteJsonAsync(res, 400, new { error = "empty body" });
                    return;
                }
                // The body is already on disk as a temp file. Just rename
                // it into SaveDir under the canonical snapshot name.
                Directory.CreateDirectory(_opts.SaveDir);
                var snapshotId = $"upload-{DateTimeOffset.UtcNow:yyyyMMddTHHmmssZ}-{Guid.NewGuid():N}".Substring(0, 36);
                var finalPath = Path.Combine(_opts.SaveDir, $"{snapshotId}.zip");
                File.Move(body.TempPath, finalPath);
                body.MarkConsumed(); // disposal won't try to delete the moved file

                var record = new Lantern.Persistence.SnapshotRecord(
                    SnapshotId: snapshotId,
                    TakenUnix: DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    FilePath: finalPath,
                    SizeBytes: new FileInfo(finalPath).Length,
                    Sha256Hex: body.Sha256Hex,
                    RetentionDays: 30);
                _saves.Database.RecordSnapshot(record);
                _saves.Database.Audit("http-api", "snapshot.upload", snapshotId,
                    $"{{\"bytes\":{record.SizeBytes}}}", record.TakenUnix);

                await WriteJsonAsync(res, 200, new
                {
                    snapshot = new
                    {
                        id = record.SnapshotId,
                        taken_unix = record.TakenUnix,
                        size_bytes = record.SizeBytes,
                        sha256 = record.Sha256Hex,
                    },
                });
                return;
            }

            if (method == "POST" && path.StartsWith("/api/v1/snapshots/") && path.EndsWith("/restore"))
            {
                body.Dispose();
                var id = path["/api/v1/snapshots/".Length..^"/restore".Length];
                var ok = await _saves.RestoreSnapshotAsync(id, "http-api", ct).ConfigureAwait(false);
                await WriteJsonAsync(res, ok ? 200 : 500, new { ok });
                return;
            }

            if (method == "POST" && path == "/api/v1/snapshots/import-restore")
            {
                if (body.SizeBytes == 0)
                {
                    body.Dispose();
                    await WriteJsonAsync(res, 400, new { error = "empty body" });
                    return;
                }
                var ok = await _saves.RestoreFromZipPathAsync(
                    body.TempPath, "http-api", "snapshot.import_restore", ct).ConfigureAwait(false);
                body.Dispose();
                await WriteJsonAsync(res, ok ? 200 : 500, new { ok });
                return;
            }

            // ----- Chat plane (protocol/chat-v1.md) -----
            // GET /chat/motd lives above the auth gate; /chat/recent is signed.
            if (method == "GET" && path == "/api/v1/chat/recent")
            {
                body.Dispose();
                var sinceMs = ParseLongQuery(req.Url, "since", 0);
                var limit = (int)ParseLongQuery(req.Url, "limit", 100);
                var msgs = _chat.GetRecent(sinceMs, limit);
                await WriteJsonAsync(res, 200, new
                {
                    version = 1,
                    instance = _identity.InstanceId,
                    now_ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    messages = msgs,
                });
                return;
            }

            if (method == "POST" && path == "/api/v1/chat/say")
            {
                body.Dispose();
                await WriteJsonAsync(res, 501, new { ok = false, error = RconHostedService.NoChatReply });
                return;
            }

            if (method == "POST" && path == "/api/v1/chat/motd")
            {
                body.Dispose();
                await WriteJsonAsync(res, 501, new { ok = false, error = RconHostedService.NoMotdReply });
                return;
            }

            body.Dispose();
            await WriteJsonAsync(res, 404, new { error = "not found", method, path });
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "HTTP {Method} {Path} threw", method, path);
            try
            {
                await WriteJsonAsync(res, 500, new { error = ex.Message });
            }
            catch { }
        }
        finally
        {
            try { _log.LogDebug("HTTP response {Method} {Path} -> {Status}", method, path, res.StatusCode); }
            catch { }
            try { res.Close(); } catch { }
        }
    }

    /// <summary>
    /// Authorizes a signed request from its headers first (<see cref="HttpAuthGate.Evaluate"/>)
    /// and only then reads the body. Returns the body (on disk, or empty) or the status and
    /// error to answer with. Nothing is written to disk before the signature has been checked,
    /// and every partial temp file is deleted.
    /// </summary>
    private async Task<(BufferedBody? Body, int Status, string Error)> AuthorizeAndReadBodyAsync(HttpListenerRequest req, CancellationToken ct)
    {
        var path = req.Url?.AbsolutePath ?? "";
        var method = req.HttpMethod.ToUpperInvariant();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var hasBody = req.HasEntityBody;
        var length = hasBody ? req.ContentLength64 : 0;

        var d = HttpAuthGate.Evaluate(_authKey, method, path,
            req.Headers[HttpAuthGate.TimestampHeader], req.Headers[HttpAuthGate.SignatureHeader],
            req.Headers[HttpAuthGate.ContentShaHeader],
            hasBody, length, _opts.MaxUploadBytes, now, ReplayWindowSeconds);

        switch (d.Verdict)
        {
            case HttpAuthGate.Verdict.Unauthorized:
                return (null, 401, "unauthorized");
            case HttpAuthGate.Verdict.ContentShaRequired:
                return (null, 401, "Update the Lantern app, then try again. (Uploads over 64 KB need the " + HttpAuthGate.ContentShaHeader + " header.)");
            case HttpAuthGate.Verdict.TooLarge:
                return (null, 413, "payload too large");

            case HttpAuthGate.Verdict.VerifiedNoBody:
                if (!_replay.TryRecord(d.Signature, d.Timestamp, now)) return (null, 401, "unauthorized");
                return (BufferedBody.Empty, 200, "");

            case HttpAuthGate.Verdict.ReadSmallThenVerify:
            {
                byte[] bytes;
                try { bytes = await HttpBodyReader.ReadSmallAsync(req.InputStream, length, d.ReadCap, ct).ConfigureAwait(false); }
                catch (HttpBodyTooLargeException) { return (null, 413, "payload too large"); }
                catch (HttpBodyIncompleteException) { return (null, 400, "incomplete body"); }
                var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
                if (!HttpAuthGate.SignatureMatches(_authKey, method, path, d.Timestamp, sha, d.Signature))
                    return (null, 401, "unauthorized");
                if (!_replay.TryRecord(d.Signature, d.Timestamp, now)) return (null, 401, "unauthorized");
                if (bytes.Length == 0) return (BufferedBody.Empty, 200, "");
                var tempPath = await HttpBodyReader.WriteTempAsync(bytes, UploadTempDir, ct).ConfigureAwait(false);
                return (new BufferedBody(tempPath, sha, bytes.Length), 200, "");
            }

            case HttpAuthGate.Verdict.VerifiedStream:
            {
                // The signature is already good over the declared hash; the replay
                // check comes before the body so a captured request cannot even
                // start a second upload.
                if (!_replay.TryRecord(d.Signature, d.Timestamp, now)) return (null, 401, "unauthorized");
                var large = d.ReadCap > HttpAuthGate.SmallBodyMaxBytes;
                if (large && !await _uploadLimiter.WaitAsync(0).ConfigureAwait(false))
                    return (null, 503, "another upload is in progress");
                try
                {
                    (string tempPath, string sha, long size) streamed;
                    try { streamed = await HttpBodyReader.StreamToTempAsync(req.InputStream, length, d.ReadCap, UploadTempDir, ct).ConfigureAwait(false); }
                    catch (HttpBodyTooLargeException) { return (null, 413, "payload too large"); }
                    catch (HttpBodyIncompleteException) { return (null, 400, "incomplete body"); }
                    var buffered = new BufferedBody(streamed.tempPath, streamed.sha, streamed.size);
                    if (!HttpAuthGate.HashMatches(streamed.sha, d.DeclaredSha!))
                    {
                        buffered.Dispose();
                        return (null, 401, "unauthorized");
                    }
                    return (buffered, 200, "");
                }
                finally
                {
                    if (large) _uploadLimiter.Release();
                }
            }
        }
        return (null, 401, "unauthorized");
    }

    // Slow-client limits on the Windows HTTP stack: a client that stops sending its
    // headers or its body loses the connection instead of holding a request slot.
    private void ApplyListenerTimeouts(HttpListener listener)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            listener.TimeoutManager.HeaderWait = TimeSpan.FromSeconds(15);
            listener.TimeoutManager.EntityBody = TimeSpan.FromSeconds(30);
            listener.TimeoutManager.DrainEntityBody = TimeSpan.FromSeconds(10);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "HttpListener timeouts not applied (non-fatal)");
        }
    }

    /// <summary>
    /// Holds a temp-file path + SHA256 of a streamed request body. Disposal
    /// deletes the temp file unless the route handler called
    /// <see cref="MarkConsumed"/> (route renamed it into permanent storage
    /// and owns it from that point on).
    /// </summary>
    private sealed class BufferedBody : IDisposable
    {
        public string TempPath { get; }
        public string Sha256Hex { get; }
        public long SizeBytes { get; }
        private int _disposed;
        private int _consumed;

        public BufferedBody(string tempPath, string sha256Hex, long sizeBytes)
        {
            TempPath = tempPath;
            Sha256Hex = sha256Hex;
            SizeBytes = sizeBytes;
        }

        /// <summary>A request with no body: no file behind it.</summary>
        public static BufferedBody Empty => new("", HttpAuthGate.EmptySha256, 0);

        public void MarkConsumed() => Interlocked.Exchange(ref _consumed, 1);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            if (_consumed != 0) return;
            try { if (TempPath.Length > 0 && File.Exists(TempPath)) File.Delete(TempPath); } catch { }
        }
    }

    private sealed class BodyTooLargeException : Exception { }

    /// <summary>
    /// Best-effort one-shot of
    /// <c>netsh http add urlacl url=http://+:{port}/ user=Everyone</c>.
    /// Succeeds when LanternServer happens to be running with elevation;
    /// silently no-ops otherwise. The caller still retries the bind and
    /// drops to localhost if this didn't help.
    /// </summary>
    private void TryRegisterUrlAcl(int port)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "netsh.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("http");
            psi.ArgumentList.Add("add");
            psi.ArgumentList.Add("urlacl");
            psi.ArgumentList.Add($"url=http://+:{port}/");
            psi.ArgumentList.Add("user=Everyone");
            using var p = System.Diagnostics.Process.Start(psi);
            if (p is null) return;
            if (!p.WaitForExit(3_000))
            {
                try { p.Kill(true); } catch { }
                return;
            }
            if (p.ExitCode == 0)
                _log.LogInformation("netsh urlacl registered for port {Port}", port);
            else
                _log.LogInformation("netsh urlacl returned {Code} for port {Port} (likely needs elevation)", p.ExitCode, port);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "netsh urlacl probe failed (non-fatal)");
        }
    }

    private static async Task WriteJsonAsync(HttpListenerResponse res, int status, object payload)
    {
        res.StatusCode = status;
        res.ContentType = "application/json; charset=utf-8";
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        });
        res.ContentLength64 = bytes.Length;
        await res.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads roster.json (written by the LanternRoster Lua mod every 5s,
    /// plus on K2_PostLogin/Logout) and projects it into the /api/v1/map/state
    /// response shape. Position fields (X/Y/Z, world.name) are populated by
    /// the same rescan that writes the player list, so map data is exactly
    /// as fresh as the roster snapshot.
    ///
    /// Why not a separate map-state.json: UE5 dedicated G2 with zero
    /// connected players ticks at near-zero rate, so a 1Hz Lua LoopAsync
    /// fires every 10+ min in idle state. Roster's 5s tick is the only
    /// scheduler in the mod chain that catches both join (via K2_PostLogin
    /// fast path) and movement (via active-engine tick when a player is
    /// in-world), so we co-locate position publishing with it.
    /// </summary>
    private async Task ServeMapStateAsync(HttpListenerResponse res, CancellationToken ct)
    {
        var rosterPath = Path.Combine(AppContext.BaseDirectory, "roster.json");
        long unixMs = 0;
        var stale = true;
        string worldName = "";
        var players = System.Array.Empty<object>();

        if (File.Exists(rosterPath))
        {
            try
            {
                var body = await File.ReadAllTextAsync(rosterPath, ct).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                if (root.TryGetProperty("unix_ms", out var ums) && ums.TryGetInt64(out var ms))
                    unixMs = ms;
                if (root.TryGetProperty("world", out var w) && w.ValueKind == JsonValueKind.Object
                    && w.TryGetProperty("name", out var wn))
                    worldName = wn.GetString() ?? "";
                if (root.TryGetProperty("players", out var pa) && pa.ValueKind == JsonValueKind.Array)
                {
                    var list = new List<object>(pa.GetArrayLength());
                    foreach (var p in pa.EnumerateArray())
                    {
                        list.Add(new
                        {
                            id    = p.TryGetProperty("LanternUserId", out var idEl) ? idEl.GetString() ?? "" : "",
                            name  = p.TryGetProperty("DisplayName",  out var nmEl) ? nmEl.GetString() ?? "" : "",
                            x     = p.TryGetProperty("X", out var xEl) && xEl.ValueKind == JsonValueKind.Number ? xEl.GetDouble() : 0,
                            y     = p.TryGetProperty("Y", out var yEl) && yEl.ValueKind == JsonValueKind.Number ? yEl.GetDouble() : 0,
                            z     = p.TryGetProperty("Z", out var zEl) && zEl.ValueKind == JsonValueKind.Number ? zEl.GetDouble() : 0,
                            biome = "",
                        });
                    }
                    players = list.ToArray();
                }
                var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                stale = (nowMs - unixMs) > _opts.Map.StaleAfterMs;
            }
            catch (Exception ex) when (ex is IOException or JsonException)
            {
                _log.LogDebug(ex, "roster.json read/parse failed for map state");
            }
        }

        await WriteJsonAsync(res, 200, new
        {
            instance = _identity.InstanceId,
            lantern_version = LanternVersionInfo.LanternVersion,
            server_name = string.IsNullOrWhiteSpace(_opts.ServerName)
                ? $"Lantern - {_identity.InstanceId}"
                : _opts.ServerName.Trim(),
            unix_ms = unixMs,
            stale,
            world = new { name = worldName },
            players,
        });
    }

    private static long ParseLongQuery(Uri? url, string key, long defaultValue)
    {
        if (url is null) return defaultValue;
        var q = url.Query;
        if (string.IsNullOrEmpty(q)) return defaultValue;
        if (q[0] == '?') q = q[1..];
        foreach (var pair in q.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq <= 0) continue;
            if (!pair.AsSpan(0, eq).SequenceEqual(key.AsSpan())) continue;
            var raw = Uri.UnescapeDataString(pair[(eq + 1)..]);
            if (long.TryParse(raw, out var v)) return v;
            return defaultValue;
        }
        return defaultValue;
    }

    private static async Task<T?> ParseJsonBodyAsync<T>(BufferedBody body, CancellationToken ct) where T : class
    {
        try
        {
            await using var fs = File.OpenRead(body.TempPath);
            return await JsonSerializer.DeserializeAsync<T>(fs, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
                PropertyNameCaseInsensitive = true,
            }, ct).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    private static async Task<string> ReadShortBodyAsync(HttpListenerRequest req, int maxBytes, CancellationToken ct)
    {
        if (!req.HasEntityBody) return "";
        if (req.ContentLength64 > maxBytes) throw new BodyTooLargeException();
        var buffer = new byte[Math.Min(maxBytes, 4096)];
        using var ms = new MemoryStream();
        int total = 0;
        var stream = req.InputStream;
        while (true)
        {
            var read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read <= 0) break;
            total += read;
            if (total > maxBytes) throw new BodyTooLargeException();
            ms.Write(buffer, 0, read);
        }
        return req.ContentEncoding.GetString(ms.ToArray());
    }

    private static object SerializeEntry(ModEntry e) => new
    {
        id = e.Id ?? "",
        name = e.Name ?? "",
        version = e.Version ?? "",
        url = e.Url ?? "",
        sha256 = string.IsNullOrEmpty(e.Sha256) ? null : e.Sha256,
        size_bytes = e.SizeBytes,
        install_root = e.InstallRoot ?? "",
        notes = string.IsNullOrEmpty(e.Notes) ? null : e.Notes,
    };
}
