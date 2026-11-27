using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
namespace MessengerDesktop.Views.Dialogs;
public partial class PeopleSearch : UserControl
{
    public PeopleSearch() => InitializeComponent();

    void OnClearTag(object? sender, RoutedEventArgs e)
    {
        PersonTag.Clear();
        PersonTag.Focus();
    }

    void OnTagKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not ShellViewModel vm || PersonTag.GetVisualDescendants().OfType<TextPresenter>()
            .Any(presenter => !string.IsNullOrEmpty(presenter.PreeditText))) return;

        if (e.Key == Key.Enter)
        {
            if (vm.SearchResults.FirstOrDefault() is { } person && vm.PickUserCommand.CanExecute(person))
                vm.PickUserCommand.Execute(person);
            else if (vm.SearchCommand.CanExecute(null))
                vm.SearchCommand.Execute(null);
        }
        else if (e.Key == Key.Down && TagResults.GetVisualDescendants().OfType<Button>()
                     .FirstOrDefault(button => button.Classes.Contains("personResult")) is { } result)
            result.Focus();
        else if (e.Key == Key.Escape && !string.IsNullOrEmpty(PersonTag.Text))
            PersonTag.Clear();
        else return;
        e.Handled = true;
    }
}
