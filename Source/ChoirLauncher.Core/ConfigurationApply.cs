using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace ChoirLauncher.Core;

public sealed record BlockingProcess(int ProcessId, string ProcessName, string Reason);

public interface IProcessInspector
{
    IReadOnlyList<BlockingProcess> FindBlockingProcesses();
}

public sealed class SongsOfSyxProcessInspector : IProcessInspector
{
    private static readonly string[] GameNames = ["songsofsyx", "syxwithout"];
    private readonly string? installedGameRoot;

    public SongsOfSyxProcessInspector(string? installedGameRoot) => this.installedGameRoot = installedGameRoot is null ? null : Path.GetFullPath(installedGameRoot);

    public IReadOnlyList<BlockingProcess> FindBlockingProcesses()
    {
        var result = new List<BlockingProcess>();
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                var name = process.ProcessName;
                if (GameNames.Any(x => name.Contains(x, StringComparison.OrdinalIgnoreCase)))
                    result.Add(new(process.Id, name, "Songs of Syx game or native launcher process is running."));
                else if (name.Equals("java", StringComparison.OrdinalIgnoreCase) && installedGameRoot is not null)
                {
                    var module = process.MainModule?.FileName;
                    if (module is not null && HostPlatform.IsWithin(module, installedGameRoot)) result.Add(new(process.Id, name, "Songs of Syx bundled Java launcher/game process is running."));
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
            finally { process.Dispose(); }
        }
        return result.OrderBy(x => x.ProcessId).ToArray();
    }

}

[Obsolete("Use SongsOfSyxProcessInspector. The replacement supports Windows, Linux, and macOS.")]
public sealed class WindowsProcessInspector : IProcessInspector
{
    private readonly SongsOfSyxProcessInspector inner;
    public WindowsProcessInspector(string? installedGameRoot) => inner = new(installedGameRoot);
    public IReadOnlyList<BlockingProcess> FindBlockingProcesses() => inner.FindBlockingProcesses();
}

public interface IConfigurationWriteGuard
{
    void Authorize(string targetPath);
}

public sealed class ProductionConfigurationWriteGuard : IConfigurationWriteGuard
{
    public void Authorize(string targetPath)
    {
        var full = Path.GetFullPath(targetPath);
        var expected = Path.GetFullPath(Environment.GetEnvironmentVariable("CHOIRLAUNCHER_SETTINGS_PATH") ?? SongsOfSyxUserDataPaths.ResolveSettingsPath());
        if (!HostPlatform.PathsEqual(full, expected)) throw new UnauthorizedAccessException("Production writer target is not the configured official LauncherSettings.txt path.");
        if (Environment.GetEnvironmentVariable("CHOIRLAUNCHER_TEST_MODE") == "1") throw new UnauthorizedAccessException("Live configuration writing is disabled in test mode.");
    }
}

public sealed class SandboxConfigurationWriteGuard : IConfigurationWriteGuard
{
    private readonly string root;
    public SandboxConfigurationWriteGuard(string root) => this.root = Path.GetFullPath(root);
    public void Authorize(string targetPath)
    {
        var full = Path.GetFullPath(targetPath);
        var relative = Path.GetRelativePath(root, full);
        if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) || Path.IsPathRooted(relative))
            throw new UnauthorizedAccessException("Writer target is outside the test sandbox.");
        var live = Path.GetFullPath(SongsOfSyxUserDataPaths.ResolveSettingsPath());
        if (HostPlatform.PathsEqual(full, live)) throw new UnauthorizedAccessException("Automated tests may not target the real LauncherSettings.txt.");
    }
}

public interface IApplyLock : IDisposable { bool Acquired { get; } }
public interface IApplyLockFactory { IApplyLock TryAcquire(string targetPath, TimeSpan timeout); }

public sealed class NamedMutexApplyLockFactory : IApplyLockFactory
{
    public IApplyLock TryAcquire(string targetPath, TimeSpan timeout)
    {
        var identity = Path.GetFullPath(targetPath);
        if (HostPlatform.Current == DesktopPlatform.Windows) identity = identity.ToUpperInvariant();
        var name = "ChoirLauncher.Apply." + Hashing.Sha256(Encoding.UTF8.GetBytes(identity))[..24];
        return new MutexApplyLock(name, timeout);
    }

    private sealed class MutexApplyLock : IApplyLock
    {
        private readonly ManualResetEventSlim acquired = new(false);
        private readonly ManualResetEventSlim release = new(false);
        private readonly Thread owner;
        private Exception? failure;
        private bool ownsMutex;

        public MutexApplyLock(string name, TimeSpan timeout)
        {
            owner = new Thread(() => OwnMutex(name, timeout))
            {
                IsBackground = true,
                Name = "ChoirLauncher configuration mutex"
            };
            owner.Start();
            acquired.Wait();
            if (failure is not null) throw new InvalidOperationException("Could not acquire the ChoirLauncher configuration mutex.", failure);
        }

        public bool Acquired => ownsMutex;

        private void OwnMutex(string name, TimeSpan timeout)
        {
            try
            {
                using var mutex = new Mutex(false, name);
                var owns = false;
                try { owns = mutex.WaitOne(timeout); }
                catch (AbandonedMutexException) { owns = true; }
                ownsMutex = owns;
                acquired.Set();
                if (!owns) return;
                release.Wait();
                mutex.ReleaseMutex();
            }
            catch (Exception ex)
            {
                failure = ex;
                acquired.Set();
            }
        }

        public void Dispose()
        {
            release.Set();
            owner.Join();
            acquired.Dispose();
            release.Dispose();
        }
    }
}

public sealed record ConfigurationBackupMetadata(
    string BackupId,
    DateTimeOffset CreatedUtc,
    string BackupFileName,
    string OriginalSha256,
    string ProposedSha256,
    string ProfileId,
    string ApplicationVersion,
    string GameVersion,
    long Size,
    string Result,
    string? Diagnostic);

public sealed record ConfigurationBackup(ConfigurationBackupMetadata Metadata, string DataPath, string MetadataPath);

public sealed class ConfigurationBackupStore
{
    private readonly string directory;
    private readonly int limit;
    public ConfigurationBackupStore(string directory, int limit = 20) { this.directory = Path.GetFullPath(directory); this.limit = Math.Max(1, limit); Directory.CreateDirectory(this.directory); }

    public ConfigurationBackup Create(byte[] originalBytes, ApplyPreview preview)
        => Create(originalBytes, preview.ProposedSha256, preview.ProfileId);

    public ConfigurationBackup Create(byte[] originalBytes, string proposedSha256, string operationId)
    {
        var id = DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff") + "-" + Guid.NewGuid().ToString("N")[..8];
        var dataName = $"LauncherSettings-{id}.txt.bak";
        var metadataName = $"LauncherSettings-{id}.json";
        var dataPath = Path.Combine(directory, dataName);
        using (var stream = new FileStream(dataPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.WriteThrough))
        {
            stream.Write(originalBytes);
            stream.Flush(true);
        }
        var metadata = new ConfigurationBackupMetadata(id, DateTimeOffset.UtcNow, dataName, Hashing.Sha256(originalBytes), proposedSha256,
            operationId, BuildInfo.Version, BuildInfo.TargetGameVersion, originalBytes.LongLength, "CREATED", null);
        var metadataPath = Path.Combine(directory, metadataName);
        WriteMetadata(metadataPath, metadata);
        Prune();
        return new(metadata, dataPath, metadataPath);
    }

    public void RecordResult(ConfigurationBackup backup, string result, string? diagnostic)
    {
        WriteMetadata(backup.MetadataPath, backup.Metadata with { Result = result, Diagnostic = diagnostic });
    }

    public IReadOnlyList<ConfigurationBackup> List()
    {
        var results = new List<ConfigurationBackup>();
        foreach (var metadataPath in Directory.EnumerateFiles(directory, "LauncherSettings-*.json").OrderByDescending(x => x, StringComparer.Ordinal))
        {
            try
            {
                var metadata = JsonSerializer.Deserialize<ConfigurationBackupMetadata>(File.ReadAllText(metadataPath), ManagerJson.Options);
                if (metadata is null) continue;
                var dataPath = Path.Combine(directory, metadata.BackupFileName);
                if (File.Exists(dataPath)) results.Add(new(metadata, dataPath, metadataPath));
            }
            catch (JsonException) { }
        }
        return results;
    }

    public void Delete(ConfigurationBackup backup)
    {
        var backups = List();
        if (backups.Count <= 1) throw new InvalidOperationException("The final available backup cannot be deleted.");
        File.Delete(backup.DataPath);
        File.Delete(backup.MetadataPath);
    }

    private static void WriteMetadata(string path, ConfigurationBackupMetadata metadata)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(metadata, ManagerJson.Options) + Environment.NewLine);
        AtomicFile.WriteValidated(path, bytes, candidate => JsonSerializer.Deserialize<ConfigurationBackupMetadata>(candidate, ManagerJson.Options) is not null, null, 0);
    }

    private void Prune()
    {
        var backups = List();
        foreach (var backup in backups.Skip(limit)) { File.Delete(backup.DataPath); File.Delete(backup.MetadataPath); }
    }
}

public sealed class LauncherOptionsWriter
{
    private readonly IConfigurationWriteGuard guard;
    private readonly IProcessInspector processInspector;
    private readonly IApplyLockFactory lockFactory;
    private readonly ConfigurationBackupStore backups;

    public LauncherOptionsWriter(IConfigurationWriteGuard guard, IProcessInspector processInspector, IApplyLockFactory lockFactory, ConfigurationBackupStore backups)
    {
        this.guard = guard;
        this.processInspector = processInspector;
        this.lockFactory = lockFactory;
        this.backups = backups;
    }

    public async Task<ApplyResult> ApplyAsync(LauncherOptionsPreview preview, CancellationToken cancellationToken = default)
    {
        guard.Authorize(preview.TargetPath);
        if (!preview.HasChanges) return new(true, false, false, null, preview.CurrentSha256, []);
        var running = processInspector.FindBlockingProcesses();
        if (running.Count > 0)
            return new(false, false, false, null, null, running.Select(x => $"Blocking process {x.ProcessName} ({x.ProcessId}): {x.Reason}").ToArray());
        using var applyLock = lockFactory.TryAcquire(preview.TargetPath, TimeSpan.FromSeconds(2));
        if (!applyLock.Acquired) return new(false, false, false, null, null, ["Another ChoirLauncher apply or restore operation holds the lock."]);
        cancellationToken.ThrowIfCancellationRequested();

        var currentBytes = await File.ReadAllBytesAsync(preview.TargetPath, cancellationToken);
        if (Hashing.Sha256(currentBytes) != preview.CurrentSha256)
            return new(false, false, false, null, null, ["Official settings changed after preview. Reopen Settings and preview again."]);
        var currentDocument = LauncherSettingsDocument.Parse(Encoding.UTF8.GetString(currentBytes));
        if (currentDocument.ContentExcludingScalarValuesSha256(LauncherGameOptions.SupportedKeys) != preview.ProtectedContentSha256)
            return new(false, false, false, null, null, ["Mod order or an unrelated official setting changed after preview."]);

        var backup = backups.Create(currentBytes, preview.ProposedSha256, "launcher-settings");
        var temporary = Path.Combine(Path.GetDirectoryName(preview.TargetPath)!, ".LauncherSettings.choirlauncher-options-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            var proposedBytes = Encoding.UTF8.GetBytes(preview.ProposedText);
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.WriteThrough))
            {
                await stream.WriteAsync(proposedBytes, cancellationToken);
                stream.Flush(true);
            }
            Validate(await File.ReadAllBytesAsync(temporary, cancellationToken), preview);
            AtomicFile.Replace(temporary, preview.TargetPath);
            var finalBytes = await File.ReadAllBytesAsync(preview.TargetPath, cancellationToken);
            Validate(finalBytes, preview);
            backups.RecordResult(backup, "APPLIED_LAUNCHER_SETTINGS", null);
            return new(true, false, false, backup.Metadata.BackupId, Hashing.Sha256(finalBytes), []);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or OperationCanceledException)
        {
            if (File.Exists(temporary)) File.Delete(temporary);
            var rollback = await RestoreBytesAsync(preview.TargetPath, currentBytes, preview.CurrentSha256, cancellationToken);
            var diagnostics = new[] { "Launcher settings apply failed: " + ex.Message, rollback ? "Rollback restored the original bytes." : "Rollback failed; inspect the verified backup immediately." };
            backups.RecordResult(backup, rollback ? "ROLLED_BACK" : "ROLLBACK_FAILED", string.Join(" ", diagnostics));
            return new(false, true, rollback, backup.Metadata.BackupId, File.Exists(preview.TargetPath) ? Hashing.Sha256File(preview.TargetPath) : null, diagnostics);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static void Validate(byte[] bytes, LauncherOptionsPreview preview)
    {
        if (Hashing.Sha256(bytes) != preview.ProposedSha256) throw new InvalidDataException("Proposed launcher-settings hash mismatch.");
        var document = LauncherSettingsDocument.Parse(Encoding.UTF8.GetString(bytes));
        if (LauncherGameOptions.From(document) != preview.Proposed) throw new InvalidDataException("Proposed launcher options failed round-trip validation.");
        if (document.ContentExcludingScalarValuesSha256(LauncherGameOptions.SupportedKeys) != preview.ProtectedContentSha256)
            throw new InvalidDataException("Mod order or an unrelated official setting changed.");
    }

    private static async Task<bool> RestoreBytesAsync(string targetPath, byte[] bytes, string expectedHash, CancellationToken cancellationToken)
    {
        try
        {
            var temporary = Path.Combine(Path.GetDirectoryName(targetPath)!, ".LauncherSettings.options-rollback-" + Guid.NewGuid().ToString("N") + ".tmp");
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken);
                stream.Flush(true);
            }
            AtomicFile.Replace(temporary, targetPath);
            var actual = await File.ReadAllBytesAsync(targetPath, cancellationToken);
            _ = LauncherSettingsDocument.Parse(Encoding.UTF8.GetString(actual));
            return Hashing.Sha256(actual) == expectedHash;
        }
        catch
        {
            return false;
        }
    }
}

public sealed record ApplyResult(bool Success, bool RollbackAttempted, bool RollbackSucceeded, string? BackupId, string? FinalSha256, IReadOnlyList<string> Diagnostics);

public interface IApplyFaultInjector { void At(string phase); }
public sealed class NoApplyFaults : IApplyFaultInjector { public static readonly NoApplyFaults Instance = new(); private NoApplyFaults() { } public void At(string phase) { } }

public sealed class OfficialConfigurationWriter
{
    private readonly IConfigurationWriteGuard guard;
    private readonly IProcessInspector processInspector;
    private readonly IApplyLockFactory lockFactory;
    private readonly ConfigurationBackupStore backups;
    private readonly IApplyFaultInjector faults;

    public OfficialConfigurationWriter(IConfigurationWriteGuard guard, IProcessInspector processInspector, IApplyLockFactory lockFactory, ConfigurationBackupStore backups, IApplyFaultInjector? faults = null)
    {
        this.guard = guard; this.processInspector = processInspector; this.lockFactory = lockFactory; this.backups = backups; this.faults = faults ?? NoApplyFaults.Instance;
    }

    public async Task<ApplyResult> ApplyAsync(ApplyPreview preview, string? acknowledgedConflictSignature, CancellationToken cancellationToken = default)
    {
        guard.Authorize(preview.TargetPath);
        if (!preview.CanApplyTechnically) return new(false, false, false, null, null, preview.HardBlockers);
        if (preview.RequiresCompatibilityAcknowledgement && acknowledgedConflictSignature != preview.ConflictSignature)
            return new(false, false, false, null, null, ["Compatibility warning confirmation is missing or stale."]);
        var running = processInspector.FindBlockingProcesses();
        if (running.Count > 0) return new(false, false, false, null, null, running.Select(x => $"Blocking process {x.ProcessName} ({x.ProcessId}): {x.Reason}").ToArray());
        using var applyLock = lockFactory.TryAcquire(preview.TargetPath, TimeSpan.FromSeconds(2));
        if (!applyLock.Acquired) return new(false, false, false, null, null, ["Another ChoirLauncher apply or restore operation holds the lock."]);
        cancellationToken.ThrowIfCancellationRequested();

        var currentBytes = await File.ReadAllBytesAsync(preview.TargetPath, cancellationToken);
        if (Hashing.Sha256(currentBytes) != preview.CurrentSha256) return new(false, false, false, null, null, ["Official settings changed after preview. Refresh and preview again."]);
        var originalText = Encoding.UTF8.GetString(currentBytes);
        var original = LauncherSettingsDocument.Parse(originalText);
        if (original.UnrelatedContentSha256() != preview.CurrentUnrelatedSha256) return new(false, false, false, null, null, ["Unrelated official settings changed after preview."]);
        var backup = backups.Create(currentBytes, preview);
        var temporary = Path.Combine(Path.GetDirectoryName(preview.TargetPath)!, ".LauncherSettings.choirlauncher-" + Guid.NewGuid().ToString("N") + ".tmp");
        var diagnostics = new List<string>();
        try
        {
            var proposedBytes = Encoding.UTF8.GetBytes(preview.ProposedText);
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.WriteThrough))
            {
                await stream.WriteAsync(proposedBytes, cancellationToken);
                stream.Flush(true);
            }
            var tempBytes = await File.ReadAllBytesAsync(temporary, cancellationToken);
            ValidateProposed(tempBytes, preview);
            faults.At("before-replace");
            AtomicFile.Replace(temporary, preview.TargetPath);
            faults.At("after-replace");
            var finalBytes = await File.ReadAllBytesAsync(preview.TargetPath, cancellationToken);
            ValidateProposed(finalBytes, preview);
            faults.At("after-validation");
            backups.RecordResult(backup, "APPLIED", null);
            return new(true, false, false, backup.Metadata.BackupId, Hashing.Sha256(finalBytes), []);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or OperationCanceledException)
        {
            diagnostics.Add("Apply failed: " + ex.Message);
            if (File.Exists(temporary)) File.Delete(temporary);
            var rollback = await RestoreBytesAsync(preview.TargetPath, currentBytes, preview.CurrentSha256, cancellationToken);
            diagnostics.Add(rollback ? "Rollback restored the original bytes." : "Rollback failed; inspect backup immediately.");
            backups.RecordResult(backup, rollback ? "ROLLED_BACK" : "ROLLBACK_FAILED", string.Join(" ", diagnostics));
            return new(false, true, rollback, backup.Metadata.BackupId, File.Exists(preview.TargetPath) ? Hashing.Sha256File(preview.TargetPath) : null, diagnostics);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public async Task<ApplyResult> RestoreAsync(string targetPath, ConfigurationBackup backup, CancellationToken cancellationToken = default)
    {
        guard.Authorize(targetPath);
        var running = processInspector.FindBlockingProcesses();
        if (running.Count > 0) return new(false, false, false, backup.Metadata.BackupId, null, running.Select(x => $"Blocking process {x.ProcessName} ({x.ProcessId}).").ToArray());
        using var applyLock = lockFactory.TryAcquire(targetPath, TimeSpan.FromSeconds(2));
        if (!applyLock.Acquired) return new(false, false, false, backup.Metadata.BackupId, null, ["Another apply or restore operation holds the lock."]);
        var current = await File.ReadAllBytesAsync(targetPath, cancellationToken);
        var currentDoc = LauncherSettingsDocument.Parse(Encoding.UTF8.GetString(current));
        var bytes = await File.ReadAllBytesAsync(backup.DataPath, cancellationToken);
        if (Hashing.Sha256(bytes) != backup.Metadata.OriginalSha256) return new(false, false, false, backup.Metadata.BackupId, null, ["Backup hash mismatch."]);
        var restoreDoc = LauncherSettingsDocument.Parse(Encoding.UTF8.GetString(bytes));
        var restorePreview = new ApplyPreview(targetPath, "restore-" + backup.Metadata.BackupId, "Backup restore", Hashing.Sha256(current), Hashing.Sha256(bytes),
            currentDoc.UnrelatedContentSha256(), restoreDoc.UnrelatedContentSha256(), currentDoc.EnabledMods, restoreDoc.EnabledMods, [], [], [], [], [],
            Hashing.Sha256(Encoding.UTF8.GetBytes("restore:" + backup.Metadata.BackupId)), Path.GetDirectoryName(backup.DataPath)!, Encoding.UTF8.GetString(bytes), DateTimeOffset.UtcNow);
        var safetyBackup = backups.Create(current, restorePreview);
        var restored = await RestoreBytesAsync(targetPath, bytes, backup.Metadata.OriginalSha256, cancellationToken);
        backups.RecordResult(safetyBackup, restored ? "RESTORE_SAFETY_BACKUP" : "RESTORE_FAILED", restored ? null : "Target backup restore verification failed.");
        return new(restored, false, restored, backup.Metadata.BackupId, restored ? backup.Metadata.OriginalSha256 : null, restored ? [] : ["Backup restore verification failed."]);
    }

    private static void ValidateProposed(byte[] bytes, ApplyPreview preview)
    {
        var text = Encoding.UTF8.GetString(bytes);
        if (Hashing.Sha256(bytes) != preview.ProposedSha256) throw new InvalidDataException("Proposed file hash mismatch.");
        var parsed = LauncherSettingsDocument.Parse(text);
        if (!parsed.EnabledMods.SequenceEqual(preview.ProposedOrder, StringComparer.Ordinal)) throw new InvalidDataException("Proposed MODS order mismatch.");
        if (parsed.EnabledMods.Distinct(StringComparer.Ordinal).Count() != parsed.EnabledMods.Count) throw new InvalidDataException("Proposed MODS contains duplicates.");
        if (parsed.UnrelatedContentSha256() != preview.CurrentUnrelatedSha256) throw new InvalidDataException("Unrelated settings changed.");
    }

    private static async Task<bool> RestoreBytesAsync(string targetPath, byte[] bytes, string expectedHash, CancellationToken cancellationToken)
    {
        try
        {
            var temporary = Path.Combine(Path.GetDirectoryName(targetPath)!, ".LauncherSettings.rollback-" + Guid.NewGuid().ToString("N") + ".tmp");
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken);
                stream.Flush(true);
            }
            AtomicFile.Replace(temporary, targetPath);
            var actual = await File.ReadAllBytesAsync(targetPath, cancellationToken);
            _ = LauncherSettingsDocument.Parse(Encoding.UTF8.GetString(actual));
            return Hashing.Sha256(actual) == expectedHash;
        }
        catch { return false; }
    }
}
