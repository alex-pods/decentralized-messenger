using Avalonia.Controls;

namespace MessengerDesktop.Views;

public partial class ShellView : UserControl
{
    ShellViewModel? _vm;

    public ShellView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Hook();
    }

    void Hook()
    {
        if (_vm != null) _vm.ScrollToEnd -= Scroll;
        _vm = DataContext as ShellViewModel;
        if (_vm != null) _vm.ScrollToEnd += Scroll;
    }

    void Scroll()
    {
        if (_vm == null || _vm.Messages.Count == 0) return;
        if (this.FindControl<ListBox>("Log") is { } list)
            list.ScrollIntoView(_vm.Messages[^1]);
    }
}
