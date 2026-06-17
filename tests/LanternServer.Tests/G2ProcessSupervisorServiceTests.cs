using LanternServer.Services;
using LanternServer.Configuration;

namespace LanternServer.Tests;

public sealed class G2ProcessSupervisorServiceTests
{
    [Fact]
    public void BuildHostTravelUrlPinsCanonicalSaveSlot()
    {
        var url = G2ProcessSupervisorService.BuildHostTravelUrl();

        Assert.Equal(
            "/Game/_Augusta/Levels/Augusta_Main/Augusta_Main?listen?LaunchType=LoadGame?SaveSlotName=savegame_0",
            url);
    }

    [Fact]
    public void BuildHostTravelOptionsEscapesCustomSaveSlot()
    {
        var options = G2ProcessSupervisorService.BuildHostTravelOptions("save slot 1");

        Assert.Equal(
            "?listen?LaunchType=LoadGame?SaveSlotName=save%20slot%201",
            options);
    }

    [Fact]
    public void ResolveG2ExecutablePathDetectsWinGdkLayout()
    {
        var root = Path.Combine(Path.GetTempPath(), "lantern-tests", Guid.NewGuid().ToString("N"));
        var exe = Path.Combine(root, "Content", "Grounded2", "Binaries", "WinGDK", "Grounded2-WinGDK-Shipping.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
        File.WriteAllText(exe, "");

        try
        {
            var resolved = G2ProcessSupervisorService.ResolveG2ExecutablePath(new LanternServerOptions
            {
                GameInstallRoot = root,
            });

            Assert.Equal(exe, resolved);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ResolveG2ExecutablePathPrefersExplicitExecutablePath()
    {
        var explicitPath = Path.Combine(Path.GetTempPath(), "Grounded2-WinGDK-Shipping.exe");

        var resolved = G2ProcessSupervisorService.ResolveG2ExecutablePath(new LanternServerOptions
        {
            GameInstallRoot = @"C:\Lantern\game",
            GameExecutablePath = explicitPath,
        });

        Assert.Equal(Path.GetFullPath(explicitPath), resolved);
    }
}
