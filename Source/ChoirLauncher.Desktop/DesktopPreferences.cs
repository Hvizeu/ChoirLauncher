using System.Text;
using System.Text.Json;
using ChoirLauncher.Core;

namespace ChoirLauncher.Desktop;

public enum LauncherLaunchAction
{
    ApplyProfileAndLaunch,
    LaunchCurrentOfficialState,
    OpenOfficialLauncher
}

public sealed record DesktopPreferences(
    int SchemaVersion = 1,
    string? LastProfileId = null,
    double? WindowWidth = null,
    double? WindowHeight = null,
    double? WindowX = null,
    double? WindowY = null,
    bool Maximized = false,
    IReadOnlyList<double>? ModListColumnWidths = null,
    LauncherLaunchAction DefaultLaunchAction = LauncherLaunchAction.ApplyProfileAndLaunch,
    bool LauncherDeveloperMode = false);

public sealed class DesktopPreferencesStore
{
    private readonly string path;
    public DesktopPreferencesStore(ManagerStoragePaths paths) { paths.EnsureCreated(); path = paths.Preferences; }

    public DesktopPreferences Load()
    {
        try
        {
            if (!File.Exists(path)) return new();
            return JsonSerializer.Deserialize<DesktopPreferences>(File.ReadAllText(path, Encoding.UTF8), ManagerJson.Options) ?? new();
        }
        catch (JsonException) { return new(); }
        catch (IOException) { return new(); }
    }

    public void Save(DesktopPreferences preferences) => AtomicFile.WriteValidated(path,
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(preferences, ManagerJson.Options) + Environment.NewLine),
        bytes => JsonSerializer.Deserialize<DesktopPreferences>(bytes, ManagerJson.Options)?.SchemaVersion == 1,
        null, 0);
}
