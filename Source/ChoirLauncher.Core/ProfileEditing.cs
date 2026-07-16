namespace ChoirLauncher.Core;

public sealed record EditOperation(string Description, ManagerProfile Before, ManagerProfile After, IReadOnlyList<string> Selection);

public sealed class ProfileEditorSession
{
    private readonly int historyLimit;
    private readonly Stack<EditOperation> undo = new();
    private readonly Stack<EditOperation> redo = new();

    public ProfileEditorSession(ManagerProfile profile, int historyLimit = 100)
    {
        ManagerProfileValidator.Validate(profile);
        Current = profile;
        this.historyLimit = Math.Max(1, historyLimit);
    }

    public ManagerProfile Current { get; private set; }
    public bool CanUndo => undo.Count > 0;
    public bool CanRedo => redo.Count > 0;
    public int UndoCount => undo.Count;
    public int RedoCount => redo.Count;
    public string? LastDescription => undo.TryPeek(out var operation) ? operation.Description : null;
    public IReadOnlyList<string> CurrentSelection { get; private set; } = [];

    public bool MoveBefore(IReadOnlyCollection<string> selectedEntryIds, string targetEntryId) => Move(selectedEntryIds, targetEntryId, true, "Move selection before target");
    public bool MoveAfter(IReadOnlyCollection<string> selectedEntryIds, string targetEntryId) => Move(selectedEntryIds, targetEntryId, false, "Move selection after target");

    public bool MoveToTop(IReadOnlyCollection<string> selectedEntryIds) => MoveToBoundary(selectedEntryIds, true);
    public bool MoveToBottom(IReadOnlyCollection<string> selectedEntryIds) => MoveToBoundary(selectedEntryIds, false);

    public bool MoveUp(IReadOnlyCollection<string> selectedEntryIds)
    {
        var selected = SelectInProfileOrder(selectedEntryIds);
        if (selected.Count == 0) return false;
        var first = Current.Mods.IndexOf(selected[0]);
        if (first <= 0) return false;
        var remaining = Current.Mods.Where(x => !selectedEntryIds.Contains(x.EntryId, StringComparer.Ordinal)).ToList();
        var insertion = Math.Max(0, first - 1);
        remaining.InsertRange(insertion, selected);
        return Commit("Move selection up", remaining, selectedEntryIds);
    }

    public bool MoveDown(IReadOnlyCollection<string> selectedEntryIds)
    {
        var selected = SelectInProfileOrder(selectedEntryIds);
        if (selected.Count == 0) return false;
        var last = Current.Mods.IndexOf(selected[^1]);
        if (last >= Current.Mods.Count - 1) return false;
        var remaining = Current.Mods.Where(x => !selectedEntryIds.Contains(x.EntryId, StringComparer.Ordinal)).ToList();
        var insertion = Math.Min(remaining.Count, Current.Mods.IndexOf(selected[0]) + 1);
        remaining.InsertRange(insertion, selected);
        return Commit("Move selection down", remaining, selectedEntryIds);
    }

    public bool SetEnabled(IReadOnlyCollection<string> entryIds, bool enabled, string description = "Change enabled state") =>
        Mutate(description, entryIds, entry => entryIds.Contains(entry.EntryId, StringComparer.Ordinal) ? entry with { Enabled = enabled } : entry);

    public bool ToggleEnabled(IReadOnlyCollection<string> entryIds)
    {
        var selected = entryIds.ToHashSet(StringComparer.Ordinal);
        if (selected.Count == 0) return false;
        var enable = Current.Mods.Where(x => selected.Contains(x.EntryId)).Any(x => !x.Enabled);
        return SetEnabled(entryIds, enable, enable ? "Enable selection" : "Disable selection");
    }

    public bool Remove(IReadOnlyCollection<string> entryIds)
    {
        var removed = Current.Mods.Where(x => entryIds.Contains(x.EntryId, StringComparer.Ordinal)).ToArray();
        if (removed.Length == 0) return false;
        var removedSources = removed.Select(x => (x.Source, x.SourceId)).ToHashSet();
        var retainedTombstones = Current.RemovedMods.Where(x => !removedSources.Contains((x.Source, x.SourceId)));
        return CommitProfile("Remove profile entries", Current with
        {
            Mods = Current.Mods.Where(x => !entryIds.Contains(x.EntryId, StringComparer.Ordinal)).ToArray(),
            RemovedMods = retainedTombstones.Concat(removed).ToArray(),
            ModifiedUtc = DateTimeOffset.UtcNow
        }, entryIds);
    }

    public bool RestoreRemoved(IReadOnlyCollection<string> entryIds)
    {
        var restored = Current.RemovedMods.Where(x => entryIds.Contains(x.EntryId, StringComparer.Ordinal)).ToArray();
        if (restored.Length == 0) return false;
        var activeEntryIds = Current.Mods.Select(x => x.EntryId).ToHashSet(StringComparer.Ordinal);
        if (restored.Any(x => !activeEntryIds.Add(x.EntryId)))
            throw new InvalidOperationException("A restored profile entry conflicts with an active entry ID.");
        return CommitProfile("Restore removed profile entries", Current with
        {
            Mods = Current.Mods.Concat(restored).ToArray(),
            RemovedMods = Current.RemovedMods.Where(x => !entryIds.Contains(x.EntryId, StringComparer.Ordinal)).ToArray(),
            ModifiedUtc = DateTimeOffset.UtcNow
        }, restored.Select(x => x.EntryId).ToArray());
    }

    public bool Append(IReadOnlyList<ManagerProfileEntry> entries, string description = "Append profile entries")
    {
        if (entries.Count == 0) return false;
        var existing = Current.Mods.Select(x => x.EntryId).ToHashSet(StringComparer.Ordinal);
        if (entries.Any(x => !existing.Add(x.EntryId))) throw new InvalidOperationException("Appended profile entries contain a duplicate entry ID.");
        return Commit(description, Current.Mods.Concat(entries).ToArray(), entries.Select(x => x.EntryId).ToArray());
    }

    public bool ReplaceMods(IReadOnlyList<ManagerProfileEntry> entries, string description)
    {
        if (entries.Select(x => x.EntryId).Distinct(StringComparer.Ordinal).Count() != entries.Count)
            throw new InvalidOperationException("Replacement profile entries contain a duplicate entry ID.");
        return Commit(description, entries, []);
    }

    public bool Relink(string entryId, ModInstallation installation) => Mutate("Relink installation", [entryId], entry =>
        entry.EntryId == entryId ? entry with
        {
            LogicalModId = installation.LogicalModId,
            Source = installation.Source,
            SourceId = installation.SourceId,
            InstallationIdHint = installation.InstallationId,
            ExpectedVersion = installation.Manifest?.Version ?? installation.Metadata.Version,
            ExpectedContentFingerprint = installation.ContentFingerprint
        } : entry);

    public bool SetNotes(string entryId, string? notes) => Mutate("Edit notes", [entryId], entry => entry.EntryId == entryId ? entry with { Notes = notes } : entry);

    public bool Rename(string displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName) || displayName == Current.DisplayName) return false;
        return CommitProfile("Rename profile", Current with { DisplayName = displayName, ModifiedUtc = DateTimeOffset.UtcNow }, []);
    }

    public bool Acknowledge(string signature, string message)
    {
        if (Current.AcknowledgedWarnings.Any(x => x.Signature == signature)) return false;
        return CommitProfile("Acknowledge warning", Current with
        {
            AcknowledgedWarnings = Current.AcknowledgedWarnings.Concat([new(signature, DateTimeOffset.UtcNow, message)]).ToArray(),
            ModifiedUtc = DateTimeOffset.UtcNow
        }, []);
    }

    public bool ApplySuggestedOrder(IReadOnlyList<string> orderedEnabledLogicalIds)
    {
        var enabledByLogical = Current.Mods.Where(x => x.Enabled).GroupBy(x => x.LogicalModId, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => new Queue<ManagerProfileEntry>(x), StringComparer.Ordinal);
        var suggestion = new Queue<ManagerProfileEntry>();
        foreach (var id in orderedEnabledLogicalIds)
            if (enabledByLogical.TryGetValue(id, out var queue) && queue.Count > 0) suggestion.Enqueue(queue.Dequeue());
        foreach (var entry in Current.Mods.Where(x => x.Enabled)) if (!suggestion.Contains(entry)) suggestion.Enqueue(entry);
        var rebuilt = Current.Mods.Select(entry => entry.Enabled && suggestion.Count > 0 ? suggestion.Dequeue() : entry).ToArray();
        return Commit("Accept suggested order", rebuilt, []);
    }

    public bool Undo()
    {
        if (!undo.TryPop(out var operation)) return false;
        redo.Push(operation);
        Current = operation.Before;
        CurrentSelection = operation.Selection;
        return true;
    }

    public bool Redo()
    {
        if (!redo.TryPop(out var operation)) return false;
        undo.Push(operation);
        Current = operation.After;
        CurrentSelection = operation.Selection;
        return true;
    }

    private bool Move(IReadOnlyCollection<string> selectedEntryIds, string targetEntryId, bool before, string description)
    {
        if (selectedEntryIds.Contains(targetEntryId, StringComparer.Ordinal)) return false;
        var selected = SelectInProfileOrder(selectedEntryIds);
        if (selected.Count == 0) return false;
        var remaining = Current.Mods.Where(x => !selectedEntryIds.Contains(x.EntryId, StringComparer.Ordinal)).ToList();
        var target = remaining.FindIndex(x => x.EntryId == targetEntryId);
        if (target < 0) return false;
        remaining.InsertRange(before ? target : target + 1, selected);
        return Commit(description, remaining, selectedEntryIds);
    }

    private bool MoveToBoundary(IReadOnlyCollection<string> selectedEntryIds, bool top)
    {
        var selected = SelectInProfileOrder(selectedEntryIds);
        if (selected.Count == 0) return false;
        var remaining = Current.Mods.Where(x => !selectedEntryIds.Contains(x.EntryId, StringComparer.Ordinal)).ToList();
        if (top) remaining.InsertRange(0, selected); else remaining.AddRange(selected);
        return Commit(top ? "Move selection to top" : "Move selection to bottom", remaining, selectedEntryIds);
    }

    private bool Mutate(string description, IReadOnlyCollection<string> selection, Func<ManagerProfileEntry, ManagerProfileEntry> mutation) =>
        Commit(description, Current.Mods.Select(mutation).ToArray(), selection);

    private bool Commit(string description, IReadOnlyList<ManagerProfileEntry> mods, IReadOnlyCollection<string> selection)
    {
        if (Current.Mods.SequenceEqual(mods)) return false;
        return CommitProfile(description, Current with { Mods = mods, ModifiedUtc = DateTimeOffset.UtcNow }, selection);
    }

    private bool CommitProfile(string description, ManagerProfile next, IReadOnlyCollection<string> selection)
    {
        if (Current == next) return false;
        var operation = new EditOperation(description, Current, next, selection.ToArray());
        undo.Push(operation);
        while (undo.Count > historyLimit) TrimOldest(undo);
        redo.Clear();
        Current = next;
        CurrentSelection = operation.Selection;
        return true;
    }

    private List<ManagerProfileEntry> SelectInProfileOrder(IReadOnlyCollection<string> selectedEntryIds) =>
        Current.Mods.Where(x => selectedEntryIds.Contains(x.EntryId, StringComparer.Ordinal)).ToList();

    private static void TrimOldest(Stack<EditOperation> stack)
    {
        var items = stack.Reverse().Skip(1).ToArray();
        stack.Clear();
        foreach (var item in items) stack.Push(item);
    }
}

internal static class ProfileListExtensions
{
    public static int IndexOf<T>(this IReadOnlyList<T> list, T item)
    {
        for (var index = 0; index < list.Count; index++) if (EqualityComparer<T>.Default.Equals(list[index], item)) return index;
        return -1;
    }
}
