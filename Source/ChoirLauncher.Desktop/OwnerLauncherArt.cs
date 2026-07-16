using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace ChoirLauncher.Desktop;

internal static class OwnerLauncherArt
{
    internal const string BackgroundUri = "avares://ChoirLauncher/Assets/OwnerLauncherBackground.png";

    internal static Bitmap CityBackground { get; } = LoadBackground();

    private static Bitmap LoadBackground()
    {
        using var stream = AssetLoader.Open(new Uri(BackgroundUri));
        return new Bitmap(stream);
    }
}
