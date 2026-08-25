using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
    private readonly Dictionary<string, long> _seenNonces = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _lastAcceptedBySource = new(StringComparer.Ordinal);
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

        if (!string.IsNullOrEmpty(_opts.LanternAuthPassword))
        {
            if (!IsLowerHex(proof, 64))
                return IdentityChoiceResult.Reject(401, "unauthorized");

            var canonical = Canonicalize(request);
            var expected = HMACSHA256.HashData(
                Encoding.UTF8.GetBytes(_opts.LanternAuthPassword),
                Encoding.UTF8.GetBytes(canonical));
            byte[] supplied;
            try { supplied = Convert.FromHexString(proof!); }
            catch (FormatException) { return IdentityChoiceResult.Reject(401, "unauthorized"); }
            if (!CryptographicOperations.FixedTimeEquals(expected, supplied))
                return IdentityChoiceResult.Reject(401, "unauthorized");
        }

        var sourceToken = SourceToken(sourceAddress);
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
                $"source={sourceToken}") + "\n";
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
            foreach (var path in Directory.EnumerateFiles(directory, "identity-choice-*", SearchOption.TopDirectoryOnly))
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

public sealed record IdentityChoiceResult(bool Ok, int StatusCode, string? Error)
{
    public static IdentityChoiceResult Accepted() => new(true, 200, null);
    public static IdentityChoiceResult Reject(int statusCode, string error) => new(false, statusCode, error);
}
