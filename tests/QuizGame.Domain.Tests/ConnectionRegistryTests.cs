using QuizGame.Contracts;
using QuizGame.Server.Auth;
using QuizGame.Server.Services;
using Xunit;

namespace QuizGame.Domain.Tests;

public class ConnectionRegistryTests
{
    private static (GameRoomManager Manager, QuizGame.Server.Domain.GameRoom Room, Guid PlayerId) Setup()
    {
        var manager = Make.Manager();
        var room = manager.CreateRoom(Guid.NewGuid());
        var playerId = room.AddPlayer("Anna", Make.T0).Player!.Id;
        return (manager, room, playerId);
    }

    [Fact]
    public void Player_is_connected_until_the_last_connection_drops()
    {
        var (manager, room, id) = Setup();
        var registry = new ConnectionRegistry();

        // Reconnect overlap: new connection arrives before the old one is reported closed.
        Assert.NotNull(registry.Connect(room, new ConnectionEntry("c1", room.Code, Roles.Player, id)));
        Assert.Null(registry.Connect(room, new ConnectionEntry("c2", room.Code, Roles.Player, id)));

        var first = registry.Disconnect("c1", manager);
        Assert.Null(first.Changed);
        Assert.True(room.GetPlayer(id)!.Connected);

        var last = registry.Disconnect("c2", manager);
        Assert.NotNull(last.Changed);
        Assert.False(room.GetPlayer(id)!.Connected);
    }

    [Fact]
    public void Disconnect_of_unknown_connection_is_a_noop()
    {
        var (manager, _, _) = Setup();
        var result = new ConnectionRegistry().Disconnect("nope", manager);

        Assert.Null(result.Info);
        Assert.Null(result.Changed);
    }

    [Fact]
    public void Host_connection_does_not_touch_player_presence()
    {
        var (manager, room, id) = Setup();
        var registry = new ConnectionRegistry();

        Assert.Null(registry.Connect(room, new ConnectionEntry("h", room.Code, Roles.GameMaster, null)));
        Assert.True(registry.HasConnections(room.Code));
        Assert.False(room.GetPlayer(id)!.Connected);
        Assert.Null(registry.Disconnect("h", manager).Changed);
        Assert.False(registry.HasConnections(room.Code));
    }

    [Fact]
    public void DetachPlayer_removes_all_their_connections()
    {
        var (_, room, id) = Setup();
        var registry = new ConnectionRegistry();
        registry.Connect(room, new ConnectionEntry("c1", room.Code, Roles.Player, id));
        registry.Connect(room, new ConnectionEntry("c2", room.Code, Roles.Player, id));

        var ids = registry.DetachPlayer(room.Code, id);

        Assert.Equal(2, ids.Count);
        Assert.Null(registry.Get("c1"));
        Assert.Null(registry.Get("c2"));
    }
}
