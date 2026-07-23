using ChoirLauncher.Core;

namespace ChoirLauncher.Installer;

internal sealed class InstallerConfigurationDialog : Form
{
    private readonly Func<GameLocationDetection> autoDetect;
    private readonly TextBox installRootText;
    private readonly TextBox gameRootText;
    private readonly Label status;

    public InstallerConfigurationDialog(
        string version,
        string installRoot,
        string? gameRoot,
        Func<GameLocationDetection> autoDetect)
    {
        ArgumentNullException.ThrowIfNull(autoDetect);
        this.autoDetect = autoDetect;

        Text = $"Install ChoirLauncher {version}";
        ClientSize = new Size(820, 390);
        MinimumSize = new Size(720, 430);
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        AutoScaleMode = AutoScaleMode.Dpi;

        var introduction = new Label
        {
            Left = 20,
            Top = 18,
            Width = 780,
            Height = 48,
            Text = "Review the detected folders before installation. You may type or browse to different folders. " +
                   "Songs of Syx game files and mods will not be changed."
        };

        var installLabel = new Label
        {
            Left = 20,
            Top = 78,
            Width = 760,
            Height = 22,
            Text = "ChoirLauncher install folder"
        };
        installRootText = new TextBox
        {
            Left = 20,
            Top = 102,
            Width = 650,
            Text = installRoot,
            AccessibleName = "ChoirLauncher install folder"
        };
        var browseInstall = new Button
        {
            Left = 680,
            Top = 100,
            Width = 120,
            Height = 30,
            Text = "Browse..."
        };
        browseInstall.Click += (_, _) => BrowseForInstallRoot();

        var gameLabel = new Label
        {
            Left = 20,
            Top = 150,
            Width = 760,
            Height = 22,
            Text = "Songs of Syx game folder (must contain SongsOfSyx.jar)"
        };
        gameRootText = new TextBox
        {
            Left = 20,
            Top = 174,
            Width = 520,
            Text = gameRoot ?? string.Empty,
            AccessibleName = "Songs of Syx game folder"
        };
        var browseGame = new Button
        {
            Left = 550,
            Top = 172,
            Width = 120,
            Height = 30,
            Text = "Browse..."
        };
        browseGame.Click += (_, _) => BrowseForGameRoot();
        var detect = new Button
        {
            Left = 680,
            Top = 172,
            Width = 120,
            Height = 30,
            Text = "Auto-detect"
        };
        detect.Click += (_, _) => AutoDetectGameRoot();

        status = new Label
        {
            Left = 20,
            Top = 222,
            Width = 780,
            Height = 72
        };

        var cancel = new Button
        {
            Left = 550,
            Top = 325,
            Width = 120,
            Height = 36,
            Text = "Cancel",
            DialogResult = DialogResult.Cancel
        };
        var continueButton = new Button
        {
            Left = 680,
            Top = 325,
            Width = 120,
            Height = 36,
            Text = "Continue"
        };
        continueButton.Click += (_, _) => ContinueInstallation();

        Controls.AddRange(
        [
            introduction,
            installLabel,
            installRootText,
            browseInstall,
            gameLabel,
            gameRootText,
            browseGame,
            detect,
            status,
            cancel,
            continueButton
        ]);
        AcceptButton = continueButton;
        CancelButton = cancel;
        Shown += (_, _) => gameRootText.Focus();
        ShowInitialStatus();
    }

    public string InstallRoot { get; private set; } = string.Empty;
    public string GameRoot { get; private set; } = string.Empty;

    private void BrowseForInstallRoot()
    {
        using var picker = new FolderBrowserDialog
        {
            Description = "Select the folder where ChoirLauncher will be installed",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true,
            SelectedPath = ExistingDirectoryOrEmpty(installRootText.Text)
        };
        if (picker.ShowDialog(this) == DialogResult.OK) installRootText.Text = picker.SelectedPath;
    }

    private void BrowseForGameRoot()
    {
        using var picker = new FolderBrowserDialog
        {
            Description = "Select the main Songs of Syx folder containing SongsOfSyx.jar",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false,
            SelectedPath = ExistingDirectoryOrEmpty(gameRootText.Text)
        };
        if (picker.ShowDialog(this) != DialogResult.OK) return;
        gameRootText.Text = picker.SelectedPath;
        ShowGameRootValidation();
    }

    private void AutoDetectGameRoot()
    {
        var detected = autoDetect();
        if (SongsOfSyxGameLocation.TryNormalize(detected.GameRoot, out var normalized, out _))
        {
            gameRootText.Text = normalized;
            SetStatus("Songs of Syx was detected. Review both folders, then select Continue.", Color.DarkGreen);
            return;
        }

        var details = detected.Diagnostics.Count == 0
            ? "Automatic detection did not find a valid Songs of Syx folder."
            : string.Join(Environment.NewLine, detected.Diagnostics.Distinct(StringComparer.Ordinal));
        SetStatus(details, Color.Firebrick);
        gameRootText.Focus();
    }

    private void ContinueInstallation()
    {
        if (!TryNormalizeInstallRoot(installRootText.Text, out var installRoot, out var installError))
        {
            SetStatus(installError, Color.Firebrick);
            installRootText.Focus();
            return;
        }
        if (!SongsOfSyxGameLocation.TryNormalize(gameRootText.Text, out var gameRoot, out var gameError))
        {
            SetStatus(gameError, Color.Firebrick);
            gameRootText.Focus();
            return;
        }

        InstallRoot = installRoot;
        GameRoot = gameRoot;
        DialogResult = DialogResult.OK;
        Close();
    }

    private void ShowInitialStatus()
    {
        if (SongsOfSyxGameLocation.TryNormalize(gameRootText.Text, out _, out _))
            SetStatus("A valid Songs of Syx folder was detected. Review both folders, then select Continue.", Color.DarkGreen);
        else
            SetStatus("Choose the main Songs of Syx installation folder. Continue returns here until both folders are valid.", Color.Firebrick);
    }

    private void ShowGameRootValidation()
    {
        if (SongsOfSyxGameLocation.TryNormalize(gameRootText.Text, out _, out var error))
            SetStatus("This Songs of Syx folder is valid. Review both folders, then select Continue.", Color.DarkGreen);
        else
            SetStatus(error, Color.Firebrick);
    }

    private void SetStatus(string message, Color color)
    {
        status.Text = message;
        status.ForeColor = color;
    }

    private static bool TryNormalizeInstallRoot(string? candidate, out string normalized, out string error)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(candidate))
        {
            error = "Choose a ChoirLauncher installation folder.";
            return false;
        }

        try
        {
            normalized = Path.GetFullPath(candidate);
            var root = Path.GetPathRoot(normalized) ?? string.Empty;
            if (normalized.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Equals(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            {
                error = "ChoirLauncher cannot be installed directly in a filesystem root.";
                return false;
            }
            if (File.Exists(normalized))
            {
                error = "The selected ChoirLauncher installation path is an existing file, not a folder.";
                return false;
            }
            error = string.Empty;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            error = "The ChoirLauncher installation folder is not valid: " + ex.Message;
            return false;
        }
    }

    private static string ExistingDirectoryOrEmpty(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        try
        {
            var full = Path.GetFullPath(path);
            return Directory.Exists(full) ? full : string.Empty;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }
}
