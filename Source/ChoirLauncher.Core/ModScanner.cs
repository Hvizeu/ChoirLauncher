using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ChoirLauncher.Core;

public sealed class ModScanner
{
    public const int MaxFilesPerMod = 25_000;
    public const int MaxArchiveEntries = 20_000;
    public const long MaxArchiveUncompressedBytes = 512L * 1024 * 1024;

    public ScanReport Scan(ScanRequest request, CancellationToken cancellationToken = default, IProgress<double>? progress = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var settingsText = File.ReadAllText(request.LauncherSettingsPath, Encoding.UTF8);
        var settings = LauncherSettingsDocument.Parse(settingsText);
        var discovered = new List<DiscoveredDirectory>();
        discovered.AddRange(DiscoverRoot(request.LocalModsRoot, ModSourceType.Local, cancellationToken));
        discovered.AddRange(DiscoverRoot(request.WorkshopModsRoot, ModSourceType.Workshop, cancellationToken));

        var firstByFolder = discovered.GroupBy(x => x.FolderName, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.First(), StringComparer.Ordinal);
        var priorityByFolder = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var index = 0; index < settings.EnabledMods.Count; index++)
            if (!priorityByFolder.ContainsKey(settings.EnabledMods[index]))
                priorityByFolder[settings.EnabledMods[index]] = ModPriorityOrder.FromOfficialIndex(index, settings.EnabledMods.Count);

        var mods = new List<ModInstallation>(discovered.Count);
        for (var index = 0; index < discovered.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = discovered[index];
            mods.Add(ScanMod(directory, request.TargetMajorVersion,
                priorityByFolder.TryGetValue(directory.FolderName, out var p) && ReferenceEquals(firstByFolder[directory.FolderName], directory),
                priorityByFolder.TryGetValue(directory.FolderName, out p) && ReferenceEquals(firstByFolder[directory.FolderName], directory) ? p : null,
                cancellationToken));
            progress?.Report(discovered.Count == 0 ? 1 : (index + 1.0) / discovered.Count);
        }
        var graph = DependencyGraphResolver.Resolve(mods.Where(x => x.Enabled).ToArray());
        var conflicts = ConflictAnalyzer.Analyze(mods, settings.EnabledMods, request.TargetMajorVersion);
        var suggestion = OrderSuggester.Suggest(mods, settings.EnabledMods, graph);
        return new("choir-launcher.scan.v1", request.TargetGameVersion, LauncherSettingsDocument.Sha256(settingsText),
            request.GameJarPath is { Length: > 0 } && File.Exists(request.GameJarPath) ? Hashing.Sha256File(request.GameJarPath) : null,
            settings.EnabledMods, mods.ToArray(), graph, conflicts, suggestion,
            ModPriorityOrder.UserFacingRule, DateTimeOffset.UtcNow);
    }

    private static IEnumerable<DiscoveredDirectory> DiscoverRoot(string root, ModSourceType source, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(root)) yield break;
        foreach (var path in Directory.EnumerateDirectories(root).Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var info = new DirectoryInfo(path);
            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
            yield return new(path, info.Name, source);
        }
    }

    private static ModInstallation ScanMod(DiscoveredDirectory directory, int targetMajor, bool enabled, int? priority, CancellationToken cancellationToken)
    {
        var diagnostics = new List<string>();
        var metadataPath = Path.Combine(directory.Path, "_Info.txt");
        var metadata = MetadataParsers.ParseInfo(File.Exists(metadataPath) ? SafeReadText(metadataPath, diagnostics) : null);
        var versionRoot = SelectVersionRoot(directory.Path, targetMajor, diagnostics, out var selectedMajor);
        var files = new List<FileInventory>();
        var jars = new List<JarInventory>();
        var stableIds = new List<StableIdObservation>();
        var javaAgentRequirements = new List<JavaAgentRequirement>();
        ChoirManifest? manifest = null;
        string? optionsProviderId = null;

        var allPaths = EnumerateSafeFiles(directory.Path, diagnostics, cancellationToken).Take(MaxFilesPerMod + 1).ToArray();
        if (allPaths.Length > MaxFilesPerMod) diagnostics.Add($"File limit exceeded ({MaxFilesPerMod}). Remaining files were not scanned.");
        foreach (var path in allPaths.Take(MaxFilesPerMod))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Normalize(Path.GetRelativePath(directory.Path, path));
            try
            {
                var info = new FileInfo(path);
                files.Add(new(relative, Hashing.Sha256File(path), info.Length, Classify(relative, selectedMajor), path));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { diagnostics.Add($"Could not hash {relative}: {ex.Message}"); }
        }

        if (versionRoot is not null)
        {
            var manifestPath = Path.Combine(versionRoot, "choir", "core-platform.properties");
            if (File.Exists(manifestPath)) manifest = MetadataParsers.ParseChoirManifest(SafeReadText(manifestPath, diagnostics));
            var providerPath = Path.Combine(versionRoot, "choir", "options-provider.properties");
            if (File.Exists(providerPath)) optionsProviderId = MetadataParsers.ParseOptionsProviderId(SafeReadText(providerPath, diagnostics));
            var launchDescriptorPath = Path.Combine(versionRoot, "choir", "launch.json");
            foreach (var jarPath in EnumerateSafeFiles(versionRoot, diagnostics, cancellationToken).Where(x => x.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)))
            {
                var jar = InspectJar(jarPath) with { RelativePath = Normalize(Path.GetRelativePath(versionRoot, jarPath)) };
                jars.Add(jar);
                if (manifest is null)
                {
                    var embedded = ReadArchiveText(jarPath, "META-INF/choir/mod.json", diagnostics);
                    if (embedded is not null) manifest = MetadataParsers.ParseChoirJsonManifest(embedded);
                    embedded ??= ReadArchiveText(jarPath, "choir/core-platform.properties", diagnostics);
                    if (embedded is not null && manifest is null) manifest = MetadataParsers.ParseChoirManifest(embedded, "embedded choir/core-platform.properties");
                }
                if (optionsProviderId is null)
                {
                    var provider = ReadArchiveText(jarPath, "choir/options-provider.properties", diagnostics);
                    if (provider is not null) optionsProviderId = MetadataParsers.ParseOptionsProviderId(provider);
                }
            }
            if (File.Exists(launchDescriptorPath))
                javaAgentRequirements.AddRange(JavaAgentDescriptorParser.Parse(SafeReadText(launchDescriptorPath, diagnostics), directory.Source, directory.FolderName, selectedMajor, diagnostics));
            stableIds.AddRange(ObserveStableIds(versionRoot, diagnostics, cancellationToken));
        }

        var contentFingerprint = ComputeContentFingerprint(files);
        var sourceId = directory.FolderName;
        var logicalId = manifest?.ModId is { Length: > 0 } id ? id : directory.FolderName;
        var installationId = $"{directory.Source.ToString().ToLowerInvariant()}:{sourceId}:{contentFingerprint[..16]}";
        if (javaAgentRequirements.Count > 0)
        {
            javaAgentRequirements = javaAgentRequirements.Select(requirement => requirement with
            {
                RequirementId = requirement.RequirementId.Replace("declared:pending:", "declared:" + installationId + ":", StringComparison.Ordinal),
                OwningInstallationId = installationId,
                OwningLogicalModId = logicalId
            }).ToList();
        }
        if (metadata.GameVersionMajor > 0 && metadata.GameVersionMajor != targetMajor)
            diagnostics.Add($"Metadata targets V{metadata.GameVersionMajor}, not V{targetMajor}.");
        var scanned = new ModInstallation(installationId, logicalId, sourceId, directory.Source, directory.FolderName, directory.Path,
            contentFingerprint, metadata, selectedMajor, enabled, priority, manifest, optionsProviderId,
            jars, files, stableIds, diagnostics.Concat(metadata.Diagnostics).Distinct(StringComparer.Ordinal).ToArray());
        javaAgentRequirements.AddRange(KnownJavaAgentRecipeCatalog.RequirementsFor(scanned));
        return new(installationId, logicalId, sourceId, directory.Source, directory.FolderName, directory.Path,
            contentFingerprint, metadata, selectedMajor, enabled, priority, manifest, optionsProviderId,
            jars, files, stableIds, diagnostics.Concat(metadata.Diagnostics).Distinct(StringComparer.Ordinal).ToArray())
        { JavaAgentRequirements = javaAgentRequirements.ToArray() };
    }

    private static string? SelectVersionRoot(string root, int targetMajor, List<string> diagnostics, out int? selectedMajor)
    {
        var exact = Path.Combine(root, $"V{targetMajor}");
        if (Directory.Exists(exact)) { selectedMajor = targetMajor; return exact; }
        var candidates = Directory.EnumerateDirectories(root, "V*", SearchOption.TopDirectoryOnly)
            .Select(x => (Path: x, Parsed: int.TryParse(Path.GetFileName(x).AsSpan(1), out var major) ? major : (int?)null))
            .Where(x => x.Parsed is not null).OrderByDescending(x => x.Parsed).ToArray();
        if (candidates.Length == 0) { selectedMajor = null; diagnostics.Add("No V<number> content directory."); return null; }
        var fallback = candidates.First();
        selectedMajor = fallback.Parsed;
        diagnostics.Add($"No exact V{targetMajor}; inspected fallback V{selectedMajor}. The game launcher has its own legacy fallback algorithm.");
        return fallback.Path;
    }

    private static IEnumerable<string> EnumerateSafeFiles(string root, List<string> diagnostics, CancellationToken cancellationToken)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = pending.Pop();
            IEnumerable<FileSystemInfo> entries;
            try { entries = new DirectoryInfo(current).EnumerateFileSystemInfos().OrderBy(x => x.Name, StringComparer.Ordinal).ToArray(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { diagnostics.Add($"Cannot enumerate {Path.GetFileName(current)}: {ex.Message}"); continue; }
            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint)) { diagnostics.Add($"Skipped reparse point: {entry.Name}"); continue; }
                if (entry is DirectoryInfo directory) pending.Push(directory.FullName);
                else if (entry is FileInfo file) yield return file.FullName;
            }
        }
    }

    public static JarInventory InspectJar(string path)
    {
        var diagnostics = new List<string>();
        var classes = new List<ArchiveClass>();
        var descriptors = new List<string>();
        try
        {
            var sha256 = File.Exists(path) ? Hashing.Sha256File(path) : "";
            using var archive = ZipFile.OpenRead(path);
            if (archive.Entries.Count > MaxArchiveEntries) throw new InvalidDataException($"Archive has more than {MaxArchiveEntries} entries.");
            long total = 0;
            JavaAgentManifestFacts? agentManifest = null;
            foreach (var entry in archive.Entries)
            {
                total += entry.Length;
                if (total > MaxArchiveUncompressedBytes) throw new InvalidDataException("Archive uncompressed-size limit exceeded.");
                if (IsUnsafeArchivePath(entry.FullName)) throw new InvalidDataException($"Unsafe archive entry path: {entry.FullName}");
                if (entry.FullName.EndsWith(".class", StringComparison.Ordinal))
                {
                    using var stream = entry.Open();
                    classes.Add(new(entry.FullName[..^6].Replace('/', '.'), entry.FullName, Hashing.Sha256(stream)));
                }
                if (entry.FullName.Equals("META-INF/choir/mod.json", StringComparison.OrdinalIgnoreCase) ||
                    entry.FullName.Equals("choir/core-platform.properties", StringComparison.OrdinalIgnoreCase) ||
                    entry.FullName.Equals("choir/options-provider.properties", StringComparison.OrdinalIgnoreCase))
                {
                    if (entry.Length > 1024 * 1024) throw new InvalidDataException($"Descriptor exceeds 1 MiB: {entry.FullName}");
                    descriptors.Add(entry.FullName);
                }
            }
            agentManifest = JavaAgentManifestReader.Read(archive, Path.GetFileName(path), sha256, diagnostics);
            var jarInfo = new FileInfo(path);
            return new(jarInfo.Name, jarInfo.Exists ? jarInfo.Length : 0, sha256, diagnostics.Count == 0,
                classes, descriptors, diagnostics) { JavaAgentManifest = agentManifest };
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            diagnostics.Add(ex.Message);
        }
        var info = new FileInfo(path);
        return new(info.Name, info.Exists ? info.Length : 0, info.Exists ? Hashing.Sha256File(path) : "", diagnostics.Count == 0,
            classes, descriptors, diagnostics);
    }

    private static string? ReadArchiveText(string path, string entryName, List<string> diagnostics)
    {
        try
        {
            using var archive = ZipFile.OpenRead(path);
            var entry = archive.Entries.FirstOrDefault(x => x.FullName.Equals(entryName, StringComparison.OrdinalIgnoreCase));
            if (entry is null) return null;
            if (entry.Length > 1024 * 1024) throw new InvalidDataException($"Descriptor too large: {entryName}");
            using var reader = new StreamReader(entry.Open(), Encoding.UTF8, true, 4096, false);
            return reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException) { diagnostics.Add($"Cannot read {entryName}: {ex.Message}"); return null; }
    }

    private static IEnumerable<StableIdObservation> ObserveStableIds(string versionRoot, List<string> diagnostics, CancellationToken cancellationToken)
    {
        var results = new List<StableIdObservation>();
        foreach (var path in EnumerateSafeFiles(versionRoot, diagnostics, cancellationToken).Where(x => !x.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)))
        {
            var relative = Normalize(Path.GetRelativePath(versionRoot, path));
            var parts = relative.Split('/');
            if (parts.Length >= 3 && parts[0].Equals("assets", StringComparison.OrdinalIgnoreCase) &&
                parts[1].Equals("init", StringComparison.OrdinalIgnoreCase))
            {
                var id = Path.GetFileNameWithoutExtension(parts[^1]);
                if (MetadataParsers.IsStableId(id)) results.Add(new(parts.Length > 2 ? parts[2] : "init", id, relative, Confidence.Proven));
            }
        }
        return results.DistinctBy(x => (x.Kind, x.Id, x.EvidencePath)).ToArray();
    }

    private static string ComputeContentFingerprint(IEnumerable<FileInventory> files)
    {
        var canonical = string.Join('\n', files.OrderBy(x => x.RelativePath, StringComparer.Ordinal)
            .Select(x => $"{x.RelativePath}\0{x.Size}\0{x.Sha256}"));
        return Hashing.Sha256(Encoding.UTF8.GetBytes(canonical));
    }

    private static bool IsUnsafeArchivePath(string path) => path.StartsWith('/') || path.StartsWith('\\') || path.Contains("../", StringComparison.Ordinal) || path.Contains("..\\", StringComparison.Ordinal) || Path.IsPathRooted(path);
    private static string SafeReadText(string path, List<string> diagnostics)
    {
        try
        {
            if (new FileInfo(path).Length > 1024 * 1024) throw new InvalidDataException($"Descriptor exceeds 1 MiB: {Path.GetFileName(path)}");
            return File.ReadAllText(path, Encoding.UTF8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { diagnostics.Add(ex.Message); return ""; }
    }
    private static string Normalize(string path) => path.Replace('\\', '/');
    private static string Classify(string path, int? selectedMajor)
    {
        if (path.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)) return "jar";
        if (selectedMajor is not null && path.StartsWith($"V{selectedMajor}/", StringComparison.OrdinalIgnoreCase)) return "runtime-selected";
        return path.StartsWith("V", StringComparison.OrdinalIgnoreCase) ? "runtime-inactive-version" : "metadata";
    }
    private sealed record DiscoveredDirectory(string Path, string FolderName, ModSourceType Source);
}
