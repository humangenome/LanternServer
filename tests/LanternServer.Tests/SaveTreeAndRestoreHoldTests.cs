using System.IO.Compression;
using FluentAssertions;
using LanternServer.Configuration;
using LanternServer.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LanternServer.Tests;

public sealed class SaveTreeValidationTests
{
    [Fact]
    public void HasNonEmptyWorld_RequiresARealDirectChildWorldFile()
    {
        using var temp = new TempDir("lantern-save-tree-tests-");
        var save = Path.Combine(temp.Path, "(ID-TEST)(AUTO-SAVE)");
        Directory.CreateDirectory(save);
        using (File.Create(Path.Combine(save, "World.csav"))) { }

        SaveOrchestratorService.HasNonEmptyWorld(temp.Path).Should().BeFalse();

        File.WriteAllText(Path.Combine(save, "World.csav"), "world");
        SaveOrchestratorService.HasNonEmptyWorld(temp.Path).Should().BeTrue();

        var wrapped = Path.Combine(temp.Path, "Saved", "Grounded2", "wrapped");
        Directory.CreateDirectory(wrapped);
        File.WriteAllText(Path.Combine(wrapped, "World.csav"), "wrong depth");
        Directory.Delete(save, recursive: true);
        SaveOrchestratorService.HasNonEmptyWorld(temp.Path).Should().BeFalse();
    }

    [Fact]
    public void ZipHasValidSaveTree_AcceptsTheDirectoryBasedGrounded2Layout()
    {
        using var temp = new TempDir("lantern-save-zip-tests-");
        var zip = Path.Combine(temp.Path, "world.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            var world = archive.CreateEntry("(ID-TEST)(AUTO-SAVE)/World.csav");
            using var writer = new StreamWriter(world.Open());
            writer.Write("world");
        }

        SaveOrchestratorService.ZipHasValidSaveTree(zip).Should().BeTrue();
    }

    [Theory]
    [InlineData("savegame_0.sav")]
    [InlineData("Grounded2/(ID-TEST)(AUTO-SAVE)/World.csav")]
    [InlineData("World.csav")]
    public void ZipHasValidSaveTree_RejectsWrongFolderShapes(string entryName)
    {
        using var temp = new TempDir("lantern-save-zip-tests-");
        var zip = Path.Combine(temp.Path, "wrong.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry(entryName);
            using var writer = new StreamWriter(entry.Open());
            writer.Write("not a canonical Grounded 2 save tree");
        }

        SaveOrchestratorService.ZipHasValidSaveTree(zip).Should().BeFalse();
    }
}

public sealed class RestoreHoldTests
{
    [Fact]
    public async Task BeginRestore_WritesAndReleasesItsExactOwnerToken()
    {
        using var temp = new TempDir("lantern-restore-hold-tests-");
        var hold = Path.Combine(temp.Path, "Logs", "g2_restore.hold");
        var coordinator = CreateCoordinator(hold);

        var lease = await coordinator.BeginRestoreAsync(CancellationToken.None);
        File.Exists(hold).Should().BeTrue();
        File.ReadAllText(hold).Should().StartWith(Environment.ProcessId + ":");

        lease.Dispose();
        File.Exists(hold).Should().BeFalse();
    }

    [Fact]
    public async Task BeginRestore_DoesNotDeleteAReplacementOwnerToken()
    {
        using var temp = new TempDir("lantern-restore-hold-tests-");
        var hold = Path.Combine(temp.Path, "Logs", "g2_restore.hold");
        var coordinator = CreateCoordinator(hold);

        var lease = await coordinator.BeginRestoreAsync(CancellationToken.None);
        File.WriteAllText(hold, "999999:replacement");
        lease.Dispose();

        File.ReadAllText(hold).Should().Be("999999:replacement");
    }

    private static G2RestartCoordinator CreateCoordinator(string hold) =>
        new(NullLogger<G2RestartCoordinator>.Instance, Options.Create(new LanternServerOptions
        {
            GameInstallRoot = "",
            GameUserDir = "",
            GamePidFile = "",
            ExternalLifecycleHoldFile = hold,
        }));
}

internal sealed class TempDir : IDisposable
{
    public TempDir(string prefix)
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch { }
    }
}
