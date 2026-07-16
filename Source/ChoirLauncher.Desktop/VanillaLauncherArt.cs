using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace ChoirLauncher.Desktop;

internal static class VanillaLauncherArt
{
    internal const string AtlasUri = "avares://ChoirLauncher/Assets/VanillaLauncherSprites.png";
    internal const string ApplicationIconUri = "avares://ChoirLauncher/Assets/SongsOfSyxIcon64.png";

    // The six-pixel magenta gutters in the source atlas are deliberately excluded.
    internal static readonly PixelRect SongsOfSyxLogoRect = new(6, 76, 384, 64);
    internal static readonly PixelRect OrnamentalDividerRect = new(6, 184, 464, 16);
    internal const int LanguageIconCount = 17;

    private static readonly Bitmap Atlas = LoadAtlas();

    internal static WindowIcon ApplicationIcon { get; } = LoadApplicationIcon();
    internal static IImage SongsOfSyxLogo { get; } = Crop(SongsOfSyxLogoRect);
    internal static IImage OrnamentalDivider { get; } = Crop(OrnamentalDividerRect);

    internal static IImage LanguageIcon(int index)
    {
        if (index < 0 || index >= LanguageIconCount) throw new ArgumentOutOfRangeException(nameof(index));
        return Crop(new PixelRect(6 + (index % 14) * 30, 258 + (index / 14) * 30, 24, 24));
    }

    private static Bitmap LoadAtlas()
    {
        using var stream = AssetLoader.Open(new Uri(AtlasUri));
        return new Bitmap(stream);
    }

    private static WindowIcon LoadApplicationIcon()
    {
        using var stream = AssetLoader.Open(new Uri(ApplicationIconUri));
        return new WindowIcon(stream);
    }

    private static CroppedBitmap Crop(PixelRect sourceRect) => new()
    {
        Source = Atlas,
        SourceRect = sourceRect
    };
}
