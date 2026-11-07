using Avalonia.Controls;
using Avalonia.Input;

namespace MessengerDesktop.Views;

public partial class LoginView : UserControl
{
    public LoginView()
    {
        InitializeComponent();
        ChassisWidth.Track(this);
    }

    void OnDragAreaPressed(object? sender, PointerPressedEventArgs e) => MainWindow.DragFrom(this, e);
}
