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
