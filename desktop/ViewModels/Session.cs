namespace MessengerDesktop;

public sealed class Navigator
{
    public Navigator(Avalonia.Controls.Window window) => Window = window;
    public Avalonia.Controls.Window Window { get; }
    public AccountManager? Accounts { get; set; }
    public void Go(object page) => Window.DataContext = page;
}
