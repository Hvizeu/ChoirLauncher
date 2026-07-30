using System.IO.Compression;
using ChoirLauncher.Core;
using Xunit;

namespace ChoirLauncher.Core.Tests;

public sealed class OrderingAndCompatibilityTests
{
    [Fact]
    public void SuggestedOrderPlacesConsumerAfterRequiredPlatform()
    {
        var consumer = Installation(
            "moon.elf.race",
            "MoonElfRace",
            0,
            required: [new("syxforge", ">=0.2.25", false)]);
        var platform = Installation("syxforge", "SyxForge", 1, descriptorKind: "detected SyxForge runtime");

        var suggestion = OrderSuggester.Suggest(
            [consumer, platform],
            ["SyxForge", "MoonElfRace"],
            []);

        Assert.Equal(["syxforge", "moon.elf.race"], suggestion.LogicalOrder);
    }

    [Fact]
    public void SuggestedOrderApplicationUsesStableEntryIdsInsteadOfLegacyLogicalIds()
    {
        var moon = Entry("moon-entry", "MoonElfRace", "MoonElfRace");
        var syxForge = Entry("syxforge-entry", "SyxForge", "SyxForge");
        var profile = Profile([moon, syxForge]);
        var editor = new ProfileEditorSession(profile);

        var changed = editor.ApplySuggestedEntryOrder([syxForge.EntryId, moon.EntryId]);

        Assert.True(changed);
        Assert.Equal([syxForge.EntryId, moon.EntryId], editor.Current.Mods.Select(x => x.EntryId));
    }

    [Fact]
    public void PlatformRuntimeOwnershipIsNotReportedAsStandaloneConflict()
    {
        using var vanilla = VanillaArchive("settlement.room.RoomBlueprint");
        var platform = Installation(
            "syxforge",
            "SyxForge",
            0,
            descriptorKind: "syxforge/core-platform.properties",
            classes: [new("settlement.room.RoomBlueprint", "settlement/room/RoomBlueprint.class", Hash('a'))]);

        var findings = ConflictAnalyzer.Analyze(
            [platform],
            [platform.FolderName],
            71,
            VanillaContentIndex.Build(vanilla.Path));

        Assert.DoesNotContain(findings, finding => finding.Category == "vanilla-class-shadow");
    }

    [Fact]
    public void TwoPlatformsOwningDifferentVersionsOfSameVanillaClassStillConflict()
    {
        using var vanilla = VanillaArchive("settlement.room.RoomBlueprint");
        var syxForge = Installation(
            "syxforge",
            "SyxForge",
            0,
            descriptorKind: "syxforge/core-platform.properties",
            classes: [new("settlement.room.RoomBlueprint", "settlement/room/RoomBlueprint.class", Hash('a'))]);
        var choir = Installation(
            "choir.framework",
            "ChoirFramework",
            1,
            descriptorKind: "choir.json",
            classes: [new("settlement.room.RoomBlueprint", "settlement/room/RoomBlueprint.class", Hash('b'))]);

        var findings = ConflictAnalyzer.Analyze(
            [syxForge, choir],
            [choir.FolderName, syxForge.FolderName],
            71,
            VanillaContentIndex.Build(vanilla.Path));

        Assert.Contains(findings, finding =>
            finding.Category == "vanilla-shadow-collision" &&
            finding.Severity == Severity.Blocking);
    }

    private static ManagerProfileEntry Entry(string entryId, string logicalModId, string sourceId) =>
        new(entryId, logicalModId, ModSourceType.Local, sourceId, $"Local:{sourceId}", true, "1.0.0", Hash('c'), null);

    private static ManagerProfile Profile(IReadOnlyList<ManagerProfileEntry> entries)
    {
        var now = DateTimeOffset.UtcNow;
        return new(ManagerProfileValidator.CurrentSchemaVersion, "test-profile", "Test Profile", "0.71.44",
            entries, [], now, now, null, null);
    }

    private static ModInstallation Installation(
        string logicalModId,
        string folderName,
        int priority,
        IReadOnlyList<DependencySpec>? required = null,
        string descriptorKind = "syxforge/core-platform.properties",
        IReadOnlyList<ArchiveClass>? classes = null)
    {
        var manifest = new ChoirManifest(1, logicalModId, logicalModId, "1.0.0",
            required ?? [], [], [], [], ">=0.71", null, descriptorKind, true, []);
        IReadOnlyList<JarInventory> jars = classes is { Count: > 0 }
            ? new[] { new JarInventory("runtime.jar", 1, Hash('d'), true, classes, [], []) }
            : [];
        var metadata = new ModMetadata(folderName, "", "1.0.0", 71, 44, "Test", "", true, []);
        return new($"Local:{folderName}", logicalModId, folderName, ModSourceType.Local, folderName, "",
            Hash('e'), metadata, 71, true, priority, manifest, null, jars, [], [], []);
    }

    private static TemporaryArchive VanillaArchive(string className)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"choirlauncher-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "SongsOfSyx.jar");
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry(className.Replace('.', '/') + ".class");
            using var stream = entry.Open();
            stream.WriteByte(0);
        }
        return new(path, directory);
    }

    private static string Hash(char value) => new(value, 64);

    private sealed class TemporaryArchive(string path, string directory) : IDisposable
    {
        public string Path { get; } = path;

        public void Dispose()
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
}
