using System.ComponentModel;
using System.Diagnostics;
using Avalonia.Controls;
using ChoirLauncher.Core;

namespace ChoirLauncher.Desktop;

internal static class FolderLauncher
{
    private static readonly TimeSpan ConfirmationTimeout = TimeSpan.FromSeconds(10);

    public static async Task OpenAsync(Window owner, string title, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            await Dialogs.ShowAsync(owner, title, "The selected entry has no resolved installation folder.");
            return;
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            await Dialogs.ShowAsync(owner, title, "The folder path is invalid: " + ex.Message);
            return;
        }

        if (!Directory.Exists(fullPath))
        {
            await Dialogs.ShowAsync(owner, title, "Folder does not exist: " + fullPath);
            return;
        }

        try
        {
            var platform = HostPlatform.Current;
            using var process = Start(fullPath, platform);
            if (platform == DesktopPlatform.Windows) return;

            using var timeout = new CancellationTokenSource(ConfirmationTimeout);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                await Dialogs.ShowAsync(owner, title,
                    "The system folder opener was still running after 10 seconds. Choir Launcher did not terminate it and cannot confirm whether the folder opened.");
                return;
            }

            if (process.ExitCode != 0)
                await Dialogs.ShowAsync(owner, title, $"The system folder opener could not open the folder (exit code {process.ExitCode}).");
        }
        catch (UnauthorizedAccessException ex)
        {
            await Dialogs.ShowAsync(owner, title, "Permission was denied while opening the folder: " + ex.Message);
        }
        catch (Win32Exception ex)
        {
            await Dialogs.ShowAsync(owner, title, "The system folder opener is unavailable or could not be started: " + ex.Message);
        }
        catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException)
        {
            await Dialogs.ShowAsync(owner, title, ex.Message);
        }
    }

    private static Process Start(string fullPath, DesktopPlatform platform)
    {
        var startInfo = platform switch
        {
            DesktopPlatform.Windows => new ProcessStartInfo
            {
                FileName = fullPath,
                UseShellExecute = true
            },
            DesktopPlatform.Linux => CreateCommandStartInfo("xdg-open", fullPath),
            DesktopPlatform.MacOS => CreateCommandStartInfo("/usr/bin/open", fullPath),
            _ => throw new PlatformNotSupportedException("Choir Launcher supports opening folders on Windows, Linux, and macOS.")
        };

        return Process.Start(startInfo)
            ?? throw new InvalidOperationException("The operating system did not return a process for the folder-opening request.");
    }

    private static ProcessStartInfo CreateCommandStartInfo(string command, string fullPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = command,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(fullPath);
        return startInfo;
    }
}
