using System.Text;
using System.Text.Json;
using ChoirLauncher.Core;

return await MainAsync(args);

static async Task<int> MainAsync(string[] args)
{
    if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
    {
        Console.WriteLine("ChoirLauncher proof of concept (read-only live scanner)");
        Console.WriteLine("  scan --local <path> --workshop <path> --settings <path> [--game-jar <path>] --out <project-owned.json>");
        Console.WriteLine("  profile --scan <scan.json> --id <id> --name <name> --out <profile.json>");
        Console.WriteLine("  simulate-write --settings-copy <path> --sandbox <path> --order <a,b,c>");
        Console.WriteLine("There is intentionally no command to modify live settings or launch the game.");
        return 0;
    }

    try
    {
        var options = Parse(args.Skip(1).ToArray());
        switch (args[0])
        {
            case "scan":
            {
                var gameJar = Optional(options, "game-jar");
                var game = SongsOfSyxGameArtifactInspector.Inspect(gameJar);
                var scanner = new ModScanner();
                var report = scanner.Scan(new(Required(options, "local"), Required(options, "workshop"), Required(options, "settings"),
                    gameJar, game.Version?.Major ?? BuildInfo.TargetGameMajor, game.Version?.Display ?? BuildInfo.TargetGameVersion));
                var output = Required(options, "out");
                EnsureExplicitOutput(output);
                await File.WriteAllTextAsync(output, ProfileStore.ExportRedactedScan(report), new UTF8Encoding(false));
                Console.WriteLine($"Scanned {report.Mods.Count} installations; {report.Conflicts.Count} findings.");
                Console.WriteLine($"Game build: {game.BuildLabel}; SHA-256: {game.JarSha256 ?? "unavailable"}.");
                Console.WriteLine($"Priority: {report.PriorityRule}");
                Console.WriteLine($"Report: {Path.GetFullPath(output)}");
                return 0;
            }
            case "profile":
            {
                var scan = JsonSerializer.Deserialize<ScanReport>(await File.ReadAllTextAsync(Required(options, "scan")),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new FormatException("Invalid scan report.");
                var profile = ProfileStore.FromScan(Required(options, "id"), Required(options, "name"), scan);
                var output = Required(options, "out");
                EnsureExplicitOutput(output);
                await File.WriteAllTextAsync(output, ProfileStore.Serialize(profile), new UTF8Encoding(false));
                Console.WriteLine($"Profile: {Path.GetFullPath(output)}");
                return 0;
            }
            case "simulate-write":
            {
                var sandbox = Required(options, "sandbox");
                var target = Required(options, "settings-copy");
                var original = await File.ReadAllTextAsync(target);
                var order = Required(options, "order").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var hash = new SandboxSettingsWriter(sandbox).SimulateAtomicWrite(target, original, order);
                Console.WriteLine($"Test-owned settings copy written atomically: {Path.GetFullPath(target)}");
                Console.WriteLine($"SHA-256: {hash}");
                return 0;
            }
            default: throw new ArgumentException($"Unknown command: {args[0]}");
        }
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"ERROR: {ex.Message}");
        return 2;
    }
}

static Dictionary<string, string> Parse(string[] args)
{
    var result = new Dictionary<string, string>(StringComparer.Ordinal);
    for (var index = 0; index < args.Length; index += 2)
    {
        if (!args[index].StartsWith("--", StringComparison.Ordinal) || index + 1 >= args.Length) throw new ArgumentException("Expected --name value pairs.");
        result[args[index][2..]] = args[index + 1];
    }
    return result;
}

static string Required(IReadOnlyDictionary<string, string> options, string name) => options.TryGetValue(name, out var value) ? value : throw new ArgumentException($"Missing --{name}.");
static string? Optional(IReadOnlyDictionary<string, string> options, string name) => options.TryGetValue(name, out var value) ? value : null;
static void EnsureExplicitOutput(string path)
{
    var full = Path.GetFullPath(path);
    var live = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "songsofsyx", "settings", "LauncherSettings.txt");
    if (string.Equals(full, Path.GetFullPath(live), StringComparison.OrdinalIgnoreCase)) throw new UnauthorizedAccessException("Live LauncherSettings.txt is a protected read-only input.");
    Directory.CreateDirectory(Path.GetDirectoryName(full) ?? ".");
}
