using System.IO.Compression;

namespace ChoirLauncher.Core;

public sealed class VanillaContentIndex
{
    private const int MaxArchiveEntries = 500_000;
    private readonly HashSet<string> classes;
    private readonly Dictionary<string, string> dataPathsByInsensitivePath;

    private VanillaContentIndex(
        HashSet<string> classes,
        Dictionary<string, string> dataPathsByInsensitivePath,
        VanillaComparisonSummary summary)
    {
        this.classes = classes;
        this.dataPathsByInsensitivePath = dataPathsByInsensitivePath;
        Summary = summary;
    }

    public static VanillaContentIndex Empty { get; } = new(
        new(StringComparer.Ordinal),
        new(StringComparer.OrdinalIgnoreCase),
        VanillaComparisonSummary.Unavailable);

    public VanillaComparisonSummary Summary { get; }

    public bool ContainsClass(string className) => classes.Contains(className);

    public bool TryGetDataPath(string runtimePath, out string canonicalPath) =>
        dataPathsByInsensitivePath.TryGetValue(NormalizeRuntimePath(runtimePath), out canonicalPath!);

    public static VanillaContentIndex Build(string? gameJarPath)
    {
        if (string.IsNullOrWhiteSpace(gameJarPath) || !File.Exists(gameJarPath))
            return Empty;

        var diagnostics = new List<string>();
        var classes = new HashSet<string>(StringComparer.Ordinal);
        var dataPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        ReadClasses(gameJarPath, classes, diagnostics);

        var gameRoot = Path.GetDirectoryName(Path.GetFullPath(gameJarPath));
        var dataZip = gameRoot is null ? null : Path.Combine(gameRoot, "base", "data.zip");
        if (dataZip is not null && File.Exists(dataZip))
            ReadDataPaths(dataZip, dataPaths, diagnostics);
        else
            diagnostics.Add("Vanilla data.zip was not found next to the selected game installation.");

        var available = classes.Count > 0 || dataPaths.Count > 0;
        return new(classes, dataPaths, new(available, classes.Count, dataPaths.Count, diagnostics.ToArray()));
    }

    private static void ReadClasses(string path, HashSet<string> output, List<string> diagnostics)
    {
        try
        {
            using var archive = ZipFile.OpenRead(path);
            if (archive.Entries.Count > MaxArchiveEntries)
                throw new InvalidDataException($"SongsOfSyx.jar exceeds the {MaxArchiveEntries} entry safety limit.");

            foreach (var entry in archive.Entries)
            {
                if (!entry.FullName.EndsWith(".class", StringComparison.Ordinal) ||
                    entry.FullName.StartsWith("META-INF/", StringComparison.OrdinalIgnoreCase))
                    continue;
                output.Add(entry.FullName[..^6].Replace('/', '.'));
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            diagnostics.Add($"SongsOfSyx.jar class index failed: {ex.Message}");
        }
    }

    private static void ReadDataPaths(string path, Dictionary<string, string> output, List<string> diagnostics)
    {
        try
        {
            using var archive = ZipFile.OpenRead(path);
            if (archive.Entries.Count > MaxArchiveEntries)
                throw new InvalidDataException($"data.zip exceeds the {MaxArchiveEntries} entry safety limit.");

            foreach (var entry in archive.Entries)
            {
                if (entry.FullName.EndsWith("/", StringComparison.Ordinal)) continue;
                var normalized = NormalizeRuntimePath(entry.FullName);
                output.TryAdd(normalized, normalized);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            diagnostics.Add($"Vanilla data.zip index failed: {ex.Message}");
        }
    }

    internal static string NormalizeRuntimePath(string path)
    {
        var normalized = path.Replace('\\', '/').TrimStart('/');
        return normalized.StartsWith("data/", StringComparison.OrdinalIgnoreCase) ? normalized[5..] : normalized;
    }
}
