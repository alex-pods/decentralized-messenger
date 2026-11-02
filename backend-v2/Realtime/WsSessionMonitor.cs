using Messenger.Api.Repositories;
using Microsoft.Extensions.Hosting;

namespace Messenger.Api.Realtime;

public sealed class WsSessionMonitor(
    WsConnectionRegistry registry,
    ISessionsRepository sessions,
    ILogger<WsSessionMonitor> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(
        CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    var groups = registry.GetAll()
                        .GroupBy(connection => connection.TokenHash);

                    foreach (var group in groups)
                    {
                        stoppingToken.ThrowIfCancellationRequested();

                        var userId = await sessions.GetUserId(group.Key);

                        foreach (var connection in group)
                        {
                            if (userId != connection.UserId)
                            {
                                connection.RequestClose(
                                    4002,
                                    "session expired or revoked");
                            }
                        }
                    }
                }
                catch (OperationCanceledException)
                    when (stoppingToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    logger.LogWarning(
                        exception,
                        "Failed to check WebSocket sessions");
                }
            }
        }
        catch (OperationCanceledException)
            when (stoppingToken.IsCancellationRequested)
        {
            
        }
    }
}