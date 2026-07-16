namespace ChoirLauncher.Core;

/// <summary>
/// Defines the one priority direction used by the official settings file,
/// conflict resolution, profile ordering, and the desktop presentation.
/// </summary>
public static class ModPriorityOrder
{
    public const int LowestProfileIndex = 0;
    public const int LowestDisplayPriority = 1;
    public const string UserFacingRule =
        "Priority 1 is lowest. Larger priority numbers are higher priority; ChoirLauncher reverses enabled mods when writing Songs of Syx's highest-first MODS array.";

    public static int ToDisplayPriority(int zeroBasedIndex)
    {
        if (zeroBasedIndex < LowestProfileIndex) throw new ArgumentOutOfRangeException(nameof(zeroBasedIndex));
        return checked(zeroBasedIndex + LowestDisplayPriority);
    }

    public static bool IsHigherPriority(int candidateIndex, int otherIndex)
    {
        if (candidateIndex < LowestProfileIndex) throw new ArgumentOutOfRangeException(nameof(candidateIndex));
        if (otherIndex < LowestProfileIndex) throw new ArgumentOutOfRangeException(nameof(otherIndex));
        return candidateIndex > otherIndex;
    }

    public static int FromOfficialIndex(int officialIndex, int enabledCount)
    {
        if (enabledCount < 0) throw new ArgumentOutOfRangeException(nameof(enabledCount));
        if (officialIndex < 0 || officialIndex >= enabledCount) throw new ArgumentOutOfRangeException(nameof(officialIndex));
        return enabledCount - 1 - officialIndex;
    }

    public static IReadOnlyList<T> ToOfficialOrder<T>(IEnumerable<T> profileLowToHigh) =>
        profileLowToHigh.Reverse().ToArray();

    public static IReadOnlyList<T> FromOfficialOrder<T>(IEnumerable<T> officialHighToLow) =>
        officialHighToLow.Reverse().ToArray();
}
