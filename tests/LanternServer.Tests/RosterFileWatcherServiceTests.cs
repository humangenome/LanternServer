using LanternServer.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace LanternServer.Tests;

public sealed class RosterFileWatcherServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "lantern-roster-tests-" + Guid.NewGuid().ToString("N"));

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

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
