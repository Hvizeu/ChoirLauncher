using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace ChoirLauncher.Core;

public sealed record GameInstallationInfo(
    string GameVersion,
    string Platform,
    string JavaRuntime,
    string GraphicsProcessor,
    string GraphicsDriver,
    string LocalFiles,
    string Saves,
    string Screenshots,
    string Mods,
    string? GameRoot,
    string? GameJarPath,
    string? GameJarSha256,
    bool VerifiedV7144,
    IReadOnlyList<string> Diagnostics);

public static class GameInstallationInfoService
{
    public const string V7144JarSha256 = "fe0137c05408e5ee9e31f06fc44fe37a5d4372d9c985b280f0852d1347c0224b";
    private static readonly Regex ReleaseValue = new("(?m)^(?<key>[A-Z0-9_]+)=\"(?<value>[^\"]*)\"$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static GameInstallationInfo Discover(SongsOfSyxEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        var diagnostics = new List<string>();
        string? jarHash = null;
        if (environment.GameJarPath is not null && File.Exists(environment.GameJarPath))
        {
            try { jarHash = Hashing.Sha256File(environment.GameJarPath); }
            catch (IOException ex) { diagnostics.Add("Could not fingerprint SongsOfSyx.jar: " + ex.Message); }
        }
        var verified = string.Equals(jarHash, V7144JarSha256, StringComparison.Ordinal);
        var gameVersion = verified ? BuildInfo.TargetGameVersion : "Unknown / unsupported artifact";
        var java = ReadJavaRelease(environment.GameRoot, diagnostics);
        var (gpu, driver) = ReadGraphics(diagnostics);
        var localRoot = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(environment.LauncherSettingsPath)!, ".."));
        return new(gameVersion, RuntimeInformation.OSDescription.Trim(), java, gpu, driver, localRoot,
            Path.Combine(localRoot, "saves", "saves"), Path.Combine(localRoot, "screenshots"), environment.LocalModsRoot,
            environment.GameRoot, environment.GameJarPath, jarHash, verified, diagnostics);
    }

    private static string ReadJavaRelease(string? gameRoot, ICollection<string> diagnostics)
    {
        var path = gameRoot is null ? null : Path.Combine(gameRoot, "jre", "release");
        if (path is null || !File.Exists(path)) return "Bundled JRE not found";
        try
        {
            var values = ReleaseValue.Matches(File.ReadAllText(path)).ToDictionary(m => m.Groups["key"].Value, m => m.Groups["value"].Value, StringComparer.Ordinal);
            var version = values.TryGetValue("JAVA_VERSION", out var java) ? java : "unknown";
            var architecture = values.TryGetValue("OS_ARCH", out var arch) ? arch : "unknown architecture";
            var implementor = values.TryGetValue("IMPLEMENTOR", out var vendor) ? vendor : "bundled";
            return $"{version} ({architecture}, {implementor})";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            diagnostics.Add("Could not read bundled JRE metadata: " + ex.Message);
            return "Bundled JRE metadata unavailable";
        }
    }

    private static (string Gpu, string Driver) ReadGraphics(ICollection<string> diagnostics)
    {
        if (!OperatingSystem.IsWindows()) return ("Unavailable on this platform", "Unavailable on this platform");
        try
        {
            using var root = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Video");
            if (root is null) return ("Not reported", "Not reported");
            foreach (var adapterName in root.GetSubKeyNames().OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                using var adapter = root.OpenSubKey(adapterName + @"\0000");
                var description = adapter?.GetValue("DriverDesc") as string;
                if (string.IsNullOrWhiteSpace(description)) continue;
                var provider = adapter?.GetValue("ProviderName") as string;
                var version = adapter?.GetValue("DriverVersion") as string;
                var gpu = string.IsNullOrWhiteSpace(provider) ? description : $"{provider}, {description}";
                return (gpu, version ?? "Not reported");
            }
            return ("Not reported", "Not reported");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            diagnostics.Add("Could not read graphics adapter metadata: " + ex.Message);
            return ("Unavailable", "Unavailable");
        }
    }
}
