using Microsoft.AspNetCore.SignalR;
using QuizGame.Contracts;
using QuizGame.Server.Hubs;

namespace QuizGame.Server.Services;

/// <summary>Periodically removes abandoned rooms (plan §7.1: cleanup of idle rooms).</summary>
public sealed class RoomCleanupService(
    GameRoomManager rooms,
    ConnectionRegistry registry,
    IHubContext<GameHub> hub,
    ISessionRecorder recorder,
    ILogger<RoomCleanupService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                foreach (var room in rooms.RemoveIdleRooms(r => registry.HasConnections(r.Code)))
                {
                    logger.LogInformation("Removed idle room {Room}", room.Code);
                    await recorder.SessionEndedAsync(room.SessionId, stoppingToken);
                    await hub.Clients.Group(GroupNames.Room(room.Code)).SendAsync(HubNames.Events.RoomClosed, stoppingToken);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }
}
