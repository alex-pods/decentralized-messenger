using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using System.Collections.Specialized;
using System.ComponentModel;

namespace MessengerDesktop.Views.Dialogs;

public partial class ForwardPicker : UserControl
{
    ShellViewModel? _vm;

    public ForwardPicker()
    {
        InitializeComponent();
        AttachedToVisualTree += (_, _) => Connect(DataContext as ShellViewModel);
        DetachedFromVisualTree += (_, _) => Connect(null);
        DataContextChanged += (_, _) => { if (this.IsAttachedToVisualTree()) Connect(DataContext as ShellViewModel); };
    }

    void Connect(ShellViewModel? vm)
    {
        if (_vm != null)
        {
            _vm.PropertyChanged -= OnModelChanged;
            _vm.Chats.CollectionChanged -= OnChatsChanged;
        }
        _vm = vm;
        if (_vm != null)
        {
            _vm.PropertyChanged += OnModelChanged;
            _vm.Chats.CollectionChanged += OnChatsChanged;
        }
        Reset();
    }

    void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShellViewModel.Overlay)) Reset();
        else if (e.PropertyName is nameof(ShellViewModel.ForwardSource) or nameof(ShellViewModel.Busy)
            or nameof(ShellViewModel.AntispamRequired)) UpdateAction();
    }

    void Reset()
    {
        DestinationSearch.Clear();
        Destinations.SelectedItem = null;
        Filter();
    }

    void OnChatsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_vm?.Overlay == "forward") Filter();
    }

    void OnSearchChanged(object? sender, TextChangedEventArgs e) => Filter();
    void OnRecipientChanged(object? sender, SelectionChangedEventArgs e) => UpdateAction();

    void Filter()
    {
        if (Destinations == null || _vm == null) return;
        var selectedId = (Destinations.SelectedItem as ChatItem)?.Id;
        var query = DestinationSearch.Text?.Trim() ?? "";
        var matches = _vm.Overlay == "forward" ? ChatSearchFilter.Match(_vm.Chats, query) : [];
        Destinations.ItemsSource = matches;
        Destinations.SelectedItem = matches.FirstOrDefault(chat => chat.Id == selectedId);
        Destinations.IsVisible = matches.Length > 0;
        ClearDestination.IsVisible = !string.IsNullOrEmpty(DestinationSearch.Text);
        NoResults.IsVisible = matches.Length == 0;
        NoResults.Text = query.Length == 0 ? "Нет чатов для пересылки" : "По этому запросу нет чатов";
        UpdateAction();
    }

    void UpdateAction()
    {
        if (SendForward != null)
            SendForward.IsEnabled = Destinations.SelectedItem is ChatItem &&
                _vm is { ForwardSource: not null, Busy: false, AntispamRequired: false };
    }

    void OnClearSearch(object? sender, RoutedEventArgs e)
    {
        DestinationSearch.Clear();
        DestinationSearch.Focus();
    }

    void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && !string.IsNullOrEmpty(DestinationSearch.Text))
            DestinationSearch.Clear();
        else if (e.Key is Key.Enter or Key.Down &&
                 !DestinationSearch.GetVisualDescendants().OfType<TextPresenter>().Any(p => !string.IsNullOrEmpty(p.PreeditText)))
        {
            Filter();
            if (Destinations.Items.Count == 0) return;
            Destinations.SelectedIndex = 0;
            if (e.Key == Key.Down) Destinations.Focus();
        }
        else return;
        e.Handled = true;
    }
}
