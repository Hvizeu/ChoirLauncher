using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ChoirLauncher.Core;

public sealed record ManagerStoragePaths(string Root, string Profiles, string Logs, string Backups, string Preferences)
{
    public static ManagerStoragePaths Resolve(string? overrideRoot = null)
    {
        var root = overrideRoot ?? Environment.GetEnvironmentVariable("CHOIRLAUNCHER_STORAGE_ROOT") ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), BuildInfo.ProductName);
        root = Path.GetFullPath(root);
        return new(root, Path.Combine(root, "profiles"), Path.Combine(root, "logs"), Path.Combine(root, "backups"), Path.Combine(root, "preferences.json"));
    }

    public void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Profiles);
        Directory.CreateDirectory(Logs);
        Directory.CreateDirectory(Backups);
    }
}

public static class ManagerJson
{
    public static readonly JsonSerializerOptions Options = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}

public sealed class ManagerProfileRepository
{
    private readonly ManagerStoragePaths paths;
    private readonly int backupLimit;

    public ManagerProfileRepository(ManagerStoragePaths paths, int backupLimit = 10)
    {
        this.paths = paths;
        this.backupLimit = Math.Max(1, backupLimit);
        paths.EnsureCreated();
    }

    public IReadOnlyList<ManagerProfile> LoadAll()
    {
        var profiles = new List<ManagerProfile>();
        foreach (var path in Directory.EnumerateFiles(paths.Profiles, "*.json", SearchOption.TopDirectoryOnly).Order(StringComparer.Ordinal))
        {
            var profile = Deserialize(File.ReadAllText(path, Encoding.UTF8));
            profiles.Add(profile);
        }
        return profiles.OrderBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.ProfileId, StringComparer.Ordinal).ToArray();
    }

    public ManagerProfile Load(string profileId) => Deserialize(File.ReadAllText(ProfilePath(profileId), Encoding.UTF8));

    public string Save(ManagerProfile profile)
    {
        ManagerProfileValidator.Validate(profile);
        var target = ProfilePath(profile.ProfileId);
        var json = Serialize(profile);
        AtomicFile.WriteValidated(target, Encoding.UTF8.GetBytes(json), bytes => Deserialize(Encoding.UTF8.GetString(bytes)).ProfileId == profile.ProfileId,
            Path.Combine(paths.Profiles, ".backups"), backupLimit);
        return Hashing.Sha256File(target);
    }

    public void Delete(string profileId)
    {
        var target = ProfilePath(profileId);
        if (File.Exists(target)) File.Delete(target);
    }

    public string Export(ManagerProfile profile, string targetPath)
    {
        ManagerProfileValidator.Validate(profile);
        var text = Serialize(profile);
        if (ContainsPrivatePath(text)) throw new InvalidDataException("Profile export contains a private absolute path.");
        AtomicFile.WriteValidated(Path.GetFullPath(targetPath), Encoding.UTF8.GetBytes(text), bytes =>
        {
            var parsed = Deserialize(Encoding.UTF8.GetString(bytes));
            return parsed.ProfileId == profile.ProfileId;
        }, null, 0);
        return Hashing.Sha256File(targetPath);
    }

    public ManagerProfile Import(string sourcePath)
    {
        var full = Path.GetFullPath(sourcePath);
        var text = File.ReadAllText(full, Encoding.UTF8);
        if (ContainsPrivatePath(text)) throw new InvalidDataException("Imported profile contains an absolute private path.");
        return Deserialize(text);
    }

    public static string Serialize(ManagerProfile profile)
    {
        ManagerProfileValidator.Validate(profile);
        return JsonSerializer.Serialize(profile, ManagerJson.Options) + Environment.NewLine;
    }

    public static ManagerProfile Deserialize(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new FormatException("Profile is empty.");
        var profile = JsonSerializer.Deserialize<ManagerProfile>(text, ManagerJson.Options) ?? throw new FormatException("Profile is invalid.");
        if (profile.SchemaVersion == 2)
        {
            profile = profile with
            {
                // Schema 2 already stored the visible profile row sequence. Preserve the
                // owner's arrangement and change only its priority interpretation.
                SchemaVersion = ManagerProfileValidator.CurrentSchemaVersion
            };
        }
        ManagerProfileValidator.Validate(profile);
        return profile;
    }

    public static bool ContainsPrivatePath(string text)
    {
        if (text.Contains("AppData", StringComparison.OrdinalIgnoreCase) || text.Contains("OneDrive", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("\\Users\\", StringComparison.OrdinalIgnoreCase) || text.Contains("/Users/", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("steamapps", StringComparison.OrdinalIgnoreCase) || text.Contains("%TEMP%", StringComparison.OrdinalIgnoreCase)) return true;
        return System.Text.RegularExpressions.Regex.IsMatch(text, "[A-Za-z]:\\\\", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    }

    private string ProfilePath(string profileId)
    {
        if (!MetadataParsers.IsStableId(profileId)) throw new ArgumentException("Invalid profile ID.", nameof(profileId));
        return Path.Combine(paths.Profiles, profileId + ".json");
    }
}

public static class AtomicFile
{
    public static void WriteValidated(string targetPath, byte[] bytes, Func<byte[], bool> validation, string? backupDirectory, int backupLimit)
    {
        var target = Path.GetFullPath(targetPath);
        var directory = Path.GetDirectoryName(target) ?? throw new InvalidOperationException("Target path has no directory.");
        Directory.CreateDirectory(directory);
        if (backupDirectory is not null && backupLimit > 0) Directory.CreateDirectory(backupDirectory);
        var temporary = Path.Combine(directory, "." + Path.GetFileName(target) + ".tmp-" + Guid.NewGuid().ToString("N"));
        string? backup = null;
        try
        {
            if (File.Exists(target) && backupDirectory is not null)
            {
                Directory.CreateDirectory(backupDirectory);
                backup = Path.Combine(backupDirectory, Path.GetFileName(target) + "." + DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff") + ".bak");
                File.Copy(target, backup, false);
            }
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(true);
            }
            var reread = File.ReadAllBytes(temporary);
            if (!reread.AsSpan().SequenceEqual(bytes) || !validation(reread)) throw new InvalidDataException("Temporary-file validation failed.");
            Replace(temporary, target);
            var final = File.ReadAllBytes(target);
            if (!final.AsSpan().SequenceEqual(bytes) || !validation(final)) throw new InvalidDataException("Post-write validation failed.");
            if (backupDirectory is not null && backupLimit > 0) PruneBackups(backupDirectory, Path.GetFileName(target) + ".", backupLimit);
        }
        catch
        {
            if (File.Exists(temporary)) File.Delete(temporary);
            if (backup is not null && File.Exists(backup))
            {
                var restore = Path.Combine(directory, "." + Path.GetFileName(target) + ".restore-" + Guid.NewGuid().ToString("N"));
                File.Copy(backup, restore, false);
                Replace(restore, target);
            }
            throw;
        }
    }

    public static void Replace(string temporary, string target)
    {
        if (File.Exists(target))
        {
            try { File.Replace(temporary, target, null, true); }
            catch (PlatformNotSupportedException) { File.Move(temporary, target, true); }
            catch (IOException) when (!OperatingSystem.IsWindows()) { File.Move(temporary, target, true); }
        }
        else File.Move(temporary, target);
    }

    public static void PruneBackups(string directory, string prefix, int limit)
    {
        var backups = Directory.EnumerateFiles(directory, prefix + "*.bak", SearchOption.TopDirectoryOnly)
            .OrderByDescending(File.GetCreationTimeUtc).ToArray();
        foreach (var path in backups.Skip(Math.Max(1, limit))) File.Delete(path);
    }
}
