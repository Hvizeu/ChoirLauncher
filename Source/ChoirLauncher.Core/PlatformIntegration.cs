namespace ChoirLauncher.Core;

public enum DesktopPlatform
{
    Windows,
    Linux,
    MacOS
}

public static class HostPlatform
{
    public static DesktopPlatform Current =>
        OperatingSystem.IsWindows() ? DesktopPlatform.Windows :
        OperatingSystem.IsLinux() ? DesktopPlatform.Linux :
        OperatingSystem.IsMacOS() ? DesktopPlatform.MacOS :
        throw new PlatformNotSupportedException("ChoirLauncher supports Windows, Linux, and macOS desktop systems.");

    public static StringComparison PathComparison(DesktopPlatform? platform = null) =>
        (platform ?? Current) == DesktopPlatform.Windows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static StringComparer PathComparer(DesktopPlatform? platform = null) =>
        (platform ?? Current) == DesktopPlatform.Windows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public static bool PathsEqual(string left, string right, DesktopPlatform? platform = null) =>
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), PathComparison(platform));

    public static bool IsWithin(string path, string root)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path));
        return relative != ".." &&
               !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
               !Path.IsPathRooted(relative);
    }

    public static string HomeDirectory()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(home)) home = Environment.GetEnvironmentVariable("HOME");
        if (string.IsNullOrWhiteSpace(home)) throw new DirectoryNotFoundException("The current user's home directory could not be determined.");
        return Path.GetFullPath(home);
    }
}

public static class SongsOfSyxUserDataPaths
{
    public static string ResolveRoot(DesktopPlatform? platform = null, string? homeOverride = null, string? windowsRoamingOverride = null)
    {
        var configured = Environment.GetEnvironmentVariable("CHOIRLAUNCHER_USER_DATA_ROOT");
        if (!string.IsNullOrWhiteSpace(configured)) return Path.GetFullPath(configured);

        var current = platform ?? HostPlatform.Current;
        var home = string.IsNullOrWhiteSpace(homeOverride) ? HostPlatform.HomeDirectory() : Path.GetFullPath(homeOverride);
        return current switch
        {
            DesktopPlatform.Windows => Path.GetFullPath(Path.Combine(
                windowsRoamingOverride ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "songsofsyx")),
            DesktopPlatform.MacOS => Path.Combine(home, "Library", "Application Support", "songsofsyx"),
            DesktopPlatform.Linux => Path.Combine(home, ".local", "share", "songsofsyx"),
            _ => throw new PlatformNotSupportedException("Unsupported desktop platform.")
        };
    }

    public static string ResolveSettingsPath(DesktopPlatform? platform = null) =>
        Path.Combine(ResolveRoot(platform), "settings", "LauncherSettings.txt");

    public static string ResolveLocalModsRoot(DesktopPlatform? platform = null) =>
        Path.Combine(ResolveRoot(platform), "mods");
}

public static class ChoirLauncherPlatformPaths
{
    public static string ResolveStorageRoot(DesktopPlatform? platform = null, string? homeOverride = null)
    {
        var current = platform ?? HostPlatform.Current;
        if (current == DesktopPlatform.Windows)
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), BuildInfo.ProductName);

        var home = string.IsNullOrWhiteSpace(homeOverride) ? HostPlatform.HomeDirectory() : Path.GetFullPath(homeOverride);
        if (current == DesktopPlatform.MacOS)
            return Path.Combine(home, "Library", "Application Support", BuildInfo.ProductName);

        var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        var data = string.IsNullOrWhiteSpace(xdg) ? Path.Combine(home, ".local", "share") : Path.GetFullPath(xdg);
        return Path.Combine(data, BuildInfo.ProductName);
    }
}
