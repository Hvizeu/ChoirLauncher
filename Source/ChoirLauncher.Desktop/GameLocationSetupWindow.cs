using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using ChoirLauncher.Core;

namespace ChoirLauncher.Desktop;

public sealed class GameLocationSetupWindow : Window
{
    private readonly Func<GameLocationDetection> autoDetect;
    private readonly TextBox gameRoot;
    private readonly TextBlock status;

    public GameLocationSetupWindow(
        string? initialGameRoot,
        string applicationRoot,
        string storageRoot,
        Func<GameLocationDetection> autoDetect)
    {
        ArgumentNullException.ThrowIfNull(autoDetect);
        this.autoDetect = autoDetect;

        Title = "ChoirLauncher Setup";
        Icon = VanillaLauncherArt.ApplicationIcon;
        Width = 760;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var application = ReadOnlyPath(applicationRoot, "ChoirLauncher application folder");
        var storage = ReadOnlyPath(storageRoot, "ChoirLauncher data folder");
        gameRoot = new TextBox
        {
            Text = initialGameRoot ?? string.Empty,
            MinWidth = 500,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        AutomationProperties.SetName(gameRoot, "Songs of Syx game folder");
        gameRoot.TextChanged += (_, _) => ShowPendingStatus();

        var browse = new Button { Content = "Browse...", MinWidth = 96 };
        browse.Click += BrowseForGameRoot;
        AutomationProperties.SetName(browse, "Browse for Songs of Syx game folder");

        var detect = new Button { Content = "Auto-detect", MinWidth = 110 };
        detect.Click += (_, _) => AutoDetectGameRoot();
        AutomationProperties.SetName(detect, "Auto-detect Songs of Syx game folder");

        var continueButton = new Button { Content = "Continue", IsDefault = true, MinWidth = 110 };
        continueButton.Click += (_, _) => ContinueSetup();
        AutomationProperties.SetName(continueButton, "Continue setup");

        var exit = new Button { Content = "Exit ChoirLauncher", IsCancel = true, MinWidth = 140 };
        exit.Click += (_, _) => Close(null);

        status = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 44
        };
        AutomationProperties.SetName(status, "Game folder validation status");

        var gamePathRow = new Grid
        {
            ColumnDefinitions = new("*,Auto,Auto"),
            ColumnSpacing = 8,
            Children = { gameRoot, browse, detect }
        };
        Grid.SetColumn(browse, 1);
        Grid.SetColumn(detect, 2);

        Content = new ScrollViewer
        {
            MaxHeight = 700,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = new StackPanel
            {
                Margin = new Thickness(24),
                Spacing = 10,
                Children =
                {
                    new TextBlock
                    {
                        Text = "Review the detected folders before ChoirLauncher continues. " +
                               "You can type or browse to a different Songs of Syx installation.",
                        TextWrapping = TextWrapping.Wrap,
                        FontSize = 16
                    },
                    Label("ChoirLauncher application folder"),
                    application,
                    Label("ChoirLauncher data folder"),
                    storage,
                    Label("Songs of Syx game folder (must contain SongsOfSyx.jar)"),
                    gamePathRow,
                    status,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Spacing = 8,
                        Children = { exit, continueButton }
                    }
                }
            }
        };

        ShowInitialStatus();
        Opened += (_, _) => gameRoot.Focus();
    }

    private static TextBlock Label(string text) => new()
    {
        Text = text,
        FontWeight = FontWeight.SemiBold,
        Margin = new Thickness(0, 5, 0, 0)
    };

    private static TextBox ReadOnlyPath(string path, string automationName)
    {
        var textBox = new TextBox
        {
            Text = Path.GetFullPath(path),
            IsReadOnly = true,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        AutomationProperties.SetName(textBox, automationName);
        return textBox;
    }

    private async void BrowseForGameRoot(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select the Songs of Syx folder containing SongsOfSyx.jar",
            AllowMultiple = false
        });
        if (folders.Count == 0) return;
        gameRoot.Text = folders[0].Path.LocalPath;
        ShowCurrentValidation();
    }

    private void AutoDetectGameRoot()
    {
        var detected = autoDetect();
        if (SongsOfSyxGameLocation.TryNormalize(detected.GameRoot, out var normalized, out _))
        {
            gameRoot.Text = normalized;
            SetStatus("Songs of Syx was detected. Review the folder, then select Continue.", Brushes.ForestGreen);
            return;
        }

        var details = detected.Diagnostics.Count == 0
            ? "Automatic detection did not find a valid Songs of Syx folder."
            : string.Join(Environment.NewLine, detected.Diagnostics.Distinct(StringComparer.Ordinal));
        SetStatus(details, Brushes.IndianRed);
        gameRoot.Focus();
    }

    private void ContinueSetup()
    {
        if (!SongsOfSyxGameLocation.TryNormalize(gameRoot.Text, out var normalized, out var error))
        {
            SetStatus(error, Brushes.IndianRed);
            gameRoot.Focus();
            return;
        }

        Close(normalized);
    }

    private void ShowInitialStatus()
    {
        if (SongsOfSyxGameLocation.TryNormalize(gameRoot.Text, out _, out _))
        {
            SetStatus("A valid Songs of Syx folder was detected. Review it, then select Continue.", Brushes.ForestGreen);
            return;
        }
        SetStatus("Choose the main Songs of Syx installation folder. Continue remains on this window until the folder is valid.", Brushes.IndianRed);
    }

    private void ShowPendingStatus() =>
        SetStatus("Select Continue to validate this Songs of Syx folder.", Brushes.Gray);

    private void ShowCurrentValidation()
    {
        if (SongsOfSyxGameLocation.TryNormalize(gameRoot.Text, out _, out var error))
            SetStatus("This Songs of Syx folder is valid. Select Continue.", Brushes.ForestGreen);
        else
            SetStatus(error, Brushes.IndianRed);
    }

    private void SetStatus(string message, IBrush foreground)
    {
        status.Text = message;
        status.Foreground = foreground;
    }
}
