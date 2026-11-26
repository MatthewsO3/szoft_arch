using Xunit;
using Microsoft.Extensions.Options;
using QuizGame.Server.Services;

namespace QuizGame.Domain.Tests;

/// <summary>Controllable clock.</summary>
public sealed class FakeTime(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan by) => _now += by;
}

public static class Make
{
    public static readonly DateTimeOffset T0 = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    public static GameRoomManager Manager(FakeTime? time = null, int maxPlayers = 30, int idleMinutes = 180) =>
        new(Options.Create(new RoomOptions { MaxPlayers = maxPlayers, IdleTimeoutMinutes = idleMinutes }),
            time ?? new FakeTime(T0));
}

/// <summary>Drives a <see cref="QuizGame.Server.Domain.GameRoom"/> through the phases through its public API only.</summary>
public static class Game
{
    /// <summary>Lobby → BaseSelection (needs the players to be connected); also fixes the turn order and starts base picks.</summary>
    public static void StartGame(QuizGame.Server.Domain.GameRoom room)
    {
        var started = room.TryStartGame(Make.T0);
        Assert.True(started.Ok);
    }

    /// <summary>Plays the whole base selection: each player in turn order takes the first valid free tile, then Conquest begins.</summary>
    public static void PlayBaseSelection(QuizGame.Server.Domain.GameRoom room)
    {
        while (room.CurrentPickView() is { } pick)
            Assert.True(room.TryPick(pick.Picker.Id, pick.ValidTerritoryIndices[0], Make.T0.AddSeconds(2)).Ok);

        Assert.True(room.TryAdvancePhase(QuizGame.Contracts.GamePhase.Conquest, Make.T0.AddSeconds(3)).Ok);
    }

    public static int BaseOf(QuizGame.Server.Domain.GameRoom room, Guid playerId) =>
        room.GetMap()!.Territories.First(t => t.OwnerPlayerId == playerId).Index;
}
