using System.ComponentModel;
using System.Diagnostics;
using System.Text;

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
    IReadOnlyList<string> CompatibilityDiagnostics);

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
    public GameLaunchTarget Resolve(SongsOfSyxEnvironment environment, GameLaunchRoute route)
    {
        ArgumentNullException.ThrowIfNull(environment);
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Songs of Syx launch handoff is currently supported only on Windows.");
        if (string.IsNullOrWhiteSpace(environment.GameRoot)) throw new FileNotFoundException("Songs of Syx game directory was not discovered.");

        var root = Path.GetFullPath(environment.GameRoot);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("Songs of Syx game directory no longer exists.");

        var expectedJarPath = Path.GetFullPath(Path.Combine(root, "SongsOfSyx.jar"));
        if (environment.GameJarPath is null || !Path.GetFullPath(environment.GameJarPath).Equals(expectedJarPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The discovered SongsOfSyx.jar is not inside the selected game directory.");
        EnsureRegularFile(expectedJarPath, "SongsOfSyx.jar");
        var game = SongsOfSyxGameArtifactInspector.Inspect(expectedJarPath);
        if (!game.StructurallyValid || game.JarSha256 is null)
            throw new InvalidDataException("The selected game JAR is not a structurally valid Songs of Syx artifact. " + string.Join(" ", game.Diagnostics));

        var (fileName, displayName) = route switch
        {
            GameLaunchRoute.DirectGame => ("SyxWithout.exe", "Songs of Syx (direct game)"),
            GameLaunchRoute.OfficialLauncher => ("SongsofSyx.exe", "Songs of Syx official launcher"),
            _ => throw new ArgumentOutOfRangeException(nameof(route), route, "Unsupported Songs of Syx launch route.")
        };
        var executable = Path.GetFullPath(Path.Combine(root, fileName));
        EnsureWithin(executable, root);
        EnsureRegularFile(executable, fileName);
        EnsurePortableExecutable(executable, fileName);
        var executableHash = Hashing.Sha256File(executable);
        var knownExecutable = KnownGameBuildCatalog.IdentifyExecutable(route, executableHash);
        var diagnostics = game.Diagnostics.ToList();
        if (knownExecutable is null)
            diagnostics.Add($"The {fileName} checksum is not in ChoirLauncher's known-build catalog. This is informational and does not block launch.");
        return new(route, displayName, executable, root, executableHash, game.JarSha256,
            game.Version?.Display, game.KnownBuild, knownExecutable is not null, diagnostics);
    }

    private static void EnsureRegularFile(string path, string name)
    {
        if (!File.Exists(path)) throw new FileNotFoundException($"Required Songs of Syx launch file was not found: {name}", path);
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException($"Launch file may not be a reparse point: {name}");
    }

    private static void EnsurePortableExecutable(string path, string name)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.ReadByte() != 'M' || stream.ReadByte() != 'Z')
            throw new InvalidDataException($"Launch target is not a Windows executable: {name}");
    }

    private static void EnsureWithin(string path, string root)
    {
        var relative = Path.GetRelativePath(root, path);
        if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) || Path.IsPathRooted(relative))
            throw new InvalidDataException("Launch target is outside the verified Songs of Syx directory.");
    }

}

public interface IGameProcessStarter
{
    int Start(GameLaunchTarget target, IReadOnlyDictionary<string, string> environmentOverrides);
}

public sealed class WindowsGameProcessStarter : IGameProcessStarter
{
    public int Start(GameLaunchTarget target, IReadOnlyDictionary<string, string> environmentOverrides)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(environmentOverrides);
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Songs of Syx launch handoff is currently supported only on Windows.");
        if (Environment.GetEnvironmentVariable("CHOIRLAUNCHER_TEST_MODE") == "1")
            throw new UnauthorizedAccessException("Process launch is disabled in ChoirLauncher test mode.");
        var start = new ProcessStartInfo
        {
            FileName = target.ExecutablePath,
            WorkingDirectory = target.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = false
        };
        foreach (var pair in environmentOverrides)
            start.Environment[pair.Key] = pair.Value;
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Windows did not return a process for the Songs of Syx launch request.");
        return process.Id;
    }
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
