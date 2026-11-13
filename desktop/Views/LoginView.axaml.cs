using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace MessengerDesktop.Views;

public partial class LoginView : UserControl
{
    bool _reveal;

    public LoginView() => InitializeComponent();

    void OnDragAreaPressed(object? sender, PointerPressedEventArgs e) => MainWindow.DragFrom(this, e);

    void OnTogglePassword(object? sender, RoutedEventArgs e)
    {
        _reveal = !_reveal;
        PasswordBox.PasswordChar = _reveal ? '\0' : '•';
        EyeOpen.IsVisible = !_reveal;
        EyeClosed.IsVisible = _reveal;
        e.Handled = true;
    }
}
