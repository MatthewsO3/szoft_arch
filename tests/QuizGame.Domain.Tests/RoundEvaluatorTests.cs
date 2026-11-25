using QuizGame.Contracts;
using QuizGame.Server.Domain.Evaluation;
using Xunit;

namespace QuizGame.Domain.Tests;

public class RoundEvaluatorTests
{
    private static readonly Participant Anna = new(Guid.NewGuid(), "Anna");
    private static readonly Participant Bela = new(Guid.NewGuid(), "Bela");
    private static readonly Participant Cili = new(Guid.NewGuid(), "Cili");
    private static readonly Participant Dani = new(Guid.NewGuid(), "Dani");
    private static readonly IReadOnlyList<Participant> All = [Anna, Bela, Cili, Dani];

    private static QuestionDto Mc() => new()
    {
        Id = Guid.NewGuid(),
        Type = QuestionType.MultipleChoice,
        Options = [new(Guid.NewGuid(), 'A', "a"), new(Guid.NewGuid(), 'B', "b"), new(Guid.NewGuid(), 'C', "c")],
        CorrectOption = 'B',
    };

    private static QuestionDto Est(decimal correct = 100m) => new()
    {
        Id = Guid.NewGuid(),
        Type = QuestionType.Estimation,
        CorrectNumber = correct,
    };

    private static AnswerEntry Pick(Participant p, char option, long ms, int seq) => new(p.Id, option, null, ms, seq);
    private static AnswerEntry Guess(Participant p, decimal number, long ms, int seq) => new(p.Id, null, number, ms, seq);

    [Fact]
    public void MultipleChoice_fastest_correct_wins_wrong_and_missing_rank_after()
    {
        var outcome = RoundEvaluator.Evaluate(RoundKind.Grab, Mc(), All,
        [
            Pick(Anna, 'B', 900, 0),  // correct, slower
            Pick(Bela, 'A', 100, 1),  // wrong, fastest
            Pick(Cili, 'B', 400, 2),  // correct, fastest correct
        ], winnersPerRound: 1);

        Assert.Equal(new[] { Cili.Id, Anna.Id, Bela.Id, Dani.Id }, outcome.Ranking.Select(r => r.PlayerId).ToArray());
        Assert.Equal(new[] { 1, 2, 3, 4 }, outcome.Ranking.Select(r => r.Rank).ToArray());
        Assert.Equal(new[] { Cili.Id }, outcome.Winners.ToArray());
        Assert.Equal(true, outcome.Ranking[0].IsCorrect);
        Assert.Equal(false, outcome.Ranking[2].IsCorrect);
        Assert.Null(outcome.Ranking[3].IsCorrect);   // Dani did not answer
        Assert.Null(outcome.Ranking[3].ElapsedMs);
    }

    [Fact]
    public void MultipleChoice_winners_per_round_takes_that_many_correct_players_in_speed_order()
    {
        var outcome = RoundEvaluator.Evaluate(RoundKind.Grab, Mc(), All,
        [
            Pick(Anna, 'B', 300, 0), Pick(Bela, 'B', 100, 1), Pick(Cili, 'B', 200, 2), Pick(Dani, 'A', 50, 3),
        ], winnersPerRound: 2);

        Assert.Equal(new[] { Bela.Id, Cili.Id }, outcome.Winners.ToArray());
    }

    [Fact]
    public void MultipleChoice_nobody_correct_means_no_winner()
    {
        var outcome = RoundEvaluator.Evaluate(RoundKind.Grab, Mc(), All, [Pick(Anna, 'A', 100, 0)], winnersPerRound: 1);
        Assert.Equal(0, outcome.Winners.Count);
        Assert.Equal(Anna.Id, outcome.Ranking[0].PlayerId);
    }

    [Fact]
    public void MultipleChoice_equal_receive_time_falls_back_to_queue_order()
    {
        var outcome = RoundEvaluator.Evaluate(RoundKind.Grab, Mc(), All,
            [Pick(Bela, 'B', 500, 1), Pick(Anna, 'B', 500, 0)], winnersPerRound: 1);
        Assert.Equal(Anna.Id, outcome.Winners[0]);
    }

    [Fact]
    public void Estimation_closest_wins_and_unanswered_rank_last_in_join_order()
    {
        var outcome = RoundEvaluator.Evaluate(RoundKind.BaseOrder, Est(100m), All,
        [
            Guess(Cili, 130m, 100, 0),   // distance 30
            Guess(Bela, 95m, 900, 1),    // distance 5
        ], winnersPerRound: 1);

        Assert.Equal(new[] { Bela.Id, Cili.Id, Anna.Id, Dani.Id }, outcome.Ranking.Select(r => r.PlayerId).ToArray());
        Assert.Equal(5m, outcome.Ranking[0].Distance);
        Assert.Null(outcome.Ranking[2].Distance);
        // Base order: everybody, in ranking order.
        Assert.Equal(outcome.Ranking.Select(r => r.PlayerId).ToArray(), outcome.Winners.ToArray());
    }

    [Fact]
    public void Estimation_distance_counts_both_directions_and_decimals()
    {
        var outcome = RoundEvaluator.Evaluate(RoundKind.BaseOrder, Est(10.5m), [Anna, Bela],
            [Guess(Anna, 12m, 10, 0), Guess(Bela, 9.5m, 20, 1)], winnersPerRound: 1);

        Assert.Equal(Bela.Id, outcome.Ranking[0].PlayerId);   // |9.5-10.5| = 1 < 1.5
        Assert.Equal(1.0m, outcome.Ranking[0].Distance);
        Assert.Equal(1.5m, outcome.Ranking[1].Distance);
    }

    [Fact]
    public void Estimation_equal_distance_is_decided_by_earlier_receive_time()
    {
        var outcome = RoundEvaluator.Evaluate(RoundKind.BaseOrder, Est(100m), [Anna, Bela],
            [Guess(Anna, 110m, 800, 1), Guess(Bela, 90m, 300, 0)], winnersPerRound: 1);
        Assert.Equal(Bela.Id, outcome.Ranking[0].PlayerId);
    }

    [Fact]
    public void Estimation_exact_hit_is_marked_correct()
    {
        var outcome = RoundEvaluator.Evaluate(RoundKind.BaseOrder, Est(100m), [Anna, Bela],
            [Guess(Anna, 100m, 10, 0), Guess(Bela, 101m, 20, 1)], winnersPerRound: 1);
        Assert.Equal(true, outcome.Ranking[0].IsCorrect);
        Assert.Equal(false, outcome.Ranking[1].IsCorrect);
    }

    [Fact]
    public void Battle_closer_attacker_takes_the_territory()
    {
        IReadOnlyList<Participant> duel = [Bela, Anna]; // defender first, as the room builds it
        var outcome = RoundEvaluator.Evaluate(RoundKind.Battle, Est(100m), duel,
            [Guess(Bela, 80m, 100, 0), Guess(Anna, 99m, 900, 1)], 1, defenderId: Bela.Id);

        Assert.Equal(new[] { Anna.Id }, outcome.Winners.ToArray());
        Assert.Equal(Anna.Id, outcome.Ranking[0].PlayerId);
    }

    [Fact]
    public void Battle_tie_goes_to_the_defender_even_if_the_attacker_was_faster()
    {
        IReadOnlyList<Participant> duel = [Bela, Anna];
        var outcome = RoundEvaluator.Evaluate(RoundKind.Battle, Est(100m), duel,
            [Guess(Anna, 90m, 100, 0), Guess(Bela, 110m, 900, 1)], 1, defenderId: Bela.Id);

        Assert.Equal(new[] { Bela.Id }, outcome.Winners.ToArray());
    }

    [Fact]
    public void Battle_defender_keeps_it_when_nobody_answers_attacker_wins_if_only_they_answer()
    {
        IReadOnlyList<Participant> duel = [Bela, Anna];

        var none = RoundEvaluator.Evaluate(RoundKind.Battle, Est(100m), duel, [], 1, defenderId: Bela.Id);
        Assert.Equal(new[] { Bela.Id }, none.Winners.ToArray());

        var onlyAttacker = RoundEvaluator.Evaluate(RoundKind.Battle, Est(100m), duel,
            [Guess(Anna, 5m, 10, 0)], 1, defenderId: Bela.Id);
        Assert.Equal(new[] { Anna.Id }, onlyAttacker.Winners.ToArray());

        var onlyDefender = RoundEvaluator.Evaluate(RoundKind.Battle, Est(100m), duel,
            [Guess(Bela, 5000m, 10, 0)], 1, defenderId: Bela.Id);
        Assert.Equal(new[] { Bela.Id }, onlyDefender.Winners.ToArray());
    }
}
