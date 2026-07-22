using System.ComponentModel;
using System.Diagnostics;

namespace ChoirLauncher.Core;

public enum GameLaunchRoute
{
    DirectGame,
    OfficialLauncher
}

public sealed record GameLaunchTarget(
    GameLaunchRoute Route,
    string DisplayName,
    string ExecutablePath,
    string WorkingDirectory,
    string ExecutableSha256,
    string GameJarSha256,
    string? GameVersion,
    bool KnownGameBuild,
    bool KnownExecutable,
    IReadOnlyList<string> CompatibilityDiagnostics)
{
    public IReadOnlyList<string> Arguments { get; init; } = [];
    public DesktopPlatform Platform { get; init; } = HostPlatform.Current;
}

public sealed record GameLaunchResult(
    bool Success,
    GameLaunchRoute Route,
    int? ProcessId,
    string? ConfigurationSha256,
    GameLaunchTarget? Target,
    IReadOnlyList<string> Diagnostics)
{
    public JavaAgentLaunchPlan? JavaAgentPlan { get; init; }
    public GameAssetCacheInvalidationResult? AssetCacheInvalidation { get; init; }
}

public interface IGameLaunchTargetResolver
{
    GameLaunchTarget Resolve(SongsOfSyxEnvironment environment, GameLaunchRoute route);
}

public sealed class SongsOfSyxGameLaunchTargetResolver : IGameLaunchTargetResolver
{
    private readonly DesktopPlatform platform;

    public SongsOfSyxGameLaunchTargetResolver(DesktopPlatform? platformOverride = null) =>
        platform = platformOverride ?? HostPlatform.Current;

    public GameLaunchTarget Resolve(SongsOfSyxEnvironment environment, GameLaunchRoute route)
    {
        ArgumentNullException.ThrowIfNull(environment);
        if (string.IsNullOrWhiteSpace(environment.GameRoot)) throw new FileNotFoundException("Songs of Syx game directory was not discovered.");

        var root = Path.GetFullPath(environment.GameRoot);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("Songs of Syx game directory no longer exists.");

        var expectedJarPath = Path.GetFullPath(Path.Combine(root, "SongsOfSyx.jar"));
        if (environment.GameJarPath is null || !HostPlatform.PathsEqual(environment.GameJarPath, expectedJarPath, platform))
            throw new InvalidDataException("The discovered SongsOfSyx.jar is not inside the selected game directory.");
        EnsureRegularFile(expectedJarPath, "SongsOfSyx.jar");
        var game = SongsOfSyxGameArtifactInspector.Inspect(expectedJarPath);
        if (!game.StructurallyValid || game.JarSha256 is null)
            throw new InvalidDataException("The selected game JAR is not a structurally valid Songs of Syx artifact. " + string.Join(" ", game.Diagnostics));

        var launch = ResolveLaunch(root, expectedJarPath, route);
        EnsureWithin(launch.ExecutablePath, launch.ContainmentRoot);
        EnsureRegularFile(launch.ExecutablePath, launch.DisplayFileName);
        EnsureExecutableFormat(launch.ExecutablePath, launch.DisplayFileName, platform);
        EnsureUnixExecutableMode(launch.ExecutablePath, launch.DisplayFileName, platform);
        var executableHash = Hashing.Sha256File(launch.ExecutablePath);
        var knownExecutable = platform == DesktopPlatform.Windows ? KnownGameBuildCatalog.IdentifyExecutable(route, executableHash) : null;
        var diagnostics = game.Diagnostics.ToList();
        if (knownExecutable is null)
            diagnostics.Add($"The {launch.DisplayFileName} checksum is not in ChoirLauncher's known-build catalog for {platform}. This is informational and does not block launch.");
        return new(route, launch.DisplayName, launch.ExecutablePath, launch.WorkingDirectory, executableHash, game.JarSha256,
            game.Version?.Display, game.KnownBuild, knownExecutable is not null, diagnostics)
        {
            Arguments = launch.Arguments,
            Platform = platform
        };
    }

    private LaunchSpecification ResolveLaunch(string contentRoot, string jarPath, GameLaunchRoute route)
    {
        if (route is not GameLaunchRoute.DirectGame and not GameLaunchRoute.OfficialLauncher)
            throw new ArgumentOutOfRangeException(nameof(route), route, "Unsupported Songs of Syx launch route.");

        if (platform == DesktopPlatform.Windows)
        {
            var fileName = route == GameLaunchRoute.DirectGame ? "SyxWithout.exe" : "SongsofSyx.exe";
            var display = route == GameLaunchRoute.DirectGame ? "Songs of Syx (direct game)" : "Songs of Syx official launcher";
            return new(Path.GetFullPath(Path.Combine(contentRoot, fileName)), contentRoot, contentRoot, fileName, display, []);
        }

        if (route == GameLaunchRoute.DirectGame)
        {
            var java = Path.GetFullPath(Path.Combine(contentRoot, "jre", "bin", "java"));
            return new(java, contentRoot, contentRoot, "bundled Java runtime", "Songs of Syx (direct game)", ["-jar", jarPath]);
        }

        if (platform == DesktopPlatform.Linux)
        {
            var launcher = Path.GetFullPath(Path.Combine(contentRoot, "songsofsyx"));
            return new(launcher, contentRoot, contentRoot, "songsofsyx", "Songs of Syx official launcher", []);
        }

        var appRoot = Path.GetFullPath(Path.Combine(contentRoot, "..", ".."));
        var macLauncher = Path.GetFullPath(Path.Combine(appRoot, "Contents", "MacOS", "songsofsyx"));
        return new(macLauncher, contentRoot, appRoot, "SongsOfSyxMac.app", "Songs of Syx official launcher", []);
    }

    private static void EnsureRegularFile(string path, string name)
    {
        if (!File.Exists(path)) throw new FileNotFoundException($"Required Songs of Syx launch file was not found: {name}", path);
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException($"Launch file may not be a reparse point: {name}");
    }

    private static void EnsureExecutableFormat(string path, string name, DesktopPlatform platform)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Span<byte> magic = stackalloc byte[4];
        if (stream.Read(magic) != magic.Length) throw new InvalidDataException($"Launch target is truncated: {name}");
        var valid = platform switch
        {
            DesktopPlatform.Windows => magic[0] == 'M' && magic[1] == 'Z',
            DesktopPlatform.Linux => magic.SequenceEqual(new byte[] { 0x7f, (byte)'E', (byte)'L', (byte)'F' }),
            DesktopPlatform.MacOS => IsMachOMagic(magic),
            _ => false
        };
        if (!valid) throw new InvalidDataException($"Launch target does not have a valid {platform} executable signature: {name}");
    }

    private static bool IsMachOMagic(ReadOnlySpan<byte> magic) =>
        magic.SequenceEqual(new byte[] { 0xfe, 0xed, 0xfa, 0xce }) ||
        magic.SequenceEqual(new byte[] { 0xce, 0xfa, 0xed, 0xfe }) ||
        magic.SequenceEqual(new byte[] { 0xfe, 0xed, 0xfa, 0xcf }) ||
        magic.SequenceEqual(new byte[] { 0xcf, 0xfa, 0xed, 0xfe }) ||
        magic.SequenceEqual(new byte[] { 0xca, 0xfe, 0xba, 0xbe }) ||
        magic.SequenceEqual(new byte[] { 0xbe, 0xba, 0xfe, 0xca });

    private static void EnsureUnixExecutableMode(string path, string name, DesktopPlatform platform)
    {
        if (platform == DesktopPlatform.Windows || OperatingSystem.IsWindows()) return;
        var mode = File.GetUnixFileMode(path);
        const UnixFileMode execute = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
        if ((mode & execute) == 0) throw new InvalidDataException($"Launch target is not marked executable: {name}");
    }

    private static void EnsureWithin(string path, string root)
    {
        if (!HostPlatform.IsWithin(path, root))
            throw new InvalidDataException("Launch target is outside the verified Songs of Syx directory.");
    }

    private sealed record LaunchSpecification(
        string ExecutablePath,
        string WorkingDirectory,
        string ContainmentRoot,
        string DisplayFileName,
        string DisplayName,
        IReadOnlyList<string> Arguments);
}

public interface IGameProcessStarter
{
    int Start(GameLaunchTarget target, IReadOnlyDictionary<string, string> environmentOverrides);
}

public sealed class PlatformGameProcessStarter : IGameProcessStarter
{
    public int Start(GameLaunchTarget target, IReadOnlyDictionary<string, string> environmentOverrides)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(environmentOverrides);
        if (Environment.GetEnvironmentVariable("CHOIRLAUNCHER_TEST_MODE") == "1")
            throw new UnauthorizedAccessException("Process launch is disabled in ChoirLauncher test mode.");
        var start = new ProcessStartInfo
        {
            FileName = target.ExecutablePath,
            WorkingDirectory = target.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = false
        };
        foreach (var argument in target.Arguments) start.ArgumentList.Add(argument);
        foreach (var pair in environmentOverrides)
            start.Environment[pair.Key] = pair.Value;
        using var process = Process.Start(start) ?? throw new InvalidOperationException("The operating system did not return a process for the Songs of Syx launch request.");
        return process.Id;
    }
}

[Obsolete("Use PlatformGameProcessStarter. The replacement supports Windows, Linux, and macOS.")]
public sealed class WindowsGameProcessStarter : IGameProcessStarter
{
    private readonly PlatformGameProcessStarter inner = new();
    public int Start(GameLaunchTarget target, IReadOnlyDictionary<string, string> environmentOverrides) => inner.Start(target, environmentOverrides);
}

public interface IGameLaunchService
{
    GameLaunchResult Launch(SongsOfSyxEnvironment environment, GameLaunchRoute route);
}

public sealed class GameLaunchService : IGameLaunchService
{
    private readonly IGameLaunchTargetResolver targetResolver;
    private readonly IProcessInspector processInspector;
    private readonly IApplyLockFactory lockFactory;
    private readonly IGameProcessStarter processStarter;
    private readonly IJavaAgentLaunchCoordinator? javaAgentLaunchCoordinator;
    private readonly IGameAssetCacheInvalidator? assetCacheInvalidator;

    public GameLaunchService(
        IGameLaunchTargetResolver targetResolver,
        IProcessInspector processInspector,
        IApplyLockFactory lockFactory,
        IGameProcessStarter processStarter,
        IJavaAgentLaunchCoordinator? javaAgentLaunchCoordinator = null,
        IGameAssetCacheInvalidator? assetCacheInvalidator = null)
    {
        this.targetResolver = targetResolver;
        this.processInspector = processInspector;
        this.lockFactory = lockFactory;
        this.processStarter = processStarter;
        this.javaAgentLaunchCoordinator = javaAgentLaunchCoordinator;
        this.assetCacheInvalidator = assetCacheInvalidator;
    }

    public GameLaunchResult Launch(SongsOfSyxEnvironment environment, GameLaunchRoute route)
    {
        GameLaunchTarget? target = null;
        try
        {
            target = targetResolver.Resolve(environment, route);
            var running = processInspector.FindBlockingProcesses();
            if (running.Count > 0) return Failure(route, target, running.Select(FormatBlockingProcess));

            using var applyLock = lockFactory.TryAcquire(environment.LauncherSettingsPath, TimeSpan.FromSeconds(2));
            if (!applyLock.Acquired) return Failure(route, target, ["Another ChoirLauncher apply, restore, or launch operation holds the configuration lock."]);

            string? settingsHash = null;
            if (File.Exists(environment.LauncherSettingsPath))
                settingsHash = Hashing.Sha256(File.ReadAllBytes(Path.GetFullPath(environment.LauncherSettingsPath)));

            // Recheck after acquiring the shared lock so a concurrent launch observed during
            // fingerprint/settings verification is rejected before Process.Start.
            running = processInspector.FindBlockingProcesses();
            if (running.Count > 0) return Failure(route, target, running.Select(FormatBlockingProcess));

            var javaAgentPrelaunch = javaAgentLaunchCoordinator?.Prepare(environment, route, target);
            if (javaAgentPrelaunch is not null && javaAgentPrelaunch.HardBlockers.Count > 0)
                return Failure(route, target, javaAgentPrelaunch.HardBlockers.Concat(javaAgentPrelaunch.Diagnostics)) with { JavaAgentPlan = javaAgentPrelaunch.Plan };
            if (javaAgentPrelaunch is not null && javaAgentPrelaunch.Plan.TrustDecisionsNeeded.Count > 0)
                return Failure(route, target, javaAgentPrelaunch.Plan.TrustDecisionsNeeded.Select(x => $"Trust required for {x.DisplayName} Java agent {x.JarRelativePath} ({x.JarSha256}).").Concat(javaAgentPrelaunch.Diagnostics)) with { JavaAgentPlan = javaAgentPrelaunch.Plan };

            var cacheInvalidation = javaAgentPrelaunch is null || assetCacheInvalidator is null
                ? null
                : assetCacheInvalidator.Invalidate(environment, javaAgentPrelaunch.Plan);
            var processId = processStarter.Start(target, javaAgentPrelaunch?.EnvironmentOverrides ?? new Dictionary<string, string>());
            var diagnostics = new List<string>
            {
                $"Started {target.DisplayName} as process {processId}.",
                $"Game version: {target.GameVersion ?? "unknown"}.",
                $"Game JAR SHA-256: {target.GameJarSha256}.",
                $"Executable SHA-256: {target.ExecutableSha256}."
            };
            if (settingsHash is not null) diagnostics.Add($"Configuration SHA-256: {settingsHash}.");
            if (javaAgentPrelaunch is not null)
            {
                if (javaAgentPrelaunch.Plan.Entries.Count > 0)
                    diagnostics.Add($"Java agents prepared: {javaAgentPrelaunch.Plan.Entries.Count} total, {javaAgentPrelaunch.Plan.TransientEntries.Count} transient.");
                diagnostics.AddRange(javaAgentPrelaunch.Diagnostics);
            }
            if (cacheInvalidation is not null && cacheInvalidation.Rules.Count > 0)
                diagnostics.AddRange(cacheInvalidation.Diagnostics);
            diagnostics.AddRange(target.CompatibilityDiagnostics);
            return new(true, route, processId, settingsHash, target, diagnostics)
            {
                JavaAgentPlan = javaAgentPrelaunch?.Plan,
                AssetCacheInvalidation = cacheInvalidation
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or ArgumentException or InvalidOperationException or PlatformNotSupportedException or Win32Exception)
        {
            return Failure(route, target, [ex.Message]);
        }
    }

    private static string FormatBlockingProcess(BlockingProcess process) =>
        $"Blocking process {process.ProcessName} ({process.ProcessId}): {process.Reason}";

    private static GameLaunchResult Failure(GameLaunchRoute route, GameLaunchTarget? target, IEnumerable<string> diagnostics) =>
        new(false, route, null, null, target, diagnostics.ToArray());
}
