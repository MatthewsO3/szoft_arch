using QuizGame.Contracts;
using QuizGame.Server.Domain;
using Xunit;

namespace QuizGame.Domain.Tests;

/// <summary>Phases, settings and the base-selection flow of a room (M3-A, plan §4.2).</summary>
public class GameRoomMapTests
{
    private static readonly TimeSpan Grace = TimeSpan.FromMilliseconds(300);

    private static (GameRoom Room, PlayerSummary Anna, PlayerSummary Bela, PlayerSummary Cili) NewRoom(
        RoomSettings? settings = null, int connected = 3)
    {
        var room = new GameRoom("123456", Guid.NewGuid(), settings ?? new RoomSettings(), 30, Make.T0);
        var players = new[] { "Anna", "Bela", "Cili" }.Select(n => room.AddPlayer(n, Make.T0).Player!).ToArray();
        foreach (var p in players.Take(connected)) room.SetConnected(p.Id, true);
        return (room, players[0], players[1], players[2]);
    }

    private static QuestionDto Est() => new() { Id = Guid.NewGuid(), Type = QuestionType.Estimation, TimeLimitSeconds = 10, CorrectNumber = 100m };

    private static QuestionDto Mc() => new()
    {
        Id = Guid.NewGuid(), Type = QuestionType.MultipleChoice, TimeLimitSeconds = 10,
        Options = [new(Guid.NewGuid(), 'A', "a"), new(Guid.NewGuid(), 'B', "b")], CorrectOption = 'B',
    };

    private static RoundInfo OpenBaseOrder(GameRoom room, DateTimeOffset? now = null)
    {
        var q = Est();
        var started = room.TryStartRound(q, new SendQuestionRequest { QuestionId = q.Id, Kind = RoundKind.BaseOrder }, now ?? Make.T0);
        Assert.True(started.Ok);
        return started.Round!;
    }

    private static void Answer(GameRoom room, RoundInfo round, Guid player, decimal number, int ms = 0) =>
        Assert.True(room.TrySubmitAnswer(player, new SubmitAnswerRequest { RoundId = round.RoundId, Number = number },
            Make.T0.AddMilliseconds(ms), Grace).Ok);

    /// <summary>Starts the game and plays the base-order round: Cili is closest, then Anna, then Bela.</summary>
    private static (GameRoom Room, PlayerSummary Anna, PlayerSummary Bela, PlayerSummary Cili) RoomAtFirstPick(RoomSettings? settings = null)
    {
        var (room, anna, bela, cili) = NewRoom(settings);
        Assert.True(room.TryStartGame(Make.T0).Ok);
        var round = OpenBaseOrder(room);
        Answer(room, round, cili.Id, 100m);
        Answer(room, round, anna.Id, 110m, 50);
        Answer(room, round, bela.Id, 150m, 20);
        Assert.NotNull(room.CloseRound(round.RoundId, Make.T0.AddSeconds(1)));
        return (room, anna, bela, cili);
    }

    // ---- starting the game ----

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
    public void Starting_generates_the_map_and_moves_to_base_selection()
    {
        var (room, _, _, _) = NewRoom();
        var started = room.TryStartGame(Make.T0);

        Assert.True(started.Ok);
        Assert.Equal(GamePhase.BaseSelection, room.Phase);
        Assert.Equal(6, started.Map!.Rows);
        Assert.Equal(8, started.Map.Cols);
        Assert.Equal(48, started.Map.Territories.Count);
        Assert.True(started.Map.Territories.All(t => t.OwnerPlayerId is null && !t.IsBase && t.Value == 100));
        Assert.Equal(48, room.Snapshot(Make.T0).Map!.Territories.Count);
        Assert.Equal(PhaseError.InvalidPhase, room.TryStartGame(Make.T0).Error);   // only once
    }

    [Fact]
    public void Map_size_follows_the_preset_and_the_values_follow_the_settings()
    {
        var (room, _, _, _) = NewRoom(new RoomSettings { MapPreset = MapPreset.Small, TerritoryValue = 7 });
        var map = room.TryStartGame(Make.T0).Map!;
        Assert.Equal((5, 5), (map.Rows, map.Cols));
        Assert.True(map.Territories.All(t => t.Value == 7));
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
        var changed = new RoomSettings { MapPreset = MapPreset.Large, BaseValue = 500, PickTimeoutSeconds = 5 };

        Assert.Equal(PhaseError.None, room.TryUpdateSettings(changed));
        Assert.Equal(changed, room.Settings);
        Assert.Equal(PhaseError.InvalidSettings, room.TryUpdateSettings(changed with { PickTimeoutSeconds = 0 }));
        Assert.Equal(PhaseError.InvalidSettings, room.TryUpdateSettings(changed with { MapPreset = (MapPreset)9 }));
        Assert.Equal(changed, room.Settings);   // refused values change nothing

        Assert.Equal(10, room.TryStartGame(Make.T0).Map!.Cols);   // the new preset is used
        Assert.Equal(PhaseError.InvalidPhase, room.TryUpdateSettings(new RoomSettings()));
    }

    // ---- phase transitions ----

    [Fact]
    public void Phases_only_move_forward_one_step_at_a_time()
    {
        var (room, _, _, _) = NewRoom();
        Assert.Equal(PhaseError.InvalidTransition, room.TryAdvancePhase(GamePhase.Conquest, Make.T0).Error);   // skip
        Assert.Equal(PhaseError.InvalidTransition, room.TryAdvancePhase(GamePhase.Results, Make.T0).Error);    // skip
        Assert.Equal(PhaseError.InvalidTransition, room.TryAdvancePhase(GamePhase.Lobby, Make.T0).Error);      // same phase
        Assert.Equal(PhaseError.InvalidTransition, room.TryAdvancePhase(GamePhase.BaseSelection, Make.T0).Error); // start via StartGame

        Game.StartGame(room);
        Assert.Equal(PhaseError.InvalidTransition, room.TryAdvancePhase(GamePhase.Lobby, Make.T0).Error);     // back
        Assert.Equal(PhaseError.InvalidTransition, room.TryAdvancePhase(GamePhase.Results, Make.T0).Error);   // skip

        Game.PlayBaseSelection(room);
        Assert.Equal(GamePhase.Conquest, room.Phase);
        Assert.Equal(PhaseError.InvalidTransition, room.TryAdvancePhase(GamePhase.BaseSelection, Make.T0).Error);

        var done = room.TryAdvancePhase(GamePhase.Results, Make.T0);
        Assert.True(done.Ok);
        Assert.Equal(GamePhase.Results, room.Phase);
        Assert.Equal(PhaseError.InvalidTransition, room.TryAdvancePhase(GamePhase.Results, Make.T0).Error);
    }

    [Fact]
    public void Conquest_needs_a_finished_base_selection()
    {
        var (room, anna, _, _) = NewRoom();
        Game.StartGame(room);
        Assert.Equal(PhaseError.BaseSelectionIncomplete, room.TryAdvancePhase(GamePhase.Conquest, Make.T0).Error); // nothing played

        var round = OpenBaseOrder(room);
        Assert.Equal(PhaseError.RoundInProgress, room.TryAdvancePhase(GamePhase.Conquest, Make.T0).Error);
        Answer(room, round, anna.Id, 100m);
        room.CloseRound(round.RoundId, Make.T0.AddSeconds(1));
        Assert.Equal(PhaseError.BaseSelectionIncomplete, room.TryAdvancePhase(GamePhase.Conquest, Make.T0).Error); // picks pending

        while (room.CurrentPickView() is { } pick)
            room.TryPick(pick.Picker.Id, pick.ValidTerritoryIndices[0], Make.T0.AddSeconds(2));
        Assert.True(room.TryAdvancePhase(GamePhase.Conquest, Make.T0.AddSeconds(3)).Ok);
    }

    [Fact]
    public void Ending_the_game_needs_the_open_round_closed()
    {
        var (room, _, _, _) = NewRoom();
        Game.StartGame(room);
        Game.PlayBaseSelection(room);

        var q = Mc();
        var round = room.TryStartRound(q, new SendQuestionRequest { QuestionId = q.Id, Kind = RoundKind.Grab }, Make.T0).Round!;
        Assert.Equal(PhaseError.RoundInProgress, room.TryAdvancePhase(GamePhase.Results, Make.T0).Error);
        room.CloseRound(round.RoundId, Make.T0.AddSeconds(1));
        Assert.True(room.TryAdvancePhase(GamePhase.Results, Make.T0.AddSeconds(2)).Ok);
    }

    // ---- which round in which phase ----

    [Fact]
    public void Round_kinds_are_tied_to_their_phase()
    {
        var (room, _, _, _) = NewRoom();
        var e = Est();
        var m = Mc();
        StartRoundError Try(QuestionDto q, RoundKind kind) =>
            room.TryStartRound(q, new SendQuestionRequest { QuestionId = q.Id, Kind = kind }, Make.T0).Error;

        Assert.Equal(StartRoundError.InvalidPhase, Try(e, RoundKind.BaseOrder));   // Lobby
        Assert.Equal(StartRoundError.InvalidPhase, Try(m, RoundKind.Grab));

        Game.StartGame(room);
        Assert.Equal(StartRoundError.InvalidPhase, Try(m, RoundKind.Grab));        // BaseSelection takes BaseOrder only
        Assert.Equal(StartRoundError.InvalidPhase, Try(e, RoundKind.Battle));

        Game.PlayBaseSelection(room);
        Assert.Equal(StartRoundError.InvalidPhase, Try(e, RoundKind.BaseOrder));   // Conquest
        Assert.Equal(StartRoundError.None, Try(m, RoundKind.Grab));
    }

    [Fact]
    public void Base_order_is_played_once()
    {
        var (room, anna, _, _) = NewRoom();
        Game.StartGame(room);
        var round = OpenBaseOrder(room);
        Answer(room, round, anna.Id, 100m);
        room.CloseRound(round.RoundId, Make.T0.AddSeconds(1));

        var q = Est();
        var again = room.TryStartRound(q, new SendQuestionRequest { QuestionId = q.Id, Kind = RoundKind.BaseOrder }, Make.T0.AddSeconds(2));
        Assert.Equal(StartRoundError.InvalidPhase, again.Error);
    }

    // ---- pick order ----

    [Fact]
    public void Players_pick_in_ranking_order()
    {
        var (room, anna, bela, cili) = RoomAtFirstPick();

        // Cili was closest, then Anna; Bela was off the most.
        var first = room.CurrentPickView()!;
        Assert.Equal(cili.Id, first.Picker.Id);
        Assert.Equal(Make.T0.AddSeconds(1 + 20), first.Deadline);   // closed at +1 s, default pick timeout 20 s
        Assert.Equal(48, first.ValidTerritoryIndices.Count);

        Assert.True(room.TryPick(cili.Id, 10, Make.T0.AddSeconds(2)).Ok);
        Assert.Equal(anna.Id, room.CurrentPickView()!.Picker.Id);
        Assert.True(room.TryPick(anna.Id, 11, Make.T0.AddSeconds(3)).Ok);
        Assert.Equal(bela.Id, room.CurrentPickView()!.Picker.Id);
        var last = room.TryPick(bela.Id, 12, Make.T0.AddSeconds(4));

        Assert.True(last.Ok);
        Assert.True(last.Change!.PickingFinished);
        Assert.Null(last.Change.NextPick);
        Assert.Null(room.CurrentPickView());
    }

    [Fact]
    public void Base_ranks_are_assigned_when_the_order_is_known()
    {
        var (room, anna, bela, cili) = RoomAtFirstPick();
        var ranks = room.Snapshot(Make.T0).Players.ToDictionary(p => p.Id, p => p.BaseRank);

        Assert.Equal(1, ranks[cili.Id]);
        Assert.Equal(2, ranks[anna.Id]);
        Assert.Equal(3, ranks[bela.Id]);
    }

    [Fact]
    public void Players_who_did_not_answer_pick_last_in_join_order()
    {
        var (room, anna, bela, cili) = NewRoom();
        Game.StartGame(room);
        var round = OpenBaseOrder(room);
        Answer(room, round, cili.Id, 400m);   // far off, but the only answer
        room.CloseRound(round.RoundId, Make.T0.AddSeconds(1));

        Assert.Equal(cili.Id, room.CurrentPickView()!.Picker.Id);
        room.TryPick(cili.Id, 0, Make.T0.AddSeconds(2));
        Assert.Equal(anna.Id, room.CurrentPickView()!.Picker.Id);
        room.TryPick(anna.Id, 1, Make.T0.AddSeconds(2));
        Assert.Equal(bela.Id, room.CurrentPickView()!.Picker.Id);
    }

    [Fact]
    public void Only_the_current_picker_may_pick_and_only_a_valid_free_territory()
    {
        var (room, anna, _, cili) = RoomAtFirstPick();

        Assert.Equal(PickError.NotYourPick, room.TryPick(anna.Id, 5, Make.T0).Error);
        Assert.Equal(PickError.NotYourPick, room.TryPick(Guid.NewGuid(), 5, Make.T0).Error);
        Assert.Equal(PickError.InvalidPick, room.TryPick(cili.Id, -1, Make.T0).Error);
        Assert.Equal(PickError.InvalidPick, room.TryPick(cili.Id, 48, Make.T0).Error);
        Assert.Equal(cili.Id, room.CurrentPickView()!.Picker.Id);   // refused picks change nothing

        Assert.True(room.TryPick(cili.Id, 5, Make.T0).Ok);
        Assert.Equal(PickError.InvalidPick, room.TryPick(anna.Id, 5, Make.T0).Error);   // taken
    }

    [Fact]
    public void Picking_without_a_pick_pending_is_refused()
    {
        var (room, anna, _, _) = NewRoom();
        Assert.Equal(PickError.InvalidPhase, room.TryPick(anna.Id, 0, Make.T0).Error);   // Lobby
        Game.StartGame(room);
        Assert.Equal(PickError.NoActivePick, room.TryPick(anna.Id, 0, Make.T0).Error);   // base order not played yet
    }

    [Fact]
    public void A_pick_makes_the_territory_a_base_worth_the_base_value()
    {
        var (room, _, _, cili) = RoomAtFirstPick(new RoomSettings { BaseValue = 250, TerritoryValue = 60 });
        var result = room.TryPick(cili.Id, 20, Make.T0.AddSeconds(2));

        var cell = Assert.Single(result.Change!.Changed);
        Assert.Equal(20, cell.Index);
        Assert.Equal(cili.Id, cell.OwnerPlayerId);
        Assert.True(cell.IsBase);
        Assert.Equal(250, cell.Value);
        Assert.Equal(60, room.GetMap()!.Territories[21].Value);   // the others keep the normal value

        var score = result.Change.Scoreboard.Single(s => s.PlayerId == cili.Id);
        Assert.Equal(250, score.Score);
        Assert.Equal(1, score.TerritoryCount);
        Assert.Equal(cili.Id, room.OwnerOf(20));
    }

    [Fact]
    public void Bases_can_be_forced_apart()
    {
        var (room, anna, _, cili) = RoomAtFirstPick(new RoomSettings { DisallowAdjacentBases = true });
        room.TryPick(cili.Id, 9, Make.T0.AddSeconds(2));

        var next = room.CurrentPickView()!;
        Assert.Equal(anna.Id, next.Picker.Id);
        foreach (var neighbour in new[] { 1, 8, 10, 17 }) Assert.False(next.ValidTerritoryIndices.Contains(neighbour));
        Assert.Equal(PickError.InvalidPick, room.TryPick(anna.Id, 10, Make.T0.AddSeconds(3)).Error);
        Assert.True(room.TryPick(anna.Id, 11, Make.T0.AddSeconds(3)).Ok);
    }

    // ---- timeout ----

    [Fact]
    public void Timeout_assigns_a_random_valid_territory_and_moves_on()
    {
        var (room, anna, _, cili) = RoomAtFirstPick();
        room.PickRandom = new Random(7);
        var pick = room.CurrentPickView()!;

        var result = room.TryAutoPick(pick.Seq, pick.Deadline);

        Assert.True(result.Ok);
        var cell = Assert.Single(result.Change!.Changed);
        Assert.Equal(cili.Id, cell.OwnerPlayerId);
        Assert.True(pick.ValidTerritoryIndices.Contains(cell.Index));
        Assert.True(cell.IsBase);
        Assert.Equal(anna.Id, result.Change.NextPick!.Picker.Id);
        Assert.Equal(pick.Deadline.AddSeconds(20), result.Change.NextPick.Deadline);
    }

    [Fact]
    public void A_stale_timeout_does_nothing()
    {
        var (room, _, _, cili) = RoomAtFirstPick();
        var pick = room.CurrentPickView()!;
        room.TryPick(cili.Id, 3, Make.T0.AddSeconds(2));          // picked in time

        Assert.Equal(PickError.NoActivePick, room.TryAutoPick(pick.Seq, pick.Deadline).Error);
        Assert.Equal(1, room.GetMap()!.Territories.Count(t => t.OwnerPlayerId is not null));
    }

    [Fact]
    public void An_offline_player_is_still_assigned_after_the_timeout()
    {
        var (room, anna, _, cili) = RoomAtFirstPick();
        room.SetConnected(cili.Id, false);
        var pick = room.CurrentPickView()!;
        Assert.Equal(cili.Id, pick.Picker.Id);
        Assert.False(pick.Picker.Connected);

        var result = room.TryAutoPick(pick.Seq, pick.Deadline);
        Assert.True(result.Ok);
        Assert.Equal(anna.Id, room.CurrentPickView()!.Picker.Id);
    }

    [Fact]
    public void A_run_of_timeouts_finishes_the_selection_with_distinct_bases()
    {
        var (room, _, _, _) = RoomAtFirstPick(new RoomSettings { DisallowAdjacentBases = true, MapPreset = MapPreset.Small });
        room.PickRandom = new Random(3);

        while (room.CurrentPickView() is { } pick)
            Assert.True(room.TryAutoPick(pick.Seq, pick.Deadline).Ok);

        var bases = room.GetMap()!.Territories.Where(t => t.IsBase).ToList();
        Assert.Equal(3, bases.Count);
        Assert.Equal(3, bases.Select(b => b.OwnerPlayerId).Distinct().Count());
        Assert.True(room.TryAdvancePhase(GamePhase.Conquest, Make.T0.AddMinutes(5)).Ok);
    }

    // ---- snapshot and reconnect ----

    [Fact]
    public void Snapshot_shows_the_pick_and_only_the_picker_gets_the_valid_cells()
    {
        var (room, anna, _, cili) = RoomAtFirstPick();

        var picker = room.Snapshot(Make.T0, cili.Id).CurrentPick!;
        Assert.Equal(cili.Id, picker.PickerPlayerId);
        Assert.Equal(48, picker.ValidTerritoryIndices.Count);

        var other = room.Snapshot(Make.T0, anna.Id).CurrentPick!;
        Assert.Equal(cili.Id, other.PickerPlayerId);
        Assert.Empty(other.ValidTerritoryIndices);
        Assert.Empty(room.Snapshot(Make.T0, forHost: true).CurrentPick!.ValidTerritoryIndices);
        Assert.Equal(picker.PickDeadlineUtc, other.PickDeadlineUtc);

        while (room.CurrentPickView() is { } pick)
            room.TryPick(pick.Picker.Id, pick.ValidTerritoryIndices[0], Make.T0.AddSeconds(2));
        Assert.Null(room.Snapshot(Make.T0, cili.Id).CurrentPick);
    }

    [Fact]
    public void Snapshot_carries_the_map_and_the_scoreboard()
    {
        var (room, anna, bela, cili) = RoomAtFirstPick();
        Assert.Equal(3, room.Snapshot(Make.T0).Scoreboard.Count);
        Assert.True(room.Snapshot(Make.T0).Scoreboard.All(s => s.Score == 0));

        room.TryPick(cili.Id, 0, Make.T0);
        room.TryPick(anna.Id, 1, Make.T0);
        room.TryPick(bela.Id, 2, Make.T0);

        var board = room.Snapshot(Make.T0).Scoreboard;
        Assert.True(board.All(s => s.Score == 300 && s.TerritoryCount == 1));
        Assert.Equal(cili.Id, board[0].PlayerId);   // equal score and territories: earlier base rank first
        Assert.Equal(anna.Id, board[1].PlayerId);
        Assert.Equal(bela.Id, board[2].PlayerId);
        Assert.Equal(3, room.Snapshot(Make.T0).Map!.Territories.Count(t => t.IsBase));
    }

    [Fact]
    public void Lobby_snapshot_has_no_map_pick_or_scores()
    {
        var (room, _, _, _) = NewRoom();
        var s = room.Snapshot(Make.T0);
        Assert.Null(s.Map);
        Assert.Null(s.CurrentPick);
        Assert.Empty(s.Scoreboard);
    }

    // ---- players leaving ----

    [Fact]
    public void When_the_picker_leaves_the_next_player_is_up()
    {
        var (room, anna, _, cili) = RoomAtFirstPick();
        room.RemovePlayer(cili.Id);

        var change = room.HandlePlayerRemoved(cili.Id, Make.T0.AddSeconds(5));

        Assert.NotNull(change);
        Assert.Equal(anna.Id, change!.NextPick!.Picker.Id);
        Assert.Equal(Make.T0.AddSeconds(25), change.NextPick.Deadline);
        Assert.Empty(change.Changed);
    }

    [Fact]
    public void A_player_who_leaves_frees_their_base_and_their_place_in_the_queue()
    {
        var (room, anna, bela, cili) = RoomAtFirstPick();
        room.TryPick(cili.Id, 4, Make.T0);                       // Cili has a base, Anna is up, Bela waits
        room.RemovePlayer(bela.Id);
        Assert.Null(room.HandlePlayerRemoved(bela.Id, Make.T0)?.Changed.FirstOrDefault());   // queue entry gone, nothing on the map
        Assert.Equal(anna.Id, room.CurrentPickView()!.Picker.Id);

        room.RemovePlayer(cili.Id);
        var change = room.HandlePlayerRemoved(cili.Id, Make.T0)!;

        var freed = Assert.Single(change.Changed);
        Assert.Equal(4, freed.Index);
        Assert.Null(freed.OwnerPlayerId);
        Assert.False(freed.IsBase);
        Assert.Equal(100, freed.Value);
        Assert.Single(change.Scoreboard);
        Assert.Null(room.OwnerOf(4));

        room.TryPick(anna.Id, 4, Make.T0);                       // the freed cell can be picked again
        Assert.True(room.GetMap()!.Territories[4].IsBase);
        Assert.Null(room.CurrentPickView());                     // Anna was the last one
        Assert.True(room.TryAdvancePhase(GamePhase.Conquest, Make.T0).Ok);
    }

    [Fact]
    public void Removing_a_player_without_a_map_or_pick_changes_nothing()
    {
        var (room, anna, _, _) = NewRoom();
        room.RemovePlayer(anna.Id);
        Assert.Null(room.HandlePlayerRemoved(anna.Id, Make.T0));
    }

    // ---- final standings ----

    [Fact]
    public void Final_standings_share_a_rank_on_equal_scores()
    {
        var (room, anna, bela, cili) = NewRoom();
        Game.StartGame(room);
        Game.PlayBaseSelection(room);
        // every player owns exactly their base (300): all tied
        var standings = room.BuildGameFinished().Standings;
        Assert.Equal(3, standings.Count);
        Assert.True(standings.All(s => s.Rank == 1 && s.Score == 300));
        Assert.Equal(48, room.BuildGameFinished().FinalMap.Territories.Count);
        Assert.Equal(new[] { anna.Id, bela.Id, cili.Id }, standings.Select(s => s.PlayerId).ToArray());
    }
}
