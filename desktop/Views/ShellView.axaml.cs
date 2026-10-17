using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.VisualTree;


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

    void OnDragAreaPressed(object? sender, PointerPressedEventArgs e) => MainWindow.DragFrom(this, e);

    void OnChipPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (sender is Control { DataContext: ReactionChip chip } && DataContext is ShellViewModel vm)
            vm.ToggleChipCommand.Execute(chip);
        e.Handled = true;   // ListBox не увидит нажатие и не выберет сообщение
    }
    
    void OnLogPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (e.Source is not Control src) return;

        if (src.FindAncestorOfType<ListBoxItem>() != null) return;
        if (src.FindAncestorOfType<ScrollBar>() != null) return;

        if (DataContext is ShellViewModel vm)
            vm.SelectedMessage = null;
    }
}
