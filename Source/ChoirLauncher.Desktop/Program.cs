using Avalonia;
using Avalonia.Fonts.Inter;
using ChoirLauncher.Core;

namespace ChoirLauncher.Desktop;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        var log = new ApplicationLog(ManagerStoragePaths.Resolve());
        log.Write("INFO", "desktop-bootstrap", $"version={BuildInfo.Version} renderer=avalonia-platform-default");
        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            log.Write("INFO", "desktop-shutdown", "classic desktop lifetime exited normally");
        }
        catch (Exception ex)
        {
            log.Write("ERROR", "desktop-startup-failed", ex.ToString());
            throw;
        }
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .WithInterFont()
        .LogToTrace();
}
