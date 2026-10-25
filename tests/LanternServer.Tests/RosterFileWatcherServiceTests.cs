using LanternServer.Services;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;

namespace LanternServer.Tests;

public sealed class RosterFileWatcherServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "lantern-roster-tests-" + Guid.NewGuid().ToString("N"));
    private int _writeNumber;

    [Fact]
    public void DedicatedHeartbeatRemainsFreshWhenRosterIsStale()
    {
        Directory.CreateDirectory(_directory);
        var rosterPath = Path.Combine(_directory, "roster.json");
        var heartbeatPath = Path.Combine(_directory, "runtime.heartbeat");
        File.WriteAllText(rosterPath, "{\"unix_ms\":1,\"players\":[]}");
        File.SetLastWriteTimeUtc(rosterPath, DateTime.UtcNow.AddMinutes(-5));
        File.WriteAllText(heartbeatPath, "1");

        var state = new PipeServerState(NullLogger<PipeServerState>.Instance);
        var watcher = new RosterFileWatcherService(
            NullLogger<RosterFileWatcherService>.Instance,
            state,
            _directory);

        watcher.PollFiles();

        Assert.True(state.HasFreshRoster(TimeSpan.FromSeconds(60)));
        Assert.Empty(state.Players);
    }

    [Fact]
    public void RosterTimestampIsTheFallbackForOlderHostMods()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(
            Path.Combine(_directory, "roster.json"),
            "{\"unix_ms\":1,\"players\":[]}");

        var state = new PipeServerState(NullLogger<PipeServerState>.Instance);
        var watcher = new RosterFileWatcherService(
            NullLogger<RosterFileWatcherService>.Instance,
            state,
            _directory);

        watcher.PollFiles();

        Assert.True(state.HasFreshRoster(TimeSpan.FromSeconds(60)));
    }

    [Fact]
    public void RepeatedRosterPollsRetainSessionStartAndRefreshOtherPlayerData()
    {
        var (watcher, state) = CreateWatcher();
        const long joined = 1_700_000_000_000;
        WriteRoster(joined, ("player-one", "Alice", joined));
        watcher.PollFiles();

        WriteRoster(joined + 125_000, ("player-one", "Alice renamed", joined + 125_000));
        watcher.PollFiles();

        var player = Assert.Single(state.Players);
        Assert.Equal(joined, player.ConnectedAtUnixMs);
        Assert.Equal(joined + 125_000, player.LastPacketUnixMs);
        Assert.Equal("Alice renamed", player.DisplayName);
        Assert.Contains("2 min", RconHostedService.FormatPlayers(state.Players, joined + 125_000));
    }

    [Fact]
    public void LeavingAndRejoiningStartsANewSessionWithoutResettingOtherPlayers()
    {
        var (watcher, state) = CreateWatcher();
        const long joined = 1_700_000_000_000;
        WriteRoster(joined, ("one", "Alice", joined), ("two", "Bob", joined));
        watcher.PollFiles();
        WriteRoster(joined + 60_000, ("two", "Bob", joined + 60_000));
        watcher.PollFiles();
        WriteRoster(joined + 120_000, ("one", "Alice", joined + 120_000), ("two", "Bob", joined + 120_000));
        watcher.PollFiles();

        Assert.Equal(joined + 120_000, state.Players.Single(p => p.LanternUserId == "one").ConnectedAtUnixMs);
        Assert.Equal(joined, state.Players.Single(p => p.LanternUserId == "two").ConnectedAtUnixMs);

        WriteRoster(joined + 180_000);
        watcher.PollFiles();
        Assert.Empty(state.Players);
        WriteRoster(joined + 240_000, ("two", "Bob", joined + 240_000));
        watcher.PollFiles();
        Assert.Equal(joined + 240_000, Assert.Single(state.Players).ConnectedAtUnixMs);
    }

    [Fact]
    public void EarlierProducerConnectionTimeIsRetainedAndMissingTimeUsesObservation()
    {
        var (watcher, state) = CreateWatcher();
        const long observed = 1_700_000_000_000;
        WriteRoster(observed, ("one", "Alice", observed - 60_000), ("two", "Bob", 0));
        watcher.PollFiles();
        WriteRoster(observed + 30_000, ("one", "Alice", observed - 60_000), ("two", "Bob", 0));
        watcher.PollFiles();

        Assert.Equal(observed - 60_000, state.Players.Single(p => p.LanternUserId == "one").ConnectedAtUnixMs);
        Assert.Equal(observed, state.Players.Single(p => p.LanternUserId == "two").ConnectedAtUnixMs);
    }

    [Fact]
    public void MalformedRosterIsRetriedWithTheSameFileMetadataAndDoesNotEndSessions()
    {
        var (watcher, state) = CreateWatcher();
        const long joined = 1_700_000_000_000;
        WriteRoster(joined, ("one", "Alice", joined));
        watcher.PollFiles();
        WriteRoster(joined + 60_000, ("one", "Alice", joined + 60_000));
        var path = Path.Combine(_directory, "roster.json");
        var valid = File.ReadAllText(path);
        var writeTime = File.GetLastWriteTimeUtc(path);
        File.WriteAllText(path, "!" + valid[1..]);
        File.SetLastWriteTimeUtc(path, writeTime);
        watcher.PollFiles();
        Assert.Equal(joined, Assert.Single(state.Players).LastPacketUnixMs);

        File.WriteAllText(path, valid);
        File.SetLastWriteTimeUtc(path, writeTime);
        watcher.PollFiles();

        var player = Assert.Single(state.Players);
        Assert.Equal(joined, player.ConnectedAtUnixMs);
        Assert.Equal(joined + 60_000, player.LastPacketUnixMs);
    }

    private (RosterFileWatcherService Watcher, PipeServerState State) CreateWatcher()
    {
        Directory.CreateDirectory(_directory);
        var state = new PipeServerState(NullLogger<PipeServerState>.Instance);
        return (new RosterFileWatcherService(NullLogger<RosterFileWatcherService>.Instance, state, _directory), state);
    }

    private void WriteRoster(long observedAt, params (string Id, string Name, long ConnectedAt)[] players)
    {
        var path = Path.Combine(_directory, "roster.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            unix_ms = observedAt,
            players = players.Select(p => new
            {
                LanternUserId = p.Id,
                DisplayName = p.Name,
                ConnectedAtUnixMs = p.ConnectedAt,
                LastPacketUnixMs = observedAt,
                PingMs = 0,
            }),
        }));
        File.SetLastWriteTimeUtc(path, DateTime.UnixEpoch.AddSeconds(++_writeNumber));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
