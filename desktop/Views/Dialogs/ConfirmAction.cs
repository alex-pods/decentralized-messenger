using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Input;
namespace MessengerDesktop.Views.Dialogs;

public static class ConfirmAction
{
    public static async Task<bool> Show(Control source, string title, string description, string action)
    {
        if (TopLevel.GetTopLevel(source) is not Window owner) return false;
        var dialog = new Window { Title = title, Width = Math.Clamp(owner.ClientSize.Width - 32, 320, 400), SizeToContent = SizeToContent.Height,
            CanResize = false, SystemDecorations = SystemDecorations.BorderOnly, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        dialog.Bind(Window.BackgroundProperty, new DynamicResourceExtension("RaisedBrush"));
        var cancel = new Button { Content = "Отмена", Classes = { "secondaryAction" }, MinWidth = 90 };
        var confirm = new Button { Content = action, Classes = { "danger", "dangerConfirm" }, MinWidth = 110 };
        cancel.Click += (_, _) => dialog.Close(false);
        confirm.Click += (_, _) => dialog.Close(true);
        var heading = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        heading.Children.Add(new TextBlock { Text = title, FontSize = 20, FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });
        var close = new DismissButton { VerticalAlignment = VerticalAlignment.Top };
        close.Click += (_, _) => dialog.Close(false);
        Grid.SetColumn(close, 1);
        heading.Children.Add(close);
        dialog.KeyDown += (_, e) => { if (e.Key == Key.Escape) dialog.Close(false); };
        dialog.Opened += (_, _) => cancel.Focus();
        dialog.Content = new StackPanel { Margin = new Thickness(24), Spacing = 18, Children =
        {
            heading,
            new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap },
            new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Children = { cancel, confirm } }
        }};
        return await dialog.ShowDialog<bool>(owner);
    }
}
