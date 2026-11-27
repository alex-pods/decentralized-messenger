using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace MessengerDesktop.Views;

public partial class ShellView
{
    string _sidebarQuery = "";
    CancellationTokenSource? _sidebarSearchCancellation;
    IReadOnlyList<UserCardDto> _directoryMatches = [];
    bool _searchingDirectory;
    bool _submitOnDirectoryResult;
    bool _openingDirectoryChat;
    string? _directoryError;

    void CancelSidebarSearch()
    {
        _sidebarSearchCancellation?.Cancel();
        _sidebarSearchCancellation = null;
        _sidebarQuery = "";
        _directoryMatches = [];
        _searchingDirectory = false;
        _submitOnDirectoryResult = false;
        _directoryError = null;
    }

    void UpdateSidebarSearch()
    {
        if (_vm is not { } vm) return;
        var query = ChatSearch.Text?.Trim() ?? "";
        if (query != _sidebarQuery)
        {
            CancelSidebarSearch();
            _sidebarQuery = query;
            if (ChatSearchFilter.DirectoryTag(query) is { } tag)
            {
                StartDirectoryLookup(vm, tag);
            }
        }
        UpdateSidebarResults();
    }

    void StartDirectoryLookup(ShellViewModel vm, string tag)
    {
        _directoryError = null;
        _searchingDirectory = true;
        var request = new CancellationTokenSource();
        _sidebarSearchCancellation = request;
        _ = SearchDirectory(vm, tag, request);
    }

    void SubmitSidebarSearch()
    {
        var result = ChatMatches.Items.FirstOrDefault() ?? PeopleMatches.Items.FirstOrDefault();
        if (result != null) _ = OpenSidebarResult(result);
        else if (_searchingDirectory) _submitOnDirectoryResult = true;
        else if (_directoryError != null && _vm is { } vm && ChatSearchFilter.DirectoryTag(_sidebarQuery) is { } tag)
        {
            _submitOnDirectoryResult = true;
            StartDirectoryLookup(vm, tag);
            UpdateSidebarResults();
        }
    }

    async Task SearchDirectory(ShellViewModel vm, string tag, CancellationTokenSource request)
    {
        try
        {
            await Task.Delay(250, request.Token);
            var matches = await vm.FindPeopleByTag(tag, request.Token);
            if (_sidebarSearchCancellation == request) _directoryMatches = matches;
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception)
        {
            if (_sidebarSearchCancellation == request) _directoryError = "Не удалось найти пользователей. Попробуйте ещё раз.";
        }
        finally
        {
            if (_sidebarSearchCancellation == request)
            {
                _sidebarSearchCancellation = null;
                _searchingDirectory = false;
                UpdateSidebarResults();
                if (_submitOnDirectoryResult && _directoryError == null && ChatSearch.IsFocused)
                    _ = OpenSidebarResult(PeopleMatches.Items.FirstOrDefault());
                _submitOnDirectoryResult = false;
            }
            request.Dispose();
        }
    }

    void UpdateSidebarResults()
    {
        var showResults = _vm != null && _sidebarQuery.Length > 0;
        SidebarResults.IsVisible = showResults;
        ChatList.IsVisible = !showResults;
        ChatEmpty.IsVisible = !showResults && _vm?.Chats.Count == 0;
        ClearChatSearch.IsVisible = !string.IsNullOrEmpty(ChatSearch.Text);
        var chats = showResults ? ChatSearchFilter.Match(_vm!.Chats, _sidebarQuery) : [];
        var existingPeople = showResults && _directoryMatches.Count > 0
            ? _vm!.Chats.Select(chat => chat.AvatarUserId).ToHashSet() : [];
        var people = showResults
            ? _directoryMatches.Where(user => !existingPeople.Contains(user.Id)).ToArray()
            : [];
        if (!ChatMatches.Items.SequenceEqual(chats)) ChatMatches.ItemsSource = chats;
        if (!PeopleMatches.Items.SequenceEqual(people)) PeopleMatches.ItemsSource = people;
        ChatMatchesHeading.IsVisible = chats.Length > 0;
        PeopleMatchesHeading.IsVisible = people.Length > 0;
        SearchStatus.Text = _directoryError ?? (_searchingDirectory ? "Поиск пользователей…" : null);
        if (SearchStatus.Text == null && chats.Length == 0 && people.Length == 0)
            SearchStatus.Text = ChatSearchFilter.DirectoryTag(_sidebarQuery) == null
                ? "Чаты не найдены. Для поиска человека введите от трёх символов его @тега."
                : "Чаты и пользователи не найдены.";
        SearchStatus.IsVisible = showResults && SearchStatus.Text != null;
    }

    bool HasSearchPreedit() => ChatSearch.GetVisualDescendants().OfType<TextPresenter>()
        .Any(presenter => !string.IsNullOrEmpty(presenter.PreeditText));

    IEnumerable<Button> SearchResultButtons() => SidebarResults.GetVisualDescendants().OfType<Button>()
        .Where(button => button.Classes.Contains("sidebarResult") && button.IsEffectivelyVisible);

    void OnSearchResultKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            ChatSearch.Clear();
            ChatSearch.Focus();
        }
        else if (e.Key is Key.Up or Key.Down)
        {
            var buttons = SearchResultButtons().ToList();
            var index = buttons.FindIndex(button => button.IsFocused);
            if (index < 0) return;
            var next = index + (e.Key == Key.Up ? -1 : 1);
            if (next < 0) ChatSearch.Focus();
            else if (next < buttons.Count)
            {
                buttons[next].Focus();
                buttons[next].BringIntoView();
            }
        }
        else return;
        e.Handled = true;
    }

    async void OnSearchResultClick(object? sender, RoutedEventArgs e) =>
        await OpenSidebarResult((sender as Button)?.DataContext);

    async Task OpenSidebarResult(object? result)
    {
        if (result is ChatItem chat)
        {
            ChatSearch.Clear();
            OpenFromSearch(chat);
            ComposerInput.Focus();
            return;
        }
        if (result is not UserCardDto user || _vm is not { } vm || _openingDirectoryChat) return;
        _openingDirectoryChat = true;
        var query = _sidebarQuery;
        var selectedId = vm.SelectedChat?.Id;
        try
        {
            var direct = await vm.FindOrCreateDirect(user.Id);
            if (_vm == vm && _sidebarQuery == query && vm.SelectedChat?.Id == selectedId)
                await OpenSidebarResult(direct);
        }
        catch (Exception)
        {
            if (_vm == vm && _sidebarQuery == query)
            {
                _directoryError = "Не удалось открыть чат. Попробуйте ещё раз.";
                UpdateSidebarResults();
            }
        }
        finally { _openingDirectoryChat = false; }
    }
}
