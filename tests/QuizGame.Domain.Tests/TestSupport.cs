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
    /// <summary>Lobby → BaseSelection (needs the players to be connected).</summary>
    public static void StartGame(QuizGame.Server.Domain.GameRoom room)
    {
        var started = room.TryStartGame(Make.T0);
        Assert.True(started.Ok);
    }

    /// <summary>
    /// Plays the whole base selection: everybody answers the estimation question (join order = ranking, so the
    /// pick order is the join order), each picker takes the first valid territory, then Conquest begins.
    /// </summary>
    public static void PlayBaseSelection(QuizGame.Server.Domain.GameRoom room)
    {
        var question = new QuizGame.Contracts.QuestionDto
        {
            Id = Guid.NewGuid(), Type = QuizGame.Contracts.QuestionType.Estimation, TimeLimitSeconds = 10, CorrectNumber = 100m,
        };
        var round = room.TryStartRound(question,
            new QuizGame.Contracts.SendQuestionRequest { QuestionId = question.Id, Kind = QuizGame.Contracts.RoundKind.BaseOrder },
            Make.T0).Round!;

        var offset = 0;
        foreach (var playerId in round.ParticipantIds)
        {
            room.TrySubmitAnswer(playerId,
                new QuizGame.Contracts.SubmitAnswerRequest { RoundId = round.RoundId, Number = 100m + offset },
                Make.T0.AddMilliseconds(100 * offset), TimeSpan.FromMilliseconds(300));
            offset++;
        }

        Assert.True(room.CloseRound(round.RoundId, Make.T0.AddSeconds(1)) is not null);
        while (room.CurrentPickView() is { } pick)
            Assert.True(room.TryPick(pick.Picker.Id, pick.ValidTerritoryIndices[0], Make.T0.AddSeconds(2)).Ok);

        Assert.True(room.TryAdvancePhase(QuizGame.Contracts.GamePhase.Conquest, Make.T0.AddSeconds(3)).Ok);
    }

    public static int BaseOf(QuizGame.Server.Domain.GameRoom room, Guid playerId) =>
        room.GetMap()!.Territories.First(t => t.OwnerPlayerId == playerId).Index;
}
