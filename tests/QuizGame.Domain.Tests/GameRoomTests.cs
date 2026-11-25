using QuizGame.Contracts;
using QuizGame.Server.Domain;
using Xunit;

namespace QuizGame.Domain.Tests;

public class GameRoomTests
{
    private static GameRoom NewRoom(int max = 30) => new("123456", Guid.NewGuid(), new RoomSettings(), max, Make.T0);

    [Fact]
    public void AddPlayer_trims_name_and_starts_offline_in_lobby()
    {
        var room = NewRoom();
        var result = room.AddPlayer("  Anna  ", Make.T0);

        Assert.True(result.Ok);
        Assert.Equal("Anna", result.Player!.Name);
        Assert.False(result.Player.Connected);
        Assert.Equal(GamePhase.Lobby, room.Phase);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("123456789012345678901")] // 21 chars
    [InlineData("bad\nname")]
    public void AddPlayer_rejects_invalid_names(string name)
    {
        var result = NewRoom().AddPlayer(name, Make.T0);

        Assert.False(result.Ok);
        Assert.Equal(JoinError.InvalidName, result.Error);
    }

    [Fact]
    public void AddPlayer_accepts_20_char_name_and_unicode()
    {
        var room = NewRoom();
        Assert.True(room.AddPlayer("12345678901234567890", Make.T0).Ok);
        Assert.True(room.AddPlayer("Béla Ő", Make.T0).Ok);
    }

    [Fact]
    public void AddPlayer_rejects_duplicate_names_case_insensitively()
    {
        var room = NewRoom();
        room.AddPlayer("Anna", Make.T0);

        var result = room.AddPlayer("aNNa", Make.T0);

        Assert.Equal(JoinError.NameTaken, result.Error);
    }

    [Fact]
    public void AddPlayer_rejects_when_room_is_full()
    {
        var room = NewRoom(max: 2);
        room.AddPlayer("A", Make.T0);
        room.AddPlayer("B", Make.T0);

        Assert.Equal(JoinError.RoomFull, room.AddPlayer("C", Make.T0).Error);
    }

    [Fact]
    public void Players_get_distinct_colours()
    {
        var room = NewRoom();
        var colours = Enumerable.Range(0, 12)
            .Select(i => room.AddPlayer($"P{i}", Make.T0).Player!.ColorHex)
            .ToList();

        Assert.Equal(12, colours.Distinct().Count());
    }

    [Fact]
    public void SetConnected_reports_only_real_changes()
    {
        var room = NewRoom();
        var id = room.AddPlayer("Anna", Make.T0).Player!.Id;

        Assert.NotNull(room.SetConnected(id, true));
        Assert.Null(room.SetConnected(id, true));
        Assert.True(room.GetPlayer(id)!.Connected);
        Assert.NotNull(room.SetConnected(id, false));
        Assert.Null(room.SetConnected(Guid.NewGuid(), true));
    }

    [Fact]
    public void RemovePlayer_frees_the_name_and_the_slot()
    {
        var room = NewRoom(max: 1);
        var id = room.AddPlayer("Anna", Make.T0).Player!.Id;

        Assert.NotNull(room.RemovePlayer(id));
        Assert.Null(room.RemovePlayer(id));
        Assert.Null(room.GetPlayer(id));
        Assert.True(room.AddPlayer("Anna", Make.T0).Ok);
    }

    [Fact]
    public void Snapshot_is_a_copy_of_current_state()
    {
        var room = NewRoom();
        var snapshot = room.Snapshot(Make.T0);
        room.AddPlayer("Anna", Make.T0);

        Assert.Equal("123456", snapshot.RoomCode);
        Assert.Equal(0, snapshot.Players.Count);
        Assert.Equal(1, room.Snapshot(Make.T0).Players.Count);
    }
}
