using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;
using ChoirLauncher.Core;

namespace ChoirLauncher.Installer;

internal static class Program
{
    private const string PayloadResource = "ChoirLauncher.Payload.zip";
    private const string PayloadManifest = "installer-payload.json";
    private const int MaxEntries = 2_000;
    private const long MaxExpandedBytes = 1_073_741_824;
    private const uint ShellChangeUpdateItem = 0x00002000;
    private const uint ShellNotifyPathW = 0x0005;

    [STAThread]
    private static int Main(string[] args)
    {
        bool verifyOnly = args.Any(value => value.Equals("--verify", StringComparison.OrdinalIgnoreCase));
        bool silent = args.Any(value => value.Equals("--silent", StringComparison.OrdinalIgnoreCase));
        bool testMode = string.Equals(Environment.GetEnvironmentVariable("CHOIRLAUNCHER_TEST_MODE"), "1", StringComparison.Ordinal);
        string? testResultFile = testMode ? GetArgumentValue(args, "--result-file") : null;
        try
        {
            PayloadIdentity identity = VerifyEmbeddedPayload();
            if (verifyOnly)
            {
                return 0;
            }

            string installRoot = testMode
                ? GetTestInstallRoot(args) ?? GetDefaultInstallRoot()
                : GetDefaultInstallRoot();
            ManagerStoragePaths managerStorage = testMode
                ? ManagerStoragePaths.Resolve(GetTestStorageRoot(args) ?? throw new ArgumentException("--storage-root is required in installer test mode."))
                : ManagerStoragePaths.Resolve();
            bool createShortcut = !(testMode && args.Any(value => value.Equals("--no-shortcut", StringComparison.OrdinalIgnoreCase)));
            string? gameRoot = ResolveGameRoot(args, silent, managerStorage);
            if (!silent)
            {
                ApplicationConfiguration.Initialize();
                gameRoot ??= SelectGameRoot();
                if (gameRoot is null) return 2;
                DialogResult answer = MessageBox.Show(
                    $"Install ChoirLauncher {identity.Version} for this Windows user?\n\n" +
                    $"ChoirLauncher install folder:\n{installRoot}\n\n" +
                    $"Songs of Syx game folder:\n{gameRoot}\n\n" +
                    "A desktop shortcut will be created. Songs of Syx game files and mods will not be changed.",
                    "Install ChoirLauncher",
                    MessageBoxButtons.OKCancel,
                    MessageBoxIcon.Information);
                if (answer != DialogResult.OK) return 2;
            }

            Install(identity, installRoot, createShortcut);
            string? locationWarning = null;
            if (gameRoot is not null)
            {
                try { new GameLocationPreferencesStore(managerStorage).Save(gameRoot, "installer"); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    locationWarning = ex.Message;
                }
            }
            if (testResultFile != null) WriteTestResult(testResultFile, "PASS");
            if (!silent)
            {
                DialogResult launch = MessageBox.Show(
                    $"ChoirLauncher {identity.Version} was installed successfully.\n\n" +
                    (locationWarning is null
                        ? $"Songs of Syx folder saved:\n{gameRoot}\n\n"
                        : $"The game folder could not be saved. ChoirLauncher will ask again when it starts.\n{locationWarning}\n\n") +
                    "Launch it now?",
                    "ChoirLauncher installed",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Information);
                if (launch == DialogResult.Yes)
                {
                    Process.Start(new ProcessStartInfo(Path.Combine(installRoot, "ChoirLauncher.exe"))
                    {
                        WorkingDirectory = installRoot,
                        UseShellExecute = true
                    });
                }
            }
            return 0;
        }
        catch (Exception exception)
        {
            if (testResultFile != null) WriteTestResult(testResultFile, exception.ToString());
            if (!silent)
            {
                ApplicationConfiguration.Initialize();
                MessageBox.Show(
                    "ChoirLauncher was not installed. No Songs of Syx game file was changed.\n\n" + exception.Message,
                    "ChoirLauncher installation failed",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
            return 1;
        }
    }

    private static string? ResolveGameRoot(IReadOnlyList<string> args, bool silent, ManagerStoragePaths managerStorage)
    {
        string? argument = GetArgumentValue(args, "--game-root");
        if (argument is not null)
        {
            if (SongsOfSyxGameLocation.TryNormalize(argument, out var normalized, out var error)) return normalized;
            if (silent) throw new ArgumentException(error, "--game-root");
        }

        var discovered = SongsOfSyxEnvironmentLocator.Locate(managerStorage);
        if (SongsOfSyxGameLocation.TryNormalize(discovered.GameRoot, out var automatic, out _)) return automatic;
        return null;
    }

    private static string? SelectGameRoot()
    {
        DialogResult explanation = MessageBox.Show(
            "ChoirLauncher could not find Songs of Syx automatically.\n\n" +
            "You now need to select the main Songs of Syx installation folder. " +
            "The correct folder directly contains SongsOfSyx.jar.\n\n" +
            "Select OK to browse for the game folder, or Cancel to stop installation.",
            "Songs of Syx folder required",
            MessageBoxButtons.OKCancel,
            MessageBoxIcon.Warning);
        if (explanation != DialogResult.OK) return null;

        while (true)
        {
            using var picker = new FolderBrowserDialog
            {
                Description = "Select the main Songs of Syx folder containing SongsOfSyx.jar",
                UseDescriptionForTitle = true,
                ShowNewFolderButton = false
            };
            if (picker.ShowDialog() != DialogResult.OK) return null;
            if (SongsOfSyxGameLocation.TryNormalize(picker.SelectedPath, out var normalized, out var error)) return normalized;
            DialogResult retry = MessageBox.Show(
                error + "\n\nSelect Retry to choose another folder, or Cancel to stop installation.",
                "That is not the Songs of Syx folder",
                MessageBoxButtons.RetryCancel,
                MessageBoxIcon.Error);
            if (retry != DialogResult.Retry) return null;
        }
    }

    private static string? GetArgumentValue(IReadOnlyList<string> args, string name)
    {
        for (int index = 0; index < args.Count; index++)
        {
            if (!args[index].Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
            if (index + 1 >= args.Count || string.IsNullOrWhiteSpace(args[index + 1]))
                throw new ArgumentException($"{name} requires a value.");
            return args[index + 1];
        }
        return null;
    }

    private static void WriteTestResult(string path, string text)
    {
        string full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static string GetDefaultInstallRoot()
    {
        string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrWhiteSpace(roaming)) throw new InvalidOperationException("The current user's AppData folder is unavailable.");
        return Path.Combine(roaming, "songsofsyx", "ChoirLauncher");
    }

    private static string? GetTestInstallRoot(IReadOnlyList<string> args)
    {
        string? value = GetArgumentValue(args, "--install-root");
        if (value != null)
        {
            string full = Path.GetFullPath(value);
            string root = Path.GetPathRoot(full) ?? string.Empty;
            if (full.TrimEnd(Path.DirectorySeparatorChar).Equals(root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("The installer test root cannot be a filesystem root.");
            return full;
        }
        return null;
    }

    private static string? GetTestStorageRoot(IReadOnlyList<string> args)
    {
        string? value = GetArgumentValue(args, "--storage-root");
        if (value is null) return null;
        string full = Path.GetFullPath(value);
        string root = Path.GetPathRoot(full) ?? string.Empty;
        if (full.TrimEnd(Path.DirectorySeparatorChar).Equals(root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The installer test storage root cannot be a filesystem root.");
        return full;
    }

    private static PayloadIdentity VerifyEmbeddedPayload()
    {
        Assembly assembly = Assembly.GetExecutingAssembly();
        Dictionary<string, string> metadata = assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .ToDictionary(attribute => attribute.Key, attribute => attribute.Value ?? string.Empty, StringComparer.Ordinal);
        string expectedHash = RequiredMetadata(metadata, "ChoirLauncherPayloadSha256").ToLowerInvariant();
        string expectedVersion = RequiredMetadata(metadata, "ChoirLauncherPayloadVersion");
        string expectedBuild = RequiredMetadata(metadata, "ChoirLauncherPayloadBuildId");

        using Stream payload = assembly.GetManifestResourceStream(PayloadResource)
            ?? throw new InvalidOperationException("The installer payload is missing.");
        string actualHash = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        if (!actualHash.Equals(expectedHash, StringComparison.Ordinal))
            throw new InvalidOperationException("The embedded launcher payload failed SHA-256 verification.");

        return new PayloadIdentity(expectedVersion, expectedBuild, expectedHash);
    }

    private static string RequiredMetadata(IReadOnlyDictionary<string, string> metadata, string key)
    {
        if (!metadata.TryGetValue(key, out string? value) || string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"Installer metadata is missing: {key}.");
        return value;
    }

    private static void Install(PayloadIdentity identity, string installRoot, bool createShortcut)
    {
        EnsureLauncherIsClosed(installRoot);
        string parent = Path.GetDirectoryName(installRoot)
            ?? throw new InvalidOperationException("The install directory has no parent.");
        Directory.CreateDirectory(parent);
        string staging = Path.Combine(parent, ".ChoirLauncher.installing-" + Guid.NewGuid().ToString("N"));
        string backup = Path.Combine(parent, ".ChoirLauncher.previous-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss"));
        bool movedExisting = false;

        try
        {
            Directory.CreateDirectory(staging);
            ExtractPayload(staging);
            VerifyExtractedPayload(staging, identity);

            if (Directory.Exists(installRoot))
            {
                if (Directory.Exists(backup)) throw new IOException("A previous installation backup already exists.");
                Directory.Move(installRoot, backup);
                movedExisting = true;
            }
            Directory.Move(staging, installRoot);

            if (createShortcut) CreateDesktopShortcut(installRoot);
            WriteInstalledIdentity(installRoot, identity);

            if (movedExisting && Directory.Exists(backup)) Directory.Delete(backup, true);
        }
        catch
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
            if (movedExisting && !Directory.Exists(installRoot) && Directory.Exists(backup))
                Directory.Move(backup, installRoot);
            throw;
        }
    }

    private static void EnsureLauncherIsClosed(string installRoot)
    {
        string expected = Path.GetFullPath(Path.Combine(installRoot, "ChoirLauncher.exe"));
        foreach (Process process in Process.GetProcessesByName("ChoirLauncher"))
        {
            try
            {
                string? running = process.MainModule?.FileName;
                if (running != null && Path.GetFullPath(running).Equals(expected, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Close ChoirLauncher before installing an update.");
            }
            catch (System.ComponentModel.Win32Exception)
            {
                throw new InvalidOperationException("Close every running ChoirLauncher window before installing.");
            }
            finally
            {
                process.Dispose();
            }
        }
    }

    private static void ExtractPayload(string staging)
    {
        Assembly assembly = Assembly.GetExecutingAssembly();
        using Stream payload = assembly.GetManifestResourceStream(PayloadResource)
            ?? throw new InvalidOperationException("The installer payload is missing.");
        using ZipArchive archive = new(payload, ZipArchiveMode.Read, leaveOpen: false);
        if (archive.Entries.Count == 0 || archive.Entries.Count > MaxEntries)
            throw new InvalidDataException("The installer payload has an invalid entry count.");

        long expanded = 0;
        string root = Path.GetFullPath(staging) + Path.DirectorySeparatorChar;
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            expanded = checked(expanded + entry.Length);
            if (expanded > MaxExpandedBytes) throw new InvalidDataException("The installer payload is too large.");
            string relative = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
            string destination = Path.GetFullPath(Path.Combine(staging, relative));
            if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The installer payload contains an unsafe path.");
            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(destination);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination, overwrite: false);
        }
    }

    private static void VerifyExtractedPayload(string staging, PayloadIdentity identity)
    {
        string executable = Path.Combine(staging, "ChoirLauncher.exe");
        string manifestPath = Path.Combine(staging, PayloadManifest);
        if (!File.Exists(executable)) throw new InvalidDataException("The launcher executable is missing from the payload.");
        if (!File.Exists(manifestPath)) throw new InvalidDataException("The installer payload manifest is missing.");

        using JsonDocument manifest = JsonDocument.Parse(File.ReadAllText(manifestPath, Encoding.UTF8));
        JsonElement root = manifest.RootElement;
        string version = root.GetProperty("version").GetString() ?? string.Empty;
        string buildId = root.GetProperty("buildId").GetString() ?? string.Empty;
        string entrypoint = root.GetProperty("entrypoint").GetString() ?? string.Empty;
        if (!version.Equals(identity.Version, StringComparison.Ordinal)
                || !buildId.Equals(identity.BuildId, StringComparison.Ordinal)
                || !entrypoint.Equals("ChoirLauncher.exe", StringComparison.Ordinal))
            throw new InvalidDataException("The installer payload identity does not match the setup executable.");
    }

    private static void WriteInstalledIdentity(string installRoot, PayloadIdentity identity)
    {
        var installed = new
        {
            schema = "choirlauncher.installation.v1",
            version = identity.Version,
            buildId = identity.BuildId,
            payloadSha256 = identity.PayloadSha256,
            installedUtc = DateTimeOffset.UtcNow
        };
        File.WriteAllText(
            Path.Combine(installRoot, "installed-release.json"),
            JsonSerializer.Serialize(installed, new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static void CreateDesktopShortcut(string installRoot)
    {
        string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (string.IsNullOrWhiteSpace(desktop)) throw new InvalidOperationException("The current user's desktop folder is unavailable.");
        string executable = Path.Combine(installRoot, "ChoirLauncher.exe");
        string shortcutPath = Path.Combine(desktop, "ChoirLauncher.lnk");
        IShellLinkW link = (IShellLinkW)(object)new ShellLink();
        try
        {
            link.SetPath(executable);
            link.SetWorkingDirectory(installRoot);
            link.SetDescription("Manage and launch Songs of Syx mod profiles");
            link.SetIconLocation(executable, 0);
            ((IPersistFile)link).Save(shortcutPath, true);
            SHChangeNotify(ShellChangeUpdateItem, ShellNotifyPathW, shortcutPath, IntPtr.Zero);
        }
        finally
        {
            Marshal.FinalReleaseComObject(link);
        }
    }

    private sealed record PayloadIdentity(string Version, string BuildId, string PayloadSha256);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern void SHChangeNotify(uint eventId, uint flags, [MarshalAs(UnmanagedType.LPWStr)] string item1, IntPtr item2);

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private sealed class ShellLink { }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int count, IntPtr findData, uint flags);
        void GetIDList(out IntPtr itemIdList);
        void SetIDList(IntPtr itemIdList);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder description, int count);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string description);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder directory, int count);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder arguments, int count);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int showCommand);
        void SetShowCmd(int showCommand);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder iconPath, int count, out int iconIndex);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int iconIndex);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        void Resolve(IntPtr window, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string path);
    }
}
