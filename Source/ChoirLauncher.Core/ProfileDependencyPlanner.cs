namespace ChoirLauncher.Core;

public sealed record ProfileDependencyChangePlan(IReadOnlyList<string> RequiredEntryIds, IReadOnlyList<string> DependentEntryIds);

public static class ProfileDependencyPlanner
{
    public static ProfileDependencyChangePlan Plan(ResolvedProfile profile, IReadOnlyCollection<string> entryIds, bool enabling)
    {
        var selected = profile.Entries.Where(x => entryIds.Contains(x.Entry.EntryId, StringComparer.Ordinal)).ToArray();
        if (enabling)
        {
            var requiredLogical = selected.SelectMany(x => x.Installation?.Manifest?.Required ?? []).Select(x => x.ModId).ToHashSet(StringComparer.Ordinal);
            return new(profile.Entries.Where(x => requiredLogical.Contains(x.Entry.LogicalModId) && !x.Entry.Enabled).Select(x => x.Entry.EntryId).ToArray(), []);
        }
        var selectedLogical = selected.Select(x => x.Entry.LogicalModId).ToHashSet(StringComparer.Ordinal);
        var dependents = profile.Entries.Where(x => x.Entry.Enabled && !entryIds.Contains(x.Entry.EntryId, StringComparer.Ordinal) &&
            (x.Installation?.Manifest?.Required.Any(d => selectedLogical.Contains(d.ModId)) ?? false)).Select(x => x.Entry.EntryId).ToArray();
        return new([], dependents);
    }
}
