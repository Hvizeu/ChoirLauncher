using System.Text;

namespace ChoirLauncher.Core;

public static class DependencyGraphResolver
{
    public static DependencyGraphResult Resolve(IReadOnlyList<ModInstallation> mods)
    {
        var byId = mods.GroupBy(x => x.LogicalModId, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.OrderByDescending(m => m.Priority ?? int.MinValue).First(), StringComparer.Ordinal);
        var blockers = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        var edges = byId.Keys.ToDictionary(x => x, _ => new HashSet<string>(StringComparer.Ordinal), StringComparer.Ordinal);
        foreach (var mod in byId.Values)
        {
            var errors = new List<string>();
            if (mod.Manifest is { IsValid: false }) errors.Add("Manifest is invalid.");
            foreach (var dependency in mod.Manifest?.Required ?? [])
            {
                if (!byId.TryGetValue(dependency.ModId, out var target))
                {
                    errors.Add($"Missing required dependency {dependency.ModId}@{dependency.Constraint}.");
                    continue;
                }
                if (!VersionConstraint.Satisfies(target.Manifest?.Version ?? target.Metadata.Version, dependency.Constraint))
                    errors.Add($"Dependency {dependency.ModId} does not satisfy {dependency.Constraint}.");
                edges[dependency.ModId].Add(mod.LogicalModId);
            }
            foreach (var incompatible in mod.Manifest?.Incompatible ?? [])
                if (byId.ContainsKey(incompatible)) errors.Add($"Incompatible mod active: {incompatible}.");
            if (errors.Count > 0) blockers[mod.LogicalModId] = errors;
        }

        var incoming = byId.Keys.ToDictionary(x => x, _ => 0, StringComparer.Ordinal);
        foreach (var outgoing in edges.Values) foreach (var target in outgoing) incoming[target]++;
        var priority = byId.Values.ToDictionary(x => x.LogicalModId, x => x.Priority ?? int.MaxValue, StringComparer.Ordinal);
        var ready = new SortedSet<string>(Comparer<string>.Create((a, b) =>
        {
            var comparison = priority[a].CompareTo(priority[b]);
            return comparison != 0 ? comparison : StringComparer.Ordinal.Compare(a, b);
        }));
        foreach (var pair in incoming) if (pair.Value == 0) ready.Add(pair.Key);
        var order = new List<string>();
        while (ready.Count > 0)
        {
            var next = ready.Min!;
            ready.Remove(next);
            order.Add(next);
            foreach (var target in edges[next].Order(StringComparer.Ordinal)) if (--incoming[target] == 0) ready.Add(target);
        }
        var cycleNodes = incoming.Where(x => x.Value > 0).Select(x => x.Key).Order(StringComparer.Ordinal).ToArray();
        var cycles = cycleNodes.Length == 0 ? Array.Empty<IReadOnlyList<string>>() : new IReadOnlyList<string>[] { cycleNodes };
        foreach (var node in cycleNodes)
            blockers[node] = (blockers.TryGetValue(node, out var existing) ? existing : []).Concat(["Dependency cycle."]).ToArray();
        return new(order, blockers, cycles);
    }
}

public static class VersionConstraint
{
    public static bool Satisfies(string version, string constraint)
    {
        if (string.IsNullOrWhiteSpace(constraint) || constraint == "*") return true;
        if (!Version.TryParse(Normalize(version), out var actual)) return false;
        foreach (var part in constraint.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var op = part.StartsWith(">=", StringComparison.Ordinal) ? ">=" : part.StartsWith("<=", StringComparison.Ordinal) ? "<=" :
                part.StartsWith('>') ? ">" : part.StartsWith('<') ? "<" : part.StartsWith('=') ? "=" : "=";
            var offset = op == "=" && !part.StartsWith("=", StringComparison.Ordinal) ? 0 : op.Length;
            var raw = part[offset..];
            if (!Version.TryParse(Normalize(raw), out var expected)) return false;
            var comparison = actual.CompareTo(expected);
            if (op == ">=" && comparison < 0 || op == "<=" && comparison > 0 || op == ">" && comparison <= 0 ||
                op == "<" && comparison >= 0 || op == "=" && comparison != 0) return false;
        }
        return true;
    }

    private static string Normalize(string value)
    {
        var clean = value.Trim().Split('-', '+')[0];
        var segments = clean.Split('.');
        return segments.Length switch { 1 => clean + ".0.0", 2 => clean + ".0", _ => clean };
    }
}

public static class ConflictAnalyzer
{
    public static IReadOnlyList<Conflict> Analyze(IReadOnlyList<ModInstallation> mods, IReadOnlyList<string> enabledOrder, int targetMajor)
    {
        var enabled = mods.Where(x => x.Enabled).OrderBy(x => x.Priority).ToArray();
        var conflicts = new List<Conflict>();
        AddDuplicateInstallations(enabled, conflicts);
        AddDuplicateLauncherEntries(enabledOrder, conflicts);
        AddMissingLauncherEntries(enabled, enabledOrder, conflicts);
        AddMetadataProblems(enabled, targetMajor, conflicts);
        AddPathCollisions(enabled, conflicts);
        AddClassCollisions(enabled, conflicts);
        AddFrameworkPackages(enabled, conflicts);
        AddDuplicateArtifacts(enabled, conflicts);
        AddStableIdCollisions(enabled, conflicts);
        AddManifestIssues(enabled, conflicts);
        AddProviderCollisions(enabled, conflicts);
        return conflicts.OrderBy(x => x.Severity).ThenBy(x => x.ConflictId, StringComparer.Ordinal).ToArray();
    }

    private static void AddDuplicateInstallations(IReadOnlyList<ModInstallation> mods, List<Conflict> output)
    {
        foreach (var group in mods.GroupBy(x => x.FolderName, StringComparer.Ordinal).Where(x => x.Count() > 1))
        {
            var ordered = group.OrderBy(x => x.Source).ToArray();
            output.Add(Make("duplicate-install", Severity.High, Confidence.Proven, ordered, group.Key,
                ordered[0].InstallationId, false, true,
                "Local and Workshop roots contain the same launcher folder ID; the local root wins discovery.",
                "Remove or rename the unintended duplicate after reviewing both copies.", ordered.Select(x => x.ContentFingerprint)));
        }
        foreach (var group in mods.Where(x => x.Manifest is not null).GroupBy(x => x.LogicalModId, StringComparer.Ordinal).Where(x => x.Count() > 1))
        {
            var ordered = group.OrderByDescending(x => x.Priority ?? int.MinValue).ToArray();
            output.Add(Make("duplicate-logical-id", Severity.Blocking, Confidence.Proven, ordered, group.Key,
                ordered.FirstOrDefault(x => x.Enabled)?.InstallationId, false, true,
                "Multiple installations declare the same Choir logical mod ID.", "Keep exactly one declaration.", ordered.Select(x => x.Manifest!.DescriptorKind)));
        }
        foreach (var group in mods.Where(x => x.ContentFingerprint.Length == 64).GroupBy(x => x.ContentFingerprint, StringComparer.Ordinal)
                     .Where(x => x.Select(y => (y.Source, y.SourceId)).Distinct().Count() > 1))
        {
            var ordered = group.OrderByDescending(x => x.Priority ?? int.MinValue).ToArray();
            output.Add(Make("exact-content-duplicate", Severity.Medium, Confidence.Proven, ordered, group.Key,
                ordered.FirstOrDefault(x => x.Enabled)?.InstallationId, false, false,
                "Byte-level content inventory is duplicated under multiple installation identities.",
                "Review the copies and retain the intended source/version.", ordered.Select(x => $"{x.Source}:{x.SourceId}")));
        }
    }

    private static void AddDuplicateLauncherEntries(IReadOnlyList<string> order, List<Conflict> output)
    {
        foreach (var group in order.Select((id, index) => (id, index)).GroupBy(x => x.id, StringComparer.Ordinal).Where(x => x.Count() > 1))
            output.Add(new($"duplicate-launcher-entry:{group.Key}", "duplicate-launcher-entry", Severity.High, Confidence.Proven,
                [group.Key], group.Key, group.Key, false, true, "The launcher MODS array contains the same folder ID more than once.",
                "Remove duplicate array entries; duplicates create repeated PATHS roots.", group.Select(x => $"priority={x.index}").ToArray()));
    }

    private static void AddMissingLauncherEntries(IReadOnlyList<ModInstallation> mods, IReadOnlyList<string> order, List<Conflict> output)
    {
        var folders = mods.Select(x => x.FolderName).ToHashSet(StringComparer.Ordinal);
        foreach (var missing in order.Where(x => !folders.Contains(x)).Distinct(StringComparer.Ordinal))
            output.Add(new($"missing-installation:{missing}", "missing-installation", Severity.High, Confidence.Proven, [missing], missing, null,
                false, true, "Launcher configuration references a folder absent from both discovered roots.",
                "Install the missing mod or remove the stale profile/configuration entry.", ["LauncherSettings MODS"]));
    }

    private static void AddMetadataProblems(IEnumerable<ModInstallation> mods, int targetMajor, List<Conflict> output)
    {
        foreach (var mod in mods)
        {
            if (!mod.Metadata.IsValid)
                output.Add(Make("malformed-metadata", Severity.Medium, Confidence.Proven, [mod], "_Info.txt", null, false, false,
                    "Metadata is missing or malformed.", "Repair _Info.txt.", mod.Metadata.Diagnostics));
            if (mod.Metadata.GameVersionMajor > 0 && mod.Metadata.GameVersionMajor != targetMajor)
                output.Add(Make("game-version-mismatch", Severity.High, Confidence.Proven, [mod], $"V{targetMajor}", null, false, false,
                    $"Mod metadata targets V{mod.Metadata.GameVersionMajor}.", "Install a compatible build.", []));
            if (mod.SelectedMajorVersion != targetMajor)
                output.Add(Make("version-folder-fallback", Severity.Medium, Confidence.Proven, [mod], $"V{targetMajor}", null, false, false,
                    "No exact version folder was selected by this conservative scan.", "Provide an exact version folder.", []));
        }
    }

    private static void AddPathCollisions(IEnumerable<ModInstallation> mods, List<Conflict> output)
    {
        foreach (var group in mods.SelectMany(m => m.DataFiles.Where(f => f.Category == "runtime-selected" && f.RelativePath.Contains("/assets/", StringComparison.OrdinalIgnoreCase)).Select(f => (m, f)))
                     .GroupBy(x => CanonicalRuntimePath(x.f.RelativePath), StringComparer.OrdinalIgnoreCase).Where(x => x.Count() > 1))
        {
            var entries = group.OrderByDescending(x => x.m.Priority ?? int.MinValue).ToArray();
            var hashes = entries.Select(x => x.f.Sha256).Distinct(StringComparer.Ordinal).Count();
            var keyResult = AnalyzeTextKeys(entries.Select(x => x.f.PhysicalPath).ToArray());
            var category = hashes == 1 ? "identical-data-path" : keyResult.Category;
            var severity = hashes == 1 ? Severity.Informational : keyResult.Severity;
            output.Add(Make(category, severity, hashes == 1 ? Confidence.Proven : keyResult.Confidence, entries.Select(x => x.m).Distinct().ToArray(), group.Key,
                entries[0].m.InstallationId, true, false,
                hashes == 1 ? "Enabled mods provide identical bytes at the same virtual path." : keyResult.Explanation,
                "Review and explicitly order the intended winner; Songs of Syx resolves the virtual path before parsing it.", entries.Select(x => $"{x.m.InstallationId}:{x.f.Sha256}")));
        }
    }

    private static void AddClassCollisions(IEnumerable<ModInstallation> mods, List<Conflict> output)
    {
        foreach (var group in mods.SelectMany(m => m.Jars.SelectMany(j => j.Classes.Select(c => (m, j, c))))
                     .GroupBy(x => x.c.ClassName, StringComparer.Ordinal).Where(x => x.Count() > 1))
        {
            var entries = group.OrderByDescending(x => x.m.Priority ?? int.MinValue).ToArray();
            var identical = entries.Select(x => x.c.Sha256).Distinct(StringComparer.Ordinal).Count() == 1;
            var vanillaShadow = IsVanillaNamespace(group.Key);
            output.Add(Make(identical ? "identical-class-duplicate" : vanillaShadow ? "vanilla-shadow-collision" : "class-collision", identical ? Severity.Low : Severity.Blocking, Confidence.Proven,
                entries.Select(x => x.m).Distinct().ToArray(), group.Key, entries[0].m.InstallationId, false, true,
                identical ? "Byte-identical class definitions share the same binary name; the first classpath copy wins." : "Different class definitions share the same binary name; classpath order can only hide one implementation.",
                identical ? "Remove redundant embedded classes where practical." : "Do not enable both builds unless their authors provide an explicit compatibility patch.", entries.Select(x => $"{x.m.InstallationId}:{x.c.Sha256}")));
        }
    }

    private static void AddFrameworkPackages(IEnumerable<ModInstallation> mods, List<Conflict> output)
    {
        foreach (var mod in mods)
        {
            var choirClasses = mod.Jars.SelectMany(x => x.Classes).Where(x => x.ClassName.StartsWith("choir.", StringComparison.Ordinal)).ToArray();
            if (choirClasses.Length > 0 && mod.LogicalModId != "choir.framework")
                output.Add(Make("embedded-choir-framework", Severity.High, Confidence.Proven, [mod], "choir.*", null, false, true,
                    "A consumer artifact embeds Choir framework classes.", "Rebuild the consumer with Choir as an external compile/runtime dependency.", choirClasses.Take(20).Select(x => x.ClassName)));
            var legacy = mod.Jars.SelectMany(x => x.Classes).Where(x => x.ClassName.StartsWith("modoptions.", StringComparison.Ordinal)).ToArray();
            if (legacy.Length > 0)
                output.Add(Make("legacy-modoptions-package", Severity.High, Confidence.Proven, [mod], "modoptions.*", null, false, false,
                    "Artifact contains the retired legacy Mod Options package.", "Migrate to Choir Options and remove embedded legacy classes.", legacy.Take(20).Select(x => x.ClassName)));
        }
    }

    private static void AddDuplicateArtifacts(IEnumerable<ModInstallation> mods, List<Conflict> output)
    {
        foreach (var group in mods.SelectMany(m => m.Jars.Select(j => (m, j))).Where(x => x.j.Sha256.Length == 64)
                     .GroupBy(x => x.j.Sha256, StringComparer.Ordinal).Where(x => x.Count() > 1))
        {
            var entries = group.ToArray();
            output.Add(Make("duplicate-jar-artifact", Severity.Medium, Confidence.Proven, entries.Select(x => x.m).Distinct().ToArray(), group.Key,
                entries.OrderByDescending(x => x.m.Priority ?? int.MinValue).First().m.InstallationId, false, false,
                "The same JAR bytes are installed more than once.", "Remove stale or duplicate copies after verifying ownership.", entries.Select(x => $"{x.m.InstallationId}:{x.j.FileName}")));
        }
    }

    private static void AddStableIdCollisions(IEnumerable<ModInstallation> mods, List<Conflict> output)
    {
        foreach (var group in mods.SelectMany(m => m.StableIds.Select(x => (m, x))).GroupBy(x => (x.x.Kind, x.x.Id))
                     .Where(x => x.Select(y => y.m.InstallationId).Distinct(StringComparer.Ordinal).Count() > 1))
        {
            var entries = group.OrderByDescending(x => x.m.Priority ?? int.MinValue).ToArray();
            output.Add(Make("stable-id-collision", Severity.High, Confidence.Likely, entries.Select(x => x.m).Distinct().ToArray(), $"{group.Key.Kind}:{group.Key.Id}",
                entries[0].m.InstallationId, true, false, "Multiple mods appear to define the same data-backed stable ID.",
                "Inspect parser semantics and choose the intended owner.", entries.Select(x => x.x.EvidencePath)));
        }
    }

    private static void AddManifestIssues(IEnumerable<ModInstallation> mods, List<Conflict> output)
    {
        var list = mods.ToArray();
        var byId = list.GroupBy(x => x.LogicalModId, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.OrderByDescending(m => m.Priority ?? int.MinValue).First(), StringComparer.Ordinal);
        foreach (var mod in list)
        {
            if (mod.Manifest is { IsValid: false } manifest)
                output.Add(Make("malformed-choir-manifest", Severity.High, Confidence.Proven, [mod], manifest.DescriptorKind, null, false, false,
                    "Choir manifest is malformed.", "Repair the manifest.", manifest.Diagnostics));
            if (mod.Manifest is { } validManifest && validManifest.Capabilities.GroupBy(x => x, StringComparer.Ordinal).Any(x => x.Count() > 1))
                output.Add(Make("duplicate-capability-declaration", Severity.Low, Confidence.Proven, [mod], validManifest.ModId, null, false, false,
                    "Manifest repeats a capability declaration.", "Deduplicate the capabilities list.", validManifest.Capabilities));
            foreach (var dependency in mod.Manifest?.Required ?? [])
            {
                if (!byId.TryGetValue(dependency.ModId, out var target))
                    output.Add(Make("missing-dependency", Severity.Blocking, Confidence.Proven, [mod], dependency.ModId, null, false, true,
                        $"Required dependency {dependency.ModId}@{dependency.Constraint} is not enabled.", "Enable a compatible dependency.", []));
                else if (!VersionConstraint.Satisfies(target.Manifest?.Version ?? target.Metadata.Version, dependency.Constraint))
                    output.Add(Make("dependency-version", Severity.Blocking, Confidence.Proven, [mod, target], dependency.ModId, null, false, true,
                        "Enabled dependency version does not satisfy the declared constraint.", "Install a compatible version.", []));
            }
            foreach (var incompatible in mod.Manifest?.Incompatible ?? []) if (byId.TryGetValue(incompatible, out var other))
                output.Add(Make("declared-incompatibility", Severity.Blocking, Confidence.Proven, [mod, other], incompatible, null, false, true,
                    "An explicitly incompatible mod is enabled.", "Disable one of the incompatible mods.", []));
            if (mod.Manifest?.ChoirApiRange is { Length: > 0 } apiRange && byId.TryGetValue("choir.framework", out var choir) &&
                !VersionConstraint.Satisfies(choir.Manifest?.Version ?? choir.Metadata.Version, apiRange))
                output.Add(Make("unsupported-choir-api", Severity.Blocking, Confidence.Proven, [mod, choir], apiRange, null, false, true,
                    "Choir version does not satisfy the consumer's API range.", "Install compatible Choir and consumer builds.", []));
        }
    }

    private static void AddProviderCollisions(IEnumerable<ModInstallation> mods, List<Conflict> output)
    {
        foreach (var group in mods.Where(x => x.OptionsProviderId is not null).GroupBy(x => x.OptionsProviderId!, StringComparer.Ordinal).Where(x => x.Count() > 1))
        {
            var entries = group.OrderByDescending(x => x.Priority ?? int.MinValue).ToArray();
            output.Add(Make("options-provider-collision", Severity.High, Confidence.Proven, entries, group.Key, entries[0].InstallationId,
                false, true, "Multiple enabled mods declare the same Choir options provider ID.", "Assign unique provider IDs.", []));
        }
    }

    private static Conflict Make(string category, Severity severity, Confidence confidence, IReadOnlyList<ModInstallation> mods, string target,
        string? winner, bool orderResolvable, bool noValidOrder, string explanation, string action, IEnumerable<string> evidence) =>
        new($"{category}:{Hashing.Sha256(Encoding.UTF8.GetBytes(target + string.Join('|', mods.Select(x => x.InstallationId))))[..16]}", category,
            severity, confidence, mods.Select(x => x.InstallationId).Distinct(StringComparer.Ordinal).ToArray(), target, winner,
            orderResolvable, noValidOrder, explanation, action, evidence.ToArray());

    private static string CanonicalRuntimePath(string relative)
    {
        var slash = relative.Replace('\\', '/');
        var index = slash.IndexOf('/');
        return index >= 0 && slash.StartsWith('V') ? slash[(index + 1)..] : slash;
    }

    private static bool IsVanillaNamespace(string name) => name.StartsWith("game.", StringComparison.Ordinal) || name.StartsWith("init.", StringComparison.Ordinal) ||
        name.StartsWith("settlement.", StringComparison.Ordinal) || name.StartsWith("world.", StringComparison.Ordinal) || name.StartsWith("snake2d.", StringComparison.Ordinal);

    private static (string Category, Severity Severity, Confidence Confidence, string Explanation) AnalyzeTextKeys(IReadOnlyList<string> paths)
    {
        try
        {
            var maps = paths.Select(ParseSimpleKeys).ToArray();
            if (maps.Any(x => x.Count == 0)) return ("data-path-collision", Severity.Medium, Confidence.Unknown, "Enabled mods provide different bytes at the same virtual path; key-level semantics were not parseable.");
            var common = maps.Select(x => x.Keys.AsEnumerable()).Aggregate((a, b) => a.Intersect(b, StringComparer.Ordinal)).ToArray();
            if (common.Length == 0) return ("data-disjoint-keys", Severity.Low, Confidence.Possible, "Static parsing found disjoint keys, but the game selects one virtual file before parsing, so independent-looking entries still do not merge automatically.");
            var conflicting = common.Where(key => maps.Select(x => x[key]).Distinct(StringComparer.Ordinal).Count() > 1).ToArray();
            return conflicting.Length == 0
                ? ("data-identical-keys", Severity.Low, Confidence.Likely, "Overlapping parsed keys have identical textual values, although other file semantics may differ.")
                : ("data-conflicting-keys", Severity.High, Confidence.Likely, $"Parsed keys differ: {string.Join(",", conflicting.Take(10))}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ("data-path-collision", Severity.Medium, Confidence.Unknown, $"Key-level comparison failed: {ex.Message}");
        }
    }

    private static Dictionary<string, string> ParseSimpleKeys(string path)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!File.Exists(path) || new FileInfo(path).Length > 2 * 1024 * 1024) return result;
        foreach (var raw in File.ReadLines(path).Take(20_000))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal) || line.StartsWith('#')) continue;
            var separator = line.IndexOf(':');
            if (separator < 1) separator = line.IndexOf('=');
            if (separator < 1) continue;
            var key = line[..separator].Trim(' ', '\t', '"');
            if (MetadataParsers.IsStableId(key)) result[key] = line[(separator + 1)..].Trim().TrimEnd(',');
        }
        return result;
    }
}

public static class OrderSuggester
{
    public static IReadOnlyList<string> Suggest(IReadOnlyList<ModInstallation> mods, IReadOnlyList<string> current, DependencyGraphResult graph)
    {
        var byLogical = mods.Where(x => x.Enabled).GroupBy(x => x.LogicalModId, StringComparer.Ordinal).ToDictionary(x => x.Key, x => x.First(), StringComparer.Ordinal);
        var result = new List<string>();
        foreach (var logical in graph.DeterministicOrder)
            if (byLogical.TryGetValue(logical, out var mod) && !result.Contains(mod.FolderName, StringComparer.Ordinal)) result.Add(mod.FolderName);
        foreach (var id in ModPriorityOrder.FromOfficialOrder(current))
            if (!result.Contains(id, StringComparer.Ordinal)) result.Add(id);
        return result;
    }
}
