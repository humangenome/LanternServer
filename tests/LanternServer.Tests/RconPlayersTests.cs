using Lantern.Protocol;
using LanternServer.Configuration;
using LanternServer.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LanternServer.Tests;

public sealed class RconPlayersTests
{
    [Fact]
    public void EmptyServerSaysNoPlayers()
    {
        Assert.Equal("no players online", RconHostedService.FormatPlayers(Array.Empty<PlayerSnapshot>(), 1_000_000));
    }

    [Fact]
    public void ListsEachPlayerByNameWithMinutesOnline()
    {
        var now = 10_000_000L;
        var players = new[]
        {
            new PlayerSnapshot("u1", "Alice", now - 125_000, now, 40),
            new PlayerSnapshot("u2", "Bob\r\nadmin", now - 30_000, now, 50),
            new PlayerSnapshot("u3", "  ", 0, now, 0),
        };
        var text = RconHostedService.FormatPlayers(players, now);
        var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
        Assert.Equal("3 players online", lines[0]);
        Assert.Equal("  Alice  2 min", lines[1]);
        Assert.Equal("  Bobadmin  0 min", lines[2]);
        Assert.Equal("  (no name yet)  0 min", lines[3]);
        Assert.Equal(4, lines.Length);
        Assert.DoesNotContain("Phase 2", text);
    }

    // Nothing in Lantern shows chat or a message of the day to players (the chat plane came over
    // from Beacon). The console must not offer them or answer "chat ok" for a message nobody sees.
    [Fact]
    public async Task HelpDoesNotOfferUndeliveredMessages()
    {
        var reply = await CreateCommandService().ExecuteAsync("help");
        Assert.DoesNotContain("say", reply);
        Assert.DoesNotContain("announce", reply);
        Assert.DoesNotContain("motd", reply);
        Assert.Contains("players", reply);
        Assert.Contains("save snapshot", reply);
    }

    [Theory]
    [InlineData("say hello", "nothing was sent")]
    [InlineData("ANNOUNCE hello", "nothing was sent")]
    [InlineData("motd hello", "never see")]
    [InlineData("motd", "never see")]
    public async Task UnsupportedMessageCommandsNeverReportDelivery(string command, string expected)
    {
        var reply = await CreateCommandService().ExecuteAsync(command);
        Assert.Contains(expected, reply);
        Assert.DoesNotContain("ok", reply);
    }

    private static RconHostedService CreateCommandService() => new(
        NullLogger<RconHostedService>.Instance,
        Options.Create(new LanternServerOptions()),
        new PipeServerState(NullLogger<PipeServerState>.Instance),
        null!); // These command paths must not touch the save service.

    [Fact]
    public void OnePlayerIsSingular()
    {
        var now = 5_000_000L;
        var text = RconHostedService.FormatPlayers(new[] { new PlayerSnapshot("u1", "Cara", now - 61_000, now, 0) }, now);
        Assert.StartsWith("1 player online", text);
    }
}
