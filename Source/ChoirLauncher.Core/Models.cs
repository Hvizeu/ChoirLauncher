using System.Text.Json.Serialization;

namespace ChoirLauncher.Core;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ModSourceType { Local, Workshop }
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum Severity { Blocking, High, Medium, Low, Informational }
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum Confidence { Proven, Likely, Possible, Unknown }

public sealed record ModMetadata(
    string Name,
    string Description,
    string Version,
    int GameVersionMajor,
    int? GameVersionMinor,
    string Author,
    string Information,
    bool IsValid,
    IReadOnlyList<string> Diagnostics);

public sealed record DependencySpec(string ModId, string Constraint, bool Optional);

public sealed record ChoirManifest(
    int FormatVersion,
    string ModId,
    string DisplayName,
    string Version,
    IReadOnlyList<DependencySpec> Required,
    IReadOnlyList<DependencySpec> Optional,
    IReadOnlyList<string> Incompatible,
    IReadOnlyList<string> Capabilities,
    string? GameVersionRange,
    string? ChoirApiRange,
    string DescriptorKind,
    bool IsValid,
    IReadOnlyList<string> Diagnostics);

public sealed record ArchiveClass(string ClassName, string EntryPath, string Sha256);

public sealed record JarInventory(
    string FileName,
    long Size,
    string Sha256,
    bool IsValid,
    IReadOnlyList<ArchiveClass> Classes,
    IReadOnlyList<string> DescriptorEntries,
    IReadOnlyList<string> Diagnostics);

public sealed record FileInventory(
    string RelativePath,
    string Sha256,
    long Size,
    string Category,
    [property: JsonIgnore] string PhysicalPath);

public sealed record StableIdObservation(string Kind, string Id, string EvidencePath, Confidence Confidence);

public sealed record ModInstallation(
    string InstallationId,
    string LogicalModId,
    string SourceId,
    ModSourceType Source,
    string FolderName,
    [property: JsonIgnore] string RootPath,
    string ContentFingerprint,
    ModMetadata Metadata,
    int? SelectedMajorVersion,
    bool Enabled,
    int? Priority,
    ChoirManifest? Manifest,
    string? OptionsProviderId,
    IReadOnlyList<JarInventory> Jars,
    IReadOnlyList<FileInventory> DataFiles,
    IReadOnlyList<StableIdObservation> StableIds,
    IReadOnlyList<string> Diagnostics);

public sealed record Conflict(
    string ConflictId,
    string Category,
    Severity Severity,
    Confidence Confidence,
    IReadOnlyList<string> InvolvedMods,
    string Target,
    string? CurrentWinner,
    bool OrderResolvable,
    bool NoValidOrder,
    string Explanation,
    string RecommendedAction,
    IReadOnlyList<string> Evidence);

public sealed record DependencyGraphResult(
    IReadOnlyList<string> DeterministicOrder,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Blockers,
    IReadOnlyList<IReadOnlyList<string>> Cycles);

public sealed record ScanReport(
    string Schema,
    string TargetGameVersion,
    string LauncherSettingsSha256,
    string? GameJarSha256,
    IReadOnlyList<string> EnabledOrder,
    IReadOnlyList<ModInstallation> Mods,
    DependencyGraphResult DependencyGraph,
    IReadOnlyList<Conflict> Conflicts,
    IReadOnlyList<string> SuggestedOrder,
    string PriorityRule,
    DateTimeOffset ScannedAtUtc);

public sealed record ScanRequest(
    string LocalModsRoot,
    string WorkshopModsRoot,
    string LauncherSettingsPath,
    string? GameJarPath,
    int TargetMajorVersion,
    string TargetGameVersion);

public sealed record ProfileMod(
    string LogicalModId,
    ModSourceType Source,
    string SourceId,
    string InstallationId,
    bool Enabled,
    int? Priority,
    string? ExpectedVersion,
    string? ExpectedContentFingerprint,
    string? Notes);

public sealed record ModProfile(
    int SchemaVersion,
    string ProfileId,
    string DisplayName,
    string TargetGameVersion,
    IReadOnlyList<ProfileMod> Mods,
    IReadOnlyList<string> AcknowledgedWarnings,
    DateTimeOffset CreatedUtc,
    DateTimeOffset ModifiedUtc,
    DateTimeOffset? LastSuccessfulApplicationUtc,
    DateTimeOffset? LastSuccessfulLaunchUtc);
