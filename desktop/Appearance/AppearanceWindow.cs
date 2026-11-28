using Avalonia.Controls;
using Avalonia.Input;

namespace MessengerDesktop.Appearance;

// Standalone host for the compact preview checks.
internal sealed class AppearanceWindow : Window
{
    readonly AppearanceEditor _editor;

    public AppearanceWindow(MessengerClient? api = null, string? owner = null)
    {
        Title = "Оформление";
        Width = 540;
        Height = 660;
        MinWidth = 360;
        MinHeight = 420;
        SystemDecorations = SystemDecorations.BorderOnly;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Content = _editor = new AppearanceEditor(api, owner, standalone: true);
        _editor.CloseRequested += Close;
        Closed += (_, _) => _editor.EndEditing();
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
    }

    internal void SelectWallpaper(WallpaperSettings wallpaper) => _editor.SelectWallpaper(wallpaper);
}
