using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Lantern.Protocol;
using LanternServer.Configuration;
using LanternServer.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LanternServer.Tests;

public sealed class JoinPassTests
{
    private const long Now = 1_800_000_000;
    private const string Nonce = "0123456789abcdef0123456789abcdef";

    [Fact]
    public void RightPassword_HandsBackATokenAndWritesAShortLivedPassForIt()
    {
        using var temp = new TempDir("lantern-pass-tests-");
        var service = Create(temp.Path, "correct horse");

        var result = service.AcceptPass(Body(Now, Nonce), Proof("correct horse", Now, Nonce),
            IPAddress.Parse("203.0.113.9"), Now);

        result.Ok.Should().BeTrue();
        result.OpenServer.Should().BeFalse();
        result.ExpiresInSeconds.Should().Be(JoinPass.LifetimeSeconds);
        result.Token.Should().MatchRegex("^[0-9a-f]{32}$");
        var marker = File.ReadAllText(Path.Combine(temp.Path, $"join-pass-{result.Token}.ok"));
        marker.Should().Be(
            "version=1\n" +
            $"accepted_at={Now}\n" +
            $"expires_at={Now + JoinPass.LifetimeSeconds}\n" +
            "source=203_0_113_9\n");
    }

    [Fact]
    public void WrongPassword_Is401AndWritesNothing()
    {
        using var temp = new TempDir("lantern-pass-tests-");
        var service = Create(temp.Path, "correct horse");

        var result = service.AcceptPass(Body(Now, Nonce), Proof("wrong horse", Now, Nonce),
            IPAddress.Parse("203.0.113.9"), Now);

        result.StatusCode.Should().Be(401);
        Directory.EnumerateFiles(temp.Path).Should().BeEmpty();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-hex")]
    [InlineData("ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789")]
    public void MissingOrMalformedProof_Is401(string? proof)
    {
        using var temp = new TempDir("lantern-pass-tests-");
        var service = Create(temp.Path, "correct horse");

        service.AcceptPass(Body(Now, Nonce), proof, IPAddress.Loopback, Now).StatusCode.Should().Be(401);
        Directory.EnumerateFiles(temp.Path).Should().BeEmpty();
    }

    [Fact]
    public void OpenServer_AnswersOkAndRecordsNothing()
    {
        using var temp = new TempDir("lantern-pass-tests-");
        var service = Create(temp.Path, "");

        var result = service.AcceptPass(Body(Now, Nonce), null, IPAddress.Loopback, Now);

        result.Ok.Should().BeTrue();
        result.OpenServer.Should().BeTrue();
        Directory.EnumerateFiles(temp.Path).Should().BeEmpty();
    }

    [Fact]
    public void ReplayedNonce_Is409()
    {
        using var temp = new TempDir("lantern-pass-tests-");
        var service = Create(temp.Path, "pw");
        var body = Body(Now, Nonce);
        var proof = Proof("pw", Now, Nonce);

        service.AcceptPass(body, proof, IPAddress.Loopback, Now).Ok.Should().BeTrue();
        service.AcceptPass(body, proof, IPAddress.Loopback, Now + 5).StatusCode.Should().Be(409);
    }

    [Fact]
    public void StaleTimestamp_Is400()
    {
        using var temp = new TempDir("lantern-pass-tests-");
        var service = Create(temp.Path, "pw");

        service.AcceptPass(Body(Now, Nonce), Proof("pw", Now, Nonce), IPAddress.Loopback,
                Now + JoinPass.MaxClockSkewSeconds + 1)
            .Error.Should().Be("stale request");
    }

    [Fact]
    public void EachRefresh_GetsItsOwnToken()
    {
        using var temp = new TempDir("lantern-pass-tests-");
        var service = Create(temp.Path, "pw");
        const string second = "fedcba9876543210fedcba9876543210";

        var first = service.AcceptPass(Body(Now, Nonce), Proof("pw", Now, Nonce), IPAddress.Loopback, Now);
        var next = service.AcceptPass(Body(Now + 60, second), Proof("pw", Now + 60, second), IPAddress.Loopback, Now + 60);

        first.Ok.Should().BeTrue();
        next.Ok.Should().BeTrue();
        next.Token.Should().NotBe(first.Token);
        File.ReadAllText(Path.Combine(temp.Path, $"join-pass-{next.Token}.ok"))
            .Should().Contain($"expires_at={Now + 60 + JoinPass.LifetimeSeconds}\n");
    }

    [Fact]
    public void TooManyWrongPasswords_FromOneAddress_AreRefusedForAMinute_OtherAddressesUnaffected()
    {
        using var temp = new TempDir("lantern-pass-tests-");
        var service = Create(temp.Path, "pw");
        var guesser = IPAddress.Parse("198.51.100.7");

        for (var i = 0; i < IdentityChoiceService.MaxFailedProofsPerWindow; i++)
        {
            var nonce = i.ToString("x32");
            service.AcceptPass(Body(Now, nonce), Proof("guess" + i, Now, nonce), guesser, Now)
                .StatusCode.Should().Be(401);
        }

        // Even the right password is refused from that address inside the window...
        const string rightNonce = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        service.AcceptPass(Body(Now, rightNonce), Proof("pw", Now, rightNonce), guesser, Now)
            .StatusCode.Should().Be(429);
        // ...another address still gets in...
        const string otherNonce = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        service.AcceptPass(Body(Now, otherNonce), Proof("pw", Now, otherNonce), IPAddress.Parse("203.0.113.9"), Now)
            .Ok.Should().BeTrue();
        // ...and the guesser's own right password works once the window has passed.
        var later = Now + IdentityChoiceService.FailedProofWindowSeconds;
        const string laterNonce = "cccccccccccccccccccccccccccccccc";
        service.AcceptPass(Body(later, laterNonce), Proof("pw", later, laterNonce), guesser, later)
            .Ok.Should().BeTrue();
    }

    [Fact]
    public void PassProof_CannotBeReplayedAsACharacterChoice()
    {
        // Same password, same timestamp and nonce: the signed lines differ, so a captured
        // pass proof is useless on the character-choice route and the other way round.
        var choice = new IdentityChoiceRequest
        {
            Version = 1, Timestamp = Now, Nonce = Nonce, Identity = 0, NameHex = "50",
        };
        JoinPass.Canonical(1, Now, Nonce).Should().NotBe(IdentityChoiceService.Canonicalize(choice));
    }

    [Fact]
    public void CharacterChoiceMarker_SaysWhetherThePasswordWasChecked()
    {
        using var temp = new TempDir("lantern-pass-tests-");
        var request = new IdentityChoiceRequest
        {
            Version = 1, Timestamp = Now, Nonce = Nonce, Identity = 2,
            NameHex = Convert.ToHexString(Encoding.UTF8.GetBytes("Player")).ToLowerInvariant(),
        };
        var body = JsonSerializer.Serialize(request, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        });
        var proof = Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes("pw"),
            Encoding.UTF8.GetBytes(IdentityChoiceService.Canonicalize(request)))).ToLowerInvariant();

        Create(temp.Path, "pw").Accept(body, proof, IPAddress.Loopback, Now).Ok.Should().BeTrue();
        File.ReadAllText(Path.Combine(temp.Path, "identity-choice-127_0_0_1.pending"))
            .Should().EndWith("source=127_0_0_1\nauth=hmac\n");

        using var open = new TempDir("lantern-pass-tests-");
        Create(open.Path, "").Accept(body, null, IPAddress.Loopback, Now).Ok.Should().BeTrue();
        File.ReadAllText(Path.Combine(open.Path, "identity-choice-127_0_0_1.pending"))
            .Should().EndWith("auth=open\n");
    }

    [Fact]
    public void Sweep_RemovesExpiredPassMarkers()
    {
        using var temp = new TempDir("lantern-pass-tests-");
        var stale = Path.Combine(temp.Path, "join-pass-00000000000000000000000000000001.ok");
        File.WriteAllText(stale, "stale");
        File.SetLastWriteTimeUtc(stale, DateTimeOffset.FromUnixTimeSeconds(Now - 601).UtcDateTime);
        var service = Create(temp.Path, "pw");

        var result = service.AcceptPass(Body(Now, Nonce), Proof("pw", Now, Nonce), IPAddress.Loopback, Now);

        result.Ok.Should().BeTrue();
        File.Exists(stale).Should().BeFalse();
        File.Exists(Path.Combine(temp.Path, $"join-pass-{result.Token}.ok")).Should().BeTrue();
    }

    private static IdentityChoiceService Create(string directory, string password) =>
        new(NullLogger<IdentityChoiceService>.Instance, Options.Create(new LanternServerOptions
        {
            IdentityChoiceDirectory = directory,
            LanternAuthPassword = password,
        }));

    private static string Body(long timestamp, string nonce) =>
        $"{{\"version\":1,\"timestamp\":{timestamp},\"nonce\":\"{nonce}\"}}";

    private static string Proof(string password, long timestamp, string nonce) =>
        JoinPass.Proof(password, JoinPass.Canonical(1, timestamp, nonce));
}
