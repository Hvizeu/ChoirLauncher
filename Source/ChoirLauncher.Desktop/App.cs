using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace ChoirLauncher.Desktop;

public sealed class App : Application
{
    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        RequestedThemeVariant = ThemeVariant.Dark;
        Resources["SystemAccentColor"] = Color.FromRgb(164, 119, 61);
        Resources["SystemAccentColorLight1"] = Color.FromRgb(186, 143, 84);
        Resources["SystemAccentColorLight2"] = Color.FromRgb(207, 170, 112);
        Resources["SystemAccentColorLight3"] = Color.FromRgb(226, 199, 151);
        Resources["SystemAccentColorDark1"] = Color.FromRgb(132, 91, 45);
        Resources["SystemAccentColorDark2"] = Color.FromRgb(101, 68, 34);
        Resources["SystemAccentColorDark3"] = Color.FromRgb(71, 47, 25);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new MainWindow();
        base.OnFrameworkInitializationCompleted();
    }
}
