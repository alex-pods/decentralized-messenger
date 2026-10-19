using Dapper;
using MessengerBackend.Core;
using MessengerBackend.Hubs;

namespace MessengerBackend.Features.Realtime;

public sealed class SessionCleanup(Hub hub, ILogger<SessionCleanup> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    var expired = Core.Accounts.ExpiredSessionHashes();
                    await hub.CloseWhere(c => expired.Contains(c.TokenHash), Hub.CloseSessionExpired, "session expired");
                    Db.Write(c => c.Execute("DELETE FROM user_sessions WHERE expires_at<=@now", new { now = Util.NowIso() }));
                }
                catch (Exception ex) { logger.LogError(ex, "Session cleanup failed"); }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
