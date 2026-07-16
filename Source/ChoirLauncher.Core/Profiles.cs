using System.Text;
using System.Text.Json;

namespace ChoirLauncher.Core;

public static class ProfileStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static string Serialize(ModProfile profile)
    {
        Validate(profile);
        return JsonSerializer.Serialize(profile, Options);
    }

    public static ModProfile Deserialize(string text)
    {
        var profile = JsonSerializer.Deserialize<ModProfile>(text, Options) ?? throw new FormatException("Profile is empty.");
        Validate(profile);
        return profile;
    }

    public static ModProfile FromScan(string id, string name, ScanReport report)
    {
        var mods = report.Mods.Select(x => new ProfileMod(x.LogicalModId, x.Source, x.SourceId, x.InstallationId, x.Enabled, x.Priority,
            x.Manifest?.Version ?? x.Metadata.Version, x.ContentFingerprint, null)).OrderBy(x => x.Priority ?? int.MaxValue).ThenBy(x => x.LogicalModId, StringComparer.Ordinal).ToArray();
        var now = DateTimeOffset.UtcNow;
        return new(1, id, name, report.TargetGameVersion, mods, [], now, now, null, null);
    }

    public static void Validate(ModProfile profile)
    {
        if (profile.SchemaVersion != 1) throw new FormatException($"Unsupported profile schema {profile.SchemaVersion}.");
        if (!MetadataParsers.IsStableId(profile.ProfileId)) throw new FormatException("Invalid profile ID.");
        if (profile.Mods.GroupBy(x => x.InstallationId, StringComparer.Ordinal).Any(x => x.Count() > 1)) throw new FormatException("Duplicate installation ID in profile.");
        var enabledPriorities = profile.Mods.Where(x => x.Enabled).Select(x => x.Priority).ToArray();
        if (enabledPriorities.Any(x => x is null) || enabledPriorities.Distinct().Count() != enabledPriorities.Length)
            throw new FormatException("Enabled profile mods require unique priorities.");
    }

    public static string ExportRedactedScan(ScanReport report) => JsonSerializer.Serialize(report, Options);
}

public sealed class SandboxSettingsWriter
{
    private readonly string root;
    public SandboxSettingsWriter(string sandboxRoot)
    {
        root = Path.GetFullPath(sandboxRoot);
        Directory.CreateDirectory(root);
    }

    public string SimulateAtomicWrite(string targetPath, string originalText, IReadOnlyList<string> enabledOrder, Func<string, bool>? validation = null)
    {
        var target = Path.GetFullPath(targetPath);
        if (!IsWithinRoot(target, root)) throw new UnauthorizedAccessException("Target is outside the approved test sandbox.");
        var document = LauncherSettingsDocument.Parse(originalText);
        var replacement = document.WithEnabledMods(enabledOrder);
        var directory = Path.GetDirectoryName(target) ?? throw new InvalidOperationException("Target has no parent directory.");
        Directory.CreateDirectory(directory);
        var temporary = target + ".tmp-" + Guid.NewGuid().ToString("N");
        var backup = target + ".backup-" + DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff");
        var hadTarget = File.Exists(target);
        if (hadTarget) File.Copy(target, backup, false);
        try
        {
            File.WriteAllText(temporary, replacement, new UTF8Encoding(false));
            var reparsed = LauncherSettingsDocument.Parse(File.ReadAllText(temporary));
            if (!reparsed.EnabledMods.SequenceEqual(enabledOrder, StringComparer.Ordinal)) throw new InvalidDataException("Temporary settings order failed round-trip validation.");
            if (validation is not null && !validation(temporary)) throw new InvalidDataException("Caller validation rejected the temporary settings file.");
            if (hadTarget) File.Move(temporary, target, true); else File.Move(temporary, target);
            return Hashing.Sha256File(target);
        }
        catch
        {
            if (File.Exists(temporary)) File.Delete(temporary);
            if (hadTarget && File.Exists(backup)) File.Copy(backup, target, true);
            throw;
        }
    }

    private static bool IsWithinRoot(string candidate, string rootPath)
    {
        var relative = Path.GetRelativePath(rootPath, candidate);
        return relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) && !Path.IsPathRooted(relative);
    }
}
