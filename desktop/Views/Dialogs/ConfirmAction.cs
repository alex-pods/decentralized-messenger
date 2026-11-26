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
        var cancel = new Button { Content = "Отмена", Classes = { "secondaryAction" }, HorizontalAlignment = HorizontalAlignment.Stretch };
        var confirm = new Button { Content = action, Classes = { "danger", "dangerConfirm" }, HorizontalAlignment = HorizontalAlignment.Stretch };
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
        var actions = new Grid { ColumnDefinitions = new ColumnDefinitions("*,8,*") };
        Grid.SetColumn(confirm, 2);
        actions.Children.Add(cancel);
        actions.Children.Add(confirm);
        dialog.Content = new StackPanel { Margin = new Thickness(20), Spacing = 14, Children =
        {
            heading,
            new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap },
            actions
        }};
        return await dialog.ShowDialog<bool>(owner);
    }
}
