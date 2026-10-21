using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using LanternServer.Configuration;
using LanternServer.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LanternServer.Tests;

/// <summary>
/// The HTTP API decides who is asking from the headers, before it reads a body.
/// Pure decisions first, then the real service on a loopback port.
/// </summary>
public sealed class HttpAuthGateTests
{
    private const long Now = 1_800_000_000;
    private static readonly byte[] Key = SHA256.HashData(Encoding.UTF8.GetBytes("rcon-secret"));
    private static string Sha(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();
    private static readonly string BigSha = Sha(new byte[200_000]);

    private static HttpAuthGate.Decision Eval(string method, string path, long ts, string? sig, string? sha, bool hasBody, long len, byte[]? key = null) =>
        HttpAuthGate.Evaluate(key ?? Key, method, path, ts.ToString(), sig, sha, hasBody, len, 64L * 1024 * 1024, Now, 300);

    [Fact]
    public void NoAdminPassword_RefusesEverySignedRoute()
    {
        var sig = HttpAuthGate.Sign(Key, "GET", "/api/v1/info", Now, HttpAuthGate.EmptySha256);
        Eval("GET", "/api/v1/info", Now, sig, null, false, 0, Array.Empty<byte>()).Verdict
            .Should().Be(HttpAuthGate.Verdict.Unauthorized);
    }

    [Fact]
    public void MissingStaleOrMalformedHeaders_AreRefused()
    {
        var sig = HttpAuthGate.Sign(Key, "GET", "/api/v1/info", Now, HttpAuthGate.EmptySha256);
        Eval("GET", "/api/v1/info", Now, null, null, false, 0).Verdict.Should().Be(HttpAuthGate.Verdict.Unauthorized);
        Eval("GET", "/api/v1/info", Now - 301, HttpAuthGate.Sign(Key, "GET", "/api/v1/info", Now - 301, HttpAuthGate.EmptySha256), null, false, 0)
            .Verdict.Should().Be(HttpAuthGate.Verdict.Unauthorized);
        Eval("GET", "/api/v1/info", Now, "zz" + sig[2..], null, false, 0).Verdict.Should().Be(HttpAuthGate.Verdict.Unauthorized);
        Eval("GET", "/api/v1/info", Now, sig, "not-a-hash", false, 0).Verdict.Should().Be(HttpAuthGate.Verdict.Unauthorized);
        Eval("GET", "/api/v1/info", Now, sig, null, false, 0).Verdict.Should().Be(HttpAuthGate.Verdict.VerifiedNoBody);
    }

    [Fact]
    public void LargeBodyWithoutDeclaredHash_IsRefusedUnread_EvenWithAPlausibleSignature()
    {
        var sig = HttpAuthGate.Sign(Key, "POST", "/api/v1/snapshots", Now, BigSha);
        Eval("POST", "/api/v1/snapshots", Now, sig, null, true, 200_000).Verdict
            .Should().Be(HttpAuthGate.Verdict.ContentShaRequired);
    }

    [Fact]
    public void LargeBodyWithDeclaredHash_IsCheckedBeforeTheBody()
    {
        var good = HttpAuthGate.Sign(Key, "POST", "/api/v1/snapshots", Now, BigSha);
        var d = Eval("POST", "/api/v1/snapshots", Now, good, BigSha, true, 200_000);
        d.Verdict.Should().Be(HttpAuthGate.Verdict.VerifiedStream);
        d.ReadCap.Should().Be(200_000);
        d.DeclaredSha.Should().Be(BigSha);

        var forged = new string('a', 64);
        Eval("POST", "/api/v1/snapshots", Now, forged, BigSha, true, 200_000).Verdict
            .Should().Be(HttpAuthGate.Verdict.Unauthorized);
        // a signature for another path does not open this one
        var otherPath = HttpAuthGate.Sign(Key, "POST", "/api/v1/snapshots/import-restore", Now, BigSha);
        Eval("POST", "/api/v1/snapshots", Now, otherPath, BigSha, true, 200_000).Verdict
            .Should().Be(HttpAuthGate.Verdict.Unauthorized);
    }

    [Fact]
    public void OverTheUploadCap_IsRefusedUnread()
    {
        var sig = HttpAuthGate.Sign(Key, "POST", "/api/v1/snapshots", Now, BigSha);
        Eval("POST", "/api/v1/snapshots", Now, sig, BigSha, true, 65L * 1024 * 1024).Verdict
            .Should().Be(HttpAuthGate.Verdict.TooLarge);
    }

    [Fact]
    public void SmallOrUnknownLengthBodiesWithoutHash_AreReadCappedSmall()
    {
        var sig = new string('b', 64);
        var small = Eval("POST", "/api/v1/chat/say", Now, sig, null, true, 100);
        small.Verdict.Should().Be(HttpAuthGate.Verdict.ReadSmallThenVerify);
        small.ReadCap.Should().Be(100);

        var chunked = Eval("POST", "/api/v1/chat/say", Now, sig, null, true, -1);
        chunked.Verdict.Should().Be(HttpAuthGate.Verdict.ReadSmallThenVerify);
        chunked.ReadCap.Should().Be(HttpAuthGate.SmallBodyMaxBytes);

        // a declared hash never lifts the small cap for a body of unknown length
        var sha = Sha(new byte[10]);
        var chunkedDeclared = Eval("POST", "/api/v1/snapshots", Now, HttpAuthGate.Sign(Key, "POST", "/api/v1/snapshots", Now, sha), sha, true, -1);
        chunkedDeclared.Verdict.Should().Be(HttpAuthGate.Verdict.VerifiedStream);
        chunkedDeclared.ReadCap.Should().Be(HttpAuthGate.SmallBodyMaxBytes);
    }

    [Fact]
    public async Task ReadSmall_RefusesMoreThanTheCap_AndShortBodies()
    {
        await FluentActions.Awaiting(() => HttpBodyReader.ReadSmallAsync(new MemoryStream(new byte[70_000]), -1, HttpAuthGate.SmallBodyMaxBytes, default))
            .Should().ThrowAsync<HttpBodyTooLargeException>();
        await FluentActions.Awaiting(() => HttpBodyReader.ReadSmallAsync(new MemoryStream(new byte[10]), 20, 20, default))
            .Should().ThrowAsync<HttpBodyIncompleteException>();
        (await HttpBodyReader.ReadSmallAsync(new MemoryStream(new byte[10]), 10, 10, default)).Length.Should().Be(10);
    }

    [Fact]
    public async Task StreamToTemp_LeavesNoFileBehindOnOverflowOrShortBody()
    {
        using var temp = new TempDir("lantern-http-stream-");
        await FluentActions.Awaiting(() => HttpBodyReader.StreamToTempAsync(new MemoryStream(new byte[300]), 200, 200, temp.Path, default))
            .Should().ThrowAsync<HttpBodyTooLargeException>();
        await FluentActions.Awaiting(() => HttpBodyReader.StreamToTempAsync(new MemoryStream(new byte[100]), 200, 200, temp.Path, default))
            .Should().ThrowAsync<HttpBodyIncompleteException>();
        Directory.EnumerateFiles(temp.Path).Should().BeEmpty();

        var body = Encoding.UTF8.GetBytes("hello world");
        var ok = await HttpBodyReader.StreamToTempAsync(new MemoryStream(body), body.Length, body.Length, temp.Path, default);
        ok.Sha256Hex.Should().Be(Sha(body));
        File.ReadAllBytes(ok.Path).Should().Equal(body);
    }

    [Fact]
    public void SweepStale_RemovesOnlyOldUploadFiles()
    {
        using var temp = new TempDir("lantern-http-sweep-");
        var old = Path.Combine(temp.Path, "upload-old.bin");
        var fresh = Path.Combine(temp.Path, "upload-new.bin");
        var other = Path.Combine(temp.Path, "keep.txt");
        File.WriteAllText(old, "x"); File.WriteAllText(fresh, "x"); File.WriteAllText(other, "x");
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddHours(-7));
        File.SetLastWriteTimeUtc(other, DateTime.UtcNow.AddHours(-7));
        HttpBodyReader.SweepStale(temp.Path, TimeSpan.FromHours(6), DateTime.UtcNow).Should().Be(1);
        File.Exists(old).Should().BeFalse();
        File.Exists(fresh).Should().BeTrue();
        File.Exists(other).Should().BeTrue();
    }

    [Fact]
    public void ReplayCache_RefusesARepeatAndForgetsAfterTheWindow()
    {
        var cache = new SignatureReplayCache(300);
        cache.TryRecord("s1", Now, Now).Should().BeTrue();
        cache.TryRecord("s1", Now, Now + 10).Should().BeFalse();
        cache.TryRecord("s2", Now + 400, Now + 400).Should().BeTrue();
        cache.Count.Should().Be(1); // s1 pruned
    }

    [Fact]
    public void PublicRoutes_AreExactlyThePlayersRoutes()
    {
        HttpAuthGate.IsPublicRoute("GET", "/api/v1/health").Should().BeTrue();
        HttpAuthGate.IsPublicRoute("GET", "/api/v1/players").Should().BeTrue();
        HttpAuthGate.IsPublicRoute("POST", "/api/v1/" + Lantern.Protocol.JoinPass.Route).Should().BeTrue();
        HttpAuthGate.IsPublicRoute("POST", "/api/v1/join/identity").Should().BeTrue();
        HttpAuthGate.IsPublicRoute("GET", "/api/v1/chat/motd").Should().BeTrue();
        HttpAuthGate.IsPublicRoute("GET", "/api/v1/chat/recent").Should().BeFalse();
        HttpAuthGate.IsPublicRoute("POST", "/api/v1/chat/player").Should().BeFalse();
        HttpAuthGate.IsPublicRoute("POST", "/api/v1/snapshots").Should().BeFalse();
        HttpAuthGate.IsPublicRoute("POST", "/api/v1/snapshots/import-restore").Should().BeFalse();
        HttpAuthGate.IsPublicRoute("GET", "/api/v1/info").Should().BeFalse();
    }

    // ---------------- the real service on a loopback port ----------------

    [Fact]
    public async Task Live_UnsignedBigUpload_IsAnswered401WithoutWritingAnything()
    {
        await using var h = await LiveHarness.StartAsync();
        var before = h.UploadTempFiles();
        var junkSig = new string('c', 64);
        var head = $"POST /api/v1/snapshots/import-restore HTTP/1.1\r\nHost: localhost\r\nContent-Length: 50000000\r\n" +
                   $"Content-Type: application/zip\r\nX-Lantern-Timestamp: {DateTimeOffset.UtcNow.ToUnixTimeSeconds()}\r\n" +
                   $"X-Lantern-Signature: {junkSig}\r\nX-Lantern-Content-Sha256: {BigSha}\r\n\r\n";
        var status = await h.RawAsync(head, new byte[4096], holdOpen: true);
        status.Should().Be(401);
        h.UploadTempFiles().Should().BeEquivalentTo(before);

        // and without the hash header at all: refused unread too
        var head2 = head.Replace($"X-Lantern-Content-Sha256: {BigSha}\r\n", "");
        (await h.RawAsync(head2, new byte[4096], holdOpen: true)).Should().Be(401);
        h.UploadTempFiles().Should().BeEquivalentTo(before);
    }

    [Fact]
    public async Task Live_SignedRequests_WorkWithAndWithoutTheHashHeader_AndAReplayIsRefused()
    {
        await using var h = await LiveHarness.StartAsync();
        // (two different bodies: an identical request in the same second is a replay)
        (await h.SignedAsync("POST", "/api/v1/chat/say", Encoding.UTF8.GetBytes("{\"msg\":\"hello\"}"), sendSha: false)).Should().Be(200);
        (await h.SignedAsync("POST", "/api/v1/chat/say", Encoding.UTF8.GetBytes("{\"msg\":\"hello again\"}"), sendSha: true)).Should().Be(200);

        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        (await h.SignedAsync("GET", "/api/v1/info", Array.Empty<byte>(), sendSha: false, ts: ts)).Should().Be(200);
        (await h.SignedAsync("GET", "/api/v1/info", Array.Empty<byte>(), sendSha: false, ts: ts)).Should().Be(401);
    }

    [Fact]
    public async Task Live_SignedUpload_IsStored_AndABodyThatDoesNotMatchItsHashIsRefusedAndRemoved()
    {
        await using var h = await LiveHarness.StartAsync();
        var zip = new byte[200_000];
        RandomNumberGenerator.Fill(zip);
        (await h.SignedAsync("POST", "/api/v1/snapshots", zip, sendSha: true)).Should().Be(200);
        Directory.EnumerateFiles(h.SaveDir, "*.zip").Should().HaveCount(1);

        // an old app (no hash header) cannot push a big body any more
        (await h.SignedAsync("POST", "/api/v1/snapshots", zip, sendSha: false)).Should().Be(401);

        // signature over one hash, a different body: refused, nothing kept
        var before = h.UploadTempFiles();
        var other = new byte[200_000];
        var declared = Sha(other);
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var sig = HttpAuthGate.Sign(h.Key, "POST", "/api/v1/snapshots", ts, declared);
        var head = $"POST /api/v1/snapshots HTTP/1.1\r\nHost: localhost\r\nContent-Length: {zip.Length}\r\n" +
                   $"X-Lantern-Timestamp: {ts}\r\nX-Lantern-Signature: {sig}\r\nX-Lantern-Content-Sha256: {declared}\r\n\r\n";
        (await h.RawAsync(head, zip, holdOpen: false)).Should().Be(401);
        h.UploadTempFiles().Should().BeEquivalentTo(before);
        Directory.EnumerateFiles(h.SaveDir, "*.zip").Should().HaveCount(1);
    }

    [Fact]
    public async Task Live_PublicRoutesStillAnswer_WhileEverySignedSlotIsHeld()
    {
        await using var h = await LiveHarness.StartAsync();
        var held = new List<TcpClient>();
        try
        {
            // six slow signed-looking requests: headers sent, body never finished
            for (int i = 0; i < 6; i++)
            {
                var c = new TcpClient();
                await c.ConnectAsync(IPAddress.Loopback, h.Port);
                var head = $"POST /api/v1/chat/say HTTP/1.1\r\nHost: localhost\r\nContent-Length: 1000\r\n" +
                           $"X-Lantern-Timestamp: {DateTimeOffset.UtcNow.ToUnixTimeSeconds()}\r\nX-Lantern-Signature: {new string('d', 64)}\r\n\r\nabc";
                var b = Encoding.ASCII.GetBytes(head);
                await c.GetStream().WriteAsync(b);
                held.Add(c);
            }
            await Task.Delay(500);
            (await h.SignedAsync("GET", "/api/v1/info", Array.Empty<byte>(), sendSha: false)).Should().Be(503);
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var players = await http.GetAsync($"http://127.0.0.1:{h.Port}/api/v1/players");
            ((int)players.StatusCode).Should().Be(200);
        }
        finally
        {
            foreach (var c in held) c.Dispose();
        }
    }

    [Fact]
    public async Task Live_ChatPlayerIsGone_AndChatRecentNeedsASignature()
    {
        await using var h = await LiveHarness.StartAsync();
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var post = await http.PostAsync($"http://127.0.0.1:{h.Port}/api/v1/chat/player",
            new StringContent("{\"sender\":\"Admin\",\"msg\":\"hi\"}", Encoding.UTF8, "application/json"));
        ((int)post.StatusCode).Should().Be(401);
        var recent = await http.GetAsync($"http://127.0.0.1:{h.Port}/api/v1/chat/recent");
        ((int)recent.StatusCode).Should().Be(401);
        (await h.SignedAsync("GET", "/api/v1/chat/recent", Array.Empty<byte>(), sendSha: true)).Should().Be(200);
        var motd = await http.GetAsync($"http://127.0.0.1:{h.Port}/api/v1/chat/motd");
        ((int)motd.StatusCode).Should().Be(200);
    }

    private sealed class LiveHarness : IAsyncDisposable
    {
        private readonly TempDir _temp;
        private readonly LanternHttpService _service;
        public int Port { get; }
        public byte[] Key { get; }
        public string SaveDir { get; }

        private LiveHarness(TempDir temp, LanternHttpService service, int port, byte[] key, string saveDir)
        {
            _temp = temp; _service = service; Port = port; Key = key; SaveDir = saveDir;
        }

        public static async Task<LiveHarness> StartAsync()
        {
            var temp = new TempDir("lantern-http-live-");
            var port = FreePort();
            var opts = new LanternServerOptions
            {
                HttpPort = port,
                RconPassword = "live-test-secret",
                HmacKeyPath = Path.Combine(temp.Path, "data", "hmac.key"),
                SaveDir = Path.Combine(temp.Path, "saves"),
                InstanceId = "live-test",
                MaxUploadBytes = 64L * 1024 * 1024,
            };
            Directory.CreateDirectory(Path.Combine(temp.Path, "data"));
            Directory.CreateDirectory(opts.SaveDir);
            var o = Options.Create(opts);
            var pipe = new PipeServerState(NullLogger<PipeServerState>.Instance);
            var saves = new SaveOrchestratorService(NullLogger<SaveOrchestratorService>.Instance, o, pipe,
                new G2RestartCoordinator(NullLogger<G2RestartCoordinator>.Instance, o));
            var service = new LanternHttpService(
                NullLogger<LanternHttpService>.Instance, o, new ConfigurationBuilder().Build(), saves, pipe,
                new InstanceIdentityProvider(o), new ChatService(NullLogger<ChatService>.Instance, new FixedMonitor(opts)),
                new IdentityChoiceService(NullLogger<IdentityChoiceService>.Instance, o));
            await service.StartAsync(CancellationToken.None);
            var h = new LiveHarness(temp, service, port, SHA256.HashData(Encoding.UTF8.GetBytes(opts.RconPassword)), opts.SaveDir);
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            for (int i = 0; i < 50; i++)
            {
                try { await http.GetAsync($"http://127.0.0.1:{port}/api/v1/players"); return h; }
                catch { await Task.Delay(100); }
            }
            throw new InvalidOperationException("listener never came up");
        }

        public string[] UploadTempFiles()
        {
            var dir = Path.Combine(Path.GetTempPath(), "lantern-uploads");
            return Directory.Exists(dir) ? Directory.GetFiles(dir) : Array.Empty<string>();
        }

        public async Task<int> SignedAsync(string method, string path, byte[] body, bool sendSha, long? ts = null)
        {
            var t = ts ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var sha = Sha(body);
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            using var req = new HttpRequestMessage(new HttpMethod(method), $"http://127.0.0.1:{Port}{path}");
            req.Headers.Add("X-Lantern-Timestamp", t.ToString());
            req.Headers.Add("X-Lantern-Signature", HttpAuthGate.Sign(Key, method, path.Split('?')[0], t, sha));
            if (sendSha) req.Headers.Add("X-Lantern-Content-Sha256", sha);
            if (body.Length > 0) req.Content = new ByteArrayContent(body);
            using var resp = await http.SendAsync(req);
            return (int)resp.StatusCode;
        }

        /// <summary>Sends raw headers plus a partial body and returns the status line's code.</summary>
        public async Task<int> RawAsync(string head, byte[] bodyPart, bool holdOpen)
        {
            using var c = new TcpClient();
            await c.ConnectAsync(IPAddress.Loopback, Port);
            var s = c.GetStream();
            await s.WriteAsync(Encoding.ASCII.GetBytes(head));
            try { await s.WriteAsync(bodyPart); } catch (IOException) { }
            if (!holdOpen) await s.FlushAsync();
            var buf = new byte[512];
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            int n = await s.ReadAsync(buf, cts.Token);
            var line = Encoding.ASCII.GetString(buf, 0, n);
            var parts = line.Split(' ');
            return int.Parse(parts[1]);
        }

        public async ValueTask DisposeAsync()
        {
            try { await _service.StopAsync(CancellationToken.None); } catch { }
            _service.Dispose();
            _temp.Dispose();
        }

        private static int FreePort()
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            var p = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return p;
        }
    }

    private sealed class FixedMonitor : IOptionsMonitor<LanternServerOptions>
    {
        public FixedMonitor(LanternServerOptions v) => CurrentValue = v;
        public LanternServerOptions CurrentValue { get; }
        public LanternServerOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<LanternServerOptions, string?> listener) => null;
    }
}
