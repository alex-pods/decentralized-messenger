using LocalBackend;
using Messenger.Core;
using Messenger.Core.Models;

namespace MessengerDesktop;

public static class DesktopBootstrap
{
    public static Task<MessengerSession> OpenAsync(
        MessengerClient api,
        ProfileDto me,
        CancellationToken ct = default)
    {
        var account = new AccountIdentity(api.ServerBase, me.Id);
        var profile = MapProfile(me);

        var dataDirectory = Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData),
            "messenger-desktop");

        // Подготовку SQLite выполняем вне UI-потока.
        return Task.Run(async () =>
        {
            var store = await SqliteStoreFactory.OpenAsync(
                account, dataDirectory, ct);

            var session = new MessengerSession(account, store, api);

            await session.UpdateProfileAsync(profile, ct);

            return session;
        }, ct);
    }

    public static User MapProfile(ProfileDto profile) => MessengerSession.MapProfile(profile);
}
