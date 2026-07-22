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
    public static SongsOfSyxEnvironment Locate(ManagerStoragePaths? storage = null)
    {
        var diagnostics = new List<string>();
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var settings = Environment.GetEnvironmentVariable("CHOIRLAUNCHER_SETTINGS_PATH") ?? Path.Combine(appData, "songsofsyx", "settings", "LauncherSettings.txt");
        var local = Environment.GetEnvironmentVariable("CHOIRLAUNCHER_LOCAL_MODS") ?? Path.Combine(appData, "songsofsyx", "mods");
        var locationStore = new GameLocationPreferencesStore(storage ?? ManagerStoragePaths.Resolve());
        var saved = locationStore.Load(diagnostics);
        var discovered = SteamGameLocationDiscovery.Discover(Environment.GetEnvironmentVariable("CHOIRLAUNCHER_GAME_ROOT"), saved, diagnostics);
        var library = discovered.SteamLibrary ?? discovered.ManifestLibrary;
        if (library is null) diagnostics.Add("Steam library containing app 1162750 was not found.");
        var workshop = Environment.GetEnvironmentVariable("CHOIRLAUNCHER_WORKSHOP_MODS") ?? (library is null ? null : Path.Combine(library, "steamapps", "workshop", "content", "1162750"));
        var gameRoot = discovered.GameRoot;
        var jar = gameRoot is null ? null : Path.Combine(gameRoot, "SongsOfSyx.jar");
        if (!File.Exists(settings)) diagnostics.Add("Official LauncherSettings.txt was not found.");
        if (!Directory.Exists(local)) diagnostics.Add("Local mod directory was not found.");
        if (workshop is null || !Directory.Exists(workshop)) diagnostics.Add("Workshop mod directory was not found.");
        if (jar is null || !File.Exists(jar)) diagnostics.Add("SongsOfSyx.jar was not found.");
        return new(Path.GetFullPath(settings), Path.GetFullPath(local), discovered.SteamRoot, library,
            workshop is null ? null : Path.GetFullPath(workshop), gameRoot is null ? null : Path.GetFullPath(gameRoot),
            jar is null ? null : Path.GetFullPath(jar), diagnostics);
    }
}
