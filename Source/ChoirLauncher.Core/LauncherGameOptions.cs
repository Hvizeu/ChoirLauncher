using System.Globalization;
using System.Text;
using System.Text.Json;

namespace ChoirLauncher.Core;

public enum GameScreenMode
{
    Borderless = 0,
    FullScreen = 1,
    Windowed = 2
}

public static class LauncherOptionPresentation
{
    public static int WindowPercent(int rawValue) => checked(rawValue * 5);
    public static int BorderlessScalePercent(int rawValue) => checked(100 + rawValue * 5);
}

public sealed record LauncherGameOptions(
    bool Debug,
    bool Developer,
    bool LinearFiltering,
    bool Shading,
    bool VSync,
    bool AdaptiveVSync,
    bool AutoIconify,
    bool WindowFix,
    bool WindowFloat,
    bool EasyUi,
    string AudioDevice,
    int FpsCap,
    GameScreenMode ScreenMode,
    int Monitor,
    int FullScreenDisplay,
    int WindowWidth,
    int WindowHeight,
    int BorderlessScale,
    bool WindowBorders,
    bool ForcedHd,
    string Language)
{
    public static readonly string[] SupportedKeys =
    [
        "DEBUG", "DEVELOPER", "LINEAR", "SHADING", "VSYNC", "VSYNC_ADAPTIVE", "WIN_AUTO_ICONIFY",
        "WINDOW_FULL_FULL", "WINDOW_FLOAT", "EASY_FONT", "OPENAL", "FPS_CAP", "SCREEN_MODE", "MONITOR",
        "FULL_DISPLAY", "WINDOW_WIDTH", "WIDOW_HEIGHT", "WIDOW_SCALE", "WINDOW_DECORATE", "WINDOW_FORCE_HD", "LANGUAGE"
    ];

    public static LauncherGameOptions From(LauncherSettingsDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var mode = document.ReadInt("SCREEN_MODE");
        if (!Enum.IsDefined(typeof(GameScreenMode), mode)) throw new FormatException($"Unsupported SCREEN_MODE value {mode}.");
        return new(
            Bool(document, "DEBUG"), Bool(document, "DEVELOPER"), Bool(document, "LINEAR"), Bool(document, "SHADING"),
            Bool(document, "VSYNC"), Bool(document, "VSYNC_ADAPTIVE"), Bool(document, "WIN_AUTO_ICONIFY"),
            Bool(document, "WINDOW_FULL_FULL"), Bool(document, "WINDOW_FLOAT"), Bool(document, "EASY_FONT"),
            document.ReadString("OPENAL"), document.ReadInt("FPS_CAP"), (GameScreenMode)mode, document.ReadInt("MONITOR"),
            document.ReadInt("FULL_DISPLAY"), document.ReadInt("WINDOW_WIDTH"), document.ReadInt("WIDOW_HEIGHT"),
            document.ReadInt("WIDOW_SCALE"), Bool(document, "WINDOW_DECORATE"), Bool(document, "WINDOW_FORCE_HD"),
            document.ReadString("LANGUAGE"));
    }

    public static LauncherGameOptions VerifiedDefaults(string language = "") => new(
        false, false, true, true, false, false, true, false, false, false,
        string.Empty, 0, GameScreenMode.Borderless, 0, 0, 15, 15, 0, true, false, language);

    public string ApplyTo(LauncherSettingsDocument document)
    {
        Validate();
        return document.WithScalarValues(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["DEBUG"] = Bit(Debug), ["DEVELOPER"] = Bit(Developer), ["LINEAR"] = Bit(LinearFiltering),
            ["SHADING"] = Bit(Shading), ["VSYNC"] = Bit(VSync), ["VSYNC_ADAPTIVE"] = Bit(AdaptiveVSync),
            ["WIN_AUTO_ICONIFY"] = Bit(AutoIconify), ["WINDOW_FULL_FULL"] = Bit(WindowFix), ["WINDOW_FLOAT"] = Bit(WindowFloat),
            ["EASY_FONT"] = Bit(EasyUi), ["OPENAL"] = JsonSerializer.Serialize(AudioDevice),
            ["FPS_CAP"] = FpsCap.ToString(CultureInfo.InvariantCulture), ["SCREEN_MODE"] = ((int)ScreenMode).ToString(CultureInfo.InvariantCulture),
            ["MONITOR"] = Monitor.ToString(CultureInfo.InvariantCulture), ["FULL_DISPLAY"] = FullScreenDisplay.ToString(CultureInfo.InvariantCulture),
            ["WINDOW_WIDTH"] = WindowWidth.ToString(CultureInfo.InvariantCulture), ["WIDOW_HEIGHT"] = WindowHeight.ToString(CultureInfo.InvariantCulture),
            ["WIDOW_SCALE"] = BorderlessScale.ToString(CultureInfo.InvariantCulture), ["WINDOW_DECORATE"] = Bit(WindowBorders),
            ["WINDOW_FORCE_HD"] = Bit(ForcedHd), ["LANGUAGE"] = JsonSerializer.Serialize(Language)
        });
    }

    public void Validate()
    {
        if (!Enum.IsDefined(ScreenMode)) throw new ArgumentOutOfRangeException(nameof(ScreenMode));
        Range(FpsCap, 0, 100, nameof(FpsCap));
        if (FpsCap is > 0 and < 40 || FpsCap > 40 && FpsCap % 20 != 0)
            throw new ArgumentOutOfRangeException(nameof(FpsCap), "The v71.44 launcher supports Screen, 40, 60, 80, or 100 FPS.");
        Range(Monitor, 0, 255, nameof(Monitor));
        Range(FullScreenDisplay, 0, 4096, nameof(FullScreenDisplay));
        Range(WindowWidth, 0, 20, nameof(WindowWidth));
        Range(WindowHeight, 0, 20, nameof(WindowHeight));
        Range(BorderlessScale, 0, 100, nameof(BorderlessScale));
        SingleLine(AudioDevice, nameof(AudioDevice));
        SingleLine(Language, nameof(Language));
    }

    private static bool Bool(LauncherSettingsDocument document, string key) => document.ReadInt(key) switch
    {
        0 => false,
        1 => true,
        var value => throw new FormatException($"Launcher setting {key} must be 0 or 1, not {value}.")
    };

    private static string Bit(bool value) => value ? "1" : "0";
    private static void Range(int value, int minimum, int maximum, string name)
    {
        if (value < minimum || value > maximum) throw new ArgumentOutOfRangeException(name, value, $"Expected {minimum}..{maximum}.");
    }
    private static void SingleLine(string value, string name)
    {
        ArgumentNullException.ThrowIfNull(value, name);
        if (value.IndexOfAny(['\r', '\n', '\0']) >= 0) throw new ArgumentException("Value must be a single line without NUL.", name);
    }
}

public sealed record LauncherOptionChange(string Key, string Before, string After);

public sealed record LauncherOptionsPreview(
    string TargetPath,
    string CurrentSha256,
    string ProposedSha256,
    string ProtectedContentSha256,
    LauncherGameOptions Current,
    LauncherGameOptions Proposed,
    IReadOnlyList<LauncherOptionChange> Changes,
    string ProposedText,
    DateTimeOffset CreatedUtc)
{
    public bool HasChanges => Changes.Count > 0;
}

public static class LauncherOptionsService
{
    public static LauncherGameOptions Load(string targetPath) => LauncherGameOptions.From(
        LauncherSettingsDocument.Parse(File.ReadAllText(Path.GetFullPath(targetPath), Encoding.UTF8)));

    public static LauncherOptionsPreview CreatePreview(string targetPath, LauncherGameOptions proposed)
    {
        var full = Path.GetFullPath(targetPath);
        var bytes = File.ReadAllBytes(full);
        var text = Encoding.UTF8.GetString(bytes);
        var document = LauncherSettingsDocument.Parse(text);
        var current = LauncherGameOptions.From(document);
        proposed.Validate();
        var proposedText = proposed.ApplyTo(document);
        var parsed = LauncherGameOptions.From(LauncherSettingsDocument.Parse(proposedText));
        if (parsed != proposed) throw new InvalidDataException("Launcher option proposal failed its round-trip check.");
        var changes = Changes(current, proposed);
        return new(full, Hashing.Sha256(bytes), Hashing.Sha256(Encoding.UTF8.GetBytes(proposedText)),
            document.ContentExcludingScalarValuesSha256(LauncherGameOptions.SupportedKeys), current, proposed, changes, proposedText, DateTimeOffset.UtcNow);
    }

    private static IReadOnlyList<LauncherOptionChange> Changes(LauncherGameOptions before, LauncherGameOptions after)
    {
        var changes = new List<LauncherOptionChange>();
        void Add(string key, object oldValue, object newValue)
        {
            if (!Equals(oldValue, newValue)) changes.Add(new(key, Display(oldValue), Display(newValue)));
        }
        Add("Debug", before.Debug, after.Debug); Add("Developer", before.Developer, after.Developer);
        Add("Linear filtering", before.LinearFiltering, after.LinearFiltering); Add("Shading", before.Shading, after.Shading);
        Add("VSync", before.VSync, after.VSync); Add("Adaptive VSync", before.AdaptiveVSync, after.AdaptiveVSync);
        Add("Auto iconify", before.AutoIconify, after.AutoIconify); Add("Window fix", before.WindowFix, after.WindowFix);
        Add("Window float", before.WindowFloat, after.WindowFloat); Add("Easy UI", before.EasyUi, after.EasyUi);
        Add("Audio device", before.AudioDevice, after.AudioDevice); Add("FPS cap", before.FpsCap, after.FpsCap);
        Add("Screen mode", before.ScreenMode, after.ScreenMode); Add("Monitor", before.Monitor, after.Monitor);
        Add("Full-screen mode index", before.FullScreenDisplay, after.FullScreenDisplay); Add("Window width", before.WindowWidth, after.WindowWidth);
        Add("Window height", before.WindowHeight, after.WindowHeight); Add("Borderless scale", before.BorderlessScale, after.BorderlessScale);
        Add("Window borders", before.WindowBorders, after.WindowBorders); Add("Forced HD", before.ForcedHd, after.ForcedHd);
        Add("Language", before.Language, after.Language);
        return changes;
    }

    private static string Display(object value) => value switch
    {
        bool flag => flag ? "On" : "Off",
        string text when text.Length == 0 => "Default",
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty
    };
}
