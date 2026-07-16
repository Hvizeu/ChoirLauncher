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
    string GameJarSha256);

public sealed record GameLaunchResult(
    bool Success,
    GameLaunchRoute Route,
    int? ProcessId,
    string? ConfigurationSha256,
    GameLaunchTarget? Target,
    IReadOnlyList<string> Diagnostics);

public interface IGameLaunchTargetResolver
{
    GameLaunchTarget Resolve(SongsOfSyxEnvironment environment, GameLaunchRoute route);
}

public sealed class V7144GameLaunchTargetResolver : IGameLaunchTargetResolver
{
    public const string DirectExecutableSha256 = "d7e2350ea6191560b2482a31f5053f6f2c48c6de8dab4a27b3c85bc5c14199f5";
    public const string OfficialLauncherSha256 = "8dc43bb4ce518b02bc4c85da62efd767163efff76523cdfcb94ed638b7bfaf9b";

    private readonly string expectedDirectSha256;
    private readonly string expectedOfficialSha256;
    private readonly string expectedJarSha256;

    public V7144GameLaunchTargetResolver(
        string expectedDirectSha256 = DirectExecutableSha256,
        string expectedOfficialSha256 = OfficialLauncherSha256,
        string expectedJarSha256 = GameInstallationInfoService.V7144JarSha256)
    {
        this.expectedDirectSha256 = ValidateFingerprint(expectedDirectSha256, nameof(expectedDirectSha256));
        this.expectedOfficialSha256 = ValidateFingerprint(expectedOfficialSha256, nameof(expectedOfficialSha256));
        this.expectedJarSha256 = ValidateFingerprint(expectedJarSha256, nameof(expectedJarSha256));
    }

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
        VerifyRegularFile(expectedJarPath, expectedJarSha256, "SongsOfSyx.jar");

        var (fileName, displayName, expectedExecutableSha256) = route switch
        {
            GameLaunchRoute.DirectGame => ("SyxWithout.exe", "Songs of Syx (direct game)", expectedDirectSha256),
            GameLaunchRoute.OfficialLauncher => ("SongsofSyx.exe", "Songs of Syx official launcher", expectedOfficialSha256),
            _ => throw new ArgumentOutOfRangeException(nameof(route), route, "Unsupported Songs of Syx launch route.")
        };
        var executable = Path.GetFullPath(Path.Combine(root, fileName));
        EnsureWithin(executable, root);
        var executableHash = VerifyRegularFile(executable, expectedExecutableSha256, fileName);
        return new(route, displayName, executable, root, executableHash, expectedJarSha256);
    }

    private static string VerifyRegularFile(string path, string expectedSha256, string name)
    {
        if (!File.Exists(path)) throw new FileNotFoundException($"Required v71.44 launch file was not found: {name}", path);
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException($"Launch file may not be a reparse point: {name}");
        var actual = Hashing.Sha256File(path);
        if (!actual.Equals(expectedSha256, StringComparison.Ordinal))
            throw new InvalidDataException($"Unsupported {name} fingerprint. Expected {expectedSha256}; actual {actual}.");
        return actual;
    }

    private static void EnsureWithin(string path, string root)
    {
        var relative = Path.GetRelativePath(root, path);
        if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) || Path.IsPathRooted(relative))
            throw new InvalidDataException("Launch target is outside the verified Songs of Syx directory.");
    }

    private static string ValidateFingerprint(string value, string parameter)
    {
        var normalized = value.Trim().ToLowerInvariant();
        if (normalized.Length != 64 || !normalized.All(Uri.IsHexDigit)) throw new ArgumentException("SHA-256 must contain exactly 64 hexadecimal characters.", parameter);
        return normalized;
    }
}

public interface IGameProcessStarter
{
    int Start(GameLaunchTarget target);
}

public sealed class WindowsGameProcessStarter : IGameProcessStarter
{
    public int Start(GameLaunchTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
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

    public GameLaunchService(
        IGameLaunchTargetResolver targetResolver,
        IProcessInspector processInspector,
        IApplyLockFactory lockFactory,
        IGameProcessStarter processStarter)
    {
        this.targetResolver = targetResolver;
        this.processInspector = processInspector;
        this.lockFactory = lockFactory;
        this.processStarter = processStarter;
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

            var settingsBytes = File.ReadAllBytes(Path.GetFullPath(environment.LauncherSettingsPath));
            _ = LauncherSettingsDocument.Parse(Encoding.UTF8.GetString(settingsBytes));
            var settingsHash = Hashing.Sha256(settingsBytes);

            // Recheck after acquiring the shared lock so a concurrent launch observed during
            // fingerprint/settings verification is rejected before Process.Start.
            running = processInspector.FindBlockingProcesses();
            if (running.Count > 0) return Failure(route, target, running.Select(FormatBlockingProcess));

            var processId = processStarter.Start(target);
            return new(true, route, processId, settingsHash, target,
                [$"Started {target.DisplayName} as process {processId}.", $"Configuration SHA-256: {settingsHash}."]);
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
