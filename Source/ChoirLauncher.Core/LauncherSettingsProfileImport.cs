using System.Text;

namespace ChoirLauncher.Core;

public static class LauncherSettingsProfileImporter
{
    public const long MaximumFileSizeBytes = 1024 * 1024;

    public static ManagerProfile ImportFile(
        string sourcePath,
        string profileId,
        string displayName,
        ScanReport currentScan)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        var fullPath = Path.GetFullPath(sourcePath);
        var info = new FileInfo(fullPath);
        if (!info.Exists) throw new FileNotFoundException("Launcher settings import file was not found.", fullPath);
        if (info.Length > MaximumFileSizeBytes)
            throw new InvalidDataException($"Launcher settings import exceeds the {MaximumFileSizeBytes / 1024} KiB size limit.");

        return ImportText(File.ReadAllText(fullPath, Encoding.UTF8), profileId, displayName, currentScan);
    }

    public static ManagerProfile ImportText(
        string text,
        string profileId,
        string displayName,
        ScanReport currentScan)
    {
        ArgumentNullException.ThrowIfNull(currentScan);
        var document = LauncherSettingsDocument.Parse(text);
        var blank = document.EnabledMods.FirstOrDefault(string.IsNullOrWhiteSpace);
        if (blank is not null) throw new FormatException("Launcher settings contains an empty MODS entry.");

        var duplicate = document.EnabledMods
            .GroupBy(x => x, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1)?.Key;
        if (duplicate is not null)
            throw new FormatException($"Launcher settings contains the duplicate MODS entry \"{duplicate}\".");

        return ProfileFactory.FromOfficialOrder(profileId, displayName, currentScan, document.EnabledMods);
    }
}
