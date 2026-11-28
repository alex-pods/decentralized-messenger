using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace MessengerDesktop;

public partial class ShellViewModel
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSendDraft))]
    [NotifyPropertyChangedFor(nameof(CanOpenImageComposer))]
    private bool _antispamRequired;
    [ObservableProperty] private bool _antispamBusy;
    [ObservableProperty] private string? _antispamError;

    void OnAntispamChanged(bool blocked) => Dispatcher.UIThread.Post(() =>
    {
        if (_closed) return;
        AntispamRequired = blocked;
        if (blocked) Error = null;
        else AntispamError = null;
    });
}
