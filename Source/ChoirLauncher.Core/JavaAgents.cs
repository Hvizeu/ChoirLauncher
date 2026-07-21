using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace ChoirLauncher.Core;

public enum JavaAgentRequirementSourceKind { Declared, CuratedRecipe }
public enum JavaAgentTrustDecision { Approved, Rejected }
public enum PersistentJavaAgentObservationKind { ExactProvider, DuplicateTransient, StaleRecognized, DisabledMod, Unknown, Malformed }

public sealed record JavaAgentManifestFacts(
    string JarRelativePath,
    string JarSha256,
    string PremainClass,
    bool CanRedefineClasses,
    bool CanRetransformClasses,
    bool PremainClassEntryPresent);

public sealed record JavaAgentRequirement(
    string RequirementId,
    string OwningInstallationId,
    string OwningLogicalModId,
    JavaAgentRequirementSourceKind SourceKind,
    string JarRelativePath,
    string ExpectedPremainClass,
    bool Required,
    int Order,
    string? GameVersionConstraint,
    bool? ExpectedCanRetransformClasses,
    string Evidence)
{
    public IReadOnlyList<GameAssetCacheInvalidationRule> AssetCacheInvalidations { get; init; } = [];
}

public sealed record GameAssetCacheInvalidationRule(
    string RuleId,
    string DisplayName,
    IReadOnlyList<string> RelativeCacheFiles,
    string Reason)
{
    public static GameAssetCacheInvalidationRule SongsOfSyxTextureCache(string ownerId, string reason) => new(
        ownerId + ":songs-of-syx-texture-cache",
        "Songs of Syx texture cache",
        [
            "data/gameTextureData.cachedata",
            "texture/gameDiffuse.png",
            "texture/gameNormal.png"
        ],
        reason);
}

public sealed record JavaAgentTrustKey(
    string InstallationIdentity,
    string JarRelativePath,
    string JarSha256,
    string PremainClass);

public sealed record JavaAgentLaunchEntry(
    string RequirementId,
    string DisplayName,
    string PhysicalJarPath,
    string JarRelativePath,
    string JarSha256,
    string PremainClass,
    int Order,
    JavaAgentTrustKey TrustKey,
    bool ProvidedPersistently)
{
    public IReadOnlyList<GameAssetCacheInvalidationRule> AssetCacheInvalidations { get; init; } = [];
}

public sealed record JavaAgentLaunchPlan(
    IReadOnlyList<JavaAgentLaunchEntry> Entries,
    IReadOnlyList<string> Diagnostics,
    IReadOnlyList<string> HardBlockers,
    IReadOnlyList<JavaAgentLaunchEntry> TrustDecisionsNeeded,
    IReadOnlyList<PersistentJavaAgentObservation> PersistentAgentObservations)
{
    public bool CanLaunch => HardBlockers.Count == 0 && TrustDecisionsNeeded.Count == 0;
    public IReadOnlyList<JavaAgentLaunchEntry> TransientEntries => Entries.Where(x => !x.ProvidedPersistently).ToArray();
}

public sealed record PersistentJavaAgentObservation(
    PersistentJavaAgentObservationKind Kind,
    string Option,
    string Explanation);

public sealed record JavaAgentPrelaunchResult(
    JavaAgentLaunchPlan Plan,
    IReadOnlyDictionary<string, string> EnvironmentOverrides,
    IReadOnlyList<string> Diagnostics,
    IReadOnlyList<string> HardBlockers);

public interface IJavaAgentTrustStore
{
    JavaAgentTrustDecision? GetDecision(JavaAgentTrustKey key);
    void Record(JavaAgentTrustKey key, JavaAgentTrustDecision decision, string reason);
}

public sealed class JavaAgentTrustStore : IJavaAgentTrustStore
{
    private readonly string path;

    public JavaAgentTrustStore(ManagerStoragePaths paths) => path = paths.AgentTrust;

    public JavaAgentTrustDecision? GetDecision(JavaAgentTrustKey key)
    {
        var state = Load();
        var id = KeyId(key);
        return state.Decisions.TryGetValue(id, out var decision) ? decision.Decision : null;
    }

    public void Record(JavaAgentTrustKey key, JavaAgentTrustDecision decision, string reason)
    {
        var state = Load();
        state.Decisions[KeyId(key)] = new(key, decision, DateTimeOffset.UtcNow, reason);
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(state, ManagerJson.Options) + Environment.NewLine);
        AtomicFile.WriteValidated(path, bytes,
            candidate => JsonSerializer.Deserialize<JavaAgentTrustState>(candidate, ManagerJson.Options) is not null,
            Path.Combine(Path.GetDirectoryName(path)!, ".backups"), 10);
    }

    private JavaAgentTrustState Load()
    {
        if (!File.Exists(path)) return new();
        try
        {
            return JsonSerializer.Deserialize<JavaAgentTrustState>(File.ReadAllText(path, Encoding.UTF8), ManagerJson.Options) ?? new();
        }
        catch (JsonException)
        {
            return new();
        }
    }

    private static string KeyId(JavaAgentTrustKey key) => Hashing.Sha256(Encoding.UTF8.GetBytes(
        string.Join('\0', key.InstallationIdentity, key.JarRelativePath, key.JarSha256.ToUpperInvariant(), key.PremainClass)));

    private sealed record JavaAgentTrustRecord(
        JavaAgentTrustKey Key,
        JavaAgentTrustDecision Decision,
        DateTimeOffset DecidedUtc,
        string Reason);

    private sealed class JavaAgentTrustState
    {
        public Dictionary<string, JavaAgentTrustRecord> Decisions { get; set; } = new(StringComparer.Ordinal);
    }
}

public static class JavaAgentManifestReader
{
    public const long MaxManifestBytes = 64 * 1024;

    public static JavaAgentManifestFacts? Read(
        ZipArchive archive,
        string jarRelativePath,
        string jarSha256,
        ICollection<string> diagnostics)
    {
        var entry = archive.Entries.FirstOrDefault(x => x.FullName.Equals("META-INF/MANIFEST.MF", StringComparison.OrdinalIgnoreCase));
        if (entry is null) return null;
        if (entry.Length > MaxManifestBytes)
        {
            diagnostics.Add("JAR manifest exceeds the Java-agent scan limit.");
            return null;
        }

        string text;
        using (var reader = new StreamReader(entry.Open(), Encoding.UTF8, true, 4096, false))
            text = reader.ReadToEnd();

        var values = ParseMainSection(text, diagnostics);
        if (!values.TryGetValue("Premain-Class", out var premain) || string.IsNullOrWhiteSpace(premain)) return null;
        if (!IsJavaClassName(premain))
        {
            diagnostics.Add("JAR manifest Premain-Class is malformed.");
            return null;
        }

        var classEntry = premain.Replace('.', '/') + ".class";
        var present = archive.Entries.Any(x => x.FullName.Equals(classEntry, StringComparison.Ordinal));
        if (!present) diagnostics.Add($"JAR manifest Premain-Class has no matching class entry: {premain}.");
        return new(
            jarRelativePath,
            jarSha256,
            premain,
            ParseBool(values, "Can-Redefine-Classes"),
            ParseBool(values, "Can-Retransform-Classes"),
            present);
    }

    private static IReadOnlyDictionary<string, string> ParseMainSection(string text, ICollection<string> diagnostics)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        string? current = null;
        foreach (var raw in text.Replace("\r", "", StringComparison.Ordinal).Split('\n'))
        {
            if (raw.Length == 0) break;
            if (raw.StartsWith(' '))
            {
                if (current is null) diagnostics.Add("JAR manifest has a continuation line before an attribute.");
                else values[current] += raw[1..];
                continue;
            }

            var split = raw.IndexOf(':');
            if (split <= 0 || split + 1 >= raw.Length || raw[split + 1] != ' ')
            {
                diagnostics.Add("JAR manifest contains a malformed main-section attribute.");
                current = null;
                continue;
            }

            var key = raw[..split];
            var value = raw[(split + 2)..];
            if (IsAgentAttribute(key) && values.ContainsKey(key))
                diagnostics.Add($"JAR manifest contains duplicate {key} attributes.");
            values[key] = value;
            current = key;
        }
        return values;
    }

    private static bool ParseBool(IReadOnlyDictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var value) && value.Equals("true", StringComparison.OrdinalIgnoreCase);

    private static bool IsAgentAttribute(string key) =>
        key is "Premain-Class" or "Can-Redefine-Classes" or "Can-Retransform-Classes";

    private static bool IsJavaClassName(string value) =>
        value.Split('.').All(part => part.Length > 0 && part.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '_' or '$'));
}

public static class JavaAgentDescriptorParser
{
    public static IReadOnlyList<JavaAgentRequirement> Parse(
        string text,
        ModSourceType source,
        string sourceId,
        int? selectedMajorVersion,
        ICollection<string> diagnostics)
    {
        var requirements = new List<JavaAgentRequirement>();
        try
        {
            using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 16, AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            var root = document.RootElement;
            if (!root.TryGetProperty("formatVersion", out var format) || !format.TryGetInt32(out var formatVersion) || formatVersion != 1)
            {
                diagnostics.Add("choir/launch.json has an unsupported formatVersion.");
                return [];
            }

            if (!root.TryGetProperty("javaAgents", out var agents) || agents.ValueKind != JsonValueKind.Array) return [];
            var index = 0;
            foreach (var agent in agents.EnumerateArray())
            {
                if (agent.ValueKind != JsonValueKind.Object) continue;
                var jar = GetString(agent, "jar");
                if (!JavaAgentPathSecurity.IsSafeRelativePath(jar))
                {
                    diagnostics.Add($"choir/launch.json has an unsafe javaAgents[{index}].jar path.");
                    index++;
                    continue;
                }

                requirements.Add(new(
                    $"declared:pending:{jar}:{index}",
                    "",
                    "",
                    JavaAgentRequirementSourceKind.Declared,
                    jar,
                    "",
                    GetBool(agent, "required") ?? true,
                    GetInt(agent, "order") ?? 100,
                    NullIfEmpty(GetString(agent, "gameVersion")),
                    null,
                    "V" + selectedMajorVersion + "/choir/launch.json in " + source + ":" + sourceId)
                {
                    AssetCacheInvalidations = ParseAssetCacheInvalidations(agent, $"declared:{source}:{sourceId}:{jar}:{index}", diagnostics)
                });
                index++;
            }
        }
        catch (JsonException ex)
        {
            diagnostics.Add("Malformed choir/launch.json: " + ex.Message);
        }
        return requirements;
    }

    private static string GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    private static int? GetInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.TryGetInt32(out var parsed) ? parsed : null;
    private static bool? GetBool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;
    private static string? NullIfEmpty(string value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static IReadOnlyList<GameAssetCacheInvalidationRule> ParseAssetCacheInvalidations(JsonElement agent, string ownerId, ICollection<string> diagnostics)
    {
        if (!agent.TryGetProperty("assetCacheInvalidation", out var value)) return [];
        if (value.ValueKind != JsonValueKind.Object)
        {
            diagnostics.Add("javaAgents assetCacheInvalidation must be an object.");
            return [];
        }

        if (GetBool(value, "songsOfSyxTextureCache") == true)
        {
            return
            [
                GameAssetCacheInvalidationRule.SongsOfSyxTextureCache(
                    ownerId,
                    "Declared Java-agent launch descriptor requests a Songs of Syx texture cache rebuild before launch.")
            ];
        }
        return [];
    }
}

public static class KnownJavaAgentRecipeCatalog
{
    public static IReadOnlyList<JavaAgentRequirement> RequirementsFor(ModInstallation installation)
    {
        if (installation.Source == ModSourceType.Workshop &&
            installation.SourceId == "3753609143" &&
            installation.Metadata.Name.Equals("SoSTransit", StringComparison.OrdinalIgnoreCase))
        {
            return
            [
                new(
                    "curated:sostransit:3753609143",
                    installation.InstallationId,
                    installation.LogicalModId,
                    JavaAgentRequirementSourceKind.CuratedRecipe,
                    "script/SoSTransit.jar",
                    "sos.transit.InstrumentationAgent",
                    true,
                    100,
                    ">=0.71.44 <0.72.0",
                    true,
                    "Curated Workshop recipe for SoSTransit 3753609143; reviewed hash 96491DA72880E3B89B0DAA8E23D4C4431221B040471D509E4857EFEC1F638495.")
                {
                    AssetCacheInvalidations =
                    [
                        GameAssetCacheInvalidationRule.SongsOfSyxTextureCache(
                            "curated:sostransit:3753609143",
                            "SoSTransit registers new sprite-composed rooms from a Java agent; clear the game texture cache before launch so the atlas is rebuilt.")
                    ]
                }
            ];
        }
        return [];
    }
}

public static class JavaAgentPathSecurity
{
    public static bool IsSafeRelativePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return false;
        if (relativePath.IndexOfAny(['\0', '\r', '\n', '"']) >= 0) return false;
        if (relativePath.StartsWith('/') || relativePath.StartsWith('\\') || Path.IsPathRooted(relativePath)) return false;
        var normalized = relativePath.Replace('\\', '/');
        return normalized.Split('/').All(part => part.Length > 0 && part != "." && part != "..");
    }

    public static string ResolveContainedRegularFile(string root, string relativePath)
    {
        if (!IsSafeRelativePath(relativePath)) throw new InvalidDataException("Java-agent path is not a safe relative path.");
        var fullRoot = Path.GetFullPath(root);
        var candidate = Path.GetFullPath(Path.Combine(fullRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        EnsureWithin(candidate, fullRoot);
        if (!File.Exists(candidate)) throw new FileNotFoundException("Java-agent JAR was not found.", relativePath);
        EnsureNoReparseComponents(candidate, fullRoot);
        if ((File.GetAttributes(candidate) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Java-agent JAR may not be a reparse point.");
        return candidate;
    }

    public static bool IsWithin(string path, string root)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path));
        return relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) && !Path.IsPathRooted(relative);
    }

    private static void EnsureWithin(string path, string root)
    {
        if (!IsWithin(path, root)) throw new InvalidDataException("Java-agent JAR is outside its owning mod.");
    }

    private static void EnsureNoReparseComponents(string path, string root)
    {
        var current = Path.GetFullPath(root);
        var relative = Path.GetRelativePath(current, Path.GetFullPath(path));
        foreach (var part in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            current = Path.Combine(current, part);
            if (File.Exists(current) || Directory.Exists(current))
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Java-agent path may not pass through a reparse point.");
            }
        }
    }
}

public sealed class JavaAgentRequirementResolver
{
    public JavaAgentLaunchPlan Resolve(
        IReadOnlyList<ModInstallation> installations,
        string gameVersion,
        LauncherSettingsDocument settings,
        IJavaAgentTrustStore trustStore)
    {
        var diagnostics = new List<string>();
        var blockers = new List<string>();
        var entries = new List<JavaAgentLaunchEntry>();

        foreach (var installation in installations.Where(x => x.Enabled))
        {
            foreach (var requirement in installation.JavaAgentRequirements)
            {
                if (requirement.GameVersionConstraint is { Length: > 0 } constraint && !VersionConstraint.Satisfies(gameVersion, constraint))
                    diagnostics.Add($"{DisplayName(installation)} declares Java agent {requirement.JarRelativePath}, but game version {gameVersion} does not satisfy {constraint}.");

                if (installation.SelectedMajorVersion is null)
                {
                    blockers.Add($"{DisplayName(installation)} requires a Java agent but has no selected V<number> content directory.");
                    continue;
                }

                var versionRoot = Path.Combine(installation.RootPath, "V" + installation.SelectedMajorVersion.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
                string physicalPath;
                try { physicalPath = JavaAgentPathSecurity.ResolveContainedRegularFile(versionRoot, requirement.JarRelativePath); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    if (requirement.Required) blockers.Add($"{DisplayName(installation)} Java agent {requirement.JarRelativePath} is invalid: {ex.Message}");
                    else diagnostics.Add($"{DisplayName(installation)} optional Java agent {requirement.JarRelativePath} was skipped: {ex.Message}");
                    continue;
                }

                var jar = ModScanner.InspectJar(physicalPath) with { RelativePath = requirement.JarRelativePath };
                var facts = jar.JavaAgentManifest;
                if (!jar.IsValid || facts is null || !facts.PremainClassEntryPresent)
                {
                    if (requirement.Required) blockers.Add($"{DisplayName(installation)} Java agent {requirement.JarRelativePath} has no valid Premain-Class.");
                    else diagnostics.Add($"{DisplayName(installation)} optional Java agent {requirement.JarRelativePath} has no valid Premain-Class.");
                    continue;
                }

                if (requirement.ExpectedPremainClass.Length > 0 && !facts.PremainClass.Equals(requirement.ExpectedPremainClass, StringComparison.Ordinal))
                {
                    blockers.Add($"{DisplayName(installation)} Java agent premain changed from {requirement.ExpectedPremainClass} to {facts.PremainClass}.");
                    continue;
                }

                if (requirement.ExpectedCanRetransformClasses == true && !facts.CanRetransformClasses)
                {
                    blockers.Add($"{DisplayName(installation)} Java agent does not declare Can-Retransform-Classes=true.");
                    continue;
                }

                var trustKey = new JavaAgentTrustKey(
                    $"{installation.Source}:{installation.SourceId}:{installation.LogicalModId}",
                    requirement.JarRelativePath,
                    jar.Sha256,
                    facts.PremainClass);
                entries.Add(new(
                    requirement.RequirementId,
                    DisplayName(installation),
                    physicalPath,
                    requirement.JarRelativePath,
                    jar.Sha256,
                    facts.PremainClass,
                    requirement.Order,
                    trustKey,
                    false)
                {
                    AssetCacheInvalidations = requirement.AssetCacheInvalidations
                });
            }
        }

        var contradictions = entries.GroupBy(x => Path.GetFullPath(x.PhysicalJarPath), StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Select(x => x.PremainClass).Distinct(StringComparer.Ordinal).Count() > 1)
            .ToArray();
        foreach (var group in contradictions)
            blockers.Add("Contradictory Java-agent declarations target the same JAR: " + string.Join(", ", group.Select(x => x.RequirementId)));

        var ordered = entries
            .GroupBy(x => (Path: Path.GetFullPath(x.PhysicalJarPath).ToUpperInvariant(), x.JarSha256, x.PremainClass))
            .Select(group => group.OrderBy(x => x.Order).ThenBy(x => x.DisplayName, StringComparer.Ordinal).ThenBy(x => x.JarRelativePath, StringComparer.Ordinal).First())
            .OrderBy(x => x.Order)
            .ThenBy(x => x.DisplayName, StringComparer.Ordinal)
            .ThenBy(x => x.JarRelativePath, StringComparer.Ordinal)
            .ToList();

        var observations = InspectPersistentAgents(settings.ReadStringArray("JVM_ARGS2"), ordered, installations, blockers);
        foreach (var exact in observations.Where(x => x.Kind == PersistentJavaAgentObservationKind.ExactProvider))
        {
            var entry = ordered.FirstOrDefault(x => AgentOptionPathEquals(exact.Option, x.PhysicalJarPath));
            if (entry is not null)
                ordered[ordered.IndexOf(entry)] = entry with { ProvidedPersistently = true };
        }

        if (ordered.Count(x => !x.ProvidedPersistently) > 1)
            diagnostics.Add("Multiple transient Java agents are active. ChoirLauncher preserves deterministic order, but bytecode-level agent conflicts are not inferred in this release.");

        var needed = ordered.Where(x => trustStore.GetDecision(x.TrustKey) != JavaAgentTrustDecision.Approved).ToArray();
        foreach (var rejected in ordered.Where(x => trustStore.GetDecision(x.TrustKey) == JavaAgentTrustDecision.Rejected))
            blockers.Add($"{rejected.DisplayName} Java agent {rejected.JarRelativePath} was previously rejected for this exact hash.");

        return new(ordered, diagnostics, blockers, needed, observations);
    }

    private static IReadOnlyList<PersistentJavaAgentObservation> InspectPersistentAgents(
        IReadOnlyList<string> options,
        IReadOnlyList<JavaAgentLaunchEntry> planned,
        IReadOnlyList<ModInstallation> installations,
        ICollection<string> blockers)
    {
        var observations = new List<PersistentJavaAgentObservation>();
        foreach (var option in options.Where(x => x.StartsWith("-javaagent:", StringComparison.OrdinalIgnoreCase)))
        {
            var rawPath = option["-javaagent:".Length..];
            if (rawPath.Length == 0 || rawPath.Contains('=') || rawPath.IndexOfAny(['\0', '\r', '\n', '"']) >= 0)
            {
                observations.Add(new(PersistentJavaAgentObservationKind.Malformed, option, "Malformed persistent -javaagent entry in JVM_ARGS2."));
                blockers.Add("Malformed persistent -javaagent entry in JVM_ARGS2. Remove or repair it before launch.");
                continue;
            }

            var exact = planned.Any(x => Path.GetFullPath(rawPath).Equals(Path.GetFullPath(x.PhysicalJarPath), StringComparison.OrdinalIgnoreCase));
            if (exact)
            {
                observations.Add(new(PersistentJavaAgentObservationKind.ExactProvider, option, "JVM_ARGS2 already provides an exact planned Java agent."));
                continue;
            }

            var recognizedDisabled = installations.Where(x => !x.Enabled).Any(x => plannedRelativeMatch(rawPath, x.JavaAgentRequirements.Select(r => r.JarRelativePath)));
            if (recognizedDisabled)
            {
                observations.Add(new(PersistentJavaAgentObservationKind.DisabledMod, option, "JVM_ARGS2 contains a Java agent for a disabled recognized mod."));
                blockers.Add("JVM_ARGS2 contains a Java agent for a disabled recognized mod. Remove the stale manual entry before launch.");
                continue;
            }

            var staleRecognized = planned.Any(x => rawPath.Replace('\\', '/').EndsWith(x.JarRelativePath, StringComparison.OrdinalIgnoreCase));
            if (staleRecognized)
            {
                observations.Add(new(PersistentJavaAgentObservationKind.StaleRecognized, option, "JVM_ARGS2 contains a stale recognized Java-agent path."));
                blockers.Add("JVM_ARGS2 contains a stale recognized Java-agent path. Remove or repair it before launch.");
                continue;
            }

            observations.Add(new(PersistentJavaAgentObservationKind.Unknown, option, "JVM_ARGS2 contains an unrelated persistent Java agent. ChoirLauncher preserves it."));
        }
        return observations;

        static bool plannedRelativeMatch(string rawPath, IEnumerable<string> relatives) =>
            relatives.Any(relative => rawPath.Replace('\\', '/').EndsWith(relative, StringComparison.OrdinalIgnoreCase));
    }

    private static bool AgentOptionPathEquals(string option, string path)
    {
        var rawPath = option.StartsWith("-javaagent:", StringComparison.OrdinalIgnoreCase) ? option["-javaagent:".Length..] : option;
        return Path.GetFullPath(rawPath).Equals(Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase);
    }

    private static string DisplayName(ModInstallation installation) =>
        string.IsNullOrWhiteSpace(installation.Metadata.Name) ? installation.LogicalModId : installation.Metadata.Name;
}

public sealed class JavaToolOptionsSerializer
{
    public (string? Value, IReadOnlyList<string> Diagnostics) Serialize(IReadOnlyList<JavaAgentLaunchEntry> entries)
    {
        var diagnostics = new List<string>();
        var tokens = new List<string>();
        foreach (var entry in entries)
        {
            var path = Path.GetFullPath(entry.PhysicalJarPath);
            if (path.IndexOfAny(['\0', '\r', '\n', '"']) >= 0)
            {
                diagnostics.Add($"Cannot serialize Java-agent path for {entry.DisplayName}; the path contains unsupported characters.");
                continue;
            }
            var token = "-javaagent:" + path;
            tokens.Add(path.Contains(' ') || path.Contains('\t') ? "\"" + token + "\"" : token);
        }
        return diagnostics.Count == 0 ? (string.Join(' ', tokens), diagnostics) : (null, diagnostics);
    }
}

public interface IJavaAgentLaunchCoordinator
{
    JavaAgentPrelaunchResult Prepare(SongsOfSyxEnvironment environment, GameLaunchRoute route, GameLaunchTarget target);
}

public sealed class JavaAgentLaunchCoordinator : IJavaAgentLaunchCoordinator
{
    private readonly IJavaAgentTrustStore trustStore;
    private readonly JavaAgentRequirementResolver resolver = new();
    private readonly JavaToolOptionsSerializer serializer = new();

    public JavaAgentLaunchCoordinator(IJavaAgentTrustStore trustStore) => this.trustStore = trustStore;

    public JavaAgentPrelaunchResult Prepare(SongsOfSyxEnvironment environment, GameLaunchRoute route, GameLaunchTarget target)
    {
        var gameVersion = target.GameVersion ?? BuildInfo.TargetGameVersion;
        var targetMajor = ParseMajor(gameVersion) ?? BuildInfo.TargetGameMajor;
        var scan = new ModScanner().Scan(new(
            environment.LocalModsRoot,
            environment.WorkshopModsRoot ?? Path.Combine(ManagerStoragePaths.Resolve().Root, "missing-workshop"),
            environment.LauncherSettingsPath,
            environment.GameJarPath,
            targetMajor,
            gameVersion));
        var settings = LauncherSettingsDocument.Parse(File.ReadAllText(environment.LauncherSettingsPath, Encoding.UTF8));
        var plan = resolver.Resolve(scan.Mods, gameVersion, settings, trustStore);
        var blockers = plan.HardBlockers.ToList();
        var diagnostics = plan.Diagnostics.ToList();
        var overrides = new Dictionary<string, string>(StringComparer.Ordinal);

        if (plan.Entries.Count > 0 && route == GameLaunchRoute.OfficialLauncher)
            blockers.Add("Required Java agents are supported only on the direct-game launch route in this release. Use Apply Profile & Launch or Launch Current Official State.");

        var transient = plan.TransientEntries;
        if (transient.Count > 0)
        {
            var existing = Environment.GetEnvironmentVariable("JAVA_TOOL_OPTIONS");
            if (!string.IsNullOrWhiteSpace(existing))
            {
                blockers.Add("JAVA_TOOL_OPTIONS is already set for ChoirLauncher. Clear it before launching agent-managed mods through ChoirLauncher.");
            }
            else
            {
                var serialized = serializer.Serialize(transient);
                diagnostics.AddRange(serialized.Diagnostics);
                if (serialized.Value is null) blockers.Add("Could not serialize required Java-agent launch options.");
                else overrides["JAVA_TOOL_OPTIONS"] = serialized.Value;
            }
        }

        foreach (var observation in plan.PersistentAgentObservations)
            diagnostics.Add(observation.Explanation);
        return new(plan, overrides, diagnostics, blockers);
    }

    private static int? ParseMajor(string version)
    {
        var parts = version.TrimStart('v', 'V').Split('.');
        if (parts.Length >= 2 && parts[0] == "0" && int.TryParse(parts[1], out var minor)) return minor;
        if (int.TryParse(parts[0], out var major)) return major;
        return null;
    }
}
