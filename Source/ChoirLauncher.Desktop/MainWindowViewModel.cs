using System.Collections.ObjectModel;
using ChoirLauncher.Core;

namespace ChoirLauncher.Desktop;

public sealed record DependencyChangePlan(IReadOnlyList<string> RequiredEntryIds, IReadOnlyList<string> DependentEntryIds);

public sealed class MainWindowViewModel : ObservableObject
{
    private readonly ManagerStoragePaths storage;
    private readonly ManagerProfileRepository repository;
    private readonly LauncherUpdatePreferencesStore updatePreferences;
    private readonly LauncherUpdateService updateService;
    private readonly ApplicationLog log;
    private readonly IGameLaunchService? gameLaunchServiceOverride;
    private readonly SemaphoreSlim launchGate = new(1, 1);
    private SongsOfSyxEnvironment environment = null!;
    private ScanReport? scan;
    private ProfileEditorSession? editor;
    private ResolvedProfile? resolved;
    private DependencyGraphResult? graph;
    private IReadOnlyList<Conflict> conflicts = [];
    private string? savedProfileText;
    private string searchText = "";
    private string filter = "All";
    private string status = "Starting…";
    private bool isBusy;
    private double progress;
    private ManagerProfile? selectedProfile;
    private CancellationTokenSource? refreshCancellation;

    public MainWindowViewModel(string? storageOverride = null, IGameLaunchService? gameLaunchServiceOverride = null, IUpdateReleaseClient? updateClient = null)
    {
        storage = ManagerStoragePaths.Resolve(storageOverride);
        repository = new(storage);
        updatePreferences = new(storage);
        updateService = new(updateClient);
        log = new(storage);
        this.gameLaunchServiceOverride = gameLaunchServiceOverride;
        log.Write("INFO", "application-start", $"version={BuildInfo.Version}");
    }

    public ObservableCollection<ManagerProfile> Profiles { get; } = [];
    public ObservableCollection<ModRowViewModel> Rows { get; } = [];
    public ObservableCollection<ModRowViewModel> VisibleRows { get; } = [];
    public IReadOnlyList<Conflict> Conflicts => conflicts;
    public IReadOnlyList<ConfigurationBackup> Backups => new ConfigurationBackupStore(storage.Backups).List();
    public ManagerStoragePaths StoragePaths => storage;
    public SongsOfSyxEnvironment Environment => environment;
    public ManagerProfile? CurrentProfile => editor?.Current;
    public ResolvedProfile? ResolvedProfile => resolved;
    public IReadOnlyList<ManagerProfileEntry> RemovedProfileEntries => CurrentProfile?.RemovedMods ?? [];
    public IReadOnlyList<ModInstallation> Installations => scan?.Mods ?? [];
    public string VersionText => $"{BuildInfo.ProductName} {BuildInfo.Version}";
    public string BuildId => BuildInfo.BuildId;
    public string PriorityHelp => ModPriorityOrder.UserFacingRule;
    public string LaunchExplanation => "Launch Songs of Syx through the verified direct-game route, apply this profile before launching, or open the official launcher. ChoirLauncher never changes the official mod state without a separate preview and confirmation.";
    public bool LaunchEnabled => true;
    public bool IsFiltering => !string.IsNullOrWhiteSpace(SearchText) || Filter != "All";
    public bool CanDrag => !IsFiltering;
    public bool CanUndo => editor?.CanUndo ?? false;
    public bool CanRedo => editor?.CanRedo ?? false;
    public IReadOnlyList<string> LastEditSelection => editor?.CurrentSelection ?? [];
    public bool IsDirty => editor is not null && savedProfileText != ManagerProfileRepository.Serialize(editor.Current);
    public int EnabledCount => CurrentProfile?.Mods.Count(x => x.Enabled) ?? 0;
    public int TotalCount => CurrentProfile?.Mods.Count ?? 0;
    public int BlockingConflictCount => conflicts.Count(x => x.Severity == Severity.Blocking);

    public ManagerProfile? SelectedProfile
    {
        get => selectedProfile;
        set { if (Set(ref selectedProfile, value) && value is not null) SelectProfile(value); }
    }

    public string SearchText
    {
        get => searchText;
        set { if (Set(ref searchText, value)) { ApplyFilter(); Raise(nameof(IsFiltering)); Raise(nameof(CanDrag)); } }
    }

    public string Filter
    {
        get => filter;
        set { if (Set(ref filter, value)) { ApplyFilter(); Raise(nameof(IsFiltering)); Raise(nameof(CanDrag)); } }
    }

    public string Status { get => status; private set => Set(ref status, value); }
    public bool IsBusy
    {
        get => isBusy;
        private set { if (Set(ref isBusy, value)) Raise(nameof(LaunchEnabled)); }
    }
    public double Progress { get => progress; private set => Set(ref progress, value); }

    public async Task InitializeAsync() => await RefreshInstallationsAsync(true);

    public SongsOfSyxEnvironment DiscoverEnvironment() => SongsOfSyxEnvironmentLocator.Locate(storage);

    public GameLocationPreference? LoadGameLocationPreference() => new GameLocationPreferencesStore(storage).Load();

    public GameLocationDetection AutoDetectGameLocation()
    {
        var detection = SongsOfSyxEnvironmentLocator.AutoDetectGameLocation();
        foreach (var diagnostic in detection.Diagnostics)
            log.Write("WARN", "game-location-auto-detect", diagnostic);
        return detection;
    }

    public LauncherUpdatePreferences LoadUpdatePreferences() => updatePreferences.Load();

    public void SaveUpdatePreferences(LauncherUpdatePreferences preferences)
    {
        updatePreferences.Save(preferences);
        log.Write("INFO", "update-preferences-saved", $"channel={preferences.Channel} startup={preferences.CheckOnStartup}");
    }

    public bool ShouldCheckForUpdatesOnStartup(DateTimeOffset now) => LauncherUpdateService.ShouldCheckOnStartup(updatePreferences.Load(), now);

    public async Task<LauncherUpdateCheckResult> CheckForUpdatesAsync(bool force, CancellationToken token = default)
    {
        var preferences = updatePreferences.Load();
        var previousStatus = Status;
        Status = force ? "Checking GitHub Releases for ChoirLauncher updates..." : "Checking for ChoirLauncher updates...";
        var result = await updateService.CheckAsync(preferences, force, DateTimeOffset.UtcNow, token);
        updatePreferences.Save(result.Preferences);
        if (force || result.ShouldNotify || result.Status == LauncherUpdateStatus.Failed)
            Status = result.Message;
        else
            Status = previousStatus;
        log.Write(result.Status == LauncherUpdateStatus.Failed ? "WARN" : "INFO", "update-check",
            $"status={result.Status} channel={result.Channel} candidate={result.Candidate?.Version ?? "none"} package={result.Candidate?.Package?.FileName ?? "none"}");
        return result;
    }

    public void SaveGameLocation(string gameRoot, string selectionSource)
    {
        new GameLocationPreferencesStore(storage).Save(gameRoot, selectionSource);
        log.Write("INFO", "game-location-saved", $"source={selectionSource} root={gameRoot}");
    }

    public LauncherGameOptions LoadLauncherOptions() => LauncherOptionsService.Load(RequireEnvironment().LauncherSettingsPath);
    public LauncherOptionsPreview CreateLauncherOptionsPreview(LauncherGameOptions proposed)
        => LauncherOptionsService.CreatePreview(RequireEnvironment().LauncherSettingsPath, proposed);
    public IReadOnlyList<GameLanguage> DiscoverGameLanguages() => GameLanguageCatalog.Discover(RequireEnvironment().GameRoot);
    public GameLauncherText LoadGameLauncherText(string languageCode) => GameLanguageCatalog.LoadText(RequireEnvironment().GameRoot, languageCode);
    public GameInstallationInfo DiscoverGameInfo() => GameInstallationInfoService.Discover(RequireEnvironment());

    public async Task<ApplyResult> ApplyLauncherOptionsAsync(LauncherOptionsPreview preview, CancellationToken token = default)
    {
        var writer = new LauncherOptionsWriter(new ProductionConfigurationWriteGuard(), new SongsOfSyxProcessInspector(RequireEnvironment().GameRoot),
            new NamedMutexApplyLockFactory(), new ConfigurationBackupStore(storage.Backups));
        var result = await writer.ApplyAsync(preview, token);
        Status = result.Success ? "Game launcher settings applied and verified." : string.Join(" ", result.Diagnostics);
        log.Write(result.Success ? "INFO" : "ERROR", "launcher-settings-apply",
            $"success={result.Success} changes={preview.Changes.Count} backup={result.BackupId ?? "none"} hash={result.FinalSha256 ?? "none"} diagnostics={string.Join(" | ", result.Diagnostics)}");
        return result;
    }

    public async Task RefreshInstallationsAsync(bool initial = false)
    {
        refreshCancellation?.Cancel();
        refreshCancellation = new();
        var token = refreshCancellation.Token;
        var automaticallyAdded = 0;
        IsBusy = true; Progress = 0.05; Status = "Discovering Songs of Syx environment…";
        try
        {
            environment = SongsOfSyxEnvironmentLocator.Locate(storage);
            foreach (var diagnostic in environment.Diagnostics)
                log.Write("WARN", "environment-discovery", diagnostic);
            if (!File.Exists(environment.LauncherSettingsPath)) throw new FileNotFoundException("Official LauncherSettings.txt was not found.", environment.LauncherSettingsPath);
            var game = SongsOfSyxGameArtifactInspector.Inspect(environment.GameJarPath);
            var targetMajor = game.Version?.Major ?? BuildInfo.TargetGameMajor;
            var targetVersion = game.Version?.Display ?? BuildInfo.TargetGameVersion;
            log.Write(game.StructurallyValid ? "INFO" : "WARN", "game-build",
                $"version={game.Version?.Display ?? "unknown"} known={game.KnownBuild} structural={game.StructurallyValid} jarHash={game.JarSha256 ?? "unavailable"} diagnostics={string.Join(" | ", game.Diagnostics)}");
            Progress = 0.15; Status = "Scanning local and Workshop mods…";
            var scanProgress = new Progress<double>(value => Progress = 0.15 + Math.Clamp(value, 0, 1) * 0.6);
            scan = await Task.Run(() => new ModScanner().Scan(new(environment.LocalModsRoot, environment.WorkshopModsRoot ?? Path.Combine(storage.Root, "missing-workshop"),
                environment.LauncherSettingsPath, environment.GameJarPath, targetMajor, targetVersion), token, scanProgress), token);
            Progress = 0.8; Status = "Resolving profiles and conflicts…";
            if (initial)
            {
                Profiles.Clear();
                var loadedProfiles = repository.LoadAll();
                DefaultProfilePolicy.Ensure(repository, loadedProfiles, scan);
                var reconciliations = ReconcileStoredProfiles();
                automaticallyAdded = reconciliations.Sum(x => x.AddedEntries.Count);
                var defaultProfile = reconciliations.Select(x => x.Profile).First(DefaultProfilePolicy.IsDefault);
                Profiles.Add(defaultProfile);
                foreach (var profile in reconciliations.Select(x => x.Profile).Where(profile => !DefaultProfilePolicy.IsDefault(profile))) Profiles.Add(profile);
                var preferred = new DesktopPreferencesStore(storage).Load().LastProfileId;
                SelectedProfile = Profiles.FirstOrDefault(x => x.ProfileId == preferred) ?? defaultProfile;
            }
            else if (editor is not null)
            {
                var wasDirty = IsDirty;
                var activeProfileId = editor.Current.ProfileId;
                var reconciliations = ReconcileStoredProfiles();
                automaticallyAdded = reconciliations.Sum(x => x.AddedEntries.Count);
                for (var index = 0; index < Profiles.Count; index++)
                {
                    if (Profiles[index].ProfileId == activeProfileId) continue;
                    var updated = reconciliations.FirstOrDefault(x => x.Profile.ProfileId == Profiles[index].ProfileId)?.Profile;
                    if (updated is not null) Profiles[index] = updated;
                }
                var storedActive = reconciliations.FirstOrDefault(x => x.Profile.ProfileId == activeProfileId);
                if (storedActive is not null)
                {
                    if (wasDirty)
                    {
                        var inMemory = ProfileInventoryReconciler.Reconcile(editor.Current, scan.Mods);
                        editor.Append(inMemory.AddedEntries, "Automatically add newly installed mods");
                    }
                    else editor = new(storedActive.Profile);
                    savedProfileText = ManagerProfileRepository.Serialize(storedActive.Profile);
                }
                Recompute();
            }
            Progress = 1;
            Status = automaticallyAdded == 0
                ? $"Ready — {scan.Mods.Count} installations, {EnabledCount} enabled."
                : $"Ready — {scan.Mods.Count} installations, {EnabledCount} enabled. Added {automaticallyAdded} new profile entr{(automaticallyAdded == 1 ? "y" : "ies")}.";
            log.Write("INFO", "scan-complete", $"installations={scan.Mods.Count} enabled={EnabledCount} profile={CurrentProfile?.ProfileId} automaticallyAdded={automaticallyAdded}");
        }
        catch (OperationCanceledException) { Status = "Refresh cancelled."; log.Write("INFO", "scan-cancelled", "user cancellation"); }
        catch (Exception ex) { Status = "Refresh failed: " + ex.Message; log.Write("ERROR", "scan-failed", ex.Message); }
        finally { IsBusy = false; Progress = 0; }
    }

    public void CancelRefresh() => refreshCancellation?.Cancel();

    public void CheckConflicts()
    {
        Recompute();
        Status = conflicts.Count == 0
            ? "Conflict check complete — no findings."
            : $"Conflict check complete — {conflicts.Count} finding(s), {BlockingConflictCount} blocking.";
        log.Write("INFO", "conflict-check", $"profile={CurrentProfile?.ProfileId ?? "none"} findings={conflicts.Count} blocking={BlockingConflictCount}");
        Raise(nameof(Status));
    }

    public ManagerProfile CreateFromOfficial(string id, string name)
    {
        EnsureScan();
        return AddAndSelect(ProfileFactory.FromOfficialState(id, name, scan!));
    }

    public ManagerProfile CreateFromAll(string id, string name, bool preserveEnabled) { EnsureScan(); return AddAndSelect(ProfileFactory.FromAllInstalled(id, name, scan!, preserveEnabled)); }
    public ManagerProfile CreateEmpty(string id, string name) => AddAndSelect(ProfileFactory.Empty(id, name, scan?.TargetGameVersion ?? BuildInfo.TargetGameVersion));
    public ManagerProfile DuplicateCurrent(string id, string name) => AddAndSelect(ProfileFactory.Duplicate(RequireProfile(), id, name));

    public void DeleteCurrent()
    {
        var profile = RequireProfile();
        if (DefaultProfilePolicy.IsDefault(profile))
            throw new InvalidOperationException("The Default profile is permanent. Duplicate it to create a disposable profile.");
        repository.Delete(profile.ProfileId);
        Profiles.Remove(Profiles.First(x => x.ProfileId == profile.ProfileId));
        SelectedProfile = Profiles.FirstOrDefault(DefaultProfilePolicy.IsDefault) ?? Profiles.First();
    }

    public void RenameCurrent(string name)
    {
        if (DefaultProfilePolicy.IsDefault(CurrentProfile))
            throw new InvalidOperationException("The permanent Default profile cannot be renamed. Duplicate it to create a named profile.");
        if (editor?.Rename(name) == true) AfterEdit();
    }
    public void SaveCurrent()
    {
        var profile = RequireProfile();
        repository.Save(profile);
        savedProfileText = ManagerProfileRepository.Serialize(profile);
        ReplaceProfileInList(profile);
        RaiseState(); Status = $"Saved profile “{profile.DisplayName}”.";
    }

    public string ExportCurrent(string path) => repository.Export(RequireProfile(), path);
    public ManagerProfile Import(string path)
    {
        return AddImportedAsNew(LoadProfileImport(path));
    }

    public ManagerProfile LoadProfileImport(string path) => repository.Import(path);

    public ManagerProfile LoadLauncherSettingsImport(string path, string profileId, string displayName)
    {
        EnsureScan();
        return LauncherSettingsProfileImporter.ImportFile(path, profileId, displayName, scan!);
    }

    public ManagerProfile AddImportedAsNew(ManagerProfile imported)
    {
        ManagerProfileValidator.Validate(imported);
        var uniqueId = UniqueProfileId(imported.ProfileId);
        if (uniqueId != imported.ProfileId) imported = imported with { ProfileId = uniqueId };
        return AddAndSelect(imported);
    }

    public ManagerProfile ReplaceCurrentWithImport(ManagerProfile imported, string description)
    {
        ManagerProfileValidator.Validate(imported);
        if (editor?.ReplaceContents(imported, description) == true) AfterEdit();
        Status = $"Imported profile contents into '{RequireProfile().DisplayName}'. Review and save the profile.";
        return RequireProfile();
    }

    public void Toggle(IReadOnlyCollection<string> ids) { if (editor?.ToggleEnabled(ids) == true) AfterEdit(); }
    public void SetEnabled(IReadOnlyCollection<string> ids, bool enabled) { if (editor?.SetEnabled(ids, enabled, enabled ? "Enable selection" : "Disable selection") == true) AfterEdit(); }
    public void MoveBefore(IReadOnlyCollection<string> ids, string target) { if (!CanDrag) return; if (editor?.MoveBefore(ids, target) == true) AfterEdit(); }
    public void MoveAfter(IReadOnlyCollection<string> ids, string target) { if (!CanDrag) return; if (editor?.MoveAfter(ids, target) == true) AfterEdit(); }
    public void MoveUp(IReadOnlyCollection<string> ids) { if (editor?.MoveUp(ids) == true) AfterEdit(); }
    public void MoveDown(IReadOnlyCollection<string> ids) { if (editor?.MoveDown(ids) == true) AfterEdit(); }
    public void MoveTop(IReadOnlyCollection<string> ids) { if (editor?.MoveToTop(ids) == true) AfterEdit(); }
    public void MoveBottom(IReadOnlyCollection<string> ids) { if (editor?.MoveToBottom(ids) == true) AfterEdit(); }
    public void Remove(IReadOnlyCollection<string> ids) { if (editor?.Remove(ids) == true) AfterEdit(); }
    public void RestoreRemoved(IReadOnlyCollection<string> ids) { if (editor?.RestoreRemoved(ids) == true) AfterEdit(); }
    public void Relink(string entryId, ModInstallation installation) { if (editor?.Relink(entryId, installation) == true) AfterEdit(); }
    public void SetNotes(string entryId, string? notes) { if (editor?.SetNotes(entryId, notes) == true) AfterEdit(); }
    public void Undo() { if (editor?.Undo() == true) AfterEdit(); }
    public void Redo() { if (editor?.Redo() == true) AfterEdit(); }

    public DependencyChangePlan PlanStateChange(IReadOnlyCollection<string> ids, bool enabling)
    {
        var plan = ProfileDependencyPlanner.Plan(resolved ?? throw new InvalidOperationException("Profile is not resolved."), ids, enabling);
        return new(plan.RequiredEntryIds, plan.DependentEntryIds);
    }

    public IReadOnlyList<(string EntryId, int OldPosition, int NewPosition, string Reason)> PreviewSuggestedOrder()
    {
        if (graph is null) return [];
        var profile = RequireProfile();
        var clone = new ProfileEditorSession(profile);
        clone.ApplySuggestedOrder(graph.DeterministicOrder);
        return profile.Mods.Select((entry, index) => (Entry: entry, Index: index)).Join(clone.Current.Mods.Select((entry, index) => (Entry: entry, Index: index)),
            a => a.Entry.EntryId, b => b.Entry.EntryId,
            (a, b) => (EntryId: a.Entry.EntryId, OldPosition: a.Index, NewPosition: b.Index, Reason: a.Index == b.Index ? "Unchanged" : "Dependency-aware stable order"))
            .Where(x => x.OldPosition != x.NewPosition).ToArray();
    }

    public void AcceptSuggestedOrder() { if (graph is not null && editor?.ApplySuggestedOrder(graph.DeterministicOrder) == true) AfterEdit(); }

    public OfficialStateComparison CompareOfficial()
    {
        var text = File.ReadAllText(environment.LauncherSettingsPath);
        return OfficialStateComparer.Compare(resolved ?? throw new InvalidOperationException("Profile is not resolved."), LauncherSettingsDocument.Parse(text), scan?.Mods ?? []);
    }

    public ManagerProfile ReplaceCurrentWithOfficial()
    {
        EnsureScan();
        var current = RequireProfile();
        var official = ProfileFactory.FromOfficialState(current.ProfileId, current.DisplayName, scan!);
        if (editor?.ReplaceMods(official.Mods, "Replace current profile with official state") == true) AfterEdit();
        return RequireProfile();
    }

    public int MergeNewlyDiscoveredMods()
    {
        EnsureScan();
        var reconciliation = ProfileInventoryReconciler.Reconcile(RequireProfile(), scan!.Mods);
        if (editor?.Append(reconciliation.AddedEntries, "Merge newly discovered mods") == true) AfterEdit();
        return reconciliation.AddedEntries.Count;
    }

    public ApplyPreview CreateApplyPreview() => ApplyPreviewService.Create(environment.LauncherSettingsPath, resolved ?? throw new InvalidOperationException("Profile is not resolved."), conflicts, storage.Backups);
    public string CreateCompatibilityWarning(ApplyPreview preview) => CompatibilityWarningFormatter.Format(preview.CompatibilityFindings, scan?.Mods ?? []);

    public async Task<ApplyResult> ApplyAsync(ApplyPreview preview, string? acknowledgement, CancellationToken token = default)
    {
        if (preview.RequiresCompatibilityAcknowledgement && acknowledgement == preview.ConflictSignature)
        {
            editor?.Acknowledge(preview.ConflictSignature, $"Accepted {preview.CompatibilityFindings.Count} compatibility warning(s). See the conflict report for details.");
            Recompute();
        }
        var writer = CreateWriter();
        var result = await writer.ApplyAsync(preview, acknowledgement, token);
        if (result.Success && result.BackupId is not null && result.FinalSha256 is not null)
        {
            var updated = RequireProfile() with { LastSuccessfulApplication = new(DateTimeOffset.UtcNow, result.FinalSha256, result.BackupId, BuildInfo.BuildId), ModifiedUtc = DateTimeOffset.UtcNow };
            editor = new(updated); SaveCurrent();
        }
        Status = result.Success ? "Profile applied and verified." : string.Join(" ", result.Diagnostics);
        log.Write(result.Success ? "INFO" : "ERROR", "apply", $"success={result.Success} profile={preview.ProfileId} backup={result.BackupId ?? "none"} hash={result.FinalSha256 ?? "none"} diagnostics={string.Join(" | ", result.Diagnostics)}");
        return result;
    }

    public async Task<GameLaunchResult> LaunchAsync(GameLaunchRoute route, bool recordProfile, CancellationToken token = default)
    {
        if (!await launchGate.WaitAsync(0, token))
            return new(false, route, null, null, null, ["A Songs of Syx launch request is already in progress."]);
        try
        {
            IsBusy = true;
            Status = route == GameLaunchRoute.DirectGame ? "Verifying and launching Songs of Syx…" : "Verifying and opening the official launcher…";
            var currentEnvironment = RequireEnvironment();
            var service = gameLaunchServiceOverride ?? new GameLaunchService(
                new SongsOfSyxGameLaunchTargetResolver(),
                new SongsOfSyxProcessInspector(currentEnvironment.GameRoot),
                new NamedMutexApplyLockFactory(),
                new PlatformGameProcessStarter(),
                new JavaAgentLaunchCoordinator(new JavaAgentTrustStore(storage)),
                new SongsOfSyxAssetCacheInvalidator());
            var result = await Task.Run(() => service.Launch(currentEnvironment, route), token);
            if (result.Success && recordProfile && editor is not null)
            {
                var updated = editor.Current with { LastSuccessfulLaunchUtc = DateTimeOffset.UtcNow, ModifiedUtc = DateTimeOffset.UtcNow };
                editor = new(updated);
                SaveCurrent();
            }
            Status = result.Success
                ? $"Started {result.Target?.DisplayName ?? "Songs of Syx"} (process {result.ProcessId})."
                : "Launch blocked: " + string.Join(" ", result.Diagnostics);
            log.Write(result.Success ? "INFO" : "ERROR", "game-launch",
                $"success={result.Success} route={route} process={result.ProcessId?.ToString() ?? "none"} gameVersion={result.Target?.GameVersion ?? "unknown"} knownGameBuild={result.Target?.KnownGameBuild.ToString() ?? "unknown"} knownExecutable={result.Target?.KnownExecutable.ToString() ?? "unknown"} settingsHash={result.ConfigurationSha256 ?? "none"} jarHash={result.Target?.GameJarSha256 ?? "none"} executableHash={result.Target?.ExecutableSha256 ?? "none"} diagnostics={string.Join(" | ", result.Diagnostics)}");
            return result;
        }
        finally
        {
            IsBusy = false;
            launchGate.Release();
            Raise(nameof(LaunchEnabled));
        }
    }

    public void TrustJavaAgent(JavaAgentLaunchEntry entry, string reason)
    {
        new JavaAgentTrustStore(storage).Record(entry.TrustKey, JavaAgentTrustDecision.Approved, reason);
        log.Write("WARN", "java-agent-trusted", $"mod={entry.DisplayName} relativePath={entry.JarRelativePath} sha256={entry.JarSha256} premain={entry.PremainClass}");
    }

    public async Task<ApplyResult> RestoreAsync(ConfigurationBackup backup, CancellationToken token = default)
    {
        var result = await CreateWriter().RestoreAsync(environment.LauncherSettingsPath, backup, token);
        Status = result.Success ? "Backup restored and verified." : string.Join(" ", result.Diagnostics);
        log.Write(result.Success ? "INFO" : "ERROR", "restore", $"success={result.Success} backup={backup.Metadata.BackupId} hash={result.FinalSha256 ?? "none"}");
        return result;
    }

    public void DeleteBackup(ConfigurationBackup backup) => new ConfigurationBackupStore(storage.Backups).Delete(backup);

    private OfficialConfigurationWriter CreateWriter() => new(new ProductionConfigurationWriteGuard(), new SongsOfSyxProcessInspector(environment.GameRoot), new NamedMutexApplyLockFactory(), new ConfigurationBackupStore(storage.Backups));

    private ManagerProfile AddAndSelect(ManagerProfile profile)
    {
        repository.Save(profile); Profiles.Add(profile); SelectedProfile = profile; return profile;
    }

    private string UniqueProfileId(string requestedId)
    {
        if (!Profiles.Any(x => x.ProfileId == requestedId)) return requestedId;
        var prefix = requestedId + "-imported-" + DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss");
        var candidate = prefix;
        for (var suffix = 2; Profiles.Any(x => x.ProfileId == candidate); suffix++) candidate = prefix + "-" + suffix;
        return candidate;
    }

    private void SelectProfile(ManagerProfile profile)
    {
        var latest = repository.Load(profile.ProfileId);
        if (scan is not null)
        {
            var reconciliation = ProfileInventoryReconciler.Reconcile(latest, scan.Mods);
            latest = reconciliation.Profile;
            if (reconciliation.AddedEntries.Count > 0) repository.Save(latest);
        }
        editor = new(latest); savedProfileText = ManagerProfileRepository.Serialize(latest); Recompute();
    }

    private void AfterEdit() { Recompute(); RaiseState(); }

    private void Recompute()
    {
        if (scan is null || editor is null) return;
        resolved = ProfileResolver.Resolve(editor.Current, scan.Mods);
        var enabledPriority = 0;
        var effective = resolved.Entries.Where(x => x.Status == ProfileResolutionStatus.Resolved)
            .Select(x => x.Installation! with { Enabled = x.Entry.Enabled, Priority = x.Entry.Enabled ? enabledPriority++ : null }).ToArray();
        var enabled = effective.Where(x => x.Enabled).ToArray();
        graph = DependencyGraphResolver.Resolve(enabled);
        conflicts = ConflictAnalyzer.Analyze(effective, resolved.EffectiveOfficialOrder, 71);
        Rows.Clear();
        for (var index = 0; index < resolved.Entries.Count; index++)
        {
            var item = resolved.Entries[index];
            var installationId = item.Installation?.InstallationId;
            var severity = conflicts.Where(c => c.InvolvedMods.Contains(installationId ?? item.Entry.InstallationIdHint ?? item.Entry.EntryId, StringComparer.Ordinal))
                .Select(c => (Severity?)c.Severity).OrderBy(x => x).FirstOrDefault();
            var dependency = graph.Blockers.TryGetValue(item.Entry.LogicalModId, out var blockers) ? string.Join("; ", blockers) : "OK";
            Rows.Add(new() { EntryId = item.Entry.EntryId, Position = index, Entry = item.Entry, Resolution = item, HighestSeverity = severity, DependencyStatus = dependency, Enabled = item.Entry.Enabled });
        }
        ApplyFilter();
        RaiseState();
    }

    private void ApplyFilter()
    {
        var query = SearchText.Trim();
        IEnumerable<ModRowViewModel> source = Rows;
        if (query.Length > 0) source = source.Where(x => x.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || x.LogicalId.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            x.Entry.SourceId.Contains(query, StringComparison.OrdinalIgnoreCase) || x.Author.Contains(query, StringComparison.OrdinalIgnoreCase));
        source = Filter switch
        {
            "Enabled" => source.Where(x => x.Enabled), "Disabled" => source.Where(x => !x.Enabled), "Local" => source.Where(x => x.Entry.Source == ModSourceType.Local),
            "Workshop" => source.Where(x => x.Entry.Source == ModSourceType.Workshop), "Choir" => source.Where(x => x.Declaration == "Choir"), "Legacy" => source.Where(x => x.Declaration == "Legacy"),
            "Missing" => source.Where(x => x.Resolution.Status == ProfileResolutionStatus.Missing), "Ambiguous" => source.Where(x => x.Resolution.Status == ProfileResolutionStatus.Ambiguous),
            "Conflicts" => source.Where(x => x.HighestSeverity is not null), "Incompatible" => source.Where(x => x.Compatibility != "V71"), _ => source
        };
        VisibleRows.Clear(); foreach (var row in source) VisibleRows.Add(row);
    }

    private void ReplaceProfileInList(ManagerProfile profile)
    {
        var index = Profiles.ToList().FindIndex(x => x.ProfileId == profile.ProfileId);
        if (index >= 0) Profiles[index] = profile;
        selectedProfile = profile; Raise(nameof(SelectedProfile));
    }

    private IReadOnlyList<ProfileInventoryReconciliation> ReconcileStoredProfiles()
    {
        EnsureScan();
        var reconciliations = repository.LoadAll().Select(profile => ProfileInventoryReconciler.Reconcile(profile, scan!.Mods)).ToArray();
        foreach (var reconciliation in reconciliations.Where(x => x.AddedEntries.Count > 0)) repository.Save(reconciliation.Profile);
        return reconciliations;
    }

    private ManagerProfile RequireProfile() => editor?.Current ?? throw new InvalidOperationException("No active profile.");
    private void EnsureScan() { if (scan is null) throw new InvalidOperationException("Installation scan is not ready."); }
    private SongsOfSyxEnvironment RequireEnvironment() => environment ??= SongsOfSyxEnvironmentLocator.Locate();
    private void RaiseState()
    {
        Raise(nameof(CurrentProfile)); Raise(nameof(RemovedProfileEntries)); Raise(nameof(CanUndo)); Raise(nameof(CanRedo)); Raise(nameof(IsDirty)); Raise(nameof(EnabledCount)); Raise(nameof(TotalCount)); Raise(nameof(BlockingConflictCount)); Raise(nameof(Conflicts));
    }
}
