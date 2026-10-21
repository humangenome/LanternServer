using System.Security.Cryptography;
using System.Text;

namespace LanternServer.Services;

/// <summary>
/// Header-first authorization for the HMAC-signed HTTP routes.
///
/// The signature covers <c>method \n path \n timestamp \n sha256(body)</c>. A request that
/// carries the body hash in <see cref="ContentShaHeader"/> can therefore be authorized from
/// its headers alone, before a single body byte is read; the streamed body must then hash to
/// exactly that value. Without the header, only a small body (<see cref="SmallBodyMaxBytes"/>)
/// is read, into memory, and verified the old way. A body larger than that without the header
/// is refused unread, and so is anything over the upload cap or a body of unknown length that
/// grows past the small cap.
/// </summary>
internal static class HttpAuthGate
{
    internal const int SmallBodyMaxBytes = 64 * 1024;
    internal const string TimestampHeader = "X-Lantern-Timestamp";
    internal const string SignatureHeader = "X-Lantern-Signature";
    internal const string ContentShaHeader = "X-Lantern-Content-Sha256";
    internal const string EmptySha256 = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    internal enum Verdict
    {
        /// <summary>Refuse with 401 without reading the body.</summary>
        Unauthorized,
        /// <summary>Refuse with 401: a large body needs <see cref="ContentShaHeader"/>.</summary>
        ContentShaRequired,
        /// <summary>Refuse with 413 without reading the body.</summary>
        TooLarge,
        /// <summary>No body and the signature is good.</summary>
        VerifiedNoBody,
        /// <summary>Signature good over the declared hash: stream the body, it must match.</summary>
        VerifiedStream,
        /// <summary>Small (or unknown-length, capped small) body without a declared hash: read it, then verify.</summary>
        ReadSmallThenVerify,
    }

    internal readonly record struct Decision(Verdict Verdict, long Timestamp, string Signature, string? DeclaredSha, long ReadCap);

    /// <param name="contentLength">The request's Content-Length, or -1 when it has a body of unknown length (chunked).</param>
    internal static Decision Evaluate(
        byte[] key, string method, string path,
        string? timestampHeader, string? signatureHeader, string? contentShaHeader,
        bool hasBody, long contentLength, long maxUploadBytes, long nowUnix, int replayWindowSeconds)
    {
        Decision Refuse(Verdict v) => new(v, 0, "", null, 0);

        if (key.Length == 0) return Refuse(Verdict.Unauthorized);
        if (string.IsNullOrWhiteSpace(timestampHeader) || string.IsNullOrWhiteSpace(signatureHeader))
            return Refuse(Verdict.Unauthorized);
        if (!long.TryParse(timestampHeader.Trim(), out var ts)) return Refuse(Verdict.Unauthorized);
        if (Math.Abs(nowUnix - ts) > replayWindowSeconds) return Refuse(Verdict.Unauthorized);
        var sig = signatureHeader.Trim().ToLowerInvariant();
        if (!IsSha256Hex(sig)) return Refuse(Verdict.Unauthorized);

        string? declared = null;
        if (!string.IsNullOrWhiteSpace(contentShaHeader))
        {
            declared = contentShaHeader.Trim().ToLowerInvariant();
            if (!IsSha256Hex(declared)) return Refuse(Verdict.Unauthorized);
        }

        if (!hasBody || contentLength == 0)
        {
            if (declared is not null && declared != EmptySha256) return Refuse(Verdict.Unauthorized);
            return SignatureMatches(key, method, path, ts, EmptySha256, sig)
                ? new Decision(Verdict.VerifiedNoBody, ts, sig, EmptySha256, 0)
                : Refuse(Verdict.Unauthorized);
        }

        if (contentLength > maxUploadBytes) return Refuse(Verdict.TooLarge);

        // A body of unknown length is never allowed past the small cap, declared hash or not.
        long cap = contentLength < 0 ? SmallBodyMaxBytes : contentLength;

        if (declared is not null)
        {
            return SignatureMatches(key, method, path, ts, declared, sig)
                ? new Decision(Verdict.VerifiedStream, ts, sig, declared, cap)
                : Refuse(Verdict.Unauthorized);
        }

        if (contentLength > SmallBodyMaxBytes) return Refuse(Verdict.ContentShaRequired);
        return new Decision(Verdict.ReadSmallThenVerify, ts, sig, null, cap);
    }

    internal static string Sign(byte[] key, string method, string path, long ts, string bodySha256Hex)
    {
        var canonical = $"{method.ToUpperInvariant()}\n{path}\n{ts}\n{bodySha256Hex.ToLowerInvariant()}";
        return Convert.ToHexString(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    internal static bool SignatureMatches(byte[] key, string method, string path, long ts, string bodySha256Hex, string signatureHex)
    {
        var expected = Sign(key, method, path, ts, bodySha256Hex);
        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(expected),
            Encoding.ASCII.GetBytes(signatureHex.Trim().ToLowerInvariant()));
    }

    internal static bool HashMatches(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(a.ToLowerInvariant()),
            Encoding.ASCII.GetBytes(b.ToLowerInvariant()));

    internal static bool IsSha256Hex(string s)
    {
        if (s.Length != 64) return false;
        foreach (var c in s)
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'))) return false;
        return true;
    }

    /// <summary>
    /// The routes anyone may call. They run on their own request slots, so signed traffic
    /// (or anything pretending to be) can never take the slots players' apps need for the
    /// health probe, the player list and the join-password check.
    /// </summary>
    internal static bool IsPublicRoute(string method, string path)
    {
        method = method.ToUpperInvariant();
        if (method == "OPTIONS") return true;
        if (method == "GET")
            return path is "/api/v1/health" or "/api/v1/players" or "/api/v1/manifest"
                or "/api/v1/chat/motd" or "/api/v1/map/state" or "/map" or "/map/" or "/map/index.html";
        if (method == "POST")
            return path == "/api/v1/" + Lantern.Protocol.JoinPass.Route || path == "/api/v1/join/identity";
        return false;
    }
}

/// <summary>Remembers accepted signatures for the replay window so a captured request cannot be sent twice.</summary>
internal sealed class SignatureReplayCache
{
    private readonly Dictionary<string, long> _seen = new();
    private readonly object _lock = new();
    private readonly int _windowSeconds;

    internal SignatureReplayCache(int windowSeconds) => _windowSeconds = windowSeconds;

    /// <summary>True the first time a signature is offered inside the window; false on a repeat.</summary>
    internal bool TryRecord(string signature, long ts, long nowUnix)
    {
        lock (_lock)
        {
            if (_seen.Count > 0)
            {
                var cutoff = nowUnix - _windowSeconds;
                foreach (var k in _seen.Where(kv => kv.Value < cutoff).Select(kv => kv.Key).ToList())
                    _seen.Remove(k);
            }
            if (_seen.ContainsKey(signature)) return false;
            _seen[signature] = ts;
            return true;
        }
    }

    internal int Count { get { lock (_lock) return _seen.Count; } }
}

internal sealed class HttpBodyTooLargeException : Exception { }

/// <summary>The client sent fewer bytes than its Content-Length promised.</summary>
internal sealed class HttpBodyIncompleteException : Exception { }

internal static class HttpBodyReader
{
    /// <summary>Reads at most <paramref name="cap"/> bytes into memory; more than that throws.</summary>
    internal static async Task<byte[]> ReadSmallAsync(Stream input, long contentLength, long cap, CancellationToken ct)
    {
        if (contentLength > cap) throw new HttpBodyTooLargeException();
        using var ms = new MemoryStream();
        var buf = new byte[16 * 1024];
        long total = 0;
        while (true)
        {
            int n = await input.ReadAsync(buf, ct).ConfigureAwait(false);
            if (n <= 0) break;
            total += n;
            if (total > cap) throw new HttpBodyTooLargeException();
            ms.Write(buf, 0, n);
        }
        if (contentLength >= 0 && total != contentLength) throw new HttpBodyIncompleteException();
        return ms.ToArray();
    }

    /// <summary>
    /// Streams the body into a new file under <paramref name="tempDir"/>, hashing as it goes.
    /// The file is deleted on any failure: too many bytes, too few, a cancelled or broken read.
    /// </summary>
    internal static async Task<(string Path, string Sha256Hex, long Size)> StreamToTempAsync(
        Stream input, long contentLength, long cap, string tempDir, CancellationToken ct)
    {
        Directory.CreateDirectory(tempDir);
        var tempPath = System.IO.Path.Combine(tempDir, $"upload-{Guid.NewGuid():N}.bin");
        long total = 0;
        try
        {
            string sha;
            await using (var fs = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true))
            {
                using var hasher = SHA256.Create();
                var buf = new byte[64 * 1024];
                while (true)
                {
                    int n = await input.ReadAsync(buf, ct).ConfigureAwait(false);
                    if (n <= 0) break;
                    total += n;
                    if (total > cap) throw new HttpBodyTooLargeException();
                    hasher.TransformBlock(buf, 0, n, null, 0);
                    await fs.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                }
                hasher.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                sha = Convert.ToHexString(hasher.Hash!).ToLowerInvariant();
            }
            if (contentLength >= 0 && total != contentLength) throw new HttpBodyIncompleteException();
            return (tempPath, sha, total);
        }
        catch
        {
            try { File.Delete(tempPath); } catch { }
            throw;
        }
    }

    /// <summary>Writes an already-verified small body to a temp file so routes can treat every body the same.</summary>
    internal static async Task<string> WriteTempAsync(byte[] body, string tempDir, CancellationToken ct)
    {
        Directory.CreateDirectory(tempDir);
        var tempPath = System.IO.Path.Combine(tempDir, $"upload-{Guid.NewGuid():N}.bin");
        try
        {
            await File.WriteAllBytesAsync(tempPath, body, ct).ConfigureAwait(false);
            return tempPath;
        }
        catch
        {
            try { File.Delete(tempPath); } catch { }
            throw;
        }
    }

    /// <summary>Removes upload temp files a crash left behind (older than <paramref name="olderThan"/>).</summary>
    internal static int SweepStale(string tempDir, TimeSpan olderThan, DateTime utcNow)
    {
        int removed = 0;
        try
        {
            if (!Directory.Exists(tempDir)) return 0;
            foreach (var f in Directory.EnumerateFiles(tempDir, "upload-*.bin"))
            {
                try
                {
                    if (utcNow - File.GetLastWriteTimeUtc(f) > olderThan) { File.Delete(f); removed++; }
                }
                catch { }
            }
        }
        catch { }
        return removed;
    }
}
