using Lantern.Protocol;
using LanternServer.Services;

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

    [Fact]
    public void OnePlayerIsSingular()
    {
        var now = 5_000_000L;
        var text = RconHostedService.FormatPlayers(new[] { new PlayerSnapshot("u1", "Cara", now - 61_000, now, 0) }, now);
        Assert.StartsWith("1 player online", text);
    }
}
