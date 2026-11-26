using QuizGame.Contracts;

namespace QuizGame.Host.Services;

/// <summary>Room settings and phase control operations invoked over the host SignalR connection.</summary>
public interface IRoomControlClient
{
    bool IsConnected { get; }
    Task UpdateSettingsAsync(RoomSettings settings);
    Task StartGameAsync();
    Task AdvancePhaseAsync(GamePhase targetPhase);
}
