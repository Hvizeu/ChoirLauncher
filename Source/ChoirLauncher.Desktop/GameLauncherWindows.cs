using System.Diagnostics;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using ChoirLauncher.Core;

namespace ChoirLauncher.Desktop;

internal sealed record Choice<T>(T Value, string Label)
{
    public override string ToString() => Label;
}

internal sealed class GameSettingsWindow : Window
{
    internal const double DisplayCoreColumnWidth = 350;
    internal const double DisplayLabelWidth = 130;
    internal const double SettingColumnSpacing = 12;
    internal const double ScreenModeControlWidth = 190;
    private static readonly IBrush Parchment = new SolidColorBrush(Color.FromRgb(218, 200, 163));
    private static readonly IBrush Bronze = new SolidColorBrush(Color.FromRgb(178, 138, 82));
    private readonly MainWindowViewModel vm;
    private readonly DesktopPreferencesStore desktopPreferences;
    private readonly DesktopPreferences initialDesktopPreferences;
    private readonly LauncherGameOptions initial;
    private readonly GameLauncherText text;
    private readonly IReadOnlyList<GameLanguage> languages;
    private readonly Dictionary<string, CheckBox> toggles = new(StringComparer.Ordinal);
    private readonly ComboBox fps;
    private readonly ComboBox screenMode;
    private readonly ComboBox audio;
    private readonly ComboBox language;
    private readonly NumericUpDown monitor;
    private readonly ComboBox width;
    private readonly ComboBox height;
    private readonly ComboBox scale;
    private readonly StackPanel windowedPanel;
    private readonly StackPanel borderlessPanel;
    private readonly TextBlock fullScreenNote;
    private readonly CheckBox launcherDeveloperMode;

    public GameSettingsWindow(MainWindowViewModel vm)
    {
        this.vm = vm;
        desktopPreferences = new(vm.StoragePaths);
        initialDesktopPreferences = desktopPreferences.Load();
        initial = vm.LoadLauncherOptions();
        text = vm.LoadGameLauncherText(initial.Language);
        languages = vm.DiscoverGameLanguages();
        Icon = VanillaLauncherArt.ApplicationIcon;
        Title = text.Get("launcher.ScreenSetting", "Settings", "Settings") + " — Songs of Syx";
        Width = 1040;
        Height = 760;
        MinWidth = 820;
        MinHeight = 580;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(24, 18, 12));

        fps = new ComboBox { ItemsSource = new[] { new Choice<int>(0, text.Get("launcher.ScreenSetting", "screen", "Screen")), new(40, "40"), new(60, "60"), new(80, "80"), new(100, "100") }, Width = 170 };
        Select(fps, initial.FpsCap);
        screenMode = new ComboBox
        {
            ItemsSource = new[]
            {
                new Choice<GameScreenMode>(GameScreenMode.Borderless, text.Get("launcher.ScreenSetting", "Borderless", "Borderless")),
                new Choice<GameScreenMode>(GameScreenMode.FullScreen, text.Get("launcher.ScreenSetting", "Full", "Full Screen")),
                new Choice<GameScreenMode>(GameScreenMode.Windowed, text.Get("launcher.ScreenSetting", "Windowed", "Windowed"))
            }, Width = ScreenModeControlWidth
        };
        Select(screenMode, initial.ScreenMode);
        screenMode.SelectionChanged += (_, _) => UpdateModePanels();

        var audioChoices = new List<Choice<string>>
        {
            new(string.Empty, text.Get("launcher.ScreenSetting", "default", "Default")),
            new("null", text.Get("launcher.ScreenSetting", "none", "None"))
        };
        if (initial.AudioDevice.Length > 0 && initial.AudioDevice != "null") audioChoices.Add(new(initial.AudioDevice, initial.AudioDevice));
        audio = new ComboBox { ItemsSource = audioChoices, Width = 230 };
        Select(audio, initial.AudioDevice);

        language = new ComboBox
        {
            ItemsSource = languages,
            ItemTemplate = new FuncDataTemplate<GameLanguage>((item, _) => LanguageRow(item!), true),
            Width = 230
        };
        language.SelectedItem = languages.FirstOrDefault(x => x.Code == initial.Language) ?? languages[0];
        monitor = Number(initial.Monitor, 0, Math.Max(initial.Monitor, 15));
        width = ChoiceBox(Enumerable.Range(0, 21).Select(value => new Choice<int>(value, $"{LauncherOptionPresentation.WindowPercent(value)}%")), initial.WindowWidth);
        height = ChoiceBox(Enumerable.Range(0, 21).Select(value => new Choice<int>(value, $"{LauncherOptionPresentation.WindowPercent(value)}%")), initial.WindowHeight);
        var scaleMaximum = Math.Max(initial.BorderlessScale, 20);
        scale = ChoiceBox(Enumerable.Range(0, scaleMaximum + 1).Select(value => new Choice<int>(value, $"{LauncherOptionPresentation.BorderlessScalePercent(value)}%")), initial.BorderlessScale);

        windowedPanel = new StackPanel { Spacing = 8 };
        windowedPanel.Children.Add(SettingRow(text.Get("launcher.ScreenSetting", "Width", "Width"), width, text.Get("launcher.ScreenSetting", "widthD", "The width of the window")));
        windowedPanel.Children.Add(SettingRow(text.Get("launcher.ScreenSetting", "Height", "Height"), height, text.Get("launcher.ScreenSetting", "HeightD", "The height of the window")));
        windowedPanel.Children.Add(Toggle("WindowBorders", text.Get("launcher.ScreenSetting", "Borders", "Borders"), text.Get("launcher.ScreenSetting", "BorderD", "Use borders and system decoration on the window"), initial.WindowBorders));
        var forcedHdToggle = Toggle("ForcedHd", "HD", "Forced HD resolution. This official option is shown only with Developer enabled.", initial.ForcedHd);
        forcedHdToggle.IsVisible = initial.Developer;
        windowedPanel.Children.Add(forcedHdToggle);

        borderlessPanel = new StackPanel { Spacing = 8 };
        borderlessPanel.Children.Add(SettingRow(text.Get("launcher.ScreenSetting", "Scale", "Scale"), scale, text.Get("launcher.ScreenSetting", "ScaleD", "100% plus 5% for each step.")));
        fullScreenNote = new TextBlock
        {
            Text = $"{text.Get("launcher.ScreenSetting", "Resolution", "Resolution")}: existing official mode #{initial.FullScreenDisplay} is preserved.\nChoirLauncher will not guess the native GLFW video-mode index.",
            TextWrapping = TextWrapping.Wrap, Foreground = Parchment, Opacity = 0.9, Margin = new(0, 6)
        };

        var featureGrid = new Grid { ColumnDefinitions = new("*,*"), RowDefinitions = new("Auto,Auto,Auto,Auto,Auto"), ColumnSpacing = 18, RowSpacing = 3 };
        AddFeature(featureGrid, Toggle("Linear", text.Get("launcher.ScreenSetting", "Linear", "Linear filtering"), text.Get("launcher.ScreenSetting", "LinearD", "Enables linear filtering when scaled."), initial.LinearFiltering), 0, 0);
        AddFeature(featureGrid, Toggle("Shading", text.Get("launcher.ScreenSetting", "Shading", "Shading"), text.Get("launcher.ScreenSetting", "ShadingD", "Use normal maps and dynamic lighting."), initial.Shading), 1, 0);
        AddFeature(featureGrid, Toggle("VSync", text.Get("launcher.ScreenSetting", "VSync", "VSync"), text.Get("launcher.ScreenSetting", "VSyncD", "Reduce screen tearing."), initial.VSync), 0, 1);
        AddFeature(featureGrid, Toggle("AdaptiveVSync", text.Get("launcher.ScreenSetting", "VSync-Adapt", "Adaptive VSync"), text.Get("launcher.ScreenSetting", "VSyncAD", "Adaptive refresh-rate synchronization."), initial.AdaptiveVSync), 1, 1);
        AddFeature(featureGrid, Toggle("Iconify", text.Get("launcher.ScreenSetting", "Iconify", "Minimize on focus loss"), text.Get("launcher.ScreenSetting", "IconifyD", "Allow full screen to minimize when focus is lost."), initial.AutoIconify), 0, 2);
        AddFeature(featureGrid, Toggle("WindowFix", text.Get("launcher.ScreenSetting", "W-Fix", "Window compatibility fix"), text.Get("launcher.ScreenSetting", "MFixD", "Borderless and multi-monitor tabbing fix."), initial.WindowFix), 1, 2);
        AddFeature(featureGrid, Toggle("WindowFloat", text.Get("launcher.ScreenSetting", "W-Float", "Always on top"), text.Get("launcher.ScreenSetting", "WFloatD", "Keep the game window above other applications."), initial.WindowFloat), 0, 3);
        AddFeature(featureGrid, Toggle("EasyUi", text.Get("launcher.ScreenSetting", "UI-Easy", "Easy-to-read UI"), text.Get("launcher.ScreenSetting", "UI-EasyD", "English-only Open Sans and clearer UI colors."), initial.EasyUi), 1, 3);
        AddFeature(featureGrid, Toggle("Debug", text.Get("launcher.ScreenSetting", "Debug", "Debug mode"), text.Get("launcher.ScreenSetting", "debugD", "Starts the game in debug mode."), initial.Debug), 0, 4);
        var developerToggle = Toggle("Developer", text.Get("launcher.ScreenSetting", "Developer", "Developer tools"), text.Get("launcher.ScreenSetting", "DeveloperD", "Enables in-game development tools."), initial.Developer);
        developerToggle.Click += (_, _) => forcedHdToggle.IsVisible = developerToggle.IsChecked == true;
        AddFeature(featureGrid, developerToggle, 1, 4);

        launcherDeveloperMode = new CheckBox
        {
            Content = "ChoirLauncher developer mode",
            IsChecked = initialDesktopPreferences.LauncherDeveloperMode,
            Foreground = Parchment,
            Margin = new(6)
        };
        ToolTip.SetTip(launcherDeveloperMode, "Shows the full technical mod-order Apply Preview before Apply Profile & Launch. Real blockers remain enforced even when this is off.");

        var displayCore = new StackPanel
        {
            Spacing = 10,
            Children =
            {
                SettingRow(text.Get("launcher.ScreenSetting", "Screen", "Screen mode"), screenMode, text.Get("launcher.ScreenSetting", "ScreedD", "The type of display to create."), DisplayLabelWidth),
                SettingRow(text.Get("launcher.ScreenSetting", "Monitor", "Monitor"), monitor, text.Get("launcher.ScreenSetting", "MonitorD", "The zero-based monitor used by the game."), DisplayLabelWidth)
            }
        };
        var displayMode = new StackPanel { Spacing = 8, Children = { fullScreenNote, borderlessPanel, windowedPanel } };
        // Both columns must contain their own label + gap + control. The previous 320px
        // left column was narrower than its 130 + 12 + 220px screen-mode row, allowing
        // the ComboBox to paint into the Width/Height column.
        var displayGrid = new Grid { ColumnDefinitions = new($"{DisplayCoreColumnWidth},*"), ColumnSpacing = 20, ClipToBounds = true };
        displayGrid.Children.Add(displayCore); Grid.SetColumn(displayMode, 1); displayGrid.Children.Add(displayMode);

        var reset = new Button { Content = text.Get("launcher.ScreenSetting", "Reset", "Reset"), MinWidth = 110 };
        reset.Click += (_, _) => ResetVisible();
        var back = new Button { Content = text.Get("launcher.ScreenSetting", "Back", "Back"), MinWidth = 110, IsCancel = true };
        back.Click += (_, _) => Close();
        var apply = new Button { Content = "Apply Settings", MinWidth = 150, IsDefault = true, Background = new SolidColorBrush(Color.FromRgb(83, 112, 82)) };
        apply.Click += async (_, _) => await PreviewAndApplyAsync();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Right, Children = { reset, back, apply } };

        var generalGrid = new Grid { ColumnDefinitions = new("*,*"), ColumnSpacing = 14 };
        generalGrid.Children.Add(Card(new StackPanel { Spacing = 10, Children = { Section("Rendering & compatibility", 19), featureGrid } }));
        var preferences = Card(new StackPanel
        {
            Spacing = 9,
            Children =
            {
                Section("Audio, frame rate & language", 19),
                SettingRow(text.Get("launcher.ScreenSetting", "Audio", "Audio"), audio, text.Get("launcher.ScreenSetting", "audioD", "Audio device selection."), 110),
                SettingRow(text.Get("launcher.ScreenSetting", "FPS", "FPS"), fps, text.Get("launcher.ScreenSetting", "fpsD", "Manual FPS cap; Screen uses the display refresh rate."), 110),
                SettingRow("Game language", language, "Changes Songs of Syx and the integrated launcher views. The mod manager itself remains English in this RC.", 110)
            }
        });
        Grid.SetColumn(preferences, 1); generalGrid.Children.Add(preferences);

        var scrollContent = new StackPanel
        {
            Margin = new(20, 8, 20, 18), Spacing = 14,
            Children =
            {
                generalGrid,
                Card(new StackPanel { Spacing = 8, Children = { Section("ChoirLauncher", 19), launcherDeveloperMode, new TextBlock { Text = "Normal mode launches a valid profile immediately. Developer mode exposes technical hashes, exact order changes, and the full Apply Preview.", TextWrapping = TextWrapping.Wrap, Foreground = Parchment, Opacity = 0.82 } } }),
                Card(new StackPanel { Spacing = 10, Children = { Section(text.Get("launcher.ScreenSetting", "Screen", "Display"), 19), displayGrid } })
            }
        };
        var header = new StackPanel
        {
            Margin = new(20, 16, 20, 8), Spacing = 4,
            Children =
            {
                Section(text.Get("launcher.ScreenSetting", "Settings", "Settings"), 28),
                new TextBlock { Text = "Official Songs of Syx v71.44 settings. Every change is previewed, backed up, and verified before it is kept.", TextWrapping = TextWrapping.Wrap, Foreground = Parchment, Opacity = 0.88 }
            }
        };
        var footer = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(235, 24, 18, 12)), BorderBrush = Bronze, BorderThickness = new(0, 1, 0, 0),
            Padding = new(18, 10), Child = buttons
        };
        var layout = new Grid { RowDefinitions = new("Auto,*,Auto") };
        AutomationProperties.SetName(layout, "Songs of Syx settings layout");
        layout.Children.Add(header);
        var scroller = new ScrollViewer { Content = scrollContent, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
        AutomationProperties.SetName(scroller, "Songs of Syx settings scroll area");
        AutomationProperties.SetName(footer, "Songs of Syx settings fixed actions");
        Grid.SetRow(scroller, 1); layout.Children.Add(scroller); Grid.SetRow(footer, 2); layout.Children.Add(footer);
        Content = WithBackground(layout);
        UpdateModePanels();
    }

    private CheckBox Toggle(string key, string label, string description, bool value)
    {
        var control = new CheckBox { Content = label, IsChecked = value, MinWidth = 170, Margin = new(6), Foreground = Parchment };
        ToolTip.SetTip(control, description);
        toggles[key] = control;
        return control;
    }

    private async Task PreviewAndApplyAsync()
    {
        LauncherOptionsPreview preview;
        try { preview = vm.CreateLauncherOptionsPreview(Capture()); }
        catch (Exception ex) when (ex is IOException or FormatException or InvalidDataException or ArgumentException)
        {
            await Dialogs.ShowAsync(this, "Settings validation", ex.Message);
            return;
        }
        var launcherPreferenceChanged = initialDesktopPreferences.LauncherDeveloperMode != (launcherDeveloperMode.IsChecked == true);
        if (!preview.HasChanges && !launcherPreferenceChanged)
        {
            await Dialogs.ShowAsync(this, "Settings", "No settings changed.");
            return;
        }
        if (!preview.HasChanges)
        {
            desktopPreferences.Save(initialDesktopPreferences with { LauncherDeveloperMode = launcherDeveloperMode.IsChecked == true });
            await Dialogs.ShowAsync(this, "Settings applied", "ChoirLauncher developer mode was updated.");
            Close(true);
            return;
        }
        var summary = string.Join(Environment.NewLine, preview.Changes.Select(change => $"• {change.Key}: {change.Before} → {change.After}"));
        var choice = await Dialogs.ChooseAsync(this, "Apply Songs of Syx settings",
            "ChoirLauncher will modify only the listed official values. The mod list and every unrelated setting remain unchanged.\n\n" + summary,
            "Apply", "Cancel");
        if (choice != "Apply") return;
        var result = await vm.ApplyLauncherOptionsAsync(preview);
        if (!result.Success)
        {
            await Dialogs.ShowAsync(this, "Settings were not applied", string.Join(Environment.NewLine, result.Diagnostics));
            return;
        }
        if (launcherPreferenceChanged)
            desktopPreferences.Save(initialDesktopPreferences with { LauncherDeveloperMode = launcherDeveloperMode.IsChecked == true });
        await Dialogs.ShowAsync(this, "Settings applied", $"Verified successfully. Backup: {result.BackupId}");
        Close(true);
    }

    private LauncherGameOptions Capture() => initial with
    {
        Debug = Checked("Debug"), Developer = Checked("Developer"), LinearFiltering = Checked("Linear"), Shading = Checked("Shading"),
        VSync = Checked("VSync"), AdaptiveVSync = Checked("AdaptiveVSync"), AutoIconify = Checked("Iconify"), WindowFix = Checked("WindowFix"),
        WindowFloat = Checked("WindowFloat"), EasyUi = Checked("EasyUi"), WindowBorders = Checked("WindowBorders"), ForcedHd = Checked("ForcedHd"),
        AudioDevice = ((Choice<string>)audio.SelectedItem!).Value, FpsCap = ((Choice<int>)fps.SelectedItem!).Value,
        ScreenMode = ((Choice<GameScreenMode>)screenMode.SelectedItem!).Value, Monitor = Decimal(monitor),
        WindowWidth = SelectedInt(width), WindowHeight = SelectedInt(height), BorderlessScale = SelectedInt(scale),
        Language = ((GameLanguage)language.SelectedItem!).Code
    };

    private void ResetVisible()
    {
        var defaults = LauncherGameOptions.VerifiedDefaults(initial.Language);
        Set("Debug", defaults.Debug); Set("Developer", defaults.Developer); Set("Linear", defaults.LinearFiltering); Set("Shading", defaults.Shading);
        Set("VSync", defaults.VSync); Set("AdaptiveVSync", defaults.AdaptiveVSync); Set("Iconify", defaults.AutoIconify); Set("WindowFix", defaults.WindowFix);
        Set("WindowFloat", defaults.WindowFloat); Set("EasyUi", defaults.EasyUi); Set("WindowBorders", defaults.WindowBorders); Set("ForcedHd", defaults.ForcedHd);
        toggles["ForcedHd"].IsVisible = defaults.Developer;
        Select(fps, defaults.FpsCap); Select(screenMode, defaults.ScreenMode); Select(audio, defaults.AudioDevice);
        monitor.Value = defaults.Monitor; Select(width, defaults.WindowWidth); Select(height, defaults.WindowHeight); Select(scale, defaults.BorderlessScale);
        UpdateModePanels();
    }

    private void UpdateModePanels()
    {
        var mode = screenMode.SelectedItem is Choice<GameScreenMode> choice ? choice.Value : initial.ScreenMode;
        windowedPanel.IsVisible = mode == GameScreenMode.Windowed;
        borderlessPanel.IsVisible = mode == GameScreenMode.Borderless;
        fullScreenNote.IsVisible = mode == GameScreenMode.FullScreen;
    }

    private bool Checked(string key) => toggles[key].IsChecked == true;
    private void Set(string key, bool value) => toggles[key].IsChecked = value;
    private static int Decimal(NumericUpDown control) => decimal.ToInt32(control.Value ?? 0);
    private static int SelectedInt(ComboBox control) => ((Choice<int>)control.SelectedItem!).Value;
    private static NumericUpDown Number(int value, int minimum, int maximum) => new() { Value = value, Minimum = minimum, Maximum = maximum, Increment = 1, Width = 170, HorizontalAlignment = HorizontalAlignment.Left };
    private static ComboBox ChoiceBox(IEnumerable<Choice<int>> choices, int selected)
    {
        var control = new ComboBox { ItemsSource = choices.ToArray(), Width = 170, HorizontalAlignment = HorizontalAlignment.Left };
        Select(control, selected);
        return control;
    }
    private static void Select<T>(ComboBox combo, T value) => combo.SelectedItem = ((IEnumerable<Choice<T>>)combo.ItemsSource!).FirstOrDefault(x => EqualityComparer<T>.Default.Equals(x.Value, value)) ?? ((IEnumerable<Choice<T>>)combo.ItemsSource!).First();
    private static Control LanguageRow(GameLanguage language) => new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { new Image { Source = VanillaLauncherArt.LanguageIcon(language.SpriteIndex), Width = 24, Height = 24 }, new TextBlock { Text = $"{language.DisplayName}  ({language.Coverage:P0})", VerticalAlignment = VerticalAlignment.Center } } };
    private static TextBlock Section(string value, double size = 21) => new() { Text = value, FontSize = size, FontWeight = FontWeight.Bold, Foreground = Parchment };
    private static void AddFeature(Grid grid, Control control, int column, int row)
    {
        Grid.SetColumn(control, column); Grid.SetRow(control, row); grid.Children.Add(control);
    }
    private static Control SettingRow(string label, Control control, string description, double labelWidth = 180)
    {
        control.HorizontalAlignment = HorizontalAlignment.Left;
        var grid = new Grid { ColumnDefinitions = new($"{labelWidth},*"), ColumnSpacing = SettingColumnSpacing, Margin = new(2) };
        var title = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Foreground = Parchment };
        ToolTip.SetTip(title, description); ToolTip.SetTip(control, description);
        grid.Children.Add(title); Grid.SetColumn(control, 1); grid.Children.Add(control); return grid;
    }
    private static Border Card(Control child) => new() { Child = child, Padding = new(14), CornerRadius = new(6), BorderBrush = Bronze, BorderThickness = new(1), Background = new SolidColorBrush(Color.FromArgb(210, 28, 22, 16)) };
    internal static Control WithBackground(Control child) => new Grid { Children = { new Image { Source = OwnerLauncherArt.CityBackground, Stretch = Stretch.UniformToFill, Opacity = 0.28, IsHitTestVisible = false }, new Border { Background = new SolidColorBrush(Color.FromArgb(150, 12, 9, 6)), IsHitTestVisible = false }, child } };
}

internal sealed class GameInfoWindow : Window
{
    public GameInfoWindow(MainWindowViewModel vm)
    {
        GameLauncherText text;
        try { text = vm.LoadGameLauncherText(vm.LoadLauncherOptions().Language); }
        catch (Exception ex) when (ex is IOException or FormatException or InvalidDataException or UnauthorizedAccessException)
        {
            text = vm.LoadGameLauncherText("");
        }
        var info = vm.DiscoverGameInfo();
        Icon = VanillaLauncherArt.ApplicationIcon;
        Title = text.Get("launcher.ScreenMain", "Info", "Info") + " — Songs of Syx";
        Width = 900; Height = 640; MinWidth = 680; MinHeight = 480; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var stack = new StackPanel { Margin = new(26), Spacing = 9 };
        stack.Children.Add(new TextBlock { Text = text.Get("launcher.ScreenMain", "Info", "Info"), FontSize = 29, FontWeight = FontWeight.Bold, Foreground = new SolidColorBrush(Color.FromRgb(218, 200, 163)) });
        stack.Children.Add(Row(text.Get("launcher.ScreenInfo", "Version", "Version"), info.GameVersion + (info.VersionDetected ? "  ✓ detected" : string.Empty)));
        stack.Children.Add(Row("Build recognition", info.BuildRecognition));
        stack.Children.Add(Row(text.Get("launcher.ScreenInfo", "Platform", "Platform"), info.Platform));
        stack.Children.Add(Row(text.Get("launcher.ScreenInfo", "JRE", "JRE"), info.JavaRuntime));
        stack.Children.Add(Row(text.Get("launcher.ScreenInfo", "GPU", "GPU"), info.GraphicsProcessor));
        stack.Children.Add(Row(text.Get("launcher.ScreenInfo", "GPU-Driver", "GPU Driver"), info.GraphicsDriver));
        stack.Children.Add(FolderRow(this, text.Get("launcher.ScreenInfo", "localF", "Local Files"), info.LocalFiles));
        stack.Children.Add(FolderRow(this, text.Get("launcher.ScreenInfo", "Saves", "Saves"), info.Saves));
        stack.Children.Add(FolderRow(this, text.Get("launcher.ScreenInfo", "Screenshots", "Screenshots"), info.Screenshots));
        stack.Children.Add(FolderRow(this, text.Get("launcher.ScreenInfo", "Mods", "Mods"), info.Mods));
        if (info.GameRoot is not null) stack.Children.Add(FolderRow(this, "Game files", info.GameRoot));
        stack.Children.Add(Row("SongsOfSyx.jar SHA-256", info.GameJarSha256 ?? "Unavailable"));
        foreach (var diagnostic in info.Diagnostics) stack.Children.Add(new TextBlock { Text = diagnostic, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Orange });
        var back = new Button { Content = text.Get("launcher.ScreenInfo", "Back", "Back"), MinWidth = 110, HorizontalAlignment = HorizontalAlignment.Right, IsCancel = true };
        back.Click += (_, _) => Close(); stack.Children.Add(back);
        Content = GameSettingsWindow.WithBackground(new ScrollViewer { Content = stack, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto });
    }

    private static Control Row(string label, string value)
    {
        var grid = new Grid { ColumnDefinitions = new("190,*"), ColumnSpacing = 12 };
        grid.Children.Add(new TextBlock { Text = label, FontWeight = FontWeight.Bold, Foreground = new SolidColorBrush(Color.FromRgb(218, 200, 163)) });
        var text = new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.FromRgb(139, 158, 96)) };
        Grid.SetColumn(text, 1); grid.Children.Add(text); return grid;
    }

    private static Control FolderRow(Window owner, string label, string path)
    {
        var button = new Button { Content = path, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left };
        button.Click += async (_, _) =>
        {
            if (!Directory.Exists(path)) { await Dialogs.ShowAsync(owner, label, "Folder does not exist: " + path); return; }
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        };
        return SettingRow(label, button);
    }

    private static Control SettingRow(string label, Control control)
    {
        var grid = new Grid { ColumnDefinitions = new("190,*"), ColumnSpacing = 12 };
        grid.Children.Add(new TextBlock { Text = label, FontWeight = FontWeight.Bold, Foreground = new SolidColorBrush(Color.FromRgb(218, 200, 163)), VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(control, 1); grid.Children.Add(control); return grid;
    }
}

internal sealed class GameLanguageWindow : Window
{
    public GameLanguageWindow(MainWindowViewModel vm)
    {
        var current = vm.LoadLauncherOptions();
        var languages = vm.DiscoverGameLanguages();
        Icon = VanillaLauncherArt.ApplicationIcon;
        Title = "Game language"; Width = 540; Height = 650; MinHeight = 420; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var list = new ListBox
        {
            ItemsSource = languages,
            SelectedItem = languages.FirstOrDefault(x => x.Code == current.Language) ?? languages[0],
            ItemTemplate = new FuncDataTemplate<GameLanguage>((language, _) => new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 12, Margin = new(5),
                Children = { new Image { Source = VanillaLauncherArt.LanguageIcon(language!.SpriteIndex), Width = 24, Height = 24 }, new TextBlock { Text = language.DisplayName, FontWeight = FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center }, new TextBlock { Text = $"{language.Coverage:P0}", Opacity = 0.65, VerticalAlignment = VerticalAlignment.Center } }
            }, true)
        };
        var apply = new Button { Content = "Preview & Apply", MinWidth = 140, IsDefault = true };
        var back = new Button { Content = "Back", MinWidth = 100, IsCancel = true };
        back.Click += (_, _) => Close();
        apply.Click += async (_, _) =>
        {
            if (list.SelectedItem is not GameLanguage selected) return;
            var preview = vm.CreateLauncherOptionsPreview(current with { Language = selected.Code });
            if (!preview.HasChanges) { Close(); return; }
            var choice = await Dialogs.ChooseAsync(this, "Change game language", $"Change Songs of Syx language to {selected.DisplayName}?\n\nThe current mod order and all unrelated settings will be preserved.", "Apply", "Cancel");
            if (choice != "Apply") return;
            var result = await vm.ApplyLauncherOptionsAsync(preview);
            if (!result.Success) { await Dialogs.ShowAsync(this, "Language was not changed", string.Join(Environment.NewLine, result.Diagnostics)); return; }
            Close(true);
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { back, apply } };
        Content = GameSettingsWindow.WithBackground(new Grid { Margin = new(18), RowDefinitions = new("Auto,*,Auto"), RowSpacing = 10, Children = { new TextBlock { Text = "Songs of Syx language", FontSize = 25, FontWeight = FontWeight.Bold }, At(list, 1), At(buttons, 2) } });
        AutomationProperties.SetName(list, "Songs of Syx language list");
    }

    private static T At<T>(T control, int row) where T : Control { Grid.SetRow(control, row); return control; }
}
