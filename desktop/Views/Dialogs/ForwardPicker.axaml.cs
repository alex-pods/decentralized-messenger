using Avalonia.Controls;
namespace MessengerDesktop.Views.Dialogs;
public partial class ForwardPicker : UserControl
{
    public ForwardPicker() => InitializeComponent();
    void OnSearchChanged(object? sender, TextChangedEventArgs e)
    {
        if (DataContext is not ShellViewModel vm) return;
        var query = DestinationSearch.Text?.Trim() ?? "";
        var chats = vm.Chats.Where(c => c.Title.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
        Destinations.ItemsSource = chats;
        NoResults.IsVisible = chats.Length == 0;
    }
}
