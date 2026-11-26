using QuizGame.Contracts;
using QuizGame.Server.Domain;
using Xunit;

namespace QuizGame.Domain.Tests;

/// <summary>Phases, settings, turn order and base selection of a room (plan §4.2).</summary>
public class GameRoomMapTests
{
    private static (GameRoom Room, PlayerSummary A, PlayerSummary B, PlayerSummary C) NewRoom(
        RoomSettings? settings = null, int connected = 3)
    {
        var room = new GameRoom("123456", Guid.NewGuid(), settings ?? new RoomSettings(), 30, Make.T0)
        {
            PickRandom = new Random(1), // deterministic turn order for tests
        };
        var players = new[] { "Anna", "Bela", "Cili" }.Select(n => room.AddPlayer(n, Make.T0).Player!).ToArray();
        foreach (var p in players.Take(connected)) room.SetConnected(p.Id, true);
        return (room, players[0], players[1], players[2]);
    }

    // ---- starting ----

    [Fact]
    public void Starting_needs_the_minimum_number_of_connected_players()
    {
        var (room, _, _, _) = NewRoom(connected: 1);
        Assert.Equal(PhaseError.NotEnoughPlayers, room.TryStartGame(Make.T0).Error);
        Assert.Equal(GamePhase.Lobby, room.Phase);
        Assert.Null(room.GetMap());

        var (two, _, _, _) = NewRoom(connected: 2);
        Assert.True(two.TryStartGame(Make.T0).Ok);

        var (strict, _, _, _) = NewRoom(new RoomSettings { MinPlayers = 3 }, connected: 2);
        Assert.Equal(PhaseError.NotEnoughPlayers, strict.TryStartGame(Make.T0).Error);
    }

    [Fact]
    public void Starting_generates_the_map_a_turn_order_and_the_first_base_pick()
    {
        var (room, _, _, _) = NewRoom();
        var started = room.TryStartGame(Make.T0);

        Assert.True(started.Ok);
        Assert.Equal(GamePhase.BaseSelection, room.Phase);
        Assert.Equal(48, started.Map!.Territories.Count);
        Assert.True(started.Map.Territories.All(t => t.OwnerPlayerId is null && !t.IsBase && t.Value == 100 && t.Health == 1));
        Assert.Equal(3, started.TurnOrder!.Count);
        Assert.Equal(started.TurnOrder, room.TurnOrder);
        Assert.NotNull(room.CurrentPickView());
        Assert.Equal(started.TurnOrder[0], room.CurrentPickView()!.Picker.Id);
        Assert.Equal(PickPurpose.Base, room.CurrentPickView()!.Purpose);
        Assert.Equal(PhaseError.InvalidPhase, room.TryStartGame(Make.T0).Error); // only once
    }

    [Fact]
    public void Nobody_can_join_once_the_game_has_started()
    {
        var (room, _, _, _) = NewRoom();
        room.TryStartGame(Make.T0);
        Assert.Equal(JoinError.NotInLobby, room.AddPlayer("Dani", Make.T0).Error);
    }

    // ---- settings ----

    [Fact]
    public void Settings_can_be_changed_in_the_lobby_only_and_must_be_in_range()
    {
        var (room, _, _, _) = NewRoom();
        var changed = new RoomSettings { MapPreset = MapPreset.Large, BaseValue = 500, BaseHealth = 2, PickTimeoutSeconds = 5 };

        Assert.Equal(PhaseError.None, room.TryUpdateSettings(changed));
        Assert.Equal(changed, room.Settings);
        Assert.Equal(PhaseError.InvalidSettings, room.TryUpdateSettings(changed with { PickTimeoutSeconds = 0 }));
        Assert.Equal(PhaseError.InvalidSettings, room.TryUpdateSettings(changed with { BaseHealth = 0 }));
        Assert.Equal(PhaseError.InvalidSettings, room.TryUpdateSettings(changed with { ConquestRounds = 0 }));
        Assert.Equal(changed, room.Settings);

        Assert.Equal(10, room.TryStartGame(Make.T0).Map!.Cols);
        Assert.Equal(PhaseError.InvalidPhase, room.TryUpdateSettings(new RoomSettings()));
    }

    // ---- base selection + picks ----

    [Fact]
    public void Each_player_picks_a_base_in_turn_order()
    {
        var (room, _, _, _) = NewRoom();
        room.TryStartGame(Make.T0);
        var order = room.TurnOrder;

        for (var i = 0; i < order.Count; i++)
        {
            var pick = room.CurrentPickView()!;
            Assert.Equal(order[i], pick.Picker.Id);
            var result = room.TryPick(pick.Picker.Id, pick.ValidTerritoryIndices[0], Make.T0.AddSeconds(1));
            Assert.True(result.Ok);
            var cell = result.Change!.Changed.Single();
            Assert.True(cell.IsBase);
            Assert.Equal(room.Settings.BaseValue, cell.Value);
            Assert.Equal(room.Settings.BaseHealth, cell.Health);
            Assert.Equal(order[i], cell.OwnerPlayerId);
        }

        Assert.Null(room.CurrentPickView());
        Assert.Equal(3, room.GetMap()!.Territories.Count(t => t.IsBase));
    }

    [Fact]
    public void Conquest_needs_a_finished_base_selection()
    {
        var (room, _, _, _) = NewRoom();
        room.TryStartGame(Make.T0);
        Assert.Equal(PhaseError.PickInProgress, room.TryAdvancePhase(GamePhase.Conquest, Make.T0).Error); // still picking

        while (room.CurrentPickView() is { } pick)
            room.TryPick(pick.Picker.Id, pick.ValidTerritoryIndices[0], Make.T0.AddSeconds(1));

        Assert.True(room.TryAdvancePhase(GamePhase.Conquest, Make.T0.AddSeconds(2)).Ok);
    }

    [Fact]
    public void Phases_only_move_forward_one_step_at_a_time()
    {
        var (room, _, _, _) = NewRoom();
        Assert.Equal(PhaseError.InvalidTransition, room.TryAdvancePhase(GamePhase.Conquest, Make.T0).Error);   // skip
        Assert.Equal(PhaseError.InvalidTransition, room.TryAdvancePhase(GamePhase.BaseSelection, Make.T0).Error); // start via StartGame

        Game.StartGame(room);
        Assert.Equal(PhaseError.InvalidTransition, room.TryAdvancePhase(GamePhase.Lobby, Make.T0).Error);      // back
        Assert.Equal(PhaseError.InvalidTransition, room.TryAdvancePhase(GamePhase.Expansion, Make.T0).Error);  // skip

        Game.PlayBaseSelection(room); // -> Conquest
        Assert.Equal(GamePhase.Conquest, room.Phase);
        Assert.Equal(PhaseError.InvalidTransition, room.TryAdvancePhase(GamePhase.Duel, Make.T0).Error);       // skip Expansion

        Assert.True(room.TryAdvancePhase(GamePhase.Expansion, Make.T0.AddSeconds(4)).Ok);
    }

    [Fact]
    public void Removing_the_active_picker_moves_to_the_next_player()
    {
        var (room, _, _, _) = NewRoom();
        room.TryStartGame(Make.T0);
        var first = room.CurrentPickView()!.Picker.Id;

        room.RemovePlayer(first);
        var change = room.HandlePlayerRemoved(first, Make.T0.AddSeconds(1));
        Assert.NotNull(change);
        Assert.NotEqual(first, room.CurrentPickView()!.Picker.Id);
        Assert.DoesNotContain(first, room.TurnOrder);
    }
}
