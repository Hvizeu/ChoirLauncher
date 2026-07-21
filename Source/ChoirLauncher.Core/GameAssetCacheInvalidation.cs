namespace ChoirLauncher.Core;

public sealed record GameAssetCacheInvalidationResult(
    IReadOnlyList<GameAssetCacheInvalidationRule> Rules,
    IReadOnlyList<string> DeletedRelativeFiles,
    IReadOnlyList<string> MissingRelativeFiles,
    IReadOnlyList<string> Diagnostics);

public interface IGameAssetCacheInvalidator
{
    GameAssetCacheInvalidationResult Invalidate(SongsOfSyxEnvironment environment, JavaAgentLaunchPlan javaAgentPlan);
}

public sealed class SongsOfSyxAssetCacheInvalidator : IGameAssetCacheInvalidator
{
    public GameAssetCacheInvalidationResult Invalidate(SongsOfSyxEnvironment environment, JavaAgentLaunchPlan javaAgentPlan)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(javaAgentPlan);

        var rules = javaAgentPlan.Entries
            .SelectMany(entry => entry.AssetCacheInvalidations)
            .GroupBy(rule => rule.RuleId, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(rule => rule.RuleId, StringComparer.Ordinal)
            .ToArray();
        if (rules.Length == 0) return new([], [], [], []);

        var diagnostics = new List<string>();
        var deleted = new List<string>();
        var missing = new List<string>();
        var cacheRoot = ResolveCacheRoot(environment.LauncherSettingsPath);
        foreach (var relative in rules
            .SelectMany(rule => rule.RelativeCacheFiles)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase))
        {
            if (!IsSafeRelativeCachePath(relative))
            {
                diagnostics.Add($"Skipped unsafe cache path from asset-cache policy: {relative}.");
                continue;
            }

            var full = Path.GetFullPath(Path.Combine(cacheRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!IsWithin(full, cacheRoot))
            {
                diagnostics.Add($"Skipped cache path outside Songs of Syx cache root: {relative}.");
                continue;
            }

            if (!File.Exists(full))
            {
                missing.Add(relative);
                continue;
            }

            var attributes = File.GetAttributes(full);
            if ((attributes & FileAttributes.Directory) != 0)
            {
                diagnostics.Add($"Skipped cache path because it is a directory: {relative}.");
                continue;
            }
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                diagnostics.Add($"Skipped cache path because it is a reparse point: {relative}.");
                continue;
            }

            File.Delete(full);
            deleted.Add(relative);
        }

        diagnostics.Add($"Asset cache invalidation prepared {rules.Length} rule(s); deleted {deleted.Count} file(s), {missing.Count} already absent.");
        return new(rules, deleted, missing, diagnostics);
    }

    private static string ResolveCacheRoot(string launcherSettingsPath)
    {
        var settings = Path.GetFullPath(launcherSettingsPath);
        var settingsDirectory = Path.GetDirectoryName(settings) ?? throw new InvalidDataException("LauncherSettings.txt path has no directory.");
        var userRoot = new DirectoryInfo(settingsDirectory).Name.Equals("settings", StringComparison.OrdinalIgnoreCase)
            ? Path.GetFullPath(Path.Combine(settingsDirectory, ".."))
            : settingsDirectory;
        var cacheRoot = Path.GetFullPath(Path.Combine(userRoot, "cache"));
        if (!IsWithin(cacheRoot, userRoot)) throw new InvalidDataException("Resolved cache root is outside the Songs of Syx user-data directory.");
        return cacheRoot;
    }

    private static bool IsSafeRelativeCachePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return false;
        if (relativePath.IndexOfAny(['\0', '\r', '\n', '"']) >= 0) return false;
        if (relativePath.StartsWith('/') || relativePath.StartsWith('\\') || Path.IsPathRooted(relativePath)) return false;
        var normalized = relativePath.Replace('\\', '/');
        return normalized.Split('/').All(part => part.Length > 0 && part != "." && part != "..");
    }

    private static bool IsWithin(string path, string root)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path));
        return relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) && !Path.IsPathRooted(relative);
    }
}
