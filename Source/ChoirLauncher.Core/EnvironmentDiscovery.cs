using System.Text.RegularExpressions;

namespace ChoirLauncher.Core;

public sealed record SongsOfSyxEnvironment(
    string LauncherSettingsPath,
    string LocalModsRoot,
    string? SteamRoot,
    string? SteamLibrary,
    string? WorkshopModsRoot,
    string? GameRoot,
    string? GameJarPath,
    IReadOnlyList<string> Diagnostics);

public static class SongsOfSyxEnvironmentLocator
{
    private static readonly Regex SteamPath = new("\"path\"\\s+\"(?<path>[^\"]+)\"", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    public static SongsOfSyxEnvironment Locate()
    {
        var diagnostics = new List<string>();
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var settings = Environment.GetEnvironmentVariable("CHOIRLAUNCHER_SETTINGS_PATH") ?? Path.Combine(appData, "songsofsyx", "settings", "LauncherSettings.txt");
        var local = Environment.GetEnvironmentVariable("CHOIRLAUNCHER_LOCAL_MODS") ?? Path.Combine(appData, "songsofsyx", "mods");
        var steamRoot = Environment.GetEnvironmentVariable("CHOIRLAUNCHER_STEAM_ROOT") ?? DefaultSteamRoot();
        var libraries = new List<string>();
        if (steamRoot is not null)
        {
            libraries.Add(steamRoot);
            var libraryFile = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
            if (File.Exists(libraryFile))
            {
                try
                {
                    var text = File.ReadAllText(libraryFile);
                    foreach (Match match in SteamPath.Matches(text))
                    {
                        var path = match.Groups["path"].Value.Replace("\\\\", "\\", StringComparison.Ordinal);
                        if (Directory.Exists(path) && !libraries.Contains(path, StringComparer.OrdinalIgnoreCase)) libraries.Add(path);
                    }
                }
                catch (IOException ex) { diagnostics.Add("Could not read Steam library metadata: " + ex.Message); }
            }
        }
        var library = libraries.FirstOrDefault(path => File.Exists(Path.Combine(path, "steamapps", "appmanifest_1162750.acf")));
        if (library is null) diagnostics.Add("Steam library containing app 1162750 was not found.");
        var workshop = Environment.GetEnvironmentVariable("CHOIRLAUNCHER_WORKSHOP_MODS") ?? (library is null ? null : Path.Combine(library, "steamapps", "workshop", "content", "1162750"));
        var gameRoot = Environment.GetEnvironmentVariable("CHOIRLAUNCHER_GAME_ROOT") ?? (library is null ? null : Path.Combine(library, "steamapps", "common", "Songs of Syx"));
        var jar = gameRoot is null ? null : Path.Combine(gameRoot, "SongsOfSyx.jar");
        if (!File.Exists(settings)) diagnostics.Add("Official LauncherSettings.txt was not found.");
        if (!Directory.Exists(local)) diagnostics.Add("Local mod directory was not found.");
        if (workshop is null || !Directory.Exists(workshop)) diagnostics.Add("Workshop mod directory was not found.");
        if (jar is null || !File.Exists(jar)) diagnostics.Add("SongsOfSyx.jar was not found.");
        return new(Path.GetFullPath(settings), Path.GetFullPath(local), steamRoot, library,
            workshop is null ? null : Path.GetFullPath(workshop), gameRoot is null ? null : Path.GetFullPath(gameRoot),
            jar is null ? null : Path.GetFullPath(jar), diagnostics);
    }

    private static string? DefaultSteamRoot()
    {
        var candidate = Environment.Is64BitOperatingSystem
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Steam");
        return Directory.Exists(candidate) ? candidate : null;
    }
}
