using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace ChoirLauncher.Core;

public sealed record GameLocationPreference(
    int SchemaVersion,
    string GameRoot,
    string SelectionSource,
    DateTimeOffset SavedUtc);

public sealed class GameLocationPreferencesStore
{
    private readonly string path;

    public GameLocationPreferencesStore(ManagerStoragePaths paths)
    {
        path = paths.GameLocation;
    }

    public GameLocationPreference? Load(ICollection<string>? diagnostics = null)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var preference = JsonSerializer.Deserialize<GameLocationPreference>(File.ReadAllText(path, Encoding.UTF8), ManagerJson.Options);
            if (preference is null || preference.SchemaVersion != 1 || string.IsNullOrWhiteSpace(preference.GameRoot))
            {
                diagnostics?.Add("Saved game-location preference is invalid.");
                return null;
            }
            return preference;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            diagnostics?.Add("Could not read the saved game location: " + ex.Message);
            return null;
        }
    }

    public void Save(string gameRoot, string selectionSource)
    {
        if (!SongsOfSyxGameLocation.TryNormalize(gameRoot, out var normalized, out var error))
            throw new InvalidDataException(error);
        var preference = new GameLocationPreference(1, normalized, selectionSource, DateTimeOffset.UtcNow);
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(preference, ManagerJson.Options) + Environment.NewLine);
        AtomicFile.WriteValidated(path, bytes, candidate =>
        {
            var reread = JsonSerializer.Deserialize<GameLocationPreference>(candidate, ManagerJson.Options);
            return reread is { SchemaVersion: 1 } && reread.GameRoot == normalized;
        }, null, 0);
    }
}

public static class SongsOfSyxGameLocation
{
    public const string GameJarName = "SongsOfSyx.jar";

    public static bool TryNormalize(string? candidate, out string normalized, out string error)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(candidate))
        {
            error = "No Songs of Syx folder was selected.";
            return false;
        }

        try
        {
            normalized = Path.GetFullPath(candidate);
            if (!Directory.Exists(normalized))
            {
                error = "The selected Songs of Syx folder does not exist.";
                return false;
            }
            if (!File.Exists(Path.Combine(normalized, GameJarName)))
            {
                error = $"The selected folder does not contain {GameJarName}. Select the main Songs of Syx installation folder.";
                return false;
            }
            error = string.Empty;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            error = "The selected Songs of Syx folder is not accessible: " + ex.Message;
            return false;
        }
    }
}

internal static class SteamGameLocationDiscovery
{
    private const string AppId = "1162750";
    private static readonly Regex SteamPath = new("\"path\"\\s+\"(?<path>[^\"]+)\"", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex InstallDirectory = new("\"installdir\"\\s+\"(?<path>[^\"]+)\"", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    public static SteamDiscoveryResult Discover(string? configuredGameRoot, GameLocationPreference? saved, ICollection<string> diagnostics)
    {
        var steamRoots = SteamRoots(diagnostics);
        var libraries = SteamLibraries(steamRoots, diagnostics);
        var manifestLibraries = libraries.Where(HasAppManifest).ToArray();

        if (TryConfiguredRoot(configuredGameRoot, "CHOIRLAUNCHER_GAME_ROOT", libraries, diagnostics, out var configured))
            return configured with { SteamRoot = steamRoots.FirstOrDefault(), ManifestLibrary = configured.ManifestLibrary ?? manifestLibraries.FirstOrDefault() };

        if (saved is not null)
        {
            if (TryConfiguredRoot(saved.GameRoot, "saved game location", libraries, diagnostics, out var remembered))
                return remembered with { SteamRoot = steamRoots.FirstOrDefault(), ManifestLibrary = remembered.ManifestLibrary ?? manifestLibraries.FirstOrDefault() };
            diagnostics.Add("The saved Songs of Syx folder is no longer valid; automatic discovery will be retried.");
        }

        foreach (var library in manifestLibraries)
        {
            var manifest = AppManifest(library);
            var installDirectory = ReadInstallDirectory(manifest, diagnostics) ?? "Songs of Syx";
            var candidate = Path.Combine(library, "steamapps", "common", installDirectory);
            if (SongsOfSyxGameLocation.TryNormalize(candidate, out var normalized, out _))
                return new(steamRoots.FirstOrDefault(), library, library, normalized, "steam-appmanifest");
            diagnostics.Add($"Steam app manifest found, but its Songs of Syx folder is invalid: {candidate}");
        }

        diagnostics.Add("A valid Songs of Syx installation folder was not discovered automatically.");
        return new(steamRoots.FirstOrDefault(), manifestLibraries.FirstOrDefault(), manifestLibraries.FirstOrDefault(), null, null);
    }

    private static bool TryConfiguredRoot(string? candidate, string source, IReadOnlyList<string> libraries,
        ICollection<string> diagnostics, out SteamDiscoveryResult result)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            result = default!;
            return false;
        }
        if (!SongsOfSyxGameLocation.TryNormalize(candidate, out var normalized, out var error))
        {
            diagnostics.Add($"Invalid {source}: {error}");
            result = default!;
            return false;
        }
        var library = InferLibrary(normalized) ?? libraries.FirstOrDefault(path => IsWithin(normalized, path));
        result = new(null, library, library, normalized, source);
        return true;
    }

    private static IReadOnlyList<string> SteamRoots(ICollection<string> diagnostics)
    {
        var candidates = new List<string?>
        {
            Environment.GetEnvironmentVariable("CHOIRLAUNCHER_STEAM_ROOT")
        };
        if (OperatingSystem.IsWindows())
        {
            candidates.Add(ReadRegistryPath(RegistryHive.CurrentUser, RegistryView.Default, @"Software\Valve\Steam", "SteamPath", diagnostics));
            candidates.Add(ReadRegistryExecutableDirectory(RegistryHive.CurrentUser, RegistryView.Default, @"Software\Valve\Steam", "SteamExe", diagnostics));
            candidates.Add(ReadRegistryPath(RegistryHive.LocalMachine, RegistryView.Registry64, @"Software\Valve\Steam", "InstallPath", diagnostics));
            candidates.Add(ReadRegistryPath(RegistryHive.LocalMachine, RegistryView.Registry32, @"Software\Valve\Steam", "InstallPath", diagnostics));
        }
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"));
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Steam"));
        return ExistingDistinctDirectories(candidates);
    }

    private static IReadOnlyList<string> SteamLibraries(IReadOnlyList<string> steamRoots, ICollection<string> diagnostics)
    {
        var candidates = new List<string?>();
        foreach (var steamRoot in steamRoots)
        {
            candidates.Add(steamRoot);
            var libraryFile = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(libraryFile)) continue;
            try
            {
                foreach (Match match in SteamPath.Matches(File.ReadAllText(libraryFile, Encoding.UTF8)))
                    candidates.Add(UnescapeValvePath(match.Groups["path"].Value));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                diagnostics.Add("Could not read Steam library metadata: " + ex.Message);
            }
        }
        return ExistingDistinctDirectories(candidates);
    }

    private static IReadOnlyList<string> ExistingDistinctDirectories(IEnumerable<string?> candidates)
    {
        var results = new List<string>();
        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            try
            {
                var full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(candidate));
                if (Directory.Exists(full) && !results.Contains(full, StringComparer.OrdinalIgnoreCase)) results.Add(full);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException) { }
        }
        return results;
    }

    private static string? ReadInstallDirectory(string manifest, ICollection<string> diagnostics)
    {
        try
        {
            var match = InstallDirectory.Match(File.ReadAllText(manifest, Encoding.UTF8));
            if (!match.Success) return null;
            var value = UnescapeValvePath(match.Groups["path"].Value);
            if (Path.IsPathRooted(value) || value is "." or ".." || value.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0)
            {
                diagnostics.Add("The Songs of Syx Steam app manifest contains an unsafe installdir value.");
                return null;
            }
            return value;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            diagnostics.Add("Could not read the Songs of Syx Steam app manifest: " + ex.Message);
            return null;
        }
    }

    [SupportedOSPlatform("windows")]
    private static string? ReadRegistryPath(RegistryHive hive, RegistryView view, string keyPath, string valueName, ICollection<string> diagnostics)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var key = baseKey.OpenSubKey(keyPath);
            return key?.GetValue(valueName) as string;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            diagnostics.Add("Could not read Steam installation metadata from the Windows registry: " + ex.Message);
            return null;
        }
    }

    [SupportedOSPlatform("windows")]
    private static string? ReadRegistryExecutableDirectory(RegistryHive hive, RegistryView view, string keyPath, string valueName, ICollection<string> diagnostics)
    {
        var executable = ReadRegistryPath(hive, view, keyPath, valueName, diagnostics);
        return string.IsNullOrWhiteSpace(executable) ? null : Path.GetDirectoryName(executable.Replace('/', Path.DirectorySeparatorChar));
    }

    private static string AppManifest(string library) => Path.Combine(library, "steamapps", $"appmanifest_{AppId}.acf");
    private static bool HasAppManifest(string library) => File.Exists(AppManifest(library));
    private static string UnescapeValvePath(string path) => path.Replace("\\\\", "\\", StringComparison.Ordinal).Replace('/', Path.DirectorySeparatorChar);

    private static string? InferLibrary(string gameRoot)
    {
        var common = Directory.GetParent(gameRoot);
        var steamApps = common?.Parent;
        var library = steamApps?.Parent;
        return common?.Name.Equals("common", StringComparison.OrdinalIgnoreCase) == true &&
               steamApps?.Name.Equals("steamapps", StringComparison.OrdinalIgnoreCase) == true
            ? library?.FullName
            : null;
    }

    private static bool IsWithin(string path, string root)
    {
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }
}

internal sealed record SteamDiscoveryResult(
    string? SteamRoot,
    string? SteamLibrary,
    string? ManifestLibrary,
    string? GameRoot,
    string? Source);
