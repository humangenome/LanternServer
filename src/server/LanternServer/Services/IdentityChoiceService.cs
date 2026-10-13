using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lantern.Protocol;
using LanternServer.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LanternServer.Services;

public sealed class IdentityChoiceService
{
    public const int RequestVersion = 1;
    public const int MaxClockSkewSeconds = 300;
    private const int ReplayRetentionSeconds = 600;
    private const int MaxNameBytes = 64;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    private readonly ILogger<IdentityChoiceService> _log;
    private readonly LanternServerOptions _opts;
    // A source that sends this many wrong-password proofs inside the window is refused until the
    // window passes, so the join routes cannot be used to guess the server password at speed.
    public const int MaxFailedProofsPerWindow = 10;
    public const int FailedProofWindowSeconds = 60;

    private readonly Dictionary<string, long> _seenNonces = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _lastAcceptedBySource = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _lastPassBySource = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<long>> _failedProofsBySource = new(StringComparer.Ordinal);
    private readonly object _seenNoncesLock = new();
    private long _lastMarkerSweepUnix;

    public IdentityChoiceService(
        ILogger<IdentityChoiceService> log,
        IOptions<LanternServerOptions> opts)
    {
        _log = log;
        _opts = opts.Value;
    }

    public IdentityChoiceResult Accept(
        string rawBody,
        string? proof,
        IPAddress? sourceAddress,
        long? nowUnix = null)
    {
        if (sourceAddress is null)
            return IdentityChoiceResult.Reject(400, "source unavailable");

        IdentityChoiceRequest? request;
        try { request = JsonSerializer.Deserialize<IdentityChoiceRequest>(rawBody, JsonOptions); }
        catch (JsonException) { return IdentityChoiceResult.Reject(400, "invalid json"); }

        if (request is null || request.Version != RequestVersion)
            return IdentityChoiceResult.Reject(400, "unsupported version");
        if (request.Identity is < 0 or > 4)
            return IdentityChoiceResult.Reject(400, "invalid identity");
        if (!IsLowerHex(request.Nonce, 32))
            return IdentityChoiceResult.Reject(400, "invalid nonce");
        if (!TryDecodeName(request.NameHex, out _))
            return IdentityChoiceResult.Reject(400, "invalid name");

        var now = nowUnix ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (Math.Abs(now - request.Timestamp) > MaxClockSkewSeconds)
            return IdentityChoiceResult.Reject(400, "stale request");

        var sourceToken = SourceToken(sourceAddress);
        var passwordSet = !string.IsNullOrEmpty(_opts.LanternAuthPassword);
        if (passwordSet)
        {
            if (TooManyFailures(sourceToken, now))
                return IdentityChoiceResult.Reject(429, "too many attempts");
            if (!ProofMatches(Canonicalize(request), proof))
            {
                RecordFailure(sourceToken, now);
                return IdentityChoiceResult.Reject(401, "unauthorized");
            }
        }

        var sweepMarkers = false;
        lock (_seenNoncesLock)
        {
            var cutoff = now - ReplayRetentionSeconds;
            foreach (var nonce in _seenNonces.Where(kv => kv.Value < cutoff).Select(kv => kv.Key).ToList())
                _seenNonces.Remove(nonce);
            foreach (var source in _lastAcceptedBySource.Where(kv => kv.Value < cutoff).Select(kv => kv.Key).ToList())
                _lastAcceptedBySource.Remove(source);
            if (_seenNonces.ContainsKey(request.Nonce))
                return IdentityChoiceResult.Reject(409, "replayed request");
            if (_lastAcceptedBySource.TryGetValue(sourceToken, out var lastAccepted) && now - lastAccepted < 1)
                return IdentityChoiceResult.Reject(429, "rate limited");
            if (_seenNonces.Count >= 4096)
                return IdentityChoiceResult.Reject(429, "rate limited");
            _seenNonces[request.Nonce] = now;
            _lastAcceptedBySource[sourceToken] = now;
            if (now - _lastMarkerSweepUnix >= 60)
            {
                _lastMarkerSweepUnix = now;
                sweepMarkers = true;
            }
        }

        try
        {
            var directory = ResolveChoiceDirectory();
            Directory.CreateDirectory(directory);
            if (sweepMarkers) SweepStaleMarkers(directory, now);
            var marker = string.Join('\n',
                $"version={RequestVersion}",
                $"accepted_at={now}",
                $"nonce={request.Nonce}",
                $"identity={request.Identity}",
                $"name_hex={request.NameHex}",
                $"source={sourceToken}",
                // The host accepts this marker as the player's password pass only when the proof
                // was checked here (older apps prove the password with the lobby character choice).
                $"auth={(passwordSet ? "hmac" : "open")}") + "\n";
            WriteMarker(Path.Combine(directory, $"identity-choice-{sourceToken}.pending"), marker);
            WriteMarker(Path.Combine(directory, "identity-choice-latest.pending"), marker);
            _log.LogInformation("Accepted character choice {Identity} from {Source}", request.Identity, sourceToken);
            return IdentityChoiceResult.Accepted();
        }
        catch
        {
            lock (_seenNoncesLock)
            {
                _seenNonces.Remove(request.Nonce);
                if (_lastAcceptedBySource.TryGetValue(sourceToken, out var acceptedAt) && acceptedAt == now)
                    _lastAcceptedBySource.Remove(sourceToken);
            }
            throw;
        }
    }

    /// <summary>
    /// The join pass (POST /api/v1/join/pass): the player's app proves it knows the server
    /// password before the game connects. On success the server hands back a random pass token and
    /// writes a short-lived marker <c>join-pass-&lt;token&gt;.ok</c> for the host. The app's join mod
    /// presents that token over its own game connection, so the host knows which player it belongs
    /// to (the host mod cannot read a connection's network address). An open server (no password)
    /// answers ok and records nothing; the host does not check players there.
    /// </summary>
    public JoinPassResult AcceptPass(
        string rawBody,
        string? proof,
        IPAddress? sourceAddress,
        long? nowUnix = null)
    {
        if (sourceAddress is null)
            return JoinPassResult.Reject(400, "source unavailable");

        JoinPassRequest? request;
        try { request = JsonSerializer.Deserialize<JoinPassRequest>(rawBody, JsonOptions); }
        catch (JsonException) { return JoinPassResult.Reject(400, "invalid json"); }

        if (request is null || request.Version != JoinPass.Version)
            return JoinPassResult.Reject(400, "unsupported version");
        if (!IsLowerHex(request.Nonce, 32))
            return JoinPassResult.Reject(400, "invalid nonce");

        var now = nowUnix ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (Math.Abs(now - request.Timestamp) > JoinPass.MaxClockSkewSeconds)
            return JoinPassResult.Reject(400, "stale request");

        if (string.IsNullOrEmpty(_opts.LanternAuthPassword))
            return JoinPassResult.Open();

        var sourceToken = SourceToken(sourceAddress);
        if (TooManyFailures(sourceToken, now))
            return JoinPassResult.Reject(429, "too many attempts");
        if (!ProofMatches(JoinPass.Canonical(request.Version, request.Timestamp, request.Nonce), proof))
        {
            RecordFailure(sourceToken, now);
            _log.LogInformation("Join pass refused for {Source}: wrong password", sourceToken);
            return JoinPassResult.Reject(401, "wrong password");
        }

        var sweepMarkers = false;
        lock (_seenNoncesLock)
        {
            var cutoff = now - ReplayRetentionSeconds;
            foreach (var nonce in _seenNonces.Where(kv => kv.Value < cutoff).Select(kv => kv.Key).ToList())
                _seenNonces.Remove(nonce);
            foreach (var source in _lastPassBySource.Where(kv => kv.Value < cutoff).Select(kv => kv.Key).ToList())
                _lastPassBySource.Remove(source);
            if (_seenNonces.ContainsKey(request.Nonce))
                return JoinPassResult.Reject(409, "replayed request");
            if (_lastPassBySource.TryGetValue(sourceToken, out var lastPass) && now - lastPass < 1)
                return JoinPassResult.Reject(429, "rate limited");
            if (_seenNonces.Count >= 4096)
                return JoinPassResult.Reject(429, "rate limited");
            _seenNonces[request.Nonce] = now;
            _lastPassBySource[sourceToken] = now;
            if (now - _lastMarkerSweepUnix >= 60)
            {
                _lastMarkerSweepUnix = now;
                sweepMarkers = true;
            }
        }

        try
        {
            var directory = ResolveChoiceDirectory();
            Directory.CreateDirectory(directory);
            if (sweepMarkers) SweepStaleMarkers(directory, now);
            var token = JoinPass.NewNonce();
            var marker = string.Join('\n',
                $"version={JoinPass.Version}",
                $"accepted_at={now}",
                $"expires_at={now + JoinPass.LifetimeSeconds}",
                $"source={sourceToken}") + "\n";
            WriteMarker(Path.Combine(directory, $"join-pass-{token}.ok"), marker);
            _log.LogInformation("Join pass accepted for {Source}", sourceToken);
            return JoinPassResult.Accepted(JoinPass.LifetimeSeconds, token);
        }
        catch
        {
            lock (_seenNoncesLock)
            {
                _seenNonces.Remove(request.Nonce);
                if (_lastPassBySource.TryGetValue(sourceToken, out var passAt) && passAt == now)
                    _lastPassBySource.Remove(sourceToken);
            }
            throw;
        }
    }

    private bool ProofMatches(string canonical, string? proof)
    {
        if (!IsLowerHex(proof, 64)) return false;
        var expected = HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(_opts.LanternAuthPassword),
            Encoding.UTF8.GetBytes(canonical));
        byte[] supplied;
        try { supplied = Convert.FromHexString(proof!); }
        catch (FormatException) { return false; }
        return CryptographicOperations.FixedTimeEquals(expected, supplied);
    }

    private bool TooManyFailures(string sourceToken, long now)
    {
        lock (_seenNoncesLock)
        {
            if (!_failedProofsBySource.TryGetValue(sourceToken, out var failures)) return false;
            failures.RemoveAll(at => now - at >= FailedProofWindowSeconds);
            if (failures.Count == 0) { _failedProofsBySource.Remove(sourceToken); return false; }
            return failures.Count >= MaxFailedProofsPerWindow;
        }
    }

    private void RecordFailure(string sourceToken, long now)
    {
        lock (_seenNoncesLock)
        {
            if (_failedProofsBySource.Count >= 4096 && !_failedProofsBySource.ContainsKey(sourceToken))
            {
                foreach (var stale in _failedProofsBySource
                             .Where(kv => kv.Value.Count == 0 || now - kv.Value[^1] >= FailedProofWindowSeconds)
                             .Select(kv => kv.Key).ToList())
                    _failedProofsBySource.Remove(stale);
                if (_failedProofsBySource.Count >= 4096) return;
            }
            if (!_failedProofsBySource.TryGetValue(sourceToken, out var failures))
                _failedProofsBySource[sourceToken] = failures = new List<long>();
            failures.Add(now);
        }
    }

    public static string Canonicalize(IdentityChoiceRequest request) =>
        $"{request.Version}\n{request.Timestamp}\n{request.Nonce}\n{request.Identity}\n{request.NameHex}";

    public static string SourceToken(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var text = address.ToString().ToLowerInvariant();
        var chars = text.Select(c => char.IsAsciiLetterOrDigit(c) ? c : '_').ToArray();
        return new string(chars);
    }

    private string ResolveChoiceDirectory()
    {
        var configured = string.IsNullOrWhiteSpace(_opts.IdentityChoiceDirectory)
            ? "identity-choices"
            : _opts.IdentityChoiceDirectory.Trim();
        return Path.GetFullPath(Path.IsPathRooted(configured)
            ? configured
            : Path.Combine(AppContext.BaseDirectory, configured));
    }

    private static void WriteMarker(string destination, string marker)
    {
        var temp = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, marker, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temp, destination, overwrite: true);
        }
        finally
        {
            try { File.Delete(temp); } catch { }
        }
    }

    private void SweepStaleMarkers(string directory, long now)
    {
        var cutoff = DateTimeOffset.FromUnixTimeSeconds(now - ReplayRetentionSeconds).UtcDateTime;
        try
        {
            foreach (var path in Directory.EnumerateFiles(directory, "identity-choice-*", SearchOption.TopDirectoryOnly)
                         .Concat(Directory.EnumerateFiles(directory, "join-pass-*", SearchOption.TopDirectoryOnly)))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(path) < cutoff) File.Delete(path);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        catch (IOException ex) { _log.LogDebug(ex, "Could not sweep stale character-choice markers"); }
        catch (UnauthorizedAccessException ex) { _log.LogDebug(ex, "Could not sweep stale character-choice markers"); }
    }

    private static bool TryDecodeName(string? value, out string name)
    {
        name = "";
        if (string.IsNullOrEmpty(value) || value.Length > MaxNameBytes * 2 || (value.Length & 1) != 0)
            return false;
        if (!IsLowerHex(value, value.Length)) return false;
        byte[] bytes;
        try { bytes = Convert.FromHexString(value); }
        catch (FormatException) { return false; }
        try { name = new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException) { return false; }
        if (string.IsNullOrWhiteSpace(name) || name != name.Trim()) return false;
        var runeCount = 0;
        foreach (var rune in name.EnumerateRunes())
        {
            if (Rune.IsControl(rune)) return false;
            runeCount++;
        }
        return runeCount is > 0 and <= 32;
    }

    private static bool IsLowerHex(string? value, int exactLength)
    {
        if (value is null || value.Length != exactLength) return false;
        foreach (var c in value)
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return false;
        return true;
    }
}

public sealed class IdentityChoiceRequest
{
    public int Version { get; set; }
    public long Timestamp { get; set; }
    public string Nonce { get; set; } = "";
    public int Identity { get; set; }
    public string NameHex { get; set; } = "";
}

public sealed class JoinPassRequest
{
    public int Version { get; set; }
    public long Timestamp { get; set; }
    public string Nonce { get; set; } = "";
}

public sealed record JoinPassResult(bool Ok, int StatusCode, string? Error, bool OpenServer, int ExpiresInSeconds, string? Token = null)
{
    public static JoinPassResult Accepted(int expiresIn, string token) => new(true, 200, null, false, expiresIn, token);
    public static JoinPassResult Open() => new(true, 200, null, true, 0);
    public static JoinPassResult Reject(int statusCode, string error) => new(false, statusCode, error, false, 0);
}

public sealed record IdentityChoiceResult(bool Ok, int StatusCode, string? Error)
{
    public static IdentityChoiceResult Accepted() => new(true, 200, null);
    public static IdentityChoiceResult Reject(int statusCode, string error) => new(false, statusCode, error);
}
