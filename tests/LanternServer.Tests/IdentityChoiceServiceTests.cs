using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using LanternServer.Configuration;
using LanternServer.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LanternServer.Tests;

public sealed class IdentityChoiceServiceTests
{
    [Fact]
    public void Accept_WithValidPasswordProof_WritesAnInstanceScopedMarker()
    {
        using var temp = new TempDir("lantern-choice-tests-");
        const long now = 1_800_000_000;
        var service = Create(temp.Path, "correct horse");
        var request = Request(now, identity: 3, name: "HumanGenome");
        var proof = Proof(request, "correct horse");

        var result = service.Accept(Body(request), proof, IPAddress.Parse("203.0.113.9"), now);

        result.Ok.Should().BeTrue();
        var marker = Path.Combine(temp.Path, "identity-choice-203_0_113_9.pending");
        File.ReadAllText(marker).Should().Contain("identity=3\n").And.Contain("name_hex=48756d616e47656e6f6d65\n");
    }

    [Fact]
    public void Accept_WithWrongPasswordProof_FailsWithoutWriting()
    {
        using var temp = new TempDir("lantern-choice-tests-");
        const long now = 1_800_000_000;
        var service = Create(temp.Path, "correct horse");
        var request = Request(now, identity: 1, name: "Player");

        var result = service.Accept(Body(request), Proof(request, "wrong horse"), IPAddress.Loopback, now);

        result.StatusCode.Should().Be(401);
        Directory.EnumerateFiles(temp.Path).Should().BeEmpty();
    }

    [Fact]
    public void Accept_RejectsReplayedNonce()
    {
        using var temp = new TempDir("lantern-choice-tests-");
        const long now = 1_800_000_000;
        var service = Create(temp.Path, "");
        var request = Request(now, identity: 2, name: "Player");
        var body = Body(request);

        service.Accept(body, null, IPAddress.Loopback, now).Ok.Should().BeTrue();
        service.Accept(body, null, IPAddress.Loopback, now).StatusCode.Should().Be(409);
    }

    [Fact]
    public void Accept_RateLimitsDistinctRequestsFromTheSameSource()
    {
        using var temp = new TempDir("lantern-choice-tests-");
        const long now = 1_800_000_000;
        var service = Create(temp.Path, "");
        var first = Request(now, identity: 2, name: "Player");
        var second = Request(now, identity: 3, name: "Player");
        second.Nonce = "fedcba9876543210fedcba9876543210";

        service.Accept(Body(first), null, IPAddress.Loopback, now).Ok.Should().BeTrue();
        service.Accept(Body(second), null, IPAddress.Loopback, now).StatusCode.Should().Be(429);
    }

    [Theory]
    [InlineData(-1, "506c61796572")]
    [InlineData(5, "506c61796572")]
    [InlineData(1, "")]
    [InlineData(1, "ABCDEF")]
    [InlineData(1, "0d0a")]
    public void Accept_RejectsInvalidIdentityOrName(int identity, string nameHex)
    {
        using var temp = new TempDir("lantern-choice-tests-");
        const long now = 1_800_000_000;
        var service = Create(temp.Path, "");
        var request = Request(now, identity, "Player");
        request.NameHex = nameHex;

        service.Accept(Body(request), null, IPAddress.Loopback, now).Ok.Should().BeFalse();
    }

    [Fact]
    public void Accept_RejectsStaleTimestamp()
    {
        using var temp = new TempDir("lantern-choice-tests-");
        var service = Create(temp.Path, "");
        var request = Request(1_800_000_000, identity: 0, name: "Player");

        service.Accept(Body(request), null, IPAddress.Loopback, 1_800_000_301)
            .Ok.Should().BeFalse();
    }

    [Fact]
    public void Accept_SweepsExpiredMarkersWithoutRemovingFreshOnes()
    {
        using var temp = new TempDir("lantern-choice-tests-");
        const long now = 1_800_000_000;
        var stale = Path.Combine(temp.Path, "identity-choice-stale.pending");
        var fresh = Path.Combine(temp.Path, "identity-choice-fresh.pending");
        File.WriteAllText(stale, "stale");
        File.WriteAllText(fresh, "fresh");
        File.SetLastWriteTimeUtc(stale, DateTimeOffset.FromUnixTimeSeconds(now - 601).UtcDateTime);
        File.SetLastWriteTimeUtc(fresh, DateTimeOffset.FromUnixTimeSeconds(now - 599).UtcDateTime);
        var service = Create(temp.Path, "");
        var request = Request(now, identity: 4, name: "Player");

        service.Accept(Body(request), null, IPAddress.Loopback, now).Ok.Should().BeTrue();

        File.Exists(stale).Should().BeFalse();
        File.Exists(fresh).Should().BeTrue();
    }

    private static IdentityChoiceService Create(string directory, string password) =>
        new(NullLogger<IdentityChoiceService>.Instance, Options.Create(new LanternServerOptions
        {
            IdentityChoiceDirectory = directory,
            LanternAuthPassword = password,
        }));

    private static IdentityChoiceRequest Request(long timestamp, int identity, string name) => new()
    {
        Version = IdentityChoiceService.RequestVersion,
        Timestamp = timestamp,
        Nonce = "0123456789abcdef0123456789abcdef",
        Identity = identity,
        NameHex = Convert.ToHexString(Encoding.UTF8.GetBytes(name)).ToLowerInvariant(),
    };

    private static string Proof(IdentityChoiceRequest request, string password) =>
        Convert.ToHexString(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(password),
            Encoding.UTF8.GetBytes(IdentityChoiceService.Canonicalize(request)))).ToLowerInvariant();

    private static string Body(IdentityChoiceRequest request) =>
        JsonSerializer.Serialize(request, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        });
}
