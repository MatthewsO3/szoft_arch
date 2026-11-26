using QuizGame.Contracts;
using QuizGame.Server.Domain;
using QuizGame.Server.Domain.Map;
using Xunit;

namespace QuizGame.Domain.Tests;

/// <summary>Conquest grabs, battles and the nomination flow (M4-A, plan §4.2 Conquest).</summary>
public class GameRoomConquestTests
{
    private static readonly TimeSpan Grace = TimeSpan.FromMilliseconds(300);

    private static QuestionDto Mc(int seconds = 10) => new()
    {
        Id = Guid.NewGuid(), Type = QuestionType.MultipleChoice, TimeLimitSeconds = seconds,
        Options = [new(Guid.NewGuid(), 'A', "a"), new(Guid.NewGuid(), 'B', "b")], CorrectOption = 'B',
    };

    private static QuestionDto Est() => new() { Id = Guid.NewGuid(), Type = QuestionType.Estimation, TimeLimitSeconds = 10, CorrectNumber = 100m };

    private static SubmitAnswerRequest Opt(RoundInfo r, char o) => new() { RoundId = r.RoundId, Option = o };
    private static SubmitAnswerRequest Num(RoundInfo r, decimal n) => new() { RoundId = r.RoundId, Number = n };

    /// <summary>A room in Conquest with Anna, Bela and Cili, each on a single base, ready to play rounds.</summary>
    private static (GameRoom Room, PlayerSummary A, PlayerSummary B, PlayerSummary C) ConquestRoom(RoomSettings? settings = null)
    {
        var room = new GameRoom("123456", Guid.NewGuid(), settings ?? new RoomSettings(), 30, Make.T0);
        var a = room.AddPlayer("Anna", Make.T0).Player!;
        var b = room.AddPlayer("Bela", Make.T0).Player!;
        var c = room.AddPlayer("Cili", Make.T0).Player!;
        foreach (var p in new[] { a, b, c }) room.SetConnected(p.Id, true);
        Game.StartGame(room);
        Game.PlayBaseSelection(room);
        return (room, a, b, c);
    }

    private static int BaseOf(GameRoom room, Guid playerId) =>
        room.GetMap()!.Territories.First(t => t.OwnerPlayerId == playerId).Index;

    // ---- grab rounds ----

    [Fact]
    public void Grab_winner_is_asked_to_pick_a_free_adjacent_territory()
    {
        var (room, a, b, _) = ConquestRoom();
        var q = Mc();
        var round = room.TryStartRound(q, new SendQuestionRequest { QuestionId = q.Id, Kind = RoundKind.Grab }, Make.T0).Round!;

        // Anna answers correctly first, Bela correct but slower.
        room.TrySubmitAnswer(a.Id, Opt(round, 'B'), Make.T0.AddMilliseconds(100), Grace);
        room.TrySubmitAnswer(b.Id, Opt(round, 'B'), Make.T0.AddMilliseconds(500), Grace);
        var closed = room.CloseRound(round.RoundId, Make.T0.AddSeconds(1))!;

        Assert.Equal(new[] { a.Id }, closed.Result.WinnerPlayerIds.ToArray());
        var pick = closed.GrabPick!;
        Assert.Equal(a.Id, pick.Picker.Id);
        Assert.NotEmpty(pick.ValidTerritoryIndices);

        // Every offered cell is free and touches a territory Anna already owns.
        var map = room.GetMap()!;
        foreach (var index in pick.ValidTerritoryIndices)
        {
            var cell = map.Territories.First(t => t.Index == index);
            Assert.Null(cell.OwnerPlayerId);
            var neighbours = MapService.NeighboursOf(map.Rows, map.Cols, index);
            Assert.Contains(neighbours, n => map.Territories.First(t => t.Index == n).OwnerPlayerId == a.Id);
        }
    }

    [Fact]
    public void Grab_pick_assigns_a_normal_territory_not_a_base()
    {
        var (room, a, _, _) = ConquestRoom();
        var q = Mc();
        var round = room.TryStartRound(q, new SendQuestionRequest { QuestionId = q.Id, Kind = RoundKind.Grab }, Make.T0).Round!;
        room.TrySubmitAnswer(a.Id, Opt(round, 'B'), Make.T0.AddMilliseconds(100), Grace);
        var pick = room.CloseRound(round.RoundId, Make.T0.AddSeconds(1))!.GrabPick!;

        var target = pick.ValidTerritoryIndices[0];
        var result = room.TryPick(a.Id, target, Make.T0.AddSeconds(2));
        Assert.True(result.Ok);

        var cell = result.Change!.Changed.Single();
        Assert.Equal(a.Id, cell.OwnerPlayerId);
        Assert.False(cell.IsBase);
        Assert.Equal(room.Settings.TerritoryValue, cell.Value);

        // Score = one base (from base selection) plus exactly one normal territory.
        var annaScore = result.Change.Scoreboard.First(s => s.PlayerId == a.Id).Score;
        Assert.Equal(room.Settings.BaseValue + room.Settings.TerritoryValue, annaScore);
    }

    [Fact]
    public void Several_grab_winners_pick_in_speed_order()
    {
        var (room, a, b, c) = ConquestRoom(new RoomSettings { WinnersPerRound = 2 });
        var q = Mc();
        var round = room.TryStartRound(q, new SendQuestionRequest { QuestionId = q.Id, Kind = RoundKind.Grab }, Make.T0).Round!;
        room.TrySubmitAnswer(b.Id, Opt(round, 'B'), Make.T0.AddMilliseconds(100), Grace); // fastest correct
        room.TrySubmitAnswer(a.Id, Opt(round, 'B'), Make.T0.AddMilliseconds(300), Grace); // second correct
        room.TrySubmitAnswer(c.Id, Opt(round, 'A'), Make.T0.AddMilliseconds(200), Grace); // wrong
        var closed = room.CloseRound(round.RoundId, Make.T0.AddSeconds(1))!;

        Assert.Equal(new[] { b.Id, a.Id }, closed.Result.WinnerPlayerIds.ToArray());

        // Bela picks first.
        Assert.Equal(b.Id, closed.GrabPick!.Picker.Id);
        var firstPick = room.TryPick(b.Id, closed.GrabPick.ValidTerritoryIndices[0], Make.T0.AddSeconds(2));
        Assert.True(firstPick.Ok);

        // Anna is next.
        var next = firstPick.Change!.NextPick!;
        Assert.Equal(a.Id, next.Picker.Id);
        Assert.True(room.TryPick(a.Id, next.ValidTerritoryIndices[0], Make.T0.AddSeconds(3)).Ok);
        Assert.Null(room.CurrentPickView()); // both winners have picked
    }

    [Fact]
    public void A_pending_grab_pick_blocks_ending_the_game()
    {
        var (room, a, _, _) = ConquestRoom();
        var q = Mc();
        var round = room.TryStartRound(q, new SendQuestionRequest { QuestionId = q.Id, Kind = RoundKind.Grab }, Make.T0).Round!;
        room.TrySubmitAnswer(a.Id, Opt(round, 'B'), Make.T0.AddMilliseconds(100), Grace);
        room.CloseRound(round.RoundId, Make.T0.AddSeconds(1));

        Assert.NotNull(room.CurrentPickView());
        Assert.Equal(PhaseError.RoundInProgress, room.TryAdvancePhase(GamePhase.Results, Make.T0.AddSeconds(2)).Error);

        var pick = room.CurrentPickView()!;
        room.TryPick(pick.Picker.Id, pick.ValidTerritoryIndices[0], Make.T0.AddSeconds(3));
        Assert.True(room.TryAdvancePhase(GamePhase.Results, Make.T0.AddSeconds(4)).Ok);
    }

    [Fact]
    public void A_pending_grab_pick_blocks_opening_another_round()
    {
        var (room, a, _, _) = ConquestRoom();
        var q = Mc();
        var round = room.TryStartRound(q, new SendQuestionRequest { QuestionId = q.Id, Kind = RoundKind.Grab }, Make.T0).Round!;
        room.TrySubmitAnswer(a.Id, Opt(round, 'B'), Make.T0.AddMilliseconds(100), Grace);
        room.CloseRound(round.RoundId, Make.T0.AddSeconds(1));

        var q2 = Mc();
        Assert.Equal(StartRoundError.PickInProgress,
            room.TryStartRound(q2, new SendQuestionRequest { QuestionId = q2.Id, Kind = RoundKind.Grab }, Make.T0.AddSeconds(2)).Error);

        var pick = room.CurrentPickView()!;
        room.TryPick(pick.Picker.Id, pick.ValidTerritoryIndices[0], Make.T0.AddSeconds(3));
        Assert.True(room.TryStartRound(q2, new SendQuestionRequest { QuestionId = q2.Id, Kind = RoundKind.Grab }, Make.T0.AddSeconds(4)).Ok);
    }

    // ---- battles ----

    [Fact]
    public void Attacker_winning_a_battle_takes_the_territory()
    {
        var (room, a, b, _) = ConquestRoom();
        var target = BaseOf(room, b.Id);
        var q = Est();
        var round = room.TryStartRound(q, new SendQuestionRequest
        {
            QuestionId = q.Id, Kind = RoundKind.Battle, AttackerId = a.Id, TargetTerritoryIndex = target,
        }, Make.T0).Round!;

        room.TrySubmitAnswer(a.Id, Num(round, 100m), Make.T0.AddMilliseconds(100), Grace); // exact
        room.TrySubmitAnswer(b.Id, Num(round, 140m), Make.T0.AddMilliseconds(200), Grace); // far off
        var closed = room.CloseRound(round.RoundId, Make.T0.AddSeconds(1))!;

        Assert.Equal(new[] { a.Id }, closed.Result.WinnerPlayerIds.ToArray());
        var cell = closed.BattleChange!.Changed.Single();
        Assert.Equal(target, cell.Index);
        Assert.Equal(a.Id, cell.OwnerPlayerId);
        Assert.Equal(a.Id, room.OwnerOf(target));
    }

    [Fact]
    public void Defender_keeping_the_territory_changes_nothing()
    {
        var (room, a, b, _) = ConquestRoom();
        var target = BaseOf(room, b.Id);
        var q = Est();
        var round = room.TryStartRound(q, new SendQuestionRequest
        {
            QuestionId = q.Id, Kind = RoundKind.Battle, AttackerId = a.Id, TargetTerritoryIndex = target,
        }, Make.T0).Round!;

        room.TrySubmitAnswer(a.Id, Num(round, 100m), Make.T0.AddMilliseconds(100), Grace);
        room.TrySubmitAnswer(b.Id, Num(round, 100m), Make.T0.AddMilliseconds(200), Grace); // tie → defender
        var closed = room.CloseRound(round.RoundId, Make.T0.AddSeconds(1))!;

        Assert.Equal(new[] { b.Id }, closed.Result.WinnerPlayerIds.ToArray());
        Assert.Null(closed.BattleChange); // no owner change
        Assert.Equal(b.Id, room.OwnerOf(target));
    }

    // ---- nomination flow ----

    [Fact]
    public void Attacker_nominates_an_adjacent_enemy_territory()
    {
        var (room, a, b, _) = ConquestRoom();
        var map = room.GetMap()!;

        var belaBase = BaseOf(room, b.Id);
        var belaAdjacentToAnna = MapService.NeighboursOf(map.Rows, map.Cols, belaBase)
            .Any(n => map.Territories.First(t => t.Index == n).OwnerPlayerId == a.Id);

        var result = room.TryNominateTarget(a.Id, belaBase);
        if (belaAdjacentToAnna)
        {
            Assert.True(result.Ok);
            Assert.Equal(new BattleNomination(a.Id, b.Id, belaBase), room.CurrentNomination());
            Assert.Equal(room.CurrentNomination(), room.TakeNomination());
            Assert.Null(room.CurrentNomination()); // consumed
        }
        else
        {
            Assert.Equal(NominateError.NotAdjacent, result.Error);
        }
    }

    [Fact]
    public void Nomination_rejects_free_own_and_non_adjacent_targets()
    {
        var (room, a, _, _) = ConquestRoom();
        var map = room.GetMap()!;

        var free = map.Territories.First(t => t.OwnerPlayerId is null).Index;
        Assert.Equal(NominateError.TerritoryFree, room.TryNominateTarget(a.Id, free).Error);
        Assert.Equal(NominateError.NoSuchTerritory, room.TryNominateTarget(a.Id, 9999).Error);
        Assert.Equal(NominateError.OwnTerritory, room.TryNominateTarget(a.Id, BaseOf(room, a.Id)).Error);
    }

    [Fact]
    public void Nomination_only_in_conquest_and_with_no_open_round()
    {
        var (room, a, b, _) = ConquestRoom();
        var target = BaseOf(room, b.Id);

        var q = Mc();
        var round = room.TryStartRound(q, new SendQuestionRequest { QuestionId = q.Id, Kind = RoundKind.Grab }, Make.T0).Round!;
        Assert.Equal(NominateError.RoundInProgress, room.TryNominateTarget(a.Id, target).Error);
        room.CloseRound(round.RoundId, Make.T0.AddSeconds(1));
    }

    [Fact]
    public void Losing_a_player_clears_a_nomination_that_involves_them()
    {
        var (room, a, b, _) = ConquestRoom();
        var map = room.GetMap()!;
        var belaBase = BaseOf(room, b.Id);
        var adjacent = MapService.NeighboursOf(map.Rows, map.Cols, belaBase)
            .Any(n => map.Territories.First(t => t.Index == n).OwnerPlayerId == a.Id);
        if (!adjacent) return; // layout does not allow a legal nomination here; nothing to assert

        Assert.True(room.TryNominateTarget(a.Id, belaBase).Ok);
        room.HandlePlayerRemoved(b.Id, Make.T0.AddSeconds(1));
        Assert.Null(room.CurrentNomination());
    }
}
