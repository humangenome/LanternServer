using LanternServer.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace LanternServer.Tests;

public sealed class PipeServerStateTests
{
    [Fact]
    public void HasFreshRosterTracksShippingRuntimeHeartbeat()
    {
        var state = new PipeServerState(NullLogger<PipeServerState>.Instance);
        var window = TimeSpan.FromSeconds(60);

        Assert.False(state.HasFreshRoster(window));

        state.LastRosterAt = DateTimeOffset.UtcNow;
        Assert.True(state.HasFreshRoster(window));

        state.LastRosterAt = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(2);
        Assert.False(state.HasFreshRoster(window));
    }
}
