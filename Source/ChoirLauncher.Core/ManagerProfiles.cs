using System.Text.Json;
using System.Text.Json.Serialization;

namespace ChoirLauncher.Core;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ProfileResolutionStatus { Resolved, Missing, Ambiguous }

public sealed record WarningAcknowledgement(string Signature, DateTimeOffset AcknowledgedUtc, string Message);

public sealed record ProfileApplicationRecord(
    DateTimeOffset AppliedUtc,
    string ConfigurationSha256,
    string BackupId,
    string BuildId);

public sealed record ManagerProfileEntry(
    string EntryId,
    string LogicalModId,
    ModSourceType Source,
    string SourceId,
    string? InstallationIdHint,
    bool Enabled,
    string? ExpectedVersion,
    string? ExpectedContentFingerprint,
    string? Notes)
{
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; init; }
}

public sealed record ManagerProfile(
    int SchemaVersion,
    string ProfileId,
    string DisplayName,
    string TargetGameVersion,
    IReadOnlyList<ManagerProfileEntry> Mods,
    IReadOnlyList<WarningAcknowledgement> AcknowledgedWarnings,
    DateTimeOffset CreatedUtc,
    DateTimeOffset ModifiedUtc,
    ProfileApplicationRecord? LastSuccessfulApplication,
    DateTimeOffset? LastSuccessfulLaunchUtc)
{
    public IReadOnlyList<ManagerProfileEntry> RemovedMods { get; init; } = [];

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; init; }

    public IReadOnlyList<string> EffectiveOfficialOrder => ModPriorityOrder.ToOfficialOrder(
        Mods.Where(x => x.Enabled).Select(x => x.SourceId));
}

public sealed record ResolvedProfileEntry(
    ManagerProfileEntry Entry,
    ProfileResolutionStatus Status,
    ModInstallation? Installation,
    IReadOnlyList<ModInstallation> Candidates,
    IReadOnlyList<string> Diagnostics);

public sealed record ResolvedProfile(ManagerProfile Profile, IReadOnlyList<ResolvedProfileEntry> Entries)
{
    public bool HasEnabledUnresolved => Entries.Any(x => x.Entry.Enabled && x.Status != ProfileResolutionStatus.Resolved);
    public IReadOnlyList<string> EffectiveOfficialOrder => ModPriorityOrder.ToOfficialOrder(Entries
        .Where(x => x.Entry.Enabled && x.Status == ProfileResolutionStatus.Resolved)
        .Select(x => x.Installation!.FolderName));
}

public static class ManagerProfileValidator
{
    public const int CurrentSchemaVersion = 3;

    public static void Validate(ManagerProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.SchemaVersion != CurrentSchemaVersion) throw new FormatException($"Unsupported manager profile schema {profile.SchemaVersion}.");
        if (!MetadataParsers.IsStableId(profile.ProfileId)) throw new FormatException("Invalid profile ID.");
        if (string.IsNullOrWhiteSpace(profile.DisplayName)) throw new FormatException("Profile display name is required.");
        if (profile.RemovedMods is null) throw new FormatException("Removed profile entries are required.");
        if (profile.Mods.GroupBy(x => x.EntryId, StringComparer.Ordinal).Any(x => x.Count() > 1)) throw new FormatException("Duplicate profile entry ID.");
        if (profile.RemovedMods.GroupBy(x => x.EntryId, StringComparer.Ordinal).Any(x => x.Count() > 1)) throw new FormatException("Duplicate removed profile entry ID.");
        if (profile.Mods.Select(x => x.EntryId).Intersect(profile.RemovedMods.Select(x => x.EntryId), StringComparer.Ordinal).Any())
            throw new FormatException("A profile entry cannot be both active and removed.");
        foreach (var entry in profile.Mods.Concat(profile.RemovedMods))
        {
            if (!MetadataParsers.IsStableId(entry.EntryId)) throw new FormatException($"Invalid profile entry ID: {entry.EntryId}");
            if (string.IsNullOrWhiteSpace(entry.SourceId) || entry.SourceId.IndexOfAny(['\r', '\n', '"']) >= 0) throw new FormatException($"Invalid source ID for {entry.EntryId}.");
            if (entry.ExpectedContentFingerprint is { Length: > 0 } fingerprint && (fingerprint.Length != 64 || !fingerprint.All(Uri.IsHexDigit)))
                throw new FormatException($"Invalid content fingerprint for {entry.EntryId}.");
        }
    }
}

public sealed record ProfileInventoryReconciliation(
    ManagerProfile Profile,
    IReadOnlyList<ManagerProfileEntry> AddedEntries);

public static class ProfileInventoryReconciler
{
    public static ProfileInventoryReconciliation Reconcile(
        ManagerProfile profile,
        IReadOnlyList<ModInstallation> installations,
        bool enableNewEntries = false)
    {
        ManagerProfileValidator.Validate(profile);
        ArgumentNullException.ThrowIfNull(installations);

        var knownInstallations = profile.Mods.Select(x => x.InstallationIdHint)
            .Where(x => !string.IsNullOrWhiteSpace(x)).ToHashSet(StringComparer.Ordinal);
        var knownSources = profile.Mods.Select(SourceKey).ToHashSet();
        var removedSources = profile.RemovedMods.Select(SourceKey).ToHashSet();

        var additions = installations
            .Where(installation => !knownInstallations.Contains(installation.InstallationId)
                && !knownSources.Contains((installation.Source, installation.SourceId))
                && !removedSources.Contains((installation.Source, installation.SourceId)))
            .OrderBy(x => x.LogicalModId, StringComparer.Ordinal)
            .ThenBy(x => x.Source)
            .ThenBy(x => x.SourceId, StringComparer.Ordinal)
            .Select(x => ProfileFactory.FromInstallation(x, enableNewEntries))
            .ToArray();

        if (additions.Length == 0) return new(profile, []);
        var reconciled = profile with
        {
            Mods = profile.Mods.Concat(additions).ToArray(),
            ModifiedUtc = DateTimeOffset.UtcNow
        };
        ManagerProfileValidator.Validate(reconciled);
        return new(reconciled, additions);
    }

    private static (ModSourceType Source, string SourceId) SourceKey(ManagerProfileEntry entry) =>
        (entry.Source, entry.SourceId);
}

public static class ProfileFactory
{
    public const string DefaultProfileId = "default";
    public const string DefaultProfileName = "Default";

    public static ManagerProfile DefaultFromOfficialState(ScanReport scan) =>
        FromOfficialState(DefaultProfileId, DefaultProfileName, scan);

    public static ManagerProfile FromOfficialState(string profileId, string displayName, ScanReport scan)
    {
        var byFolder = scan.Mods.GroupBy(x => x.FolderName, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.OrderBy(y => y.Source).First(), StringComparer.Ordinal);
        var entries = new List<ManagerProfileEntry>();
        foreach (var folder in ModPriorityOrder.FromOfficialOrder(scan.EnabledOrder))
        {
            if (byFolder.TryGetValue(folder, out var installation)) entries.Add(FromInstallation(installation, true));
            else entries.Add(new(CreateEntryId(folder, ModSourceType.Local), folder, ModSourceType.Local, folder, null, true, null, null, "Missing from current installation inventory."));
        }
        foreach (var installation in scan.Mods.Where(x => !scan.EnabledOrder.Contains(x.FolderName, StringComparer.Ordinal))
                     .OrderBy(x => x.Metadata.Name, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.SourceId, StringComparer.Ordinal))
            entries.Add(FromInstallation(installation, false));
        return New(profileId, displayName, scan.TargetGameVersion, entries);
    }

    public static ManagerProfile FromAllInstalled(string profileId, string displayName, ScanReport scan, bool preserveOfficialEnabled)
    {
        var enabled = scan.EnabledOrder.ToHashSet(StringComparer.Ordinal);
        var ordered = scan.Mods.OrderBy(x => x.Metadata.Name, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.SourceId, StringComparer.Ordinal)
            .Select(x => FromInstallation(x, preserveOfficialEnabled && enabled.Contains(x.FolderName))).ToArray();
        return New(profileId, displayName, scan.TargetGameVersion, ordered);
    }

    public static ManagerProfile Empty(string profileId, string displayName, string gameVersion) => New(profileId, displayName, gameVersion, []);

    public static ManagerProfile Duplicate(ManagerProfile source, string profileId, string displayName)
    {
        var now = DateTimeOffset.UtcNow;
        return source with { ProfileId = profileId, DisplayName = displayName, CreatedUtc = now, ModifiedUtc = now, LastSuccessfulApplication = null, LastSuccessfulLaunchUtc = null };
    }

    public static ManagerProfileEntry FromInstallation(ModInstallation installation, bool enabled) => new(
        CreateEntryId(installation.LogicalModId, installation.Source), installation.LogicalModId, installation.Source,
        installation.SourceId, installation.InstallationId, enabled, installation.Manifest?.Version ?? installation.Metadata.Version,
        installation.ContentFingerprint, null);

    private static ManagerProfile New(string id, string name, string gameVersion, IReadOnlyList<ManagerProfileEntry> entries)
    {
        var now = DateTimeOffset.UtcNow;
        var profile = new ManagerProfile(ManagerProfileValidator.CurrentSchemaVersion, id, name, gameVersion, entries, [], now, now, null, null);
        ManagerProfileValidator.Validate(profile);
        return profile;
    }

    private static string CreateEntryId(string value, ModSourceType source)
    {
        var safe = new string(value.Select(c => char.IsLetterOrDigit(c) || c is '.' or '_' or '-' ? c : '-').ToArray()).Trim('-');
        return $"{source.ToString().ToLowerInvariant()}-{safe}-{Guid.NewGuid():N}";
    }
}

public static class DefaultProfilePolicy
{
    public static bool IsDefault(ManagerProfile? profile) =>
        string.Equals(profile?.ProfileId, ProfileFactory.DefaultProfileId, StringComparison.Ordinal);

    public static ManagerProfile Ensure(
        ManagerProfileRepository repository,
        IReadOnlyList<ManagerProfile> loadedProfiles,
        ScanReport scan)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(loadedProfiles);
        ArgumentNullException.ThrowIfNull(scan);

        var existing = loadedProfiles.FirstOrDefault(IsDefault);
        if (existing is not null) return existing;

        var created = ProfileFactory.DefaultFromOfficialState(scan);
        repository.Save(created);
        return created;
    }
}

public static class ProfileResolver
{
    public static ResolvedProfile Resolve(ManagerProfile profile, IReadOnlyList<ModInstallation> installations)
    {
        ManagerProfileValidator.Validate(profile);
        var resolved = profile.Mods.Select(entry => ResolveEntry(entry, installations)).ToArray();
        return new(profile, resolved);
    }

    public static ResolvedProfileEntry ResolveEntry(ManagerProfileEntry entry, IReadOnlyList<ModInstallation> installations)
    {
        if (entry.InstallationIdHint is { Length: > 0 })
        {
            var exact = installations.Where(x => x.InstallationId == entry.InstallationIdHint).ToArray();
            if (exact.Length == 1) return Resolved(entry, exact[0], exact);
        }

        var sourceMatches = installations.Where(x => x.Source == entry.Source && x.SourceId == entry.SourceId).ToArray();
        if (sourceMatches.Length == 1) return Resolved(entry, sourceMatches[0], sourceMatches);
        if (sourceMatches.Length > 1) return Ambiguous(entry, sourceMatches, "Multiple installations match source identity.");

        var logical = installations.Where(x => x.LogicalModId == entry.LogicalModId).ToArray();
        if (logical.Length == 1) return Resolved(entry, logical[0], logical);
        if (logical.Length > 1) return Ambiguous(entry, logical, "Multiple installations match logical mod identity.");
        return new(entry, ProfileResolutionStatus.Missing, null, [], ["No installed mod matches this profile entry."]);
    }

    private static ResolvedProfileEntry Resolved(ManagerProfileEntry entry, ModInstallation installation, IReadOnlyList<ModInstallation> candidates)
    {
        var diagnostics = new List<string>();
        var actualVersion = installation.Manifest?.Version ?? installation.Metadata.Version;
        if (entry.ExpectedVersion is { Length: > 0 } && entry.ExpectedVersion != actualVersion) diagnostics.Add($"Version changed: expected {entry.ExpectedVersion}, actual {actualVersion}.");
        if (entry.ExpectedContentFingerprint is { Length: > 0 } && entry.ExpectedContentFingerprint != installation.ContentFingerprint) diagnostics.Add("Content fingerprint changed.");
        return new(entry, ProfileResolutionStatus.Resolved, installation, candidates, diagnostics);
    }

    private static ResolvedProfileEntry Ambiguous(ManagerProfileEntry entry, IReadOnlyList<ModInstallation> candidates, string message) =>
        new(entry, ProfileResolutionStatus.Ambiguous, null, candidates.OrderBy(x => x.InstallationId, StringComparer.Ordinal).ToArray(), [message]);
}
