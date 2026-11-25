using Xunit;

namespace QuizGame.Domain.Tests;

public class GameRoomManagerTests
{
    [Fact]
    public void CreateRoom_returns_unique_six_digit_codes_without_leading_zero()
    {
        var manager = Make.Manager();
        var gm = Guid.NewGuid();

        var codes = Enumerable.Range(0, 500).Select(_ => manager.CreateRoom(gm).Code).ToList();

        Assert.Equal(500, codes.Distinct().Count());
        Assert.True(codes.All(c => c.Length == 6 && c.All(char.IsAsciiDigit) && c[0] != '0'));
        Assert.Equal(500, manager.Count);
    }

    [Fact]
    public void TryGet_finds_rooms_and_rejects_malformed_codes()
    {
        var manager = Make.Manager();
        var room = manager.CreateRoom(Guid.NewGuid());

        Assert.True(manager.TryGet(room.Code, out var found));
        Assert.Equal(room.SessionId, found.SessionId);
        Assert.False(manager.TryGet(null, out _));
        Assert.False(manager.TryGet("", out _));
        Assert.False(manager.TryGet("12345", out _));
        Assert.False(manager.TryGet("12345a", out _));
        Assert.False(manager.TryGet("000000", out _));
    }

    [Fact]
    public void Remove_deletes_the_room()
    {
        var manager = Make.Manager();
        var room = manager.CreateRoom(Guid.NewGuid());

        Assert.True(manager.Remove(room.Code));
        Assert.False(manager.Remove(room.Code));
        Assert.False(manager.TryGet(room.Code, out _));
    }

    [Fact]
    public void RemoveIdleRooms_removes_only_old_unused_rooms()
    {
        var time = new FakeTime(Make.T0);
        var manager = Make.Manager(time, idleMinutes: 60);
        var gm = Guid.NewGuid();
        var idle = manager.CreateRoom(gm);
        var busy = manager.CreateRoom(gm);
        var fresh = manager.CreateRoom(gm);

        time.Advance(TimeSpan.FromMinutes(61));
        fresh.Touch(time.GetUtcNow());

        var removed = manager.RemoveIdleRooms(r => r.Code == busy.Code);

        Assert.Equal(1, removed.Count);
        Assert.Equal(idle.Code, removed[0].Code);
        Assert.False(manager.TryGet(idle.Code, out _));
        Assert.True(manager.TryGet(busy.Code, out _));
        Assert.True(manager.TryGet(fresh.Code, out _));
    }
}
