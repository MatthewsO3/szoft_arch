using QuizGame.Contracts;
using QuizGame.Server.Domain;
using Xunit;

namespace QuizGame.Domain.Tests;

public class GameRoomRoundTests
{
    private static readonly TimeSpan Grace = TimeSpan.FromMilliseconds(300);

    private static QuestionDto Mc(int seconds = 10) => new()
    {
        Id = Guid.NewGuid(),
        Type = QuestionType.MultipleChoice,
        TimeLimitSeconds = seconds,
        Text = "secret question text",
        Options = [new(Guid.NewGuid(), 'A', "a"), new(Guid.NewGuid(), 'B', "b")],
        CorrectOption = 'B',
    };

    private static QuestionDto Est() => new() { Id = Guid.NewGuid(), Type = QuestionType.Estimation, TimeLimitSeconds = 10, CorrectNumber = 100m };

    private static (GameRoom Room, PlayerSummary A, PlayerSummary B, PlayerSummary C) RoomWithPlayers(RoomSettings? settings = null)
    {
        var room = new GameRoom("123456", Guid.NewGuid(), settings ?? new RoomSettings(), 30, Make.T0);
        var a = room.AddPlayer("Anna", Make.T0).Player!;
        var b = room.AddPlayer("Bela", Make.T0).Player!;
        var c = room.AddPlayer("Cili", Make.T0).Player!;
        foreach (var p in new[] { a, b, c }) room.SetConnected(p.Id, true);
        return (room, a, b, c);
    }

    private static SendQuestionRequest Req(RoundKind kind, QuestionDto q) => new() { QuestionId = q.Id, Kind = kind };
    private static SubmitAnswerRequest Opt(RoundInfo r, char o) => new() { RoundId = r.RoundId, Option = o };
    private static SubmitAnswerRequest Num(RoundInfo r, decimal n) => new() { RoundId = r.RoundId, Number = n };

    [Fact]
    public void Round_opens_with_deadline_from_the_question_time_limit()
    {
        var (room, _, _, _) = RoomWithPlayers();
        var q = Mc(seconds: 15);
        var started = room.TryStartRound(q, Req(RoundKind.Grab, q), Make.T0);

        Assert.True(started.Ok);
        Assert.Equal(Make.T0.AddSeconds(15), started.Round!.Deadline);
        Assert.Equal(3, started.Round.ParticipantIds.Count);
        Assert.True(room.HasOpenRound);
    }

    [Fact]
    public void Question_without_time_limit_uses_the_room_default()
    {
        var (room, _, _, _) = RoomWithPlayers(new RoomSettings { DefaultQuestionSeconds = 33 });
        var q = Mc(seconds: 0);
        var started = room.TryStartRound(q, Req(RoundKind.Grab, q), Make.T0);
        Assert.Equal(Make.T0.AddSeconds(33), started.Round!.Deadline);
    }

    [Fact]
    public void Only_one_round_at_a_time()
    {
        var (room, _, _, _) = RoomWithPlayers();
        var q = Mc();
        Assert.True(room.TryStartRound(q, Req(RoundKind.Grab, q), Make.T0).Ok);
        Assert.Equal(StartRoundError.RoundActive, room.TryStartRound(q, Req(RoundKind.Grab, q), Make.T0).Error);
    }

    [Fact]
    public void Round_needs_players_and_a_matching_question_type()
    {
        var empty = new GameRoom("123456", Guid.NewGuid(), new RoomSettings(), 30, Make.T0);
        var q = Mc();
        Assert.Equal(StartRoundError.NoPlayers, empty.TryStartRound(q, Req(RoundKind.Grab, q), Make.T0).Error);

        var (room, _, _, _) = RoomWithPlayers();
        Assert.Equal(StartRoundError.InvalidRound, room.TryStartRound(q, Req(RoundKind.BaseOrder, q), Make.T0).Error);
        var e = Est();
        Assert.Equal(StartRoundError.InvalidRound, room.TryStartRound(e, Req(RoundKind.Grab, e), Make.T0).Error);
        Assert.False(room.HasOpenRound);
    }

    [Fact]
    public void Malformed_questions_are_refused()
    {
        var (room, _, _, _) = RoomWithPlayers();
        var noCorrect = new QuestionDto { Id = Guid.NewGuid(), Type = QuestionType.MultipleChoice, Options = [new(Guid.NewGuid(), 'A', "a"), new(Guid.NewGuid(), 'B', "b")] };
        Assert.Equal(StartRoundError.InvalidRound, room.TryStartRound(noCorrect, Req(RoundKind.Grab, noCorrect), Make.T0).Error);

        var noNumber = new QuestionDto { Id = Guid.NewGuid(), Type = QuestionType.Estimation };
        Assert.Equal(StartRoundError.InvalidRound, room.TryStartRound(noNumber, Req(RoundKind.BaseOrder, noNumber), Make.T0).Error);
    }

    [Fact]
    public void First_answer_is_final_and_options_are_validated()
    {
        var (room, a, _, _) = RoomWithPlayers();
        var q = Mc();
        var round = room.TryStartRound(q, Req(RoundKind.Grab, q), Make.T0).Round!;

        Assert.Equal(SubmitStatus.InvalidAnswer, room.TrySubmitAnswer(a.Id, new SubmitAnswerRequest { RoundId = round.RoundId }, Make.T0, Grace).Status);
        Assert.Equal(SubmitStatus.InvalidAnswer, room.TrySubmitAnswer(a.Id, Opt(round, 'Z'), Make.T0, Grace).Status);
        Assert.Equal(SubmitStatus.InvalidAnswer, room.TrySubmitAnswer(a.Id, Num(round, 5m), Make.T0, Grace).Status);

        Assert.True(room.TrySubmitAnswer(a.Id, Opt(round, 'a'), Make.T0.AddSeconds(1), Grace).Ok); // lower case is normalised
        Assert.Equal(SubmitStatus.AlreadyAnswered, room.TrySubmitAnswer(a.Id, Opt(round, 'B'), Make.T0.AddSeconds(2), Grace).Status);

        var closed = room.CloseRound(round.RoundId, Make.T0.AddSeconds(3))!;
        Assert.Equal('A', closed.Record.Answers.Single().Option); // the first answer stood
    }

    [Fact]
    public void Estimation_answer_needs_a_number()
    {
        var (room, a, _, _) = RoomWithPlayers();
        var q = Est();
        var round = room.TryStartRound(q, Req(RoundKind.BaseOrder, q), Make.T0).Round!;
        Assert.Equal(SubmitStatus.InvalidAnswer, room.TrySubmitAnswer(a.Id, Opt(round, 'A'), Make.T0, Grace).Status);
        Assert.True(room.TrySubmitAnswer(a.Id, Num(round, 12.5m), Make.T0, Grace).Ok);
    }

    [Fact]
    public void Deadline_plus_grace_is_the_cut_off()
    {
        var (room, a, b, _) = RoomWithPlayers();
        var q = Mc(seconds: 10);
        var round = room.TryStartRound(q, Req(RoundKind.Grab, q), Make.T0).Round!;

        Assert.True(room.TrySubmitAnswer(a.Id, Opt(round, 'B'), round.Deadline + Grace, Grace).Ok);
        Assert.Equal(SubmitStatus.TooLate,
            room.TrySubmitAnswer(b.Id, Opt(round, 'B'), round.Deadline + Grace + TimeSpan.FromMilliseconds(1), Grace).Status);
    }

    [Fact]
    public void Wrong_round_or_no_round_and_strangers_are_rejected()
    {
        var (room, a, _, _) = RoomWithPlayers();
        Assert.Equal(SubmitStatus.NoActiveRound,
            room.TrySubmitAnswer(a.Id, new SubmitAnswerRequest { RoundId = Guid.NewGuid(), Option = 'A' }, Make.T0, Grace).Status);

        var q = Mc();
        var round = room.TryStartRound(q, Req(RoundKind.Grab, q), Make.T0).Round!;
        Assert.Equal(SubmitStatus.NoActiveRound,
            room.TrySubmitAnswer(a.Id, new SubmitAnswerRequest { RoundId = Guid.NewGuid(), Option = 'A' }, Make.T0, Grace).Status);
        Assert.Equal(SubmitStatus.NotParticipant, room.TrySubmitAnswer(Guid.NewGuid(), Opt(round, 'A'), Make.T0, Grace).Status);
    }

    [Fact]
    public void All_connected_players_answering_is_reported_offline_players_do_not_block()
    {
        var (room, a, b, c) = RoomWithPlayers();
        room.SetConnected(c.Id, false);
        var q = Mc();
        var round = room.TryStartRound(q, Req(RoundKind.Grab, q), Make.T0).Round!;

        var first = room.TrySubmitAnswer(a.Id, Opt(round, 'B'), Make.T0.AddSeconds(1), Grace);
        Assert.False(first.AllAnswered);
        Assert.Equal(1, first.Answered);
        Assert.Equal(3, first.Total);

        var second = room.TrySubmitAnswer(b.Id, Opt(round, 'A'), Make.T0.AddSeconds(2), Grace);
        Assert.True(second.AllAnswered);
    }

    [Fact]
    public void Closing_ranks_clears_the_round_and_is_idempotent()
    {
        var (room, a, b, c) = RoomWithPlayers();
        var q = Mc();
        var round = room.TryStartRound(q, Req(RoundKind.Grab, q), Make.T0).Round!;
        room.TrySubmitAnswer(a.Id, Opt(round, 'B'), Make.T0.AddMilliseconds(900), Grace);
        room.TrySubmitAnswer(b.Id, Opt(round, 'B'), Make.T0.AddMilliseconds(400), Grace);

        var closed = room.CloseRound(round.RoundId, Make.T0.AddSeconds(10))!;

        Assert.Equal(new[] { b.Id }, closed.Result.WinnerPlayerIds.ToArray());
        Assert.Equal('B', closed.Result.CorrectOption);
        Assert.Equal(new[] { b.Id, a.Id, c.Id }, closed.Result.Ranking.Select(r => r.PlayerId).ToArray());
        Assert.Equal(400L, closed.Result.Ranking[0].ElapsedMs);
        Assert.False(room.HasOpenRound);
        Assert.Null(room.CloseRound(round.RoundId, Make.T0.AddSeconds(11)));   // timer firing after the host closed
        Assert.Null(room.CloseRound(null, Make.T0.AddSeconds(11)));
    }

    [Fact]
    public void Closed_round_record_has_what_the_database_needs()
    {
        var (room, a, _, _) = RoomWithPlayers();
        var q = Est();
        var round = room.TryStartRound(q, Req(RoundKind.BaseOrder, q), Make.T0).Round!;
        room.TrySubmitAnswer(a.Id, Num(round, 120m), Make.T0.AddMilliseconds(250), Grace);

        var rec = room.CloseRound(round.RoundId, Make.T0.AddSeconds(10))!.Record;

        Assert.Equal(1, rec.Seq);
        Assert.Equal(q.Id, rec.QuestionId);
        Assert.Equal(RoundKind.BaseOrder, rec.Kind);
        Assert.Equal(a.Id, rec.WinnerPlayerId);
        var answer = rec.Answers.Single();           // only players who answered get a row
        Assert.Equal(120m, answer.Number);
        Assert.Equal(20m, answer.Distance);
        Assert.Equal(1, answer.Rank);
        Assert.Equal(Make.T0.AddMilliseconds(250), answer.ReceivedAt);

        var q2 = Mc();
        var next = room.TryStartRound(q2, Req(RoundKind.Grab, q2), Make.T0.AddSeconds(20)).Round!;
        Assert.Equal(2, room.CloseRound(next.RoundId, Make.T0.AddSeconds(30))!.Record.Seq);
    }

    [Fact]
    public void Players_never_get_the_correct_answer_or_the_question_text()
    {
        var (room, _, _, _) = RoomWithPlayers();
        var q = Mc();
        var round = room.TryStartRound(q, Req(RoundKind.Grab, q), Make.T0).Round!;
        var forPlayer = round.ForPlayer();

        Assert.Equal(round.RoundId, forPlayer.RoundId);
        Assert.Equal(2, forPlayer.Options.Count);
        var json = System.Text.Json.JsonSerializer.Serialize(forPlayer);
        Assert.False(json.Contains("secret question text"));
        Assert.False(json.Contains("Correct"));

        // The public snapshot of the room carries no answer either.
        var snapshot = System.Text.Json.JsonSerializer.Serialize(room.Snapshot(Make.T0));
        Assert.False(snapshot.Contains("CorrectOption"));
        Assert.Equal(round.RoundId, room.Snapshot(Make.T0).CurrentRound!.RoundId);
    }

    [Fact]
    public void Battle_involves_only_attacker_and_defender()
    {
        var (room, a, b, c) = RoomWithPlayers();
        room.TerritoryOwner = index => index == 7 ? b.Id : null;
        var q = Est();
        var request = new SendQuestionRequest { QuestionId = q.Id, Kind = RoundKind.Battle, AttackerId = a.Id, TargetTerritoryIndex = 7 };

        var round = room.TryStartRound(q, request, Make.T0).Round!;
        Assert.Equal(b.Id, round.DefenderId);
        Assert.Equal(2, round.ParticipantIds.Count);
        Assert.Equal(SubmitStatus.NotParticipant, room.TrySubmitAnswer(c.Id, Num(round, 1m), Make.T0, Grace).Status);

        room.TrySubmitAnswer(a.Id, Num(round, 101m), Make.T0.AddMilliseconds(100), Grace);
        var last = room.TrySubmitAnswer(b.Id, Num(round, 101m), Make.T0.AddMilliseconds(900), Grace);
        Assert.True(last.AllAnswered);

        var closed = room.CloseRound(round.RoundId, Make.T0.AddSeconds(2))!;
        Assert.Equal(new[] { b.Id }, closed.Result.WinnerPlayerIds.ToArray());   // tie → defender keeps it
    }

    [Fact]
    public void Battle_requires_attacker_target_and_an_owned_foreign_territory()
    {
        var (room, a, b, _) = RoomWithPlayers();
        var q = Est();
        Assert.Equal(StartRoundError.InvalidRound,
            room.TryStartRound(q, new SendQuestionRequest { QuestionId = q.Id, Kind = RoundKind.Battle }, Make.T0).Error);

        var request = new SendQuestionRequest { QuestionId = q.Id, Kind = RoundKind.Battle, AttackerId = a.Id, TargetTerritoryIndex = 3 };
        Assert.Equal(StartRoundError.InvalidRound, room.TryStartRound(q, request, Make.T0).Error);   // nobody owns 3 yet

        room.TerritoryOwner = _ => a.Id;
        Assert.Equal(StartRoundError.InvalidRound, room.TryStartRound(q, request, Make.T0).Error);   // own territory

        room.TerritoryOwner = _ => b.Id;
        Assert.True(room.TryStartRound(q, request, Make.T0).Ok);
    }

    [Fact]
    public void Removed_player_does_not_hold_the_round_open()
    {
        var (room, a, b, c) = RoomWithPlayers();
        var q = Mc();
        var round = room.TryStartRound(q, Req(RoundKind.Grab, q), Make.T0).Round!;
        room.RemovePlayer(c.Id);

        room.TrySubmitAnswer(a.Id, Opt(round, 'B'), Make.T0.AddSeconds(1), Grace);
        Assert.True(room.TrySubmitAnswer(b.Id, Opt(round, 'B'), Make.T0.AddSeconds(2), Grace).AllAnswered);
    }
}
