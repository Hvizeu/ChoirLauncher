using System.Diagnostics;
using System.Text;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using ChoirLauncher.Core;

namespace ChoirLauncher.Desktop;

public sealed class MainWindow : Window
{
    private const int ModListColumnCount = 10;
    private const double ColumnSeparatorWidth = 5;
    private static readonly double[] MinimumColumnWidths = [28, 32, 42, 74, 74, 52, 48, 52, 60, 58];
    private static readonly IBrush Parchment = new SolidColorBrush(Color.FromRgb(218, 200, 163));
    private static readonly IBrush Bronze = new SolidColorBrush(Color.FromRgb(178, 138, 82));
    private static readonly IBrush DarkBronze = new SolidColorBrush(Color.FromRgb(100, 73, 40));
    private static readonly IBrush Moss = new SolidColorBrush(Color.FromRgb(139, 158, 96));
    private static readonly IBrush Forest = new SolidColorBrush(Color.FromRgb(83, 112, 82));
    private static readonly IBrush Clay = new SolidColorBrush(Color.FromRgb(184, 103, 76));
    private static readonly IBrush Workshop = new SolidColorBrush(Color.FromRgb(196, 139, 96));
    private static readonly IBrush Iron = new SolidColorBrush(Color.FromRgb(169, 161, 146));
    private readonly MainWindowViewModel vm;
    private readonly ListBox list;
    private readonly ContentControl details;
    private readonly ContentControl effectiveOrderContent;
    private readonly TextBlock selectedCount;
    private readonly TextBlock status;
    private readonly TextBlock conflictSummary;
    private readonly ProgressBar progress;
    private readonly Button saveButton;
    private readonly Button undoButton;
    private readonly Button redoButton;
    private Button launchButton = null!;
    private ComboBox launchActionSelector = null!;
    private readonly ListBox conflictList;
    private readonly ListBox backupList;
    private readonly TextBlock conflictContext;
    private readonly DesktopPreferencesStore preferences;
    private readonly List<WeakReference<Grid>> rowGrids = [];
    private TabControl detailsTabs = null!;
    private Grid columnHeader = null!;
    private Grid listLayout = null!;
    private double[]? modListColumnWidths;
    private bool fittingColumnWidths;
    private Point dragStart;
    private bool dragArmed;
    private PointerPressedEventArgs? dragPress;

    public MainWindow() : this(new MainWindowViewModel()) { }

    public MainWindow(MainWindowViewModel viewModel)
    {
        vm = viewModel;
        preferences = new(vm.StoragePaths);
        DataContext = vm;
        Title = $"ChoirLauncher {BuildInfo.Version}";
        Icon = VanillaLauncherArt.ApplicationIcon;
        Width = 1480; Height = 860; MinWidth = 1100; MinHeight = 680;
        Background = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.FromRgb(20, 15, 10), 0),
                new GradientStop(Color.FromRgb(42, 29, 18), 0.55),
                new GradientStop(Color.FromRgb(19, 30, 20), 1)
            }
        };
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        RestoreWindowPreferences();

        list = new ListBox
        {
            SelectionMode = SelectionMode.Multiple,
            ItemsSource = vm.VisibleRows,
            ItemTemplate = new FuncDataTemplate<ModRowViewModel>((row, _) => CreateRow(row!), true),
            ItemsPanel = new FuncTemplate<Panel?>(() => new VirtualizingStackPanel())
        };
        ScrollViewer.SetVerticalScrollBarVisibility(list, ScrollBarVisibility.Auto);
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        list.SelectionChanged += (_, _) => UpdateSelectionDetails();
        AutomationProperties.SetName(list, "Profile mod list");

        details = new ContentControl { Margin = new(10) };
        effectiveOrderContent = new ContentControl { Margin = new(10) };
        selectedCount = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        status = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        conflictSummary = new TextBlock { VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeight.SemiBold };
        conflictContext = new TextBlock { Margin = new(8), FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
        progress = new ProgressBar { Width = 150, Height = 8, IsVisible = false, Minimum = 0, Maximum = 1 };
        saveButton = Button("Save", (_, _) => Save(), "Save profile (Ctrl+S)");
        undoButton = Button("Undo", (_, _) => { vm.Undo(); UpdateUi(); Reselect(vm.LastEditSelection); }, "Undo (Ctrl+Z)");
        redoButton = Button("Redo", (_, _) => { vm.Redo(); UpdateUi(); Reselect(vm.LastEditSelection); }, "Redo (Ctrl+Y)");

        conflictList = new ListBox
        {
            ItemTemplate = new FuncDataTemplate<Conflict>((conflict, _) => CreateConflictCard(conflict), true)
        };
        backupList = new ListBox
        {
            ItemTemplate = new FuncDataTemplate<ConfigurationBackup>((b, _) => new TextBlock { Text = $"{b!.Metadata.CreatedUtc:u} — {b.Metadata.ProfileId}\n{b.Metadata.OriginalSha256[..12]}… — {b.Metadata.Result}", Margin = new(6) }, true)
        };

        Content = BuildLayout();
        KeyDown += OnKeyDown;
        Opened += async (_, _) => { await vm.InitializeAsync(); UpdateUi(); };
        Closing += (_, _) => SaveWindowPreferences();
        vm.PropertyChanged += (_, _) => UpdateUi();
    }

    private Control BuildLayout()
    {
        var root = new DockPanel { LastChildFill = true, Background = Brushes.Transparent };
        var header = BuildHeader(); DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        var footer = BuildFooter(); DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);

        var body = new Grid { Margin = new(12, 6), ColumnDefinitions = new("3*,2*"), ColumnSpacing = 12, ClipToBounds = true };
        var listArea = Surface(BuildListArea(), Bronze); AutomationProperties.SetName(listArea, "Mod list viewport"); Grid.SetColumn(listArea, 0); body.Children.Add(listArea);
        var detailArea = Surface(BuildDetailsArea(), Forest); Grid.SetColumn(detailArea, 1); body.Children.Add(detailArea);
        root.Children.Add(body);

        var city = new Image
        {
            Source = OwnerLauncherArt.CityBackground,
            Stretch = Stretch.UniformToFill,
            Opacity = 0.62,
            IsHitTestVisible = false
        };
        city.Classes.Add("owner-city-background");
        var veil = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(112, 12, 9, 6)),
            IsHitTestVisible = false
        };
        return new Grid { Children = { city, veil, root } };
    }

    private Control BuildHeader()
    {
        var profileSelector = new ComboBox { Width = 310, Height = 42, ItemsSource = vm.Profiles, VerticalAlignment = VerticalAlignment.Center };
        profileSelector.Bind(SelectingItemsControl.SelectedItemProperty, new Binding(nameof(vm.SelectedProfile)) { Mode = BindingMode.TwoWay });
        profileSelector.ItemTemplate = new FuncDataTemplate<ManagerProfile>((profile, _) =>
            new TextBlock { Text = profile?.DisplayName ?? string.Empty });
        AutomationProperties.SetName(profileSelector, "Active profile");

        var manageProfiles = Button(string.Empty, OpenProfileManager, "Manage profiles");
        manageProfiles.Content = new TextBlock
        {
            Text = "•••",
            FontSize = 12,
            FontWeight = FontWeight.Bold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Center
        };
        manageProfiles.Width = 42; manageProfiles.Height = 42; manageProfiles.Margin = new(0); manageProfiles.Padding = new(0);
        manageProfiles.HorizontalContentAlignment = HorizontalAlignment.Center;
        manageProfiles.VerticalContentAlignment = VerticalAlignment.Center;
        manageProfiles.Background = DarkBronze;
        var profileArea = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock { Text = "Profile", VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeight.Bold, Foreground = Parchment },
                profileSelector,
                manageProfiles
            }
        };

        var launcherText = GameLanguageCatalog.LoadText(null, string.Empty);
        var launcherTools = ToolbarGroup(
            "Game and launcher tools",
            Color.FromRgb(64, 86, 82),
            Button(launcherText.Get("launcher.ScreenMain", "Settings", "Settings"), OpenGameSettings, "Configure the official Songs of Syx v71.44 launcher settings"),
            Button(launcherText.Get("launcher.ScreenMain", "Info", "Info"), OpenGameInfo, "Show the installed game version, hardware, and Songs of Syx folders"),
            GameLanguageButton());
        var analysisTools = ToolbarGroup(
            "Mod analysis tools",
            Color.FromRgb(111, 76, 39),
            Button("Rescan Mods", async (_, _) => { await vm.RefreshInstallationsAsync(); UpdateUi(); }, "Rescan local and Workshop mod folders (F5)"),
            Button("Cancel Scan", (_, _) => vm.CancelRefresh(), "Cancel the current mod scan"),
            Button("Check Conflicts", CheckConflicts, "Analyze enabled mods and show the conflict report"),
            Button("Suggested Order", SuggestedOrder, "Preview dependency-aware order"));
        var profileEdits = ToolbarGroup(
            "Profile edit tools",
            Color.FromRgb(72, 91, 56),
            saveButton,
            undoButton,
            redoButton);

        var actions = new WrapPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        actions.Children.Add(launcherTools);
        actions.Children.Add(analysisTools);
        actions.Children.Add(profileEdits);

        var version = new TextBlock { Margin = new(8, 0), VerticalAlignment = VerticalAlignment.Center, Opacity = 0.75 };
        version.Bind(TextBlock.TextProperty, new Binding(nameof(vm.VersionText)));
        var left = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { actions, version } };
        var panel = new Grid { Margin = new(12, 10), ColumnDefinitions = new("*,Auto"), ColumnSpacing = 12 };
        panel.Children.Add(left); Grid.SetColumn(profileArea, 1); panel.Children.Add(profileArea);
        return new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(225, 33, 24, 16)),
            BorderBrush = Bronze,
            BorderThickness = new(0, 0, 0, 1),
            Child = panel
        };
    }

    private Control BuildListArea()
    {
        var search = new TextBox { PlaceholderText = "Search name, logical ID, source ID, or author…", MinWidth = 320 };
        search.Bind(TextBox.TextProperty, new Binding(nameof(vm.SearchText)) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
        AutomationProperties.SetName(search, "Search mods");
        var filter = new ComboBox { Width = 150, ItemsSource = new[] { "All", "Enabled", "Disabled", "Local", "Workshop", "Choir", "Legacy", "Missing", "Ambiguous", "Conflicts", "Incompatible" }, SelectedIndex = 0 };
        filter.Bind(SelectingItemsControl.SelectedItemProperty, new Binding(nameof(vm.Filter)) { Mode = BindingMode.TwoWay });
        AutomationProperties.SetName(filter, "Filter mods");

        var searchRow = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 8 };
        searchRow.Children.Add(search); Grid.SetColumn(filter, 1); searchRow.Children.Add(filter);

        columnHeader = new Grid { ColumnDefinitions = CreateModListColumns(), Background = Brushes.Transparent, Margin = new(4, 3) };
        AddHeader(columnHeader, "On", 0); AddHeader(columnHeader, "Drag", 1); AddHeader(columnHeader, "Priority", 2); AddHeader(columnHeader, "Mod", 3); AddHeader(columnHeader, "Logical ID", 4);
        ToolTip.SetTip(columnHeader.Children.OfType<TextBlock>().First(block => block.Text == "Priority"), ModPriorityOrder.UserFacingRule);
        AddHeader(columnHeader, "Source", 5); AddHeader(columnHeader, "Version", 6); AddHeader(columnHeader, "Game", 7); AddHeader(columnHeader, "State", 8); AddHeader(columnHeader, "Conflict", 9);
        AddHeaderSplitters(columnHeader);

        var priority = new TextBlock { Text = vm.PriorityHelp, FontWeight = FontWeight.Bold, Foreground = Parchment };
        var filtered = new TextBlock { Text = "Clear the search and choose All to drag rows into a new order.", Foreground = Brushes.DarkOrange };
        filtered.Bind(IsVisibleProperty, new Binding(nameof(vm.IsFiltering)));
        listLayout = new Grid
        {
            RowDefinitions = new("Auto,Auto,Auto,Auto,*"), RowSpacing = 6, ClipToBounds = true,
            Children =
            {
                At(searchRow, 0), At(priority, 1), At(filtered, 2), At(columnHeader, 3), At(list, 4)
            }
        };
        AutomationProperties.SetName(listLayout, "Resizable mod table");
        listLayout.SizeChanged += (_, _) => FitColumnsToViewport(listLayout.Bounds.Width);
        return listLayout;
    }

    private Control BuildDetailsArea()
    {
        var detailButtons = new WrapPanel { Orientation = Orientation.Horizontal };
        detailButtons.Children.Add(Button("Open Folder", OpenSelectedFolder, "Open selected installation folder"));
        detailButtons.Children.Add(Button("Workshop Page", OpenSelectedWorkshop, "Open selected Workshop page"));
        detailButtons.Children.Add(Button("Choose Installed Copy", RelinkSelected, "Choose which installed copy this profile entry uses; no mod files are moved"));
        detailButtons.Children.Add(Button("Edit Profile Notes", EditSelectedNotes, "Edit notes stored only in this ChoirLauncher profile"));
        detailButtons.Children.Add(Button("Add Removed Mods Back", RestoreRemovedMods, "Restore intentionally removed entries to this profile"));
        detailButtons.Children.Add(Button("Remove from Profile", async (_, _) => await ConfirmRemoveAsync(), "Remove selected mods from this profile only; installed files are not deleted"));
        var detailPanel = new DockPanel(); DockPanel.SetDock(detailButtons, Dock.Top); detailPanel.Children.Add(detailButtons); detailPanel.Children.Add(new ScrollViewer { Content = details });

        var conflictsPanel = new Grid { RowDefinitions = new("Auto,*") };
        conflictsPanel.Children.Add(conflictContext);
        Grid.SetRow(conflictList, 1); conflictsPanel.Children.Add(conflictList);

        detailsTabs = new TabControl
        {
            ItemsSource = new[]
            {
                new TabItem { Header = "Details", Content = detailPanel },
                new TabItem { Header = "Conflicts", Content = conflictsPanel },
                new TabItem { Header = "Effective Order", Content = new ScrollViewer { Content = effectiveOrderContent } },
                new TabItem { Header = "Backups", Content = backupList }
            }
        };

        var actions = new WrapPanel { Orientation = Orientation.Horizontal };
        actions.Children.Add(Button("Compare with Official State", CompareOfficial, "Compare this profile with the official launcher configuration"));
        actions.Children.Add(Button("Preview & Apply Profile", PreviewApply, "Preview the exact mod-order change before applying it"));
        actions.Children.Add(Button("Restore Backup", RestoreBackup, "Inspect and restore a verified launcher-settings backup"));
        var actionCard = new Border
        {
            Padding = new(8), Margin = new(0, 0, 0, 8), CornerRadius = new(6),
            Background = new SolidColorBrush(Color.FromArgb(68, 67, 48, 28)),
            BorderBrush = Bronze,
            BorderThickness = new(3, 0, 0, 0),
            Child = new StackPanel
            {
                Spacing = 4,
                Children =
                {
                    new TextBlock { Text = "Official configuration", FontWeight = FontWeight.Bold, Foreground = Parchment },
                    new TextBlock { Text = "Compare, preview, apply, or restore the selected profile.", Opacity = 0.75 },
                    actions
                }
            }
        };
        var layout = new Grid { RowDefinitions = new("Auto,*") };
        layout.Children.Add(actionCard); Grid.SetRow(detailsTabs, 1); layout.Children.Add(detailsTabs);
        return layout;
    }

    private Control BuildFooter()
    {
        var launchMark = new StackPanel
        {
            Spacing = 2,
            HorizontalAlignment = HorizontalAlignment.Center,
            Children =
            {
                new Image
                {
                    Source = VanillaLauncherArt.SongsOfSyxLogo,
                    Width = 192,
                    Height = 32,
                    Stretch = Stretch.Uniform,
                    HorizontalAlignment = HorizontalAlignment.Center
                },
                new Image
                {
                    Source = VanillaLauncherArt.OrnamentalDivider,
                    Width = 190,
                    Height = 7,
                    Stretch = Stretch.Fill,
                    Opacity = 0.82,
                    HorizontalAlignment = HorizontalAlignment.Center
                }
            }
        };
        launchButton = new Button
        {
            Content = launchMark, MinWidth = 460, MinHeight = 96, Padding = new(28, 12),
            Background = Forest, BorderBrush = Bronze, BorderThickness = new(2), CornerRadius = new(4)
        };
        launchButton.Click += LaunchSongsOfSyx;
        ToolTip.SetTip(launchButton, vm.LaunchExplanation); AutomationProperties.SetName(launchButton, "Launch Songs of Syx");
        var launchActions = new[]
        {
            new Choice<LauncherLaunchAction>(LauncherLaunchAction.ApplyProfileAndLaunch, "Apply Profile & Launch"),
            new Choice<LauncherLaunchAction>(LauncherLaunchAction.LaunchCurrentOfficialState, "Launch Current Official State"),
            new Choice<LauncherLaunchAction>(LauncherLaunchAction.OpenOfficialLauncher, "Open Official Launcher")
        };
        var savedLaunchAction = preferences.Load().DefaultLaunchAction;
        launchActionSelector = new ComboBox
        {
            ItemsSource = launchActions,
            SelectedItem = launchActions.First(item => item.Value == savedLaunchAction),
            Width = 250,
            Height = 44,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new(0, 0, 10, 0)
        };
        AutomationProperties.SetName(launchActionSelector, "Default launch action");
        ToolTip.SetTip(launchActionSelector, "Choose what the Songs of Syx launch button does. This choice is remembered.");
        launchActionSelector.SelectionChanged += (_, _) => SaveLaunchPreference();
        var launchGroup = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { launchActionSelector, launchButton }
        };
        DockPanel.SetDock(launchGroup, Dock.Right);
        var left = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { selectedCount, conflictSummary, progress, status } };
        var panel = new DockPanel { Margin = new(12, 8) }; panel.Children.Add(launchGroup); panel.Children.Add(left);
        return new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(225, 31, 23, 16)),
            BorderBrush = Bronze,
            BorderThickness = new(0, 1, 0, 0),
            Child = panel
        };
    }

    private Control CreateRow(ModRowViewModel row)
    {
        var grid = new Grid { ColumnDefinitions = CreateModListColumns(), Margin = new(4, 2), MinHeight = 34 };
        rowGrids.Add(new(grid));
        var enabled = new CheckBox { IsChecked = row.Enabled, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        AutomationProperties.SetName(enabled, $"Enable {row.Name}");
        enabled.Click += async (_, _) => { enabled.IsChecked = row.Enabled; SelectRowIfNeeded(row); await ChangeEnabledAsync(!row.Enabled); };
        var handle = new TextBlock { Text = "☰", FontSize = 17, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Cursor = new Cursor(StandardCursorType.SizeAll) };
        AutomationProperties.SetName(handle, $"Drag {row.Name}");
        ToolTip.SetTip(handle, vm.CanDrag ? "Drag this handle; drop on the upper or lower half of another row." : "Drag disabled while filtering");
        handle.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(handle).Properties.IsLeftButtonPressed) return;
            dragStart = e.GetPosition(handle); dragArmed = true; dragPress = e; SelectRowIfNeeded(row);
            e.Pointer.Capture(handle); e.Handled = true;
        };
        handle.PointerMoved += async (_, e) =>
        {
            if (!dragArmed || !vm.CanDrag || !e.GetCurrentPoint(handle).Properties.IsLeftButtonPressed) return;
            var delta = e.GetPosition(handle) - dragStart; if (Math.Abs(delta.X) + Math.Abs(delta.Y) < 5) return;
            dragArmed = false;
            var press = dragPress; dragPress = null;
            e.Pointer.Capture(null);
            if (press is null) return;
            var data = new DataTransfer(); data.Add(DataTransferItem.CreateText(string.Join('\n', SelectedIds())));
            await DragDrop.DoDragDropAsync(press, data, DragDropEffects.Move);
        };
        handle.PointerReleased += (_, e) => { dragArmed = false; dragPress = null; e.Pointer.Capture(null); };
        Add(grid, enabled, 0); Add(grid, handle, 1); Add(grid, Text(row.DisplayPriority.ToString(), foreground: Parchment), 2); Add(grid, Text(row.Name, FontWeight.SemiBold), 3);
        Add(grid, Text(row.LogicalId), 4); Add(grid, Text(row.Source, foreground: row.IsWorkshop ? Workshop : Parchment), 5); Add(grid, Text(row.Version), 6);
        Add(grid, Text(row.Compatibility, foreground: Moss), 7); Add(grid, Text(row.State, foreground: StateAccent(row.State)), 8); Add(grid, Text(row.SeverityText, foreground: ConflictAccent(row.HighestSeverity)), 9);
        AddRowColumnSeparators(grid);

        var dropSurface = new Border
        {
            Background = new SolidColorBrush(row.Position % 2 == 0 ? Color.FromArgb(58, 82, 62, 42) : Color.FromArgb(36, 58, 72, 45)),
            BorderBrush = Brushes.Gold,
            BorderThickness = new(0),
            Child = grid
        };
        AutomationProperties.SetName(dropSurface, $"Drop target {row.Name}");
        DragDrop.SetAllowDrop(dropSurface, true);
        dropSurface.AddHandler(DragDrop.DragOverEvent, (_, e) =>
        {
            if (vm.CanDrag && e.DataTransfer.TryGetText() is not null)
            {
                e.DragEffects = DragDropEffects.Move;
                SetDropIndicator(dropSurface, e.GetPosition(dropSurface).Y < dropSurface.Bounds.Height / 2);
            }
            else
            {
                e.DragEffects = DragDropEffects.None;
                ClearDropIndicator(dropSurface);
            }
            e.Handled = true;
        }, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, true);
        dropSurface.AddHandler(DragDrop.DragLeaveEvent, (_, e) =>
        {
            ClearDropIndicator(dropSurface); e.Handled = true;
        }, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, true);
        dropSurface.AddHandler(DragDrop.DropEvent, (_, e) =>
        {
            var before = e.GetPosition(dropSurface).Y < dropSurface.Bounds.Height / 2;
            ClearDropIndicator(dropSurface);
            if (!vm.CanDrag || e.DataTransfer.TryGetText() is not string raw) return;
            var ids = raw.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            if (before) vm.MoveBefore(ids, row.EntryId); else vm.MoveAfter(ids, row.EntryId);
            UpdateUi(); Reselect(ids); e.Handled = true;
        }, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, true);
        return dropSurface;
    }

    private static void SetDropIndicator(Border surface, bool before) =>
        surface.BorderThickness = before ? new(0, 2, 0, 0) : new(0, 0, 0, 2);

    private static void ClearDropIndicator(Border surface) => surface.BorderThickness = new(0);

    private async void OpenProfileManager(object? sender, RoutedEventArgs e)
    {
        var manager = new Window
        {
            Title = "Manage Profiles", Width = 700, Height = 480, MinWidth = 620, MinHeight = 420,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Icon = VanillaLauncherArt.ApplicationIcon
        };
        manager.DataContext = vm;
        var profileList = new ListBox
        {
            ItemsSource = vm.Profiles,
            ItemTemplate = new FuncDataTemplate<ManagerProfile>((profile, _) => new StackPanel
            {
                Margin = new(6),
                Children =
                {
                    new TextBlock { Text = profile?.DisplayName ?? string.Empty, FontWeight = FontWeight.SemiBold },
                    new TextBlock { Text = profile?.ProfileId ?? string.Empty, Opacity = 0.65 }
                }
            })
        };
        profileList.Bind(SelectingItemsControl.SelectedItemProperty, new Binding(nameof(vm.SelectedProfile)) { Mode = BindingMode.TwoWay });
        profileList.SelectedItem = vm.SelectedProfile;
        var actions = new WrapPanel { Orientation = Orientation.Horizontal };
        actions.Children.Add(Button("New", async (_, _) => await NewProfileAsync(manager), "Create a profile"));
        actions.Children.Add(Button("Duplicate", async (_, _) => await DuplicateProfileAsync(manager), "Duplicate the selected profile"));
        actions.Children.Add(Button("Rename", async (_, _) => await RenameProfileAsync(manager), "Rename the selected profile"));
        actions.Children.Add(Button("Delete", async (_, _) => await DeleteProfileAsync(manager), "Delete the selected ChoirLauncher profile"));
        actions.Children.Add(Button("Import", async (_, _) => await ImportProfileAsync(manager), "Import a profile JSON file"));
        actions.Children.Add(Button("Export", async (_, _) => await ExportProfileAsync(manager), "Export the selected profile without private paths"));
        actions.Children.Add(Button("Save", (_, _) => Save(manager), "Save the selected profile"));
        var close = Button("Close", (_, _) => manager.Close(), "Close profile manager");
        close.HorizontalAlignment = HorizontalAlignment.Right;
        var layout = new Grid { Margin = new(16), RowDefinitions = new("Auto,*,Auto,Auto"), RowSpacing = 10 };
        layout.Children.Add(new TextBlock { Text = "Select the active profile, or create and manage profile presets here.", TextWrapping = TextWrapping.Wrap });
        Grid.SetRow(profileList, 1); layout.Children.Add(profileList);
        Grid.SetRow(actions, 2); layout.Children.Add(actions);
        Grid.SetRow(close, 3); layout.Children.Add(close);
        manager.Content = layout;
        await manager.ShowDialog(this);
        UpdateUi();
    }

    private async Task NewProfileAsync(Window owner)
    {
        var name = await Dialogs.PromptAsync(owner, "New profile", "Profile name:", "New Profile"); if (string.IsNullOrWhiteSpace(name)) return;
        var type = await Dialogs.ChooseAsync(owner, "Profile source", "Choose initial profile content.", "Official State", "All Installed (preserve enabled)", "All Installed (disabled)", "Empty", "Cancel");
        var id = StableProfileId(name);
        if (type == "Official State") vm.CreateFromOfficial(id, name); else if (type == "All Installed (preserve enabled)") vm.CreateFromAll(id, name, true);
        else if (type == "All Installed (disabled)") vm.CreateFromAll(id, name, false); else if (type == "Empty") vm.CreateEmpty(id, name); UpdateUi();
    }

    private async Task DuplicateProfileAsync(Window owner) { var name = await Dialogs.PromptAsync(owner, "Duplicate profile", "New profile name:", (vm.CurrentProfile?.DisplayName ?? "Profile") + " Copy"); if (!string.IsNullOrWhiteSpace(name)) { vm.DuplicateCurrent(StableProfileId(name), name); UpdateUi(); } }
    private async Task RenameProfileAsync(Window owner)
    {
        if (DefaultProfilePolicy.IsDefault(vm.CurrentProfile))
        {
            await Dialogs.ShowAsync(owner, "Default profile", "The permanent Default profile cannot be renamed. Duplicate it to create a named profile.");
            return;
        }
        var name = await Dialogs.PromptAsync(owner, "Rename profile", "New profile name:", vm.CurrentProfile?.DisplayName ?? "");
        if (!string.IsNullOrWhiteSpace(name)) { vm.RenameCurrent(name); UpdateUi(); }
    }

    private async Task DeleteProfileAsync(Window owner)
    {
        if (DefaultProfilePolicy.IsDefault(vm.CurrentProfile))
        {
            await Dialogs.ShowAsync(owner, "Default profile", "The permanent Default profile cannot be deleted. Duplicate it to create a disposable profile.");
            return;
        }
        if (await Dialogs.ChooseAsync(owner, "Delete profile", "Delete this ChoirLauncher profile? Installed mod files are never deleted.", "Delete Profile", "Cancel") == "Delete Profile")
        {
            vm.DeleteCurrent();
            UpdateUi();
        }
    }

    private async Task ImportProfileAsync(Window owner)
    {
        var files = await owner.StorageProvider.OpenFilePickerAsync(new() { Title = "Import ChoirLauncher profile", AllowMultiple = false, FileTypeFilter = [new("JSON profile") { Patterns = ["*.json"] }] });
        if (files.Count == 0) return; try { vm.Import(files[0].Path.LocalPath); UpdateUi(); } catch (Exception ex) { await Dialogs.ShowAsync(owner, "Import failed", ex.Message); }
    }

    private async Task ExportProfileAsync(Window owner)
    {
        var file = await owner.StorageProvider.SaveFilePickerAsync(new() { Title = "Export ChoirLauncher profile", SuggestedFileName = (vm.CurrentProfile?.ProfileId ?? "profile") + ".json", DefaultExtension = "json" });
        if (file is null) return; try { var hash = vm.ExportCurrent(file.Path.LocalPath); await Dialogs.ShowAsync(owner, "Profile exported", $"SHA-256: {hash}"); } catch (Exception ex) { await Dialogs.ShowAsync(owner, "Export failed", ex.Message); }
    }

    private void Save() => Save(this);
    private void Save(Window owner) { try { vm.SaveCurrent(); UpdateUi(); } catch (Exception ex) { _ = Dialogs.ShowAsync(owner, "Save failed", ex.Message); } }

    private async Task ChangeEnabledAsync(bool enabled)
    {
        var ids = SelectedIds(); if (ids.Count == 0) return;
        var plan = vm.PlanStateChange(ids, enabled);
        if (enabled && plan.RequiredEntryIds.Count > 0)
        {
            var choice = await Dialogs.ChooseAsync(this, "Required dependencies", $"{plan.RequiredEntryIds.Count} required dependencies are disabled.", "Enable Required Dependencies", "Enable Only This Mod", "Cancel");
            if (choice == "Cancel" || choice is null) return;
            if (choice == "Enable Required Dependencies") ids = ids.Concat(plan.RequiredEntryIds).Distinct(StringComparer.Ordinal).ToArray();
        }
        else if (!enabled && plan.DependentEntryIds.Count > 0)
        {
            var choice = await Dialogs.ChooseAsync(this, "Enabled dependents", $"{plan.DependentEntryIds.Count} enabled mods depend on this selection.", "Disable Dependents Too", "Disable Only This Mod", "Cancel");
            if (choice == "Cancel" || choice is null) return;
            if (choice == "Disable Dependents Too") ids = ids.Concat(plan.DependentEntryIds).Distinct(StringComparer.Ordinal).ToArray();
        }
        vm.SetEnabled(ids, enabled); UpdateUi(); Reselect(ids);
    }

    private async void SuggestedOrder(object? sender, RoutedEventArgs e)
    {
        var changes = vm.PreviewSuggestedOrder();
        if (changes.Count == 0) { await Dialogs.ShowAsync(this, "Suggested order", "The current enabled order already satisfies the deterministic dependency suggestion."); return; }
        var text = string.Join('\n', changes.Select(x => $"{x.EntryId}: {x.OldPosition + 1} → {x.NewPosition + 1} ({x.Reason})"));
        if (await Dialogs.ChooseAsync(this, "Suggested-order preview", text + "\n\nThis does not claim semantic gameplay compatibility.", "Accept Suggested Order", "Cancel") == "Accept Suggested Order") { vm.AcceptSuggestedOrder(); UpdateUi(); }
    }

    private void CheckConflicts(object? sender, RoutedEventArgs e)
    {
        vm.CheckConflicts();
        UpdateUi();
        detailsTabs.SelectedIndex = 1;
    }

    private async void LaunchSongsOfSyx(object? sender, RoutedEventArgs e)
    {
        if (vm.IsBusy)
        {
            await Dialogs.ShowAsync(this, "Launch Songs of Syx", "ChoirLauncher is already scanning, applying, restoring, or launching. Wait for the current operation to finish.");
            return;
        }
        try
        {
            var action = (launchActionSelector.SelectedItem as Choice<LauncherLaunchAction>)?.Value ?? LauncherLaunchAction.ApplyProfileAndLaunch;
            if (action == LauncherLaunchAction.ApplyProfileAndLaunch)
            {
                var preview = vm.CreateApplyPreview();
                var developerMode = preferences.Load().LauncherDeveloperMode;
                if (preview.CurrentSha256 != preview.ProposedSha256 && !await ConfirmAndApplyAsync(preview, false, developerMode)) return;
                await StartGameAsync(GameLaunchRoute.DirectGame, true);
            }
            else if (action == LauncherLaunchAction.LaunchCurrentOfficialState)
            {
                await StartGameAsync(GameLaunchRoute.DirectGame, false);
            }
            else
            {
                await StartGameAsync(GameLaunchRoute.OfficialLauncher, false);
            }
        }
        catch (Exception ex) { await Dialogs.ShowAsync(this, "Launch blocked", ex.Message); }
    }

    private async Task StartGameAsync(GameLaunchRoute route, bool recordProfile)
    {
        var result = await vm.LaunchAsync(route, recordProfile);
        UpdateUi();
        if (!result.Success) await Dialogs.ShowAsync(this, "Launch blocked", string.Join('\n', result.Diagnostics));
    }

    private async void OpenGameSettings(object? sender, RoutedEventArgs e)
    {
        try { await new GameSettingsWindow(vm).ShowDialog<bool?>(this); }
        catch (Exception ex) when (ex is IOException or FormatException or InvalidDataException or UnauthorizedAccessException)
        { await Dialogs.ShowAsync(this, "Settings unavailable", ex.Message); }
        UpdateUi();
    }

    private async void OpenGameInfo(object? sender, RoutedEventArgs e)
    {
        try { await new GameInfoWindow(vm).ShowDialog(this); }
        catch (Exception ex) when (ex is IOException or FormatException or InvalidDataException or UnauthorizedAccessException)
        { await Dialogs.ShowAsync(this, "Game information unavailable", ex.Message); }
    }

    private async void OpenGameLanguage(object? sender, RoutedEventArgs e)
    {
        try { await new GameLanguageWindow(vm).ShowDialog<bool?>(this); }
        catch (Exception ex) when (ex is IOException or FormatException or InvalidDataException or UnauthorizedAccessException)
        { await Dialogs.ShowAsync(this, "Language selection unavailable", ex.Message); }
        UpdateUi();
    }

    private Button GameLanguageButton()
    {
        var button = Button(string.Empty, OpenGameLanguage, "Choose the Songs of Syx game language");
        button.Content = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 6,
            Children = { new Image { Source = VanillaLauncherArt.LanguageIcon(0), Width = 24, Height = 24 }, new TextBlock { Text = "Language", VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeight.Bold } }
        };
        AutomationProperties.SetName(button, "Choose Songs of Syx language");
        return button;
    }

    private async void CompareOfficial(object? sender, RoutedEventArgs e)
    {
        try
        {
            var comparison = vm.CompareOfficial();
            var text = comparison.EffectiveStatesIdentical ? "Effective enabled order is identical." : string.Join('\n', comparison.Differences.Where(x => x.Kind != OfficialDifferenceKind.MatchingPriority).Select(x => $"{x.Kind}: {x.Identity} — {x.Explanation}"));
            var action = await Dialogs.ChooseAsync(this, "Official-state comparison", text,
                "Create New Profile from Official State", "Replace Current Profile with Official State", "Merge Newly Discovered Mods", "Cancel");
            if (action == "Create New Profile from Official State")
            {
                var name = await Dialogs.PromptAsync(this, "New official-state profile", "Profile name:", "Official State " + DateTimeOffset.Now.ToString("yyyy-MM-dd"));
                if (!string.IsNullOrWhiteSpace(name)) vm.CreateFromOfficial(StableProfileId(name), name);
            }
            else if (action == "Replace Current Profile with Official State")
            {
                if (await Dialogs.ChooseAsync(this, "Confirm replacement", "Replace the current in-memory profile order and state with the current official state? This is undoable until the profile is switched.", "Replace", "Cancel") == "Replace") vm.ReplaceCurrentWithOfficial();
            }
            else if (action == "Merge Newly Discovered Mods")
            {
                var count = vm.MergeNewlyDiscoveredMods();
                await Dialogs.ShowAsync(this, "Merge complete", $"Appended {count} newly discovered installation(s) as disabled entries. Existing positions were unchanged.");
            }
            UpdateUi();
        }
        catch (Exception ex) { await Dialogs.ShowAsync(this, "Comparison failed", ex.Message); }
    }

    private async void PreviewApply(object? sender, RoutedEventArgs e)
    {
        try { await ConfirmAndApplyAsync(vm.CreateApplyPreview(), true); }
        catch (Exception ex) { await Dialogs.ShowAsync(this, "Apply failed", ex.Message); }
    }

    private async Task<bool> ConfirmAndApplyAsync(ApplyPreview preview, bool showSuccess, bool showPreview = true)
    {
        var summary = BuildApplySummary(preview);
        if (!preview.CanApplyTechnically) { await Dialogs.ShowAsync(this, "Apply blocked", summary); return false; }
        if (showPreview && await Dialogs.ChooseAsync(this, "Apply Preview", summary, "Apply", "Cancel") != "Apply") return false;
        string? acknowledgement = null;
        if (preview.RequiresCompatibilityAcknowledgement)
        {
            var warning = vm.CreateCompatibilityWarning(preview);
            if (await Dialogs.ChooseAsync(this, "Compatibility warning", warning, "OK") != "OK") return false;
            acknowledgement = preview.ConflictSignature;
        }
        var result = await vm.ApplyAsync(preview, acknowledgement);
        if (!result.Success || showSuccess)
            await Dialogs.ShowAsync(this, result.Success ? "Apply verified" : "Apply failed", result.Success ? $"Backup: {result.BackupId}\nFinal SHA-256: {result.FinalSha256}" : string.Join('\n', result.Diagnostics));
        UpdateUi();
        return result.Success;
    }

    private static string BuildApplySummary(ApplyPreview preview) => new StringBuilder()
        .AppendLine($"Target: {preview.TargetPath}").AppendLine($"Profile: {preview.ProfileName}")
        .AppendLine($"Current SHA-256: {preview.CurrentSha256}").AppendLine($"Proposed SHA-256: {preview.ProposedSha256}")
        .AppendLine($"Backup directory: {preview.BackupDirectory}").AppendLine().AppendLine("The official MODS array below is highest priority first; it is the reverse of the profile's low-to-high order.")
        .AppendLine("Current: " + string.Join(" -> ", preview.CurrentOrder)).AppendLine("Proposed: " + string.Join(" -> ", preview.ProposedOrder))
        .AppendLine($"Added: {string.Join(", ", preview.Added)}").AppendLine($"Removed: {string.Join(", ", preview.Removed)}").AppendLine($"Moved: {preview.Moved.Count}")
        .AppendLine($"Hard blockers: {preview.HardBlockers.Count}").AppendLine(string.Join('\n', preview.HardBlockers))
        .AppendLine($"Compatibility warnings: {preview.CompatibilityFindings.Count}")
        .AppendLine(preview.CompatibilityWarningAcknowledged ? "The current warning set was already acknowledged for this profile." : "A new warning set will be shown before Apply.").ToString();

    private async void RestoreBackup(object? sender, RoutedEventArgs e)
    {
        var backups = vm.Backups; if (backups.Count == 0) { await Dialogs.ShowAsync(this, "Restore backup", "No verified backups are available."); return; }
        var choiceText = string.Join('\n', backups.Select((b, i) => $"{i + 1}. {b.Metadata.CreatedUtc:u} — {b.Metadata.ProfileId} — {b.Metadata.OriginalSha256[..12]}…"));
        var input = await Dialogs.PromptAsync(this, "Restore backup", choiceText + "\n\nEnter the backup number to inspect and restore:", "1");
        if (!int.TryParse(input, out var index) || index < 1 || index > backups.Count) return;
        var backup = backups[index - 1];
        var currentHash = File.Exists(vm.Environment.LauncherSettingsPath) ? Hashing.Sha256File(vm.Environment.LauncherSettingsPath) : "missing";
        var currentOrder = File.Exists(vm.Environment.LauncherSettingsPath) ? LauncherSettingsDocument.Parse(File.ReadAllText(vm.Environment.LauncherSettingsPath)).EnabledMods : [];
        var backupOrder = LauncherSettingsDocument.Parse(File.ReadAllText(backup.DataPath)).EnabledMods;
        var added = currentOrder.Except(backupOrder, StringComparer.Ordinal).ToArray();
        var removed = backupOrder.Except(currentOrder, StringComparer.Ordinal).ToArray();
        var message = $"Backup: {backup.Metadata.CreatedUtc:u}\nOriginal: {backup.Metadata.OriginalSha256}\nProposed: {backup.Metadata.ProposedSha256}\nCurrent: {currentHash}\nSize: {backup.Metadata.Size}\nResult: {backup.Metadata.Result}\n\nCurrent order: {string.Join(" → ", currentOrder)}\nBackup order: {string.Join(" → ", backupOrder)}\nOnly current: {string.Join(", ", added)}\nOnly backup: {string.Join(", ", removed)}";
        var action = await Dialogs.ChooseAsync(this, "Inspect backup difference", message, "Restore", "Delete Old Backup", "Cancel");
        if (action == "Restore")
        {
            var result = await vm.RestoreAsync(backup); await Dialogs.ShowAsync(this, result.Success ? "Restore verified" : "Restore failed", result.Success ? "The original SHA-256 was reproduced." : string.Join('\n', result.Diagnostics));
        }
        else if (action == "Delete Old Backup") { try { vm.DeleteBackup(backup); } catch (Exception ex) { await Dialogs.ShowAsync(this, "Delete blocked", ex.Message); } }
        UpdateUi();
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        var control = e.KeyModifiers.HasFlag(KeyModifiers.Control); var alt = e.KeyModifiers.HasFlag(KeyModifiers.Alt); var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        if (control && e.Key == Key.S) Save();
        else if (control && e.Key == Key.Z && !shift) { vm.Undo(); UpdateUi(); Reselect(vm.LastEditSelection); }
        else if ((control && e.Key == Key.Y) || (control && shift && e.Key == Key.Z)) { vm.Redo(); UpdateUi(); Reselect(vm.LastEditSelection); }
        else if (control && e.Key == Key.A) list.SelectAll();
        else if (e.Key == Key.Space) _ = ChangeEnabledAsync(SelectedRows().Any(x => !x.Enabled));
        else if (alt && e.Key == Key.Up) MoveSelected(vm.MoveUp);
        else if (alt && e.Key == Key.Down) MoveSelected(vm.MoveDown);
        else if (control && e.Key == Key.Home) MoveSelected(vm.MoveTop);
        else if (control && e.Key == Key.End) MoveSelected(vm.MoveBottom);
        else if (e.Key == Key.Delete) _ = ConfirmRemoveAsync();
        else if (e.Key == Key.F5) _ = vm.RefreshInstallationsAsync();
        else return;
        e.Handled = true; UpdateUi();
    }

    private async Task ConfirmRemoveAsync()
    {
        if (SelectedIds().Count == 0) return;
        var action = await Dialogs.ChooseAsync(this, "Remove from profile",
            "Remove the selected mod entries from the current profile? Their installed local or Workshop files will not be deleted. You can restore them later with Add Removed Mods Back.",
            "Remove from Profile", "Cancel");
        if (action == "Remove from Profile") { vm.Remove(SelectedIds()); UpdateUi(); }
    }

    private async void RestoreRemovedMods(object? sender, RoutedEventArgs e)
    {
        var removed = vm.RemovedProfileEntries;
        if (removed.Count == 0)
        {
            await Dialogs.ShowAsync(this, "Add removed mods back", "This profile has no removed mod entries to restore.");
            return;
        }

        var choices = string.Join('\n', removed.Select((entry, index) =>
            $"{index + 1}. {entry.LogicalModId} — {entry.Source}:{entry.SourceId} — previously {(entry.Enabled ? "enabled" : "disabled")}"));
        var input = await Dialogs.PromptAsync(this, "Add removed mods back",
            choices + "\n\nEnter one or more numbers separated by commas, or ALL. Restored entries are appended at the highest profile priority and keep their previous enabled state.", "ALL");
        if (string.IsNullOrWhiteSpace(input)) return;

        IReadOnlyList<ManagerProfileEntry> selected;
        if (input.Trim().Equals("ALL", StringComparison.OrdinalIgnoreCase)) selected = removed;
        else
        {
            var indexes = input.Split([',', ';', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries)
                .Select(value => int.TryParse(value, out var index) ? index - 1 : -1).Distinct().ToArray();
            if (indexes.Length == 0 || indexes.Any(index => index < 0 || index >= removed.Count))
            {
                await Dialogs.ShowAsync(this, "Add removed mods back", "Enter valid list numbers separated by commas, or ALL.");
                return;
            }
            selected = indexes.Select(index => removed[index]).ToArray();
        }

        var ids = selected.Select(entry => entry.EntryId).ToArray();
        vm.RestoreRemoved(ids);
        UpdateUi();
        Reselect(ids);
    }

    private void OpenSelectedFolder(object? sender, RoutedEventArgs e)
    {
        var path = SelectedRows().FirstOrDefault()?.Resolution.Installation?.RootPath;
        if (path is null || !Directory.Exists(path)) { _ = Dialogs.ShowAsync(this, "Open folder", "The selected entry has no resolved installation folder."); return; }
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
    }

    private void OpenSelectedWorkshop(object? sender, RoutedEventArgs e)
    {
        var row = SelectedRows().FirstOrDefault();
        if (row is null || !row.IsWorkshop || !long.TryParse(row.Entry.SourceId, out _)) { _ = Dialogs.ShowAsync(this, "Workshop page", "The selected entry has no valid Workshop ID."); return; }
        Process.Start(new ProcessStartInfo($"https://steamcommunity.com/sharedfiles/filedetails/?id={row.Entry.SourceId}") { UseShellExecute = true });
    }

    private async void RelinkSelected(object? sender, RoutedEventArgs e)
    {
        var row = SelectedRows().SingleOrDefault();
        if (row is null) { await Dialogs.ShowAsync(this, "Choose installed copy", "Select exactly one mod entry from the current profile."); return; }
        var candidates = row.Resolution.Candidates.Count > 0 ? row.Resolution.Candidates : vm.Installations;
        var listText = string.Join('\n', candidates.Select((x, i) => $"{i + 1}. {x.Metadata.Name} — {x.Source}:{x.SourceId} — {x.ContentFingerprint[..12]}…"));
        var input = await Dialogs.PromptAsync(this, "Choose installed copy",
            "This changes which discovered installation the profile entry points to. It does not move, copy, enable, disable, or delete mod files.\n\n" +
            listText + "\n\nEnter the installed-copy number:", "1");
        if (int.TryParse(input, out var index) && index >= 1 && index <= candidates.Count) { vm.Relink(row.EntryId, candidates[index - 1]); UpdateUi(); }
    }

    private async void EditSelectedNotes(object? sender, RoutedEventArgs e)
    {
        var row = SelectedRows().SingleOrDefault();
        if (row is null) { await Dialogs.ShowAsync(this, "Edit profile notes", "Select exactly one mod entry from the current profile."); return; }
        var notes = await Dialogs.PromptAsync(this, "Edit profile notes", "Notes stored only in this ChoirLauncher profile:", row.Entry.Notes ?? "");
        if (notes is not null) { vm.SetNotes(row.EntryId, notes); UpdateUi(); }
    }
    private void MoveSelected(Action<IReadOnlyCollection<string>> action) { var ids = SelectedIds(); if (ids.Count > 0) action(ids); UpdateUi(); Reselect(ids); }
    private IReadOnlyList<ModRowViewModel> SelectedRows() => list.SelectedItems?.OfType<ModRowViewModel>().ToArray() ?? [];
    private IReadOnlyList<string> SelectedIds() => SelectedRows().Select(x => x.EntryId).ToArray();
    private void SelectRowIfNeeded(ModRowViewModel row) { if (!(list.SelectedItems?.Contains(row) ?? false)) { list.SelectedItems?.Clear(); list.SelectedItems?.Add(row); } }
    private void Reselect(IEnumerable<string> entryIds)
    {
        var wanted = entryIds.ToHashSet(StringComparer.Ordinal);
        if (wanted.Count == 0 || list.SelectedItems is null) return;
        list.SelectedItems.Clear();
        foreach (var row in vm.VisibleRows.Where(x => wanted.Contains(x.EntryId))) list.SelectedItems.Add(row);
    }

    private void UpdateSelectionDetails()
    {
        var selected = SelectedRows(); selectedCount.Text = $"{selected.Count} selected";
        var row = selected.FirstOrDefault();
        var relevantConflicts = ConflictsForRows(selected);
        conflictList.ItemsSource = relevantConflicts;
        conflictContext.Text = selected.Count switch
        {
            0 => $"General conflict report — {relevantConflicts.Count} finding(s) across the active profile.",
            1 => $"Conflict report for {row!.Name} — {relevantConflicts.Count} finding(s).",
            _ => $"Conflict report for {selected.Count} selected mods — {relevantConflicts.Count} finding(s)."
        };
        conflictContext.Foreground = relevantConflicts.Any(x => x.Severity is Severity.Blocking or Severity.High)
            ? Brushes.IndianRed : relevantConflicts.Count > 0 ? Brushes.DarkOrange : Moss;
        details.Content = row is null
            ? new TextBlock { Text = "Select a mod to inspect its information, dependencies, notes, and conflicts.", TextWrapping = TextWrapping.Wrap, Opacity = 0.75 }
            : BuildDetailsView(row, relevantConflicts);
    }

    private Control BuildDetailsView(ModRowViewModel row, IReadOnlyList<Conflict> conflicts)
    {
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(new TextBlock
        {
            Text = row.Name,
            FontSize = 22,
            FontWeight = FontWeight.Bold,
            Foreground = Parchment,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            TextWrapping = TextWrapping.Wrap
        });
        panel.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(row.Description) ? "No description was provided by this mod." : row.Description,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.82,
            Margin = new(8, 0, 8, 4)
        });

        var metadata = new WrapPanel { Orientation = Orientation.Horizontal };
        metadata.Children.Add(MetadataTile("Author", string.IsNullOrWhiteSpace(row.Author) ? "Not provided" : row.Author));
        metadata.Children.Add(MetadataTile("Version", row.Version));
        metadata.Children.Add(MetadataTile("Source", $"{row.Source} / {row.Entry.SourceId}"));
        metadata.Children.Add(MetadataTile("Logical ID", row.LogicalId));
        panel.Children.Add(metadata);

        var compatibilityBadges = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var label in GameCompatibilityLabels(row)) compatibilityBadges.Children.Add(StatusBadge(label, Bronze));
        panel.Children.Add(SectionCard("Compatible game versions", compatibilityBadges, Bronze));

        var manifest = row.Resolution.Installation?.Manifest;
        var dependencyPanel = new StackPanel { Spacing = 6 };
        dependencyPanel.Children.Add(StatusBadge(row.DependencyStatus,
            row.DependencyStatus.Equals("OK", StringComparison.OrdinalIgnoreCase) ? Moss : Brushes.Orange));
        dependencyPanel.Children.Add(LabeledText("Required", FormatDependencies(manifest?.Required)));
        dependencyPanel.Children.Add(LabeledText("Optional", FormatDependencies(manifest?.Optional)));
        dependencyPanel.Children.Add(LabeledText("Cannot be used with", FormatValues(manifest?.Incompatible)));
        panel.Children.Add(SectionCard("Dependency status", dependencyPanel,
            row.DependencyStatus.Equals("OK", StringComparison.OrdinalIgnoreCase) ? Forest : Brushes.DarkOrange));

        panel.Children.Add(SectionCard("Profile notes",
            new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(row.Entry.Notes) ? "No notes have been added for this profile." : row.Entry.Notes,
                TextWrapping = TextWrapping.Wrap,
                Opacity = string.IsNullOrWhiteSpace(row.Entry.Notes) ? 0.7 : 1
            }, Bronze));

        var conflictPanel = new StackPanel { Spacing = 4 };
        if (conflicts.Count == 0)
            conflictPanel.Children.Add(new TextBlock { Text = "No conflicts found for this mod.", Foreground = Moss, TextWrapping = TextWrapping.Wrap });
        else
            foreach (var conflict in conflicts) conflictPanel.Children.Add(CreateConflictCard(conflict));
        panel.Children.Add(SectionCard("Conflict relationships", conflictPanel,
            conflicts.Count == 0 ? Forest : conflicts.Any(x => x.Severity is Severity.Blocking or Severity.High) ? Brushes.IndianRed : Brushes.DarkOrange));

        var technical = new StackPanel { Spacing = 5 };
        technical.Children.Add(LabeledText("Installed copy", row.Entry.InstallationIdHint ?? "Unresolved"));
        technical.Children.Add(LabeledText("Content fingerprint", row.Entry.ExpectedContentFingerprint ?? "Unknown"));
        technical.Children.Add(LabeledText("Declaration type", row.Declaration));
        technical.Children.Add(LabeledText("Resolution state", row.State));
        technical.Children.Add(LabeledText("Choir features used", FormatValues(manifest?.Capabilities)));
        technical.Children.Add(LabeledText("Runtime JARs", FormatValues(row.Resolution.Installation?.Jars.Select(x => x.FileName))));
        panel.Children.Add(new Expander
        {
            Header = "Technical metadata",
            IsExpanded = false,
            Content = new Border
            {
                Padding = new(10), Margin = new(0, 4, 0, 0), CornerRadius = new(4),
                Background = new SolidColorBrush(Color.FromArgb(54, 64, 48, 33)),
                Child = technical
            }
        });
        return panel;
    }

    private static Border MetadataTile(string label, string value) => new()
    {
        Width = 210,
        MinHeight = 64,
        Margin = new(3),
        Padding = new(9),
        CornerRadius = new(6),
        Background = new SolidColorBrush(Color.FromArgb(72, 76, 58, 38)),
        Child = new StackPanel
        {
            Spacing = 3,
            Children =
            {
                new TextBlock { Text = label, FontWeight = FontWeight.SemiBold, Foreground = Parchment },
                new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap }
            }
        }
    };

    private static Border SectionCard(string title, Control content, IBrush accent) => new()
    {
        Padding = new(10),
        CornerRadius = new(6),
        BorderThickness = new(3, 0, 0, 0),
        BorderBrush = accent,
        Background = new SolidColorBrush(Color.FromArgb(54, 64, 48, 33)),
        Child = new StackPanel
        {
            Spacing = 7,
            Children =
            {
                new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeight.Bold, Foreground = accent },
                content
            }
        }
    };

    private static Border StatusBadge(string text, IBrush accent) => new()
    {
        Margin = new(3),
        Padding = new(9, 4),
        CornerRadius = new(12),
        BorderThickness = new(1),
        BorderBrush = accent,
        Background = new SolidColorBrush(Color.FromArgb(65, 81, 62, 39)),
        Child = new TextBlock { Text = text, Foreground = accent, FontWeight = FontWeight.SemiBold }
    };

    private static TextBlock LabeledText(string label, string value) => new()
    {
        Text = $"{label}: {value}",
        TextWrapping = TextWrapping.Wrap
    };

    private static string FormatDependencies(IEnumerable<DependencySpec>? dependencies)
    {
        var values = dependencies?.Select(item => $"{item.ModId} ({item.Constraint})").ToArray() ?? [];
        return values.Length == 0 ? "None declared" : string.Join(", ", values);
    }

    private static string FormatValues(IEnumerable<string>? values)
    {
        var materialized = values?.Where(value => !string.IsNullOrWhiteSpace(value)).ToArray() ?? [];
        return materialized.Length == 0 ? "None declared" : string.Join(", ", materialized);
    }

    private static IReadOnlyList<string> GameCompatibilityLabels(ModRowViewModel row)
    {
        var installation = row.Resolution.Installation;
        if (!string.IsNullOrWhiteSpace(installation?.Manifest?.GameVersionRange)) return [installation.Manifest.GameVersionRange!];
        if (installation?.Metadata.GameVersionMajor is int major && major > 0)
            return [installation.Metadata.GameVersionMinor is int minor ? $"V{major}.{minor}" : $"V{major}"];
        return [row.Compatibility];
    }

    private IReadOnlyList<Conflict> ConflictsForRows(IReadOnlyList<ModRowViewModel> rows)
    {
        if (rows.Count == 0) return vm.Conflicts;
        var identities = rows.SelectMany(RowIdentities).ToHashSet(StringComparer.Ordinal);
        return vm.Conflicts.Where(conflict => conflict.InvolvedMods.Any(identities.Contains)).ToArray();
    }

    private string FormatConflictRelationships(ModRowViewModel row, IReadOnlyList<Conflict> conflicts)
    {
        if (conflicts.Count == 0) return "None detected.";
        var own = RowIdentities(row).ToHashSet(StringComparer.Ordinal);
        return string.Join("\n\n", conflicts.Select(conflict =>
        {
            var others = conflict.InvolvedMods.Where(id => !own.Contains(id)).Select(DisplayIdentity).Distinct(StringComparer.Ordinal).ToArray();
            var relationship = conflict.CurrentWinner is null
                ? "No deterministic winner."
                : own.Contains(conflict.CurrentWinner)
                    ? $"Overrides: {(others.Length == 0 ? "another contribution" : string.Join(", ", others))}."
                    : $"Overridden by: {DisplayIdentity(conflict.CurrentWinner)}.";
            return $"[{conflict.Severity}] {conflict.Category}: {conflict.Target}\n{relationship}\n{conflict.Explanation}";
        }));
    }

    private IEnumerable<string> RowIdentities(ModRowViewModel row)
    {
        yield return row.EntryId;
        yield return row.LogicalId;
        yield return row.Entry.SourceId;
        if (row.Entry.InstallationIdHint is { Length: > 0 } hint) yield return hint;
        if (row.Resolution.Installation?.InstallationId is { Length: > 0 } installationId) yield return installationId;
    }

    private string DisplayIdentity(string identity)
    {
        var installation = vm.Installations.FirstOrDefault(item => item.InstallationId == identity || item.LogicalModId == identity || item.SourceId == identity);
        return installation?.Metadata.Name is { Length: > 0 } name ? name : identity;
    }

    private Control CreateConflictCard(Conflict? conflict)
    {
        if (conflict is null) return new TextBlock();
        var accent = conflict.Severity switch
        {
            Severity.Blocking or Severity.High => Brushes.IndianRed,
            Severity.Medium => Brushes.DarkOrange,
            _ => Brushes.Goldenrod
        };
        return new Border
        {
            Margin = new(4), Padding = new(8), BorderThickness = new(3, 0, 0, 0), BorderBrush = accent,
            Child = new TextBlock
            {
                Text = $"{conflict.Severity.ToString().ToUpperInvariant()} / {conflict.Confidence.ToString().ToUpperInvariant()}\n{conflict.Category}: {conflict.Target}\nInvolved: {string.Join(", ", conflict.InvolvedMods.Select(DisplayIdentity))}\nWinner: {(conflict.CurrentWinner is null ? "none" : DisplayIdentity(conflict.CurrentWinner))}\n{conflict.Explanation}\n{conflict.RecommendedAction}",
                TextWrapping = TextWrapping.Wrap, Foreground = accent
            }
        };
    }

    private void UpdateUi()
    {
        launchButton.IsEnabled = vm.LaunchEnabled;
        ToolTip.SetTip(launchButton, vm.LaunchExplanation);
        saveButton.Content = vm.IsDirty ? "Save ●" : "Save"; undoButton.IsEnabled = vm.CanUndo; redoButton.IsEnabled = vm.CanRedo;
        status.Text = vm.Status; progress.IsVisible = vm.IsBusy; progress.Value = vm.Progress;
        conflictSummary.Text = $"Conflicts: {vm.Conflicts.Count} ({vm.BlockingConflictCount} blocking)";
        conflictSummary.Foreground = vm.BlockingConflictCount > 0 ? Brushes.IndianRed : vm.Conflicts.Count > 0 ? Brushes.DarkOrange : Moss;
        backupList.ItemsSource = vm.Backups;
        UpdateSelectionDetails();
        effectiveOrderContent.Content = BuildEffectiveOrderView();
    }

    private Control BuildEffectiveOrderView()
    {
        var profile = vm.CurrentProfile;
        if (profile is null) return new TextBlock { Text = "No profile is loaded.", Opacity = 0.75 };

        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(new TextBlock
        {
            Text = "Profile priority",
            FontSize = 20,
            FontWeight = FontWeight.Bold,
            Foreground = Parchment,
            TextAlignment = TextAlignment.Center
        });
        panel.Children.Add(new TextBlock
        {
            Text = "Priority 1 is lowest. Every larger number is higher priority. ChoirLauncher reverses the enabled profile order when writing Songs of Syx because its official MODS array is highest-first.",
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            Opacity = 0.78
        });

        var complete = new StackPanel { Spacing = 4 };
        foreach (var item in profile.Mods.Select((entry, index) => (entry, index)))
            complete.Children.Add(OrderRow(item.index + 1, DisplayProfileEntry(item.entry),
                item.entry.Enabled ? "Enabled" : "Disabled", item.entry.Enabled ? Moss : Iron));
        if (profile.Mods.Count == 0) complete.Children.Add(new TextBlock { Text = "This profile has no mod entries.", Opacity = 0.7 });
        panel.Children.Add(SectionCard("Complete profile order — lowest to highest", complete, Bronze));

        var effective = new StackPanel { Spacing = 4 };
        var enabled = profile.Mods.Where(entry => entry.Enabled).Reverse().ToArray();
        foreach (var item in enabled.Select((entry, index) => (entry, index)))
            effective.Children.Add(OrderRow(item.index + 1, DisplayProfileEntry(item.entry), item.entry.SourceId, Moss));
        if (enabled.Length == 0) effective.Children.Add(new TextBlock { Text = "No mods are enabled in this profile.", Opacity = 0.7 });
        panel.Children.Add(SectionCard("Official MODS order — highest priority first", effective, Forest));
        return panel;
    }

    private string DisplayProfileEntry(ManagerProfileEntry entry)
    {
        var row = vm.Rows.FirstOrDefault(candidate => candidate.EntryId == entry.EntryId);
        return row is null ? entry.LogicalModId : $"{row.Name}  ·  {entry.LogicalModId}";
    }

    private static Border OrderRow(int position, string name, string state, IBrush accent)
    {
        var grid = new Grid { ColumnDefinitions = new("42,*,Auto"), ColumnSpacing = 8 };
        grid.Children.Add(new TextBlock
        {
            Text = position.ToString(),
            FontWeight = FontWeight.Bold,
            Foreground = accent,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Center
        });
        var nameText = new TextBlock { Text = name, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(nameText, 1); grid.Children.Add(nameText);
        var badge = StatusBadge(state, accent); Grid.SetColumn(badge, 2); grid.Children.Add(badge);
        return new Border
        {
            Padding = new(6, 3),
            CornerRadius = new(4),
            Background = new SolidColorBrush(Color.FromArgb(42, 74, 55, 35)),
            Child = grid
        };
    }

    private ColumnDefinitions CreateModListColumns()
    {
        GridLength[] defaults =
        [
            new(38), new(42), new(55), new(2, GridUnitType.Star), new(1.4, GridUnitType.Star),
            new(90), new(70), new(80), new(105), new(80)
        ];
        var definitions = new ColumnDefinitions();
        for (var i = 0; i < ModListColumnCount; i++)
        {
            definitions.Add(new ColumnDefinition
            {
                Width = modListColumnWidths is { Length: ModListColumnCount } ? new(modListColumnWidths[i]) : defaults[i],
                MinWidth = MinimumColumnWidths[i]
            });
            if (i < ModListColumnCount - 1) definitions.Add(new ColumnDefinition { Width = new(ColumnSeparatorWidth) });
        }
        return definitions;
    }

    private void AddHeaderSplitters(Grid header)
    {
        for (var logicalColumn = 0; logicalColumn < ModListColumnCount - 1; logicalColumn++)
        {
            var splitter = new GridSplitter
            {
                ResizeDirection = GridResizeDirection.Columns,
                ResizeBehavior = GridResizeBehavior.PreviousAndNext,
                ShowsPreview = false,
                DragIncrement = 1,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Margin = new(1, 0),
                Background = new SolidColorBrush(Color.FromArgb(185, 178, 138, 82)),
                Cursor = new Cursor(StandardCursorType.SizeWestEast)
            };
            ToolTip.SetTip(splitter, "Drag to resize adjacent columns");
            Grid.SetColumn(splitter, logicalColumn * 2 + 1);
            splitter.DragDelta += (_, _) => Dispatcher.UIThread.Post(SyncModListColumnWidths, DispatcherPriority.Render);
            splitter.DragCompleted += (_, _) => SyncModListColumnWidths();
            header.Children.Add(splitter);
        }
    }

    private static void AddRowColumnSeparators(Grid row)
    {
        for (var logicalColumn = 0; logicalColumn < ModListColumnCount - 1; logicalColumn++)
        {
            var separator = new Border
            {
                Width = 1,
                HorizontalAlignment = HorizontalAlignment.Center,
                Background = new SolidColorBrush(Color.FromArgb(72, 196, 171, 124)),
                IsHitTestVisible = false
            };
            Grid.SetColumn(separator, logicalColumn * 2 + 1);
            row.Children.Add(separator);
        }
    }

    private void SyncModListColumnWidths()
    {
        if (fittingColumnWidths || columnHeader.ColumnDefinitions.Count < ModListColumnCount * 2 - 1) return;
        var widths = Enumerable.Range(0, ModListColumnCount)
            .Select(index => columnHeader.ColumnDefinitions[index * 2].ActualWidth)
            .ToArray();
        if (widths.Where((width, index) => !double.IsFinite(width) || width < MinimumColumnWidths[index]).Any()) return;
        ApplyColumnWidths(FitColumnWidths(widths, listLayout.Bounds.Width));
    }

    private void FitColumnsToViewport(double viewportWidth)
    {
        if (fittingColumnWidths || viewportWidth <= 0 || columnHeader.ColumnDefinitions.Count < ModListColumnCount * 2 - 1) return;
        var widths = modListColumnWidths is { Length: ModListColumnCount }
            ? modListColumnWidths.ToArray()
            : Enumerable.Range(0, ModListColumnCount).Select(index => columnHeader.ColumnDefinitions[index * 2].ActualWidth).ToArray();
        if (widths.Where((width, index) => !double.IsFinite(width) || width < MinimumColumnWidths[index]).Any()) return;
        ApplyColumnWidths(FitColumnWidths(widths, viewportWidth));
    }

    private static double[] FitColumnWidths(IReadOnlyList<double> requested, double viewportWidth)
    {
        var separatorSpace = ColumnSeparatorWidth * (ModListColumnCount - 1);
        var available = Math.Max(MinimumColumnWidths.Sum(), viewportWidth - separatorSpace - 8);
        var total = requested.Sum();
        if (total <= available + 0.5) return requested.ToArray();

        var minimumTotal = MinimumColumnWidths.Sum();
        var availableSlack = Math.Max(0, available - minimumTotal);
        var requestedSlack = Enumerable.Range(0, ModListColumnCount)
            .Sum(index => Math.Max(0, requested[index] - MinimumColumnWidths[index]));
        if (requestedSlack <= 0) return MinimumColumnWidths.ToArray();

        var scale = Math.Min(1, availableSlack / requestedSlack);
        return Enumerable.Range(0, ModListColumnCount)
            .Select(index => MinimumColumnWidths[index] + Math.Max(0, requested[index] - MinimumColumnWidths[index]) * scale)
            .ToArray();
    }

    private void ApplyColumnWidths(double[] widths)
    {
        fittingColumnWidths = true;
        try
        {
            modListColumnWidths = widths;
            for (var column = 0; column < ModListColumnCount; column++)
                columnHeader.ColumnDefinitions[column * 2].Width = new GridLength(widths[column]);
            for (var index = rowGrids.Count - 1; index >= 0; index--)
            {
                if (!rowGrids[index].TryGetTarget(out var row)) { rowGrids.RemoveAt(index); continue; }
                for (var column = 0; column < ModListColumnCount; column++)
                    row.ColumnDefinitions[column * 2].Width = new GridLength(widths[column]);
            }
        }
        finally { fittingColumnWidths = false; }
    }

    private void RestoreWindowPreferences()
    {
        var saved = preferences.Load();
        if (saved.WindowWidth is >= 1100 and <= 8000) Width = saved.WindowWidth.Value;
        if (saved.WindowHeight is >= 680 and <= 8000) Height = saved.WindowHeight.Value;
        if (saved.WindowX is >= -32000 and <= 32000 && saved.WindowY is >= -32000 and <= 32000)
        {
            Position = new PixelPoint((int)saved.WindowX.Value, (int)saved.WindowY.Value);
            WindowStartupLocation = WindowStartupLocation.Manual;
        }
        if (saved.Maximized) WindowState = WindowState.Maximized;
        if (saved.ModListColumnWidths is { Count: ModListColumnCount } widths &&
            widths.Where((width, index) => !double.IsFinite(width) || width < MinimumColumnWidths[index] || width > 4000).Any() == false)
            modListColumnWidths = widths.ToArray();
    }

    private void SaveWindowPreferences()
    {
        var maximized = WindowState == WindowState.Maximized;
        var saved = preferences.Load() with
        {
            LastProfileId = vm.CurrentProfile?.ProfileId,
            WindowWidth = Width,
            WindowHeight = Height,
            WindowX = Position.X,
            WindowY = Position.Y,
            Maximized = maximized,
            ModListColumnWidths = modListColumnWidths
        };
        try { preferences.Save(saved); } catch (IOException) { }
    }

    private void SaveLaunchPreference()
    {
        if (launchActionSelector.SelectedItem is not Choice<LauncherLaunchAction> selected) return;
        try { preferences.Save(preferences.Load() with { DefaultLaunchAction = selected.Value }); }
        catch (IOException) { }
    }

    private static Button Button(string text, EventHandler<RoutedEventArgs> handler, string automationName)
    {
        var button = new Button { Content = text, Margin = new(3), Padding = new(10, 5) }; button.Click += handler; AutomationProperties.SetName(button, automationName); ToolTip.SetTip(button, automationName); return button;
    }
    private static Border ToolbarGroup(string automationName, Color accent, params Button[] buttons)
    {
        var buttonBrush = new SolidColorBrush(Color.FromArgb(220, accent.R, accent.G, accent.B));
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 1 };
        foreach (var button in buttons)
        {
            button.Background = buttonBrush;
            button.Foreground = Parchment;
            panel.Children.Add(button);
        }
        var group = new Border
        {
            Margin = new(0, 0, 8, 0),
            Padding = new(2),
            CornerRadius = new(5),
            BorderThickness = new(1),
            BorderBrush = new SolidColorBrush(accent),
            Background = new SolidColorBrush(Color.FromArgb(92, accent.R, accent.G, accent.B)),
            Child = panel
        };
        AutomationProperties.SetName(group, automationName);
        return group;
    }
    private static Border Surface(Control content, IBrush accent) => new()
    {
        Padding = new(8),
        CornerRadius = new(7),
        BorderBrush = accent,
        BorderThickness = new(1),
        Background = new SolidColorBrush(Color.FromArgb(222, 30, 24, 18)),
        ClipToBounds = true,
        Child = content
    };
    private static IBrush StateAccent(string state) => state switch
    {
        "RESOLVED" => Moss,
        "AMBIGUOUS" => Brushes.Orange,
        "MISSING" => Clay,
        _ => Iron
    };
    private static IBrush ConflictAccent(Severity? severity) => severity switch
    {
        Severity.Blocking or Severity.High => Clay,
        Severity.Medium => Brushes.Orange,
        Severity.Low or Severity.Informational => Brushes.Gold,
        _ => Moss
    };
    private static TextBlock Text(string value, FontWeight? weight = null, IBrush? foreground = null)
    {
        var block = new TextBlock
        {
            Text = value,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            FontWeight = weight ?? FontWeight.Normal
        };
        if (foreground is not null) block.Foreground = foreground;
        return block;
    }
    private static void Add(Grid grid, Control control, int column) { Grid.SetColumn(control, column * 2); grid.Children.Add(control); }
    private static void AddHeader(Grid grid, string text, int column) => Add(grid, new TextBlock { Text = text, FontWeight = FontWeight.Bold, Foreground = Parchment, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis }, column);
    private static T At<T>(T control, int row) where T : Control { Grid.SetRow(control, row); return control; }
    private static string StableProfileId(string name)
    {
        var safe = new string(name.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '-').ToArray()).Trim('-');
        return (safe.Length == 0 ? "profile" : safe) + "-" + DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss");
    }
}
