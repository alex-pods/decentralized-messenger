using Avalonia.Controls;
using Avalonia.Interactivity;
using MessengerDesktop.Antispam;

namespace MessengerDesktop.Views;

public partial class ShellView
{
    async void OnConfirmAntispam(object? sender, RoutedEventArgs e)
    {
        if (_vm is not { } vm || vm.AntispamBusy || TopLevel.GetTopLevel(this) is not Window owner) return;
        vm.AntispamBusy = true; vm.AntispamError = null;
        try
        {
            var dialog = new AntispamWindow(vm.ThemeApi);
            var outcome = await dialog.ShowDialog<AntispamOutcome?>(owner);
            // Alt+F4 on an already completed result screen returns no dialog value.
            // Reconcile with the server instead of leaving a passed account locally blocked.
            await vm.ThemeApi.RefreshAntispam();
            if (!vm.ThemeApi.AntispamBlocked)
            {
                ComposerInput.Focus();
            }
            else vm.AntispamError = outcome?.Error ?? "Проверка закрыта до завершения. Попробуйте снова.";
        }
        catch { vm.AntispamError = "Не удалось запустить проверку. Проверьте соединение и драйвер видеокарты."; }
        finally { vm.AntispamBusy = false; }
    }
}
