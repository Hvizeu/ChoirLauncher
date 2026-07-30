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
        var capabilityProviders = byId.Values
            .SelectMany(mod => (mod.Manifest?.Capabilities ?? []).Select(capability => (capability, mod)))
            .GroupBy(x => x.capability, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.Select(y => y.mod).ToArray(), StringComparer.Ordinal);
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
            foreach (var dependency in mod.Manifest?.Optional ?? [])
            {
                if (!byId.TryGetValue(dependency.ModId, out var target) ||
                    !VersionConstraint.Satisfies(target.Manifest?.Version ?? target.Metadata.Version, dependency.Constraint))
                    continue;
                edges[dependency.ModId].Add(mod.LogicalModId);
            }
            foreach (var capability in mod.Manifest?.RequiredCapabilities ?? [])
            {
                if (!capabilityProviders.TryGetValue(capability, out var providers) || providers.Length == 0)
                {
                    errors.Add($"Missing required capability {capability}.");
                    continue;
                }
                foreach (var provider in providers.Where(x => x.LogicalModId != mod.LogicalModId))
                    edges[provider.LogicalModId].Add(mod.LogicalModId);
            }
            foreach (var capability in mod.Manifest?.OptionalCapabilities ?? [])
                if (capabilityProviders.TryGetValue(capability, out var providers))
                    foreach (var provider in providers.Where(x => x.LogicalModId != mod.LogicalModId))
                        edges[provider.LogicalModId].Add(mod.LogicalModId);
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
    public static IReadOnlyList<Conflict> Analyze(
        IReadOnlyList<ModInstallation> mods,
        IReadOnlyList<string> enabledOrder,
        int targetMajor,
        VanillaContentIndex? vanillaContent = null)
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
        AddPackageIntegrity(enabled, conflicts);
        AddVanillaInteractions(enabled, vanillaContent ?? VanillaContentIndex.Empty, conflicts);
        return conflicts.OrderBy(x => x.Severity).ThenBy(x => x.ConflictId, StringComparer.Ordinal).ToArray();
    }

    private static void AddPackageIntegrity(IEnumerable<ModInstallation> mods, List<Conflict> output)
    {
        foreach (var mod in mods)
        {
            foreach (var jar in mod.Jars.Where(x => !x.IsValid))
                output.Add(Make("invalid-jar", Severity.High, Confidence.Proven, [mod], jar.RelativePath ?? jar.FileName,
                    null, false, true, "A Java archive could not be inspected safely.",
                    "Reinstall or repair the mod archive before launching it.", jar.Diagnostics));

            foreach (var jar in mod.Jars)
            {
                foreach (var group in jar.Classes.GroupBy(x => x.ClassName, StringComparer.Ordinal).Where(x => x.Count() > 1))
                    output.Add(Make("duplicate-class-in-jar", Severity.High, Confidence.Proven, [mod],
                        $"{jar.RelativePath ?? jar.FileName}:{group.Key}", null, false, true,
                        "One Java archive contains the same binary class name more than once.",
                        "Rebuild the archive with exactly one owner for this class.", group.Select(x => x.EntryPath)));
            }

            foreach (var group in mod.DataFiles.Where(x => x.Category == "runtime-selected")
                         .GroupBy(x => CanonicalRuntimePath(x.RelativePath), StringComparer.OrdinalIgnoreCase)
                         .Where(x => x.Select(y => CanonicalRuntimePath(y.RelativePath)).Distinct(StringComparer.Ordinal).Count() > 1))
            {
                output.Add(Make("internal-path-case-collision", Severity.High, Confidence.Proven, [mod], group.Key,
                    null, false, true, "This mod contains runtime paths that differ only by letter case.",
                    "Use one canonical path spelling so the package behaves consistently on Windows, Linux, and macOS.",
                    group.Select(x => x.RelativePath)));
            }

            var ignoreVanilla = mod.DataFiles.Where(x => x.Category == "runtime-selected" &&
                Path.GetFileName(x.RelativePath).Equals("_IgnoreVanilla.txt", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (ignoreVanilla.Length > 0)
                output.Add(Make("ignore-vanilla-directive", Severity.High, Confidence.Proven, [mod], "_IgnoreVanilla.txt",
                    null, false, false, "The package requests broad suppression of vanilla assets for part of its virtual tree.",
                    "Verify the directive is intentional and test the affected game version in isolation.",
                    ignoreVanilla.Select(x => x.RelativePath)));
        }
    }

    private static void AddVanillaInteractions(
        IEnumerable<ModInstallation> mods,
        VanillaContentIndex vanilla,
        List<Conflict> output)
    {
        if (!vanilla.Summary.Available) return;

        foreach (var mod in mods)
        {
            var shadows = mod.Jars.SelectMany(x => x.Classes)
                .Where(x => vanilla.ContainsClass(x.ClassName))
                .Select(x => x.ClassName)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray();
            if (shadows.Length > 0 && !IsPlatformRuntime(mod))
                output.Add(Make("vanilla-class-shadow", Severity.High, Confidence.Proven, [mod],
                    $"{shadows.Length} vanilla Java class shadow(s)", null, false, false,
                    "The mod replaces game Java classes. These replacements are version-sensitive and cannot be composed by load order.",
                    "Confirm the exact game build and avoid enabling another mod that owns any of the same classes.",
                    shadows.Take(100)));

            var exactOverrides = new List<string>();
            var caseMismatches = new List<string>();
            foreach (var file in mod.DataFiles.Where(x => x.Category == "runtime-selected"))
            {
                var runtimePath = VanillaContentIndex.NormalizeRuntimePath(CanonicalRuntimePath(file.RelativePath));
                if (!vanilla.TryGetDataPath(runtimePath, out var vanillaPath)) continue;
                if (runtimePath.Equals(vanillaPath, StringComparison.Ordinal)) exactOverrides.Add(runtimePath);
                else caseMismatches.Add($"{runtimePath} -> {vanillaPath}");
            }

            if (exactOverrides.Count > 0)
                output.Add(Make("vanilla-data-override", Severity.Informational, Confidence.Proven, [mod],
                    $"{exactOverrides.Count} vanilla data path override(s)", mod.InstallationId, true, false,
                    "The mod intentionally supplies files at vanilla virtual paths.",
                    "No action is required unless another enabled mod overrides the same paths.",
                    exactOverrides.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Take(100)));

            if (caseMismatches.Count > 0)
                output.Add(Make("vanilla-path-case-mismatch", Severity.Medium, Confidence.Proven, [mod],
                    $"{caseMismatches.Count} case-mismatched vanilla path(s)", null, false, false,
                    "The mod targets vanilla paths with different letter casing, which can behave differently across operating systems.",
                    "Rename package paths to match the vanilla archive exactly.",
                    caseMismatches.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Take(100)));
        }
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
            var syxForgeRuntimeClasses = mod.Jars.SelectMany(x => x.Classes).Where(x =>
                x.ClassName.Equals("io.github.syxforge.api.SyxForge", StringComparison.Ordinal) ||
                x.ClassName.StartsWith("io.github.syxforge.internal.", StringComparison.Ordinal)).ToArray();
            if (syxForgeRuntimeClasses.Length > 0 && mod.LogicalModId != "syxforge")
                output.Add(Make("embedded-syxforge-runtime", Severity.High, Confidence.Proven, [mod], "io.github.syxforge.*", null, false, true,
                    "A consumer artifact embeds SyxForge runtime classes.",
                    "Rebuild the consumer with SyxForge as an external compile/runtime dependency.",
                    syxForgeRuntimeClasses.Take(20).Select(x => x.ClassName)));
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
        var providedCapabilities = list.SelectMany(x => x.Manifest?.Capabilities ?? [])
            .ToHashSet(StringComparer.Ordinal);
        foreach (var mod in list)
        {
            if (mod.Manifest is { IsValid: false } manifest)
                output.Add(Make("malformed-platform-manifest", Severity.High, Confidence.Proven, [mod], manifest.DescriptorKind, null, false, false,
                    "Platform manifest is malformed.", "Repair the manifest.", manifest.Diagnostics));
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
            foreach (var incompatible in mod.Manifest?.Incompatible ?? [])
                if (byId.TryGetValue(incompatible, out var other))
                    output.Add(Make("declared-incompatibility", Severity.Blocking, Confidence.Proven, [mod, other], incompatible, null, false, true,
                        "An explicitly incompatible mod is enabled.", "Disable one of the incompatible mods.", []));
            foreach (var capability in mod.Manifest?.RequiredCapabilities ?? [])
                if (!providedCapabilities.Contains(capability))
                    output.Add(Make("missing-capability", Severity.Blocking, Confidence.Proven, [mod], capability, null, false, true,
                        $"Required capability {capability} is not provided by an enabled mod.", "Enable a compatible capability provider.", []));
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

    private static bool IsPlatformRuntime(ModInstallation mod) =>
        mod.LogicalModId is "syxforge" or "choir.framework" ||
        mod.Manifest?.DescriptorKind.Equals("detected SyxForge runtime", StringComparison.OrdinalIgnoreCase) == true;

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

public sealed record OrderSuggestion(
    IReadOnlyList<string> LogicalOrder,
    IReadOnlyList<string> FolderOrder,
    IReadOnlyDictionary<string, IReadOnlyList<string>> ReasonsByLogicalMod,
    IReadOnlyList<string> SkippedConstraints);

public static class OrderSuggester
{
    public static OrderSuggestion Suggest(
        IReadOnlyList<ModInstallation> mods,
        IReadOnlyList<string> current,
        IReadOnlyList<Conflict> conflicts)
    {
        var enabled = mods.Where(x => x.Enabled).OrderBy(x => x.Priority ?? int.MaxValue).ToArray();
        var byLogical = enabled.GroupBy(x => x.LogicalModId, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.OrderByDescending(m => m.Priority ?? int.MinValue).First(), StringComparer.Ordinal);
        var byInstallation = enabled.ToDictionary(x => x.InstallationId, StringComparer.Ordinal);
        var edges = byLogical.Keys.ToDictionary(x => x, _ => new HashSet<string>(StringComparer.Ordinal), StringComparer.Ordinal);
        var reasons = byLogical.Keys.ToDictionary(x => x, _ => new List<string>(), StringComparer.Ordinal);
        var skipped = new List<string>();

        foreach (var mod in byLogical.Values)
        {
            foreach (var dependency in mod.Manifest?.Required ?? [])
            {
                if (!byLogical.ContainsKey(dependency.ModId) || dependency.ModId == mod.LogicalModId) continue;
                if (edges[dependency.ModId].Add(mod.LogicalModId))
                    reasons[mod.LogicalModId].Add($"Loads after required dependency {dependency.ModId}.");
            }
            foreach (var dependency in mod.Manifest?.Optional ?? [])
            {
                if (!byLogical.TryGetValue(dependency.ModId, out var target) ||
                    dependency.ModId == mod.LogicalModId ||
                    !VersionConstraint.Satisfies(target.Manifest?.Version ?? target.Metadata.Version, dependency.Constraint))
                    continue;
                if (edges[dependency.ModId].Add(mod.LogicalModId))
                    reasons[mod.LogicalModId].Add($"Loads after available optional dependency {dependency.ModId}.");
            }
            foreach (var capability in mod.Manifest?.RequiredCapabilities ?? [])
            {
                foreach (var provider in byLogical.Values.Where(candidate =>
                             candidate.LogicalModId != mod.LogicalModId &&
                             (candidate.Manifest?.Capabilities ?? []).Contains(capability, StringComparer.Ordinal)))
                    if (edges[provider.LogicalModId].Add(mod.LogicalModId))
                        reasons[mod.LogicalModId].Add($"Loads after provider {provider.LogicalModId} for required capability {capability}.");
            }
            foreach (var capability in mod.Manifest?.OptionalCapabilities ?? [])
            {
                foreach (var provider in byLogical.Values.Where(candidate =>
                             candidate.LogicalModId != mod.LogicalModId &&
                             (candidate.Manifest?.Capabilities ?? []).Contains(capability, StringComparer.Ordinal)))
                    if (edges[provider.LogicalModId].Add(mod.LogicalModId))
                        reasons[mod.LogicalModId].Add($"Loads after provider {provider.LogicalModId} for optional capability {capability}.");
            }
        }

        foreach (var conflict in conflicts.Where(x => x.OrderResolvable && x.CurrentWinner is not null && x.InvolvedMods.Count > 1)
                     .OrderBy(x => x.Severity).ThenBy(x => x.ConflictId, StringComparer.Ordinal))
        {
            if (!byInstallation.TryGetValue(conflict.CurrentWinner!, out var winner)) continue;
            foreach (var loserId in conflict.InvolvedMods.Where(x => x != conflict.CurrentWinner))
            {
                if (!byInstallation.TryGetValue(loserId, out var loser) || loser.LogicalModId == winner.LogicalModId) continue;
                if (HasPath(edges, winner.LogicalModId, loser.LogicalModId))
                {
                    skipped.Add($"{conflict.Category}: cannot keep {winner.LogicalModId} above {loser.LogicalModId} without violating a dependency or earlier constraint.");
                    continue;
                }
                if (edges[loser.LogicalModId].Add(winner.LogicalModId))
                    reasons[winner.LogicalModId].Add($"Keeps the selected winner over {loser.LogicalModId} for {conflict.Category}.");
            }
        }

        var currentRank = enabled.Select((mod, index) => (mod.LogicalModId, index))
            .GroupBy(x => x.LogicalModId, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.Min(y => y.index), StringComparer.Ordinal);
        var incoming = byLogical.Keys.ToDictionary(x => x, _ => 0, StringComparer.Ordinal);
        foreach (var targets in edges.Values)
            foreach (var target in targets)
                incoming[target]++;

        var ready = new SortedSet<string>(Comparer<string>.Create((left, right) =>
        {
            var comparison = currentRank.GetValueOrDefault(left, int.MaxValue).CompareTo(currentRank.GetValueOrDefault(right, int.MaxValue));
            return comparison != 0 ? comparison : StringComparer.Ordinal.Compare(left, right);
        }));
        foreach (var pair in incoming)
            if (pair.Value == 0)
                ready.Add(pair.Key);

        var logicalOrder = new List<string>(byLogical.Count);
        while (ready.Count > 0)
        {
            var next = ready.Min!;
            ready.Remove(next);
            logicalOrder.Add(next);
            foreach (var target in edges[next].Order(StringComparer.Ordinal))
                if (--incoming[target] == 0)
                    ready.Add(target);
        }
        foreach (var remaining in byLogical.Keys.Except(logicalOrder, StringComparer.Ordinal).OrderBy(x => currentRank.GetValueOrDefault(x, int.MaxValue)))
        {
            logicalOrder.Add(remaining);
            skipped.Add($"Dependency cycle keeps {remaining} in its current relative position.");
        }

        var folderOrder = logicalOrder.Select(x => byLogical[x].FolderName).ToList();
        foreach (var folder in ModPriorityOrder.FromOfficialOrder(current))
            if (!folderOrder.Contains(folder, StringComparer.Ordinal))
                folderOrder.Add(folder);

        return new(
            logicalOrder,
            folderOrder,
            reasons.ToDictionary(x => x.Key, x => (IReadOnlyList<string>)x.Value.Distinct(StringComparer.Ordinal).ToArray(), StringComparer.Ordinal),
            skipped.Distinct(StringComparer.Ordinal).ToArray());
    }

    private static bool HasPath(IReadOnlyDictionary<string, HashSet<string>> edges, string start, string target)
    {
        var pending = new Stack<string>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        pending.Push(start);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if (!visited.Add(current)) continue;
            if (current == target) return true;
            if (edges.TryGetValue(current, out var next))
                foreach (var candidate in next)
                    pending.Push(candidate);
        }
        return false;
    }
}
