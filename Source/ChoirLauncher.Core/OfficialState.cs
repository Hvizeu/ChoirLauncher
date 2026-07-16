using System.Text;

namespace ChoirLauncher.Core;

[Flags]
public enum OfficialDifferenceKind
{
    None = 0,
    EnabledOnlyInProfile = 1,
    EnabledOnlyInOfficial = 2,
    DifferentPriority = 4,
    MatchingPriority = 8,
    MissingInstallation = 16,
    NewlyDiscoveredInstallation = 32,
    VersionMismatch = 64,
    FingerprintMismatch = 128,
    AmbiguousIdentity = 256,
    InvalidOfficialEntry = 512
}

public sealed record OfficialStateDifference(string Identity, OfficialDifferenceKind Kind, int? ProfilePriority, int? OfficialPriority, string Explanation);

public sealed record OfficialStateComparison(
    string OfficialSha256,
    IReadOnlyList<string> OfficialOrder,
    IReadOnlyList<string> ProfileOrder,
    IReadOnlyList<OfficialStateDifference> Differences,
    bool EffectiveStatesIdentical);

public static class OfficialStateComparer
{
    public static OfficialStateComparison Compare(ResolvedProfile profile, LauncherSettingsDocument official, IReadOnlyList<ModInstallation> installations)
    {
        var profileOrder = profile.EffectiveOfficialOrder.ToArray();
        var officialOrder = official.EnabledMods.ToArray();
        var differences = new List<OfficialStateDifference>();
        var all = profileOrder.Concat(officialOrder).Distinct(StringComparer.Ordinal).ToArray();
        foreach (var id in all)
        {
            var profileIndex = Array.IndexOf(profileOrder, id);
            var officialIndex = Array.IndexOf(officialOrder, id);
            if (profileIndex >= 0 && officialIndex < 0) differences.Add(new(id, OfficialDifferenceKind.EnabledOnlyInProfile, profileIndex, null, "Enabled only in the profile."));
            else if (profileIndex < 0 && officialIndex >= 0) differences.Add(new(id, OfficialDifferenceKind.EnabledOnlyInOfficial, null, officialIndex, "Enabled only in official settings."));
            else if (profileIndex == officialIndex) differences.Add(new(id, OfficialDifferenceKind.MatchingPriority, profileIndex, officialIndex, "Enabled at matching priority."));
            else differences.Add(new(id, OfficialDifferenceKind.DifferentPriority, profileIndex, officialIndex, "Enabled at a different priority."));
        }
        foreach (var entry in profile.Entries)
        {
            if (entry.Status == ProfileResolutionStatus.Missing) differences.Add(new(entry.Entry.EntryId, OfficialDifferenceKind.MissingInstallation, null, null, "Profile entry is missing."));
            else if (entry.Status == ProfileResolutionStatus.Ambiguous) differences.Add(new(entry.Entry.EntryId, OfficialDifferenceKind.AmbiguousIdentity, null, null, "Profile entry is ambiguous."));
            else
            {
                if (entry.Diagnostics.Any(x => x.StartsWith("Version changed", StringComparison.Ordinal))) differences.Add(new(entry.Entry.EntryId, OfficialDifferenceKind.VersionMismatch, null, null, entry.Diagnostics.First(x => x.StartsWith("Version changed", StringComparison.Ordinal))));
                if (entry.Diagnostics.Any(x => x.Contains("fingerprint", StringComparison.OrdinalIgnoreCase))) differences.Add(new(entry.Entry.EntryId, OfficialDifferenceKind.FingerprintMismatch, null, null, "Installed content fingerprint differs."));
            }
        }
        var installedFolders = installations.Select(x => x.FolderName).ToHashSet(StringComparer.Ordinal);
        foreach (var officialId in officialOrder.Where(x => !installedFolders.Contains(x)).Distinct(StringComparer.Ordinal))
            differences.Add(new(officialId, OfficialDifferenceKind.InvalidOfficialEntry, null, Array.IndexOf(officialOrder, officialId), "Official settings reference a missing installation."));
        var profileSources = profile.Profile.Mods.Select(x => x.SourceId).ToHashSet(StringComparer.Ordinal);
        foreach (var discovered in installations.Where(x => !profileSources.Contains(x.SourceId)))
            differences.Add(new(discovered.InstallationId, OfficialDifferenceKind.NewlyDiscoveredInstallation, null, null, "Discovered installation is absent from the profile."));
        var identical = profileOrder.SequenceEqual(officialOrder, StringComparer.Ordinal) && !differences.Any(x => x.Kind is OfficialDifferenceKind.MissingInstallation or OfficialDifferenceKind.AmbiguousIdentity or OfficialDifferenceKind.InvalidOfficialEntry);
        return new(LauncherSettingsDocument.Sha256(official.OriginalText), officialOrder, profileOrder, differences, identical);
    }
}

public sealed record OrderMove(string ModId, int OldPriority, int NewPriority);

public sealed record ApplyPreview(
    string TargetPath,
    string ProfileId,
    string ProfileName,
    string CurrentSha256,
    string ProposedSha256,
    string CurrentUnrelatedSha256,
    string ProposedUnrelatedSha256,
    IReadOnlyList<string> CurrentOrder,
    IReadOnlyList<string> ProposedOrder,
    IReadOnlyList<string> Added,
    IReadOnlyList<string> Removed,
    IReadOnlyList<OrderMove> Moved,
    IReadOnlyList<string> HardBlockers,
    IReadOnlyList<Conflict> CompatibilityFindings,
    string ConflictSignature,
    string BackupDirectory,
    string ProposedText,
    DateTimeOffset CreatedUtc)
{
    public bool CompatibilityWarningAcknowledged { get; init; }
    public bool CanApplyTechnically => HardBlockers.Count == 0;
    public bool RequiresCompatibilityAcknowledgement => CompatibilityFindings.Count > 0 && !CompatibilityWarningAcknowledged;
}

public static class ApplyPreviewService
{
    public static ApplyPreview Create(string targetPath, ResolvedProfile profile, IReadOnlyList<Conflict> conflicts, string backupDirectory)
    {
        var full = Path.GetFullPath(targetPath);
        var currentBytes = File.ReadAllBytes(full);
        var currentText = Encoding.UTF8.GetString(currentBytes);
        var document = LauncherSettingsDocument.Parse(currentText);
        var blockers = new List<string>();
        foreach (var entry in profile.Entries.Where(x => x.Entry.Enabled && x.Status != ProfileResolutionStatus.Resolved))
            blockers.Add($"Enabled entry {entry.Entry.LogicalModId} is {entry.Status.ToString().ToUpperInvariant()}.");
        var proposedOrder = profile.EffectiveOfficialOrder;
        if (proposedOrder.Distinct(StringComparer.Ordinal).Count() != proposedOrder.Count) blockers.Add("Effective official order contains duplicate folder IDs.");
        var proposedText = document.WithEnabledMods(proposedOrder);
        var proposed = LauncherSettingsDocument.Parse(proposedText);
        if (!proposed.EnabledMods.SequenceEqual(proposedOrder, StringComparer.Ordinal)) blockers.Add("Proposed MODS list failed round-trip validation.");
        var currentUnrelated = document.UnrelatedContentSha256();
        var proposedUnrelated = proposed.UnrelatedContentSha256();
        if (currentUnrelated != proposedUnrelated) blockers.Add("Proposed serialization changed unrelated settings bytes.");
        var compatibility = conflicts.Where(x => x.Severity is Severity.Blocking or Severity.High or Severity.Medium).ToArray();
        var conflictSignature = Hashing.Sha256(Encoding.UTF8.GetBytes(string.Join('\n', compatibility.OrderBy(x => x.ConflictId, StringComparer.Ordinal).Select(x => x.ConflictId + ":" + x.Severity))));
        var warningAcknowledged = compatibility.Length > 0 && profile.Profile.AcknowledgedWarnings.Any(x => x.Signature == conflictSignature);
        return new(full, profile.Profile.ProfileId, profile.Profile.DisplayName, Hashing.Sha256(currentBytes), Hashing.Sha256(Encoding.UTF8.GetBytes(proposedText)),
            currentUnrelated, proposedUnrelated, document.EnabledMods, proposedOrder,
            proposedOrder.Except(document.EnabledMods, StringComparer.Ordinal).ToArray(), document.EnabledMods.Except(proposedOrder, StringComparer.Ordinal).ToArray(),
            Moves(document.EnabledMods, proposedOrder), blockers, compatibility, conflictSignature, Path.GetFullPath(backupDirectory), proposedText, DateTimeOffset.UtcNow)
        {
            CompatibilityWarningAcknowledged = warningAcknowledged
        };
    }

    private static IReadOnlyList<OrderMove> Moves(IReadOnlyList<string> before, IReadOnlyList<string> after)
    {
        var moves = new List<OrderMove>();
        foreach (var id in before.Intersect(after, StringComparer.Ordinal))
        {
            var oldIndex = before.IndexOf(id);
            var newIndex = after.IndexOf(id);
            if (oldIndex != newIndex) moves.Add(new(id, oldIndex, newIndex));
        }
        return moves;
    }
}

public static class CompatibilityWarningFormatter
{
    public static string Format(IReadOnlyList<Conflict> findings, IReadOnlyList<ModInstallation> installations)
    {
        ArgumentNullException.ThrowIfNull(findings);
        ArgumentNullException.ThrowIfNull(installations);
        if (findings.Count == 0) return "No compatibility warnings were found.";

        var byInstallation = installations.GroupBy(x => x.InstallationId, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.First(), StringComparer.Ordinal);
        var lines = new List<string>
        {
            findings.Count == 1
                ? "ChoirLauncher found this compatibility warning:"
                : $"ChoirLauncher found {findings.Count} compatibility warnings:"
        };

        for (var index = 0; index < findings.Count; index++)
        {
            var finding = findings[index];
            var mods = finding.InvolvedMods.Select(id => DescribeInstallation(id, installations, byInstallation))
                .Distinct(StringComparer.Ordinal).ToArray();
            lines.Add(string.Empty);
            if (findings.Count > 1) lines.Add($"Warning {index + 1}");
            lines.Add($"Mod{(mods.Length == 1 ? string.Empty : "s")}: {(mods.Length == 0 ? "Unknown" : string.Join(" + ", mods))}");
            lines.Add($"Severity: {finding.Severity.ToString().ToUpperInvariant()}");
            lines.Add($"Type: {FriendlyCategory(finding.Category)}");
            lines.Add($"Reason: {finding.Explanation}");
            if (!string.IsNullOrWhiteSpace(finding.Target)) lines.Add($"Affected item: {finding.Target}");
            if (!string.IsNullOrWhiteSpace(finding.RecommendedAction)) lines.Add($"Suggested action: {finding.RecommendedAction}");
        }

        lines.Add(string.Empty);
        lines.Add("This warning does not block Apply. Select OK to continue.");
        return string.Join(Environment.NewLine, lines);
    }

    private static string DescribeInstallation(
        string identity,
        IReadOnlyList<ModInstallation> installations,
        IReadOnlyDictionary<string, ModInstallation> byInstallation)
    {
        if (!byInstallation.TryGetValue(identity, out var installation))
            installation = installations.FirstOrDefault(x => x.FolderName == identity || x.SourceId == identity);
        if (installation is null) return identity;
        var name = string.IsNullOrWhiteSpace(installation.Metadata.Name) ? installation.LogicalModId : installation.Metadata.Name;
        return $"{name} ({installation.Source} {installation.SourceId})";
    }

    private static string FriendlyCategory(string category)
    {
        if (string.IsNullOrWhiteSpace(category)) return "Compatibility warning";
        var text = category.Replace('-', ' ');
        return char.ToUpperInvariant(text[0]) + text[1..];
    }
}
