using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;

namespace ChoirLauncher.Desktop;

public static class Dialogs
{
    public static async Task<string?> PromptAsync(Window owner, string title, string message, string initial = "")
    {
        var input = new TextBox { Text = initial, MinWidth = 380 };
        var dialog = Base(title, message, out var buttons);
        ((StackPanel)dialog.Content!).Children.Insert(1, input);
        string? result = null;
        var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 90 };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 90 };
        ok.Click += (_, _) => { result = input.Text; dialog.Close(); };
        cancel.Click += (_, _) => dialog.Close();
        buttons.Children.Add(ok); buttons.Children.Add(cancel);
        dialog.Opened += (_, _) => input.Focus();
        await dialog.ShowDialog(owner);
        return result;
    }

    public static async Task<string?> ChooseAsync(Window owner, string title, string message, params string[] choices)
    {
        var dialog = Base(title, message, out var buttons);
        string? result = null;
        foreach (var choice in choices)
        {
            var button = new Button { Content = choice, MinWidth = 120 };
            button.Click += (_, _) => { result = choice; dialog.Close(); };
            buttons.Children.Add(button);
        }
        await dialog.ShowDialog(owner);
        return result;
    }

    public static async Task ShowAsync(Window owner, string title, string message)
    {
        var dialog = Base(title, message, out var buttons);
        var ok = new Button { Content = "OK", IsDefault = true, IsCancel = true, MinWidth = 90 };
        ok.Click += (_, _) => dialog.Close(); buttons.Children.Add(ok);
        await dialog.ShowDialog(owner);
    }

    private static Window Base(string title, string message, out StackPanel buttons)
    {
        buttons = new() { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        var messageBlock = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 510 };
        var messageScroll = new ScrollViewer
        {
            MaxHeight = 420,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = messageBlock
        };
        return new Window
        {
            Title = title, Width = 560, SizeToContent = SizeToContent.Height, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Icon = VanillaLauncherArt.ApplicationIcon,
            Content = new StackPanel
            {
                Margin = new(20), Spacing = 16,
                Children =
                {
                    messageScroll,
                    buttons
                }
            }
        };
    }
}
