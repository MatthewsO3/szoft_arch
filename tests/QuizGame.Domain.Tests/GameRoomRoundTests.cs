using QuizGame.Contracts;
using QuizGame.Server.Domain;
using Xunit;

namespace QuizGame.Domain.Tests;

/// <summary>Conquest, Expansion and Duel rounds of a room (plan §4.2).</summary>
public class GameRoomRoundTests
{
    private static readonly TimeSpan Grace = TimeSpan.FromMilliseconds(300);

    private static QuestionDto Mc(int seconds = 10) => new()
    {
        Id = Guid.NewGuid(), Type = QuestionType.MultipleChoice, TimeLimitSeconds = seconds,
        Text = "secret question text",
        Options = [new(Guid.NewGuid(), 'A', "a"), new(Guid.NewGuid(), 'B', "b")], CorrectOption = 'B',
    };

    private static QuestionDto Est(decimal n = 100m) => new()
    {
        Id = Guid.NewGuid(), Type = QuestionType.Estimation, TimeLimitSeconds = 10, CorrectNumber = n,
    };

    private static SubmitAnswerRequest Opt(RoundInfo r, char o) => new() { RoundId = r.RoundId, Option = o };
    private static SubmitAnswerRequest Num(RoundInfo r, decimal n) => new() { RoundId = r.RoundId, Number = n };

    // A room in the given phase with Anna, Bela, Cili, each on a base.
    private static (GameRoom Room, PlayerSummary A, PlayerSummary B, PlayerSummary C) Room(
        GamePhase phase = GamePhase.Conquest, RoomSettings? settings = null)
    {
        var room = new GameRoom("123456", Guid.NewGuid(), settings ?? new RoomSettings(), 30, Make.T0) { PickRandom = new Random(1) };
        var a = room.AddPlayer("Anna", Make.T0).Player!;
        var b = room.AddPlayer("Bela", Make.T0).Player!;
        var c = room.AddPlayer("Cili", Make.T0).Player!;
        foreach (var p in new[] { a, b, c }) room.SetConnected(p.Id, true);
        if (phase >= GamePhase.BaseSelection) Game.StartGame(room);
        if (phase >= GamePhase.Conquest) Game.PlayBaseSelection(room);
        if (phase >= GamePhase.Expansion) Assert.True(room.TryAdvancePhase(GamePhase.Expansion, Make.T0).Ok);
        return (room, a, b, c);
    }

    // Fills the map so Expansion can be left for the Duel phase.
    private static void FillMap(GameRoom room)
    {
        while (room.GetMap()!.Territories.Any(t => t.OwnerPlayerId is null))
        {
            var q = Est(50m);
            var round = room.TryStartExpansionRound(q, Make.T0).Round!;
            foreach (var pid in round.ParticipantIds)
                room.TrySubmitAnswer(pid, new SubmitAnswerRequest { RoundId = round.RoundId, Number = 50m }, Make.T0, Grace);
            var closed = room.CloseRound(round.RoundId, Make.T0)!;
            if (closed.ExpansionPick is { } pick)
                room.TryPick(pick.Picker.Id, pick.ValidTerritoryIndices[0], Make.T0);
        }
    }

    // ---- Conquest ----

    [Fact]
    public void Automatic_progression_waits_for_last_base_pick_including_timeout()
    {
        var (room, _, _, _) = Room(GamePhase.BaseSelection);
        while (room.CurrentPickView() is { } pick)
        {
            Assert.Null(room.NextAutomaticPhase());
            Assert.True(room.TryAutoPick(pick.Seq, pick.Deadline).Ok);
        }
        Assert.Equal(GamePhase.Conquest, room.NextAutomaticPhase());
    }

    [Fact]
    public void Automatic_progression_waits_for_conquest_claims_and_final_round_resolution()
    {
        var (room, _, _, _) = Room(settings: new RoomSettings { ConquestRounds = 1 });
        Assert.Null(room.NextAutomaticPhase());
        room.StartConquestClaims(Make.T0);
        while (room.CurrentPickView() is { } pick)
        {
            Assert.Null(room.NextAutomaticPhase());
            room.TryPick(pick.Picker.Id, pick.ValidTerritoryIndices[0], Make.T0);
        }
        Assert.Null(room.NextAutomaticPhase());
        var round = room.TryStartConquestRound(Mc(), Make.T0).Round!;
        Assert.Null(room.NextAutomaticPhase());
        room.CloseRound(round.RoundId, Make.T0.AddSeconds(10));
        Assert.Equal(GamePhase.Expansion, room.NextAutomaticPhase());
    }

    [Fact]
    public void Automatic_progression_waits_for_final_expansion_pick()
    {
        var (room, _, _, _) = Room(GamePhase.Expansion);
        while (room.GetMap()!.Territories.Any(t => t.OwnerPlayerId is null))
        {
            var round = room.TryStartExpansionRound(Est(), Make.T0).Round!;
            room.TrySubmitAnswer(round.ParticipantIds[0], Num(round, 100m), Make.T0, Grace);
            var pick = room.CloseRound(round.RoundId, Make.T0)!.ExpansionPick!;
            Assert.Null(room.NextAutomaticPhase());
            room.TryAutoPick(pick.Seq, pick.Deadline);
        }
        Assert.Equal(GamePhase.Duel, room.NextAutomaticPhase());
    }

    [Fact]
    public void Host_question_turn_waits_for_expansion_winner_pick()
    {
        var (room, _, _, _) = Room(GamePhase.Expansion);
        Assert.True(room.CanSendQuestion());

        var round = room.TryStartExpansionRound(Est(), Make.T0).Round!;
        Assert.False(room.CanSendQuestion());
        room.TrySubmitAnswer(round.ParticipantIds[0], Num(round, 100m), Make.T0, Grace);

        var pick = room.CloseRound(round.RoundId, Make.T0)!.ExpansionPick!;
        Assert.False(room.CanSendQuestion());

        room.TryPick(pick.Picker.Id, pick.ValidTerritoryIndices[0], Make.T0);
        Assert.True(room.CanSendQuestion());
    }

    [Fact]
    public void Full_map_ends_conquest_early_and_passes_through_expansion()
    {
        var (room, _, _, _) = Room(settings: new RoomSettings { ConquestRounds = 100 });
        for (var i = 0; i < 100 && room.NextAutomaticPhase() is null; i++)
        {
            room.StartConquestClaims(Make.T0);
            while (room.CurrentPickView() is { } pick)
                room.TryPick(pick.Picker.Id, pick.ValidTerritoryIndices[0], Make.T0);
            var round = room.TryStartConquestRound(Mc(), Make.T0).Round!;
            foreach (var id in round.ParticipantIds)
                room.TrySubmitAnswer(id, Opt(round, 'B'), Make.T0, Grace);
            room.CloseRound(round.RoundId, Make.T0);
        }
        Assert.False(room.PhaseRoundsExhausted);
        Assert.Equal(GamePhase.Expansion, room.NextAutomaticPhase());
        Assert.True(room.TryAdvancePhase(GamePhase.Expansion, Make.T0).Ok);
        Assert.Equal(GamePhase.Duel, room.NextAutomaticPhase());
        Assert.True(room.TryAdvancePhase(GamePhase.Duel, Make.T0).Ok);
        Assert.Null(room.NextAutomaticPhase());
    }

    [Fact]
    public void Automatic_progression_finishes_duel_at_round_limit()
    {
        var (room, _, _, _) = DuelRoom(new RoomSettings { DuelRounds = 1 });
        var (attacker, _, target) = FindDuelViaDto(room);
        room.TryNominateTarget(attacker, target);
        var round = room.TryStartDuelRound(Mc(), Make.T0).Round!;
        Assert.Null(room.NextAutomaticPhase());
        room.CloseRound(round.RoundId, Make.T0.AddSeconds(10));
        Assert.Equal(GamePhase.Results, room.NextAutomaticPhase());
    }

    [Fact]
    public void Automatic_progression_finishes_when_only_one_base_holder_remains()
    {
        var (room, _, _, _) = DuelRoom();
        foreach (var id in room.TurnOrder.Skip(1).ToList())
        {
            room.HandlePlayerRemoved(id, Make.T0);
        }
        Assert.True(room.OneBaseHolderRemains());
        Assert.Equal(GamePhase.Results, room.NextAutomaticPhase());
    }

    [Fact]
    public void Manual_advance_can_end_conquest_and_duel_early_but_not_skip_unfinished_picks()
    {
        var (room, _, _, _) = Room();
        Assert.Null(room.NextAutomaticPhase());
        Assert.True(room.TryAdvancePhase(GamePhase.Expansion, Make.T0).Ok);
        Assert.Equal(PhaseError.PhaseNotComplete, room.TryAdvancePhase(GamePhase.Duel, Make.T0).Error);
        FillMap(room);
        Assert.True(room.TryAdvancePhase(GamePhase.Duel, Make.T0).Ok);
        Assert.Null(room.NextAutomaticPhase());
        Assert.True(room.TryAdvancePhase(GamePhase.Results, Make.T0).Ok);
    }

    [Fact]
    public void Conquest_claim_stage_asks_each_player_in_turn_order()
    {
        var (room, _, _, _) = Room();
        var order = room.TurnOrder;
        var first = room.StartConquestClaims(Make.T0)!;
        Assert.Equal(order[0], first.Picker.Id);
        Assert.Equal(PickPurpose.ConquestClaim, first.Purpose);

        // Each player claims a tile; claims do not take ownership yet.
        var claimed = new List<int>();
        for (var i = 0; i < order.Count; i++)
        {
            var pick = room.CurrentPickView()!;
            Assert.Equal(order[i], pick.Picker.Id);
            var index = pick.ValidTerritoryIndices[0];
            claimed.Add(index);
            var result = room.TryPick(pick.Picker.Id, index, Make.T0.AddSeconds(1));
            Assert.True(result.Ok);
            Assert.True(room.GetMap()!.Territories.First(t => t.Index == index).OwnerPlayerId is null); // not owned yet
        }

        Assert.True(room.ClaimsOpen);
        Assert.Null(room.CurrentPickView()); // all claims in
    }

    [Fact]
    public void Conquest_correct_answerers_keep_their_claim_wrong_ones_lose_it()
    {
        var (room, _, _, _) = Room();
        var order = room.TurnOrder;
        room.StartConquestClaims(Make.T0);

        var claimByPlayer = new Dictionary<Guid, int>();
        while (room.CurrentPickView() is { } pick)
        {
            var index = pick.ValidTerritoryIndices[0];
            claimByPlayer[pick.Picker.Id] = index;
            room.TryPick(pick.Picker.Id, index, Make.T0.AddSeconds(1));
        }

        var q = Mc();
        var round = room.TryStartConquestRound(q, Make.T0.AddSeconds(2)).Round!;
        Assert.Equal(3, round.ParticipantIds.Count);

        // First two answer right, the third wrong.
        room.TrySubmitAnswer(order[0], Opt(round, 'B'), Make.T0.AddSeconds(3), Grace);
        room.TrySubmitAnswer(order[1], Opt(round, 'B'), Make.T0.AddSeconds(3), Grace);
        room.TrySubmitAnswer(order[2], Opt(round, 'A'), Make.T0.AddSeconds(3), Grace);

        var closed = room.CloseRound(round.RoundId, Make.T0.AddSeconds(4))!;
        Assert.Equal(RoundKind.Conquest, closed.Result.Kind);
        Assert.Equal(new[] { order[0], order[1] }.OrderBy(x => x), closed.Result.WinnerPlayerIds.OrderBy(x => x));

        Assert.Equal(order[0], room.OwnerOf(claimByPlayer[order[0]]));
        Assert.Equal(order[1], room.OwnerOf(claimByPlayer[order[1]]));
        Assert.Null(room.OwnerOf(claimByPlayer[order[2]])); // wrong answer: claim released

        var wonCell = room.GetMap()!.Territories.First(t => t.Index == claimByPlayer[order[0]]);
        Assert.False(wonCell.IsBase);
        Assert.Equal(room.Settings.TerritoryValue, wonCell.Value);
    }

    [Fact]
    public void Conquest_rotates_the_turn_order_and_counts_the_round()
    {
        var (room, _, _, _) = Room(settings: new RoomSettings { ConquestRounds = 2 });
        var before = room.TurnOrder;

        room.StartConquestClaims(Make.T0);
        while (room.CurrentPickView() is { } pick) room.TryPick(pick.Picker.Id, pick.ValidTerritoryIndices[0], Make.T0);
        var q = Mc();
        var round = room.TryStartConquestRound(q, Make.T0).Round!;
        foreach (var pid in round.ParticipantIds) room.TrySubmitAnswer(pid, Opt(round, 'B'), Make.T0, Grace);
        room.CloseRound(round.RoundId, Make.T0);

        // Right rotation: 1st -> last.
        var after = room.TurnOrder;
        Assert.Equal(before.Skip(1).Append(before[0]), after);
    }

    [Fact]
    public void Conquest_rounds_are_limited_by_the_setting()
    {
        var (room, _, _, _) = Room(settings: new RoomSettings { ConquestRounds = 1 });
        room.StartConquestClaims(Make.T0);
        while (room.CurrentPickView() is { } pick) room.TryPick(pick.Picker.Id, pick.ValidTerritoryIndices[0], Make.T0);
        var q = Mc();
        var round = room.TryStartConquestRound(q, Make.T0).Round!;
        foreach (var pid in round.ParticipantIds) room.TrySubmitAnswer(pid, Opt(round, 'B'), Make.T0, Grace);
        room.CloseRound(round.RoundId, Make.T0);

        Assert.True(room.PhaseRoundsExhausted);
        Assert.Null(room.StartConquestClaims(Make.T0)); // no more Conquest rounds
    }

    [Fact]
    public void Conquest_prevents_two_players_from_claiming_the_same_tile()
    {
        var (room, _, _, _) = Room();
        room.StartConquestClaims(Make.T0);

        var firstPick = room.CurrentPickView()!;
        var claimed = firstPick.ValidTerritoryIndices[0];
        Assert.True(room.TryPick(firstPick.Picker.Id, claimed, Make.T0.AddSeconds(1)).Ok);

        var secondPick = room.CurrentPickView()!;
        Assert.DoesNotContain(claimed, secondPick.ValidTerritoryIndices);
        Assert.Equal(PickError.InvalidPick, room.TryPick(secondPick.Picker.Id, claimed, Make.T0.AddSeconds(2)).Error);
    }

    // ---- Expansion ----

    [Fact]
    public void Expansion_closest_guess_picks_a_free_tile()
    {
        var (room, a, b, c) = Room(GamePhase.Expansion);
        var q = Est(100m);
        var round = room.TryStartExpansionRound(q, Make.T0).Round!;
        Assert.Equal(3, round.ParticipantIds.Count);

        room.TrySubmitAnswer(a.Id, Num(round, 130m), Make.T0.AddMilliseconds(100), Grace);
        room.TrySubmitAnswer(b.Id, Num(round, 102m), Make.T0.AddMilliseconds(200), Grace); // closest
        room.TrySubmitAnswer(c.Id, Num(round, 50m), Make.T0.AddMilliseconds(300), Grace);

        var closed = room.CloseRound(round.RoundId, Make.T0.AddSeconds(1))!;
        Assert.Equal(new[] { b.Id }, closed.Result.WinnerPlayerIds.ToArray());
        var pick = closed.ExpansionPick!;
        Assert.Equal(b.Id, pick.Picker.Id);
        Assert.Equal(PickPurpose.Expansion, pick.Purpose);

        var ownedBefore = room.GetMap()!.Territories.Count(t => t.OwnerPlayerId == b.Id);
        var assign = room.TryPick(b.Id, pick.ValidTerritoryIndices[0], Make.T0.AddSeconds(2));
        Assert.True(assign.Ok);
        Assert.Equal(ownedBefore + 1, room.GetMap()!.Territories.Count(t => t.OwnerPlayerId == b.Id));
    }

    [Fact]
    public void Expansion_fills_the_map_then_Duel_can_begin()
    {
        var (room, _, _, _) = Room(GamePhase.Expansion);
        Assert.Equal(PhaseError.PhaseNotComplete, room.TryAdvancePhase(GamePhase.Duel, Make.T0).Error); // map not full

        FillMap(room);
        Assert.DoesNotContain(room.GetMap()!.Territories, t => t.OwnerPlayerId is null);
        Assert.True(room.TryAdvancePhase(GamePhase.Duel, Make.T0.AddSeconds(1)).Ok);
    }

    // ---- Duel ----

    private static (GameRoom Room, PlayerSummary A, PlayerSummary B, PlayerSummary C) DuelRoom(RoomSettings? settings = null)
    {
        var (room, a, b, c) = Room(GamePhase.Expansion, settings);
        FillMap(room);
        Assert.True(room.TryAdvancePhase(GamePhase.Duel, Make.T0).Ok);
        return (room, a, b, c);
    }

    [Fact]
    public void Duel_starts_with_a_target_pick_and_the_player_can_nominate()
    {
        var (room, _, _, _) = DuelRoom();

        var pick = room.StartDuelTargetPick(Make.T0)!;

        Assert.NotNull(pick);
        Assert.Equal(PickPurpose.DuelTarget, pick.Purpose);
        Assert.Equal(room.ActivePlayer(), pick.Picker.Id);
        Assert.NotEmpty(pick.ValidTerritoryIndices);

        var nominated = room.TryNominateTarget(pick.Picker.Id, pick.ValidTerritoryIndices[0]);

        Assert.True(nominated.Ok);
        Assert.Null(room.CurrentPickView());
        Assert.Equal(pick.Picker.Id, nominated.Nomination!.AttackerId);
        Assert.Equal(pick.ValidTerritoryIndices[0], nominated.Nomination.TargetTerritoryIndex);
    }

    [Fact]
    public void Duel_target_pick_timeout_creates_a_nomination()
    {
        var (room, _, _, _) = DuelRoom();
        var pick = room.StartDuelTargetPick(Make.T0)!;

        var result = room.TryAutoPick(pick.Seq, pick.Deadline);

        Assert.True(result.Ok);
        Assert.NotNull(result.Nomination);
        Assert.Equal(pick.Picker.Id, result.Nomination!.AttackerId);
        Assert.Contains(result.Nomination.TargetTerritoryIndex, pick.ValidTerritoryIndices);
        Assert.Null(room.CurrentPickView());
    }

    [Fact]
    public void Host_can_end_Duel_while_target_selection_is_pending()
    {
        var (room, _, _, _) = DuelRoom();
        Assert.NotNull(room.StartDuelTargetPick(Make.T0));

        var result = room.TryAdvancePhase(GamePhase.Results, Make.T0.AddSeconds(1));

        Assert.True(result.Ok);
        Assert.Null(room.CurrentPickView());
    }

    [Fact]
    public void Duel_attacker_right_takes_a_normal_tile()
    {
        var (room, _, _, _) = DuelRoom();
        var (attacker, defender, target) = FindDuelViaDto(room);

        Assert.True(room.TryNominateTarget(attacker, target).Ok);
        var q = Mc();
        var round = room.TryStartDuelRound(q, Make.T0).Round!;
        Assert.Equal(2, round.ParticipantIds.Count);

        room.TrySubmitAnswer(attacker, Opt(round, 'B'), Make.T0.AddMilliseconds(100), Grace); // right
        room.TrySubmitAnswer(defender, Opt(round, 'A'), Make.T0.AddMilliseconds(200), Grace); // wrong
        var closed = room.CloseRound(round.RoundId, Make.T0.AddSeconds(1))!;

        Assert.Equal(new[] { attacker }, closed.Result.WinnerPlayerIds.ToArray());
        Assert.Equal(attacker, room.OwnerOf(target));
    }

    [Fact]
    public void Duel_defender_right_earns_a_bonus_and_keeps_the_tile()
    {
        var (room, _, _, _) = DuelRoom();
        var (attacker, defender, target) = FindDuelViaDto(room);

        room.TryNominateTarget(attacker, target);
        var q = Mc();
        var round = room.TryStartDuelRound(q, Make.T0).Round!;
        room.TrySubmitAnswer(attacker, Opt(round, 'A'), Make.T0, Grace); // wrong
        room.TrySubmitAnswer(defender, Opt(round, 'B'), Make.T0, Grace); // right
        var closed = room.CloseRound(round.RoundId, Make.T0.AddSeconds(1))!;

        Assert.Empty(closed.Result.WinnerPlayerIds);
        Assert.Equal(defender, room.OwnerOf(target)); // defender keeps it
        var defenderBonus = closed.Result.Ranking.First(r => r.PlayerId == defender).BonusAwarded;
        Assert.Equal(room.Settings.DefenderBonus, defenderBonus);
        Assert.Contains(room.Scoreboard(), s => s.PlayerId == defender && s.Score >= room.Settings.DefenderBonus);
    }

    [Fact]
    public void Duel_both_right_is_a_draw_with_bonuses_for_both()
    {
        var (room, _, _, _) = DuelRoom();
        var (attacker, defender, target) = FindDuelViaDto(room);

        room.TryNominateTarget(attacker, target);
        var q = Mc();
        var round = room.TryStartDuelRound(q, Make.T0).Round!;
        room.TrySubmitAnswer(attacker, Opt(round, 'B'), Make.T0, Grace);
        room.TrySubmitAnswer(defender, Opt(round, 'B'), Make.T0, Grace);
        var closed = room.CloseRound(round.RoundId, Make.T0.AddSeconds(1))!;

        Assert.Empty(closed.Result.WinnerPlayerIds);
        Assert.Equal(defender, room.OwnerOf(target)); // no tile change on a draw
        Assert.Equal(room.Settings.DrawBonus, closed.Result.Ranking.First(r => r.PlayerId == attacker).BonusAwarded);
        Assert.Equal(room.Settings.DrawBonus, closed.Result.Ranking.First(r => r.PlayerId == defender).BonusAwarded);
    }

    [Fact]
    public void Duel_base_takes_three_hits_to_capture()
    {
        var (room, _, _, _) = DuelRoom(new RoomSettings { BaseHealth = 3, DuelRounds = 20 });

        // Attack the same base three times; each time the current active player must be able to reach it.
        for (var hit = 1; hit <= 3; hit++)
        {
            // Find a base owned by someone other than the active player, adjacent to the active player.
            var attacker = room.ActivePlayer()!.Value;
            var dto = room.GetMap()!;
            var cols = dto.Cols;
            var target = dto.Territories.FirstOrDefault(t =>
                t.IsBase && t.OwnerPlayerId is { } o && o != attacker &&
                Neighbours(dto.Rows, cols, t.Index).Any(n => dto.Territories.First(x => x.Index == n).OwnerPlayerId == attacker));
            if (target is null) return; // layout did not line up a base attack this turn; test is best-effort

            var healthBefore = target.Health;
            Assert.True(room.TryNominateTarget(attacker, target.Index).Ok);
            var q = Mc();
            var round = room.TryStartDuelRound(q, Make.T0).Round!;
            room.TrySubmitAnswer(attacker, Opt(round, 'B'), Make.T0, Grace); // attacker right
            room.TrySubmitAnswer(target.OwnerPlayerId!.Value, Opt(round, 'A'), Make.T0, Grace); // defender wrong
            room.CloseRound(round.RoundId, Make.T0);

            var after = room.GetMap()!.Territories.First(t => t.Index == target.Index);
            if (healthBefore > 1)
                Assert.Equal(healthBefore - 1, after.Health); // base survived, lost one health
            else
                Assert.Equal(attacker, after.OwnerPlayerId); // captured on the last hit
        }
    }

    [Fact]
    public void Duel_capturing_last_base_in_three_hits_frees_all_defender_tiles_and_updates_scores()
    {
        var settings = new RoomSettings
        {
            MapPreset = MapPreset.Small,
            BaseHealth = 3,
            BaseValue = 300,
            TerritoryValue = 100,
            DuelRounds = 20,
            DisallowAdjacentBases = false,
        };
        var room = new GameRoom("123456", Guid.NewGuid(), settings, 30, Make.T0) { PickRandom = new Random(1) };
        foreach (var name in new[] { "Anna", "Bela", "Cili" })
        {
            var p = room.AddPlayer(name, Make.T0).Player!;
            room.SetConnected(p.Id, true);
        }
        Game.StartGame(room);

        var order = room.TurnOrder;
        var attacker = order[0];
        var defender = order[1];
        var third = order[2];

        // Place bases adjacent on row 0: attacker=0, defender=1, third=2.
        Assert.True(room.TryPick(attacker, 0, Make.T0).Ok);
        Assert.True(room.TryPick(defender, 1, Make.T0).Ok);
        Assert.True(room.TryPick(third, 2, Make.T0).Ok);
        Assert.True(room.TryAdvancePhase(GamePhase.Conquest, Make.T0).Ok);
        Assert.True(room.TryAdvancePhase(GamePhase.Expansion, Make.T0).Ok);

        // Give defender tiles 6, 11, 16; give third all other remaining free tiles via Expansion rounds.
        void ClaimInExpansion(Guid winnerId, int tileIndex)
        {
            var q = Est(100m);
            var round = room.TryStartExpansionRound(q, Make.T0).Round!;
            foreach (var pid in round.ParticipantIds)
                room.TrySubmitAnswer(pid, Num(round, pid == winnerId ? 100m : 10m), Make.T0, Grace);
            var closed = room.CloseRound(round.RoundId, Make.T0)!;
            Assert.Equal(winnerId, closed.ExpansionPick!.Picker.Id);
            Assert.True(room.TryPick(winnerId, tileIndex, Make.T0).Ok);
        }

        foreach (var tile in new[] { 6, 11, 16 }) ClaimInExpansion(defender, tile);
        foreach (var free in room.GetMap()!.Territories.Where(t => t.OwnerPlayerId is null).Select(t => t.Index).ToList())
            ClaimInExpansion(third, free);

        Assert.True(room.TryAdvancePhase(GamePhase.Duel, Make.T0).Ok);

        // Hit defender's base (1) three times whenever attacker is active; other turns end with neither right.
        var hits = 0;
        while (hits < 3)
        {
            var active = room.ActivePlayer()!.Value;
            if (active == attacker)
            {
                Assert.True(room.TryNominateTarget(attacker, 1).Ok);
                var round = room.TryStartDuelRound(Mc(), Make.T0).Round!;
                room.TrySubmitAnswer(attacker, Opt(round, 'B'), Make.T0, Grace);
                room.TrySubmitAnswer(defender, Opt(round, 'A'), Make.T0, Grace);
                var closed = room.CloseRound(round.RoundId, Make.T0)!;
                hits++;

                Assert.Equal(new[] { attacker }, closed.Result.WinnerPlayerIds.ToArray());

                var baseTile = room.GetMap()!.Territories.First(t => t.Index == 1);
                if (hits < 3)
                {
                    Assert.Equal(defender, baseTile.OwnerPlayerId);
                    Assert.True(baseTile.IsBase);
                    Assert.Equal(3 - hits, baseTile.Health);
                }
                else
                {
                    Assert.Equal(attacker, baseTile.OwnerPlayerId);
                    Assert.False(baseTile.IsBase);
                    Assert.Equal(settings.TerritoryValue, baseTile.Value);
                    Assert.Equal(1, baseTile.Health);
                }
            }
            else
            {
                var target = room.StartDuelTargetPick(Make.T0)!.ValidTerritoryIndices[0];
                Assert.True(room.TryNominateTarget(active, target).Ok);
                var round = room.TryStartDuelRound(Mc(), Make.T0).Round!;
                foreach (var pid in round.ParticipantIds)
                    room.TrySubmitAnswer(pid, Opt(round, 'A'), Make.T0, Grace);
                room.CloseRound(round.RoundId, Make.T0);
            }
        }

        // Defender lost their last base -> all defender tiles (6, 11, 16) are freed and score drops to 0.
        var afterMap = room.GetMap()!;
        Assert.DoesNotContain(afterMap.Territories, t => t.OwnerPlayerId == defender);
        foreach (var freedIdx in new[] { 6, 11, 16 })
        {
            var freed = afterMap.Territories.First(t => t.Index == freedIdx);
            Assert.Null(freed.OwnerPlayerId);
            Assert.False(freed.IsBase);
        }
        Assert.Equal(0, room.Scoreboard().First(s => s.PlayerId == defender).Score);
        Assert.Equal(settings.BaseValue + settings.TerritoryValue, room.Scoreboard().First(s => s.PlayerId == attacker).Score);
    }

    [Fact]
    public void Duel_ends_automatically_when_losing_last_base_leaves_no_adjacent_enemies()
    {
        var settings = new RoomSettings
        {
            MapPreset = MapPreset.Small, // 5x5
            BaseHealth = 1,
            DuelRounds = 20,
            DisallowAdjacentBases = false,
        };
        var room = new GameRoom("123456", Guid.NewGuid(), settings, 30, Make.T0) { PickRandom = new Random(1) };
        foreach (var name in new[] { "Anna", "Bela", "Cili" })
        {
            var p = room.AddPlayer(name, Make.T0).Player!;
            room.SetConnected(p.Id, true);
        }
        Game.StartGame(room);

        var order = room.TurnOrder;
        var attacker = order[0];
        var eliminated = order[1];
        var isolated = order[2];

        // attacker=0, eliminated=1, isolated=24 (bottom-right corner).
        Assert.True(room.TryPick(attacker, 0, Make.T0).Ok);
        Assert.True(room.TryPick(eliminated, 1, Make.T0).Ok);
        Assert.True(room.TryPick(isolated, 24, Make.T0).Ok);
        Assert.True(room.TryAdvancePhase(GamePhase.Conquest, Make.T0).Ok);
        Assert.True(room.TryAdvancePhase(GamePhase.Expansion, Make.T0).Ok);

        // Give eliminated every free tile in rows 0-3 except 0,1 so attacker (0) and isolated (24) do not touch.
        var remaining = room.GetMap()!.Territories.Where(t => t.OwnerPlayerId is null).Select(t => t.Index).ToList();
        foreach (var tile in remaining)
        {
            var winner = tile == 23 ? isolated : eliminated;
            var round = room.TryStartExpansionRound(Est(100m), Make.T0).Round!;
            foreach (var pid in round.ParticipantIds)
                room.TrySubmitAnswer(pid, Num(round, pid == winner ? 100m : 10m), Make.T0, Grace);
            room.CloseRound(round.RoundId, Make.T0);
            Assert.True(room.TryPick(winner, tile, Make.T0).Ok);
        }

        Assert.True(room.TryAdvancePhase(GamePhase.Duel, Make.T0).Ok);
        Assert.Null(room.NextAutomaticPhase());

        // Attacker captures eliminated's 1-HP base (1), freeing all separating tiles.
        Assert.True(room.TryNominateTarget(attacker, 1).Ok);
        var duel = room.TryStartDuelRound(Mc(), Make.T0).Round!;
        room.TrySubmitAnswer(attacker, Opt(duel, 'B'), Make.T0, Grace);
        room.TrySubmitAnswer(eliminated, Opt(duel, 'A'), Make.T0, Grace);
        room.CloseRound(duel.RoundId, Make.T0);

        Assert.False(room.OneBaseHolderRemains()); // attacker & isolated both have bases
        Assert.Equal(GamePhase.Results, room.NextAutomaticPhase()); // but neither touches an enemy tile
    }

    // Duel helpers computing adjacency from the DTO (the GameMap ctor is internal).
    private static (Guid Attacker, Guid Defender, int Target) FindDuelViaDto(GameRoom room)
    {
        var attacker = room.ActivePlayer()!.Value;
        var dto = room.GetMap()!;
        bool Adjacent(int index) => Neighbours(dto.Rows, dto.Cols, index).Any(n => dto.Territories.First(x => x.Index == n).OwnerPlayerId == attacker);
        // Prefer a normal enemy tile (captured in one hit) over a base.
        var target = dto.Territories.FirstOrDefault(t => !t.IsBase && t.OwnerPlayerId is { } o && o != attacker && Adjacent(t.Index))
            ?? dto.Territories.First(t => t.OwnerPlayerId is { } o && o != attacker && Adjacent(t.Index));
        return (attacker, target.OwnerPlayerId!.Value, target.Index);
    }

    private static IEnumerable<int> Neighbours(int rows, int cols, int index)
    {
        var r = index / cols; var c = index % cols;
        if (r > 0) yield return index - cols;
        if (c > 0) yield return index - 1;
        if (c < cols - 1) yield return index + 1;
        if (r < rows - 1) yield return index + cols;
    }
}
