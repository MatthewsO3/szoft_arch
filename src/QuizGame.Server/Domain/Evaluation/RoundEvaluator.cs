using QuizGame.Contracts;

namespace QuizGame.Server.Domain.Evaluation;

/// <summary>A player taking part in a round, in join order.</summary>
public sealed record Participant(Guid Id, string Name);

/// <summary>An accepted answer. <see cref="ElapsedMs"/> is server receive time minus round start.</summary>
public sealed record AnswerEntry(Guid PlayerId, char? Option, decimal? Number, long ElapsedMs, int Sequence);

public sealed record RoundOutcome(IReadOnlyList<AnswerResult> Ranking, IReadOnlyList<Guid> Winners);

/// <summary>
/// Pure ranking of a round (plan §4.5, §7 "Domain/Evaluation"): no I/O, no clock, no room state.
/// <list type="bullet">
/// <item>Multiple choice: correct answers first by server receive time, then wrong answers, then players who did not answer (join order). Winners = the first <c>winnersPerRound</c> correct players.</item>
/// <item>Estimation: smallest |guess − correct| first, ties → earlier receive time; players who did not answer last (join order). BaseOrder winners = everybody in ranking order (the pick order).</item>
/// <item>Battle: same distance ranking, but a tie is decided for the defender (plan §4.2), and the winner is the first in the ranking.</item>
/// </list>
/// </summary>
public static class RoundEvaluator
{
    public static RoundOutcome Evaluate(
        RoundKind kind,
        QuestionDto question,
        IReadOnlyList<Participant> participants,
        IReadOnlyList<AnswerEntry> answers,
        int winnersPerRound,
        Guid? defenderId = null)
    {
        var byPlayer = answers.ToDictionary(a => a.PlayerId);
        return question.Type == QuestionType.MultipleChoice
            ? EvaluateMultipleChoice(kind, question, participants, byPlayer, winnersPerRound)
            : EvaluateEstimation(kind, question, participants, byPlayer, defenderId);
    }

    private static RoundOutcome EvaluateMultipleChoice(
        RoundKind kind, QuestionDto question, IReadOnlyList<Participant> participants,
        Dictionary<Guid, AnswerEntry> byPlayer, int winnersPerRound)
    {
        if (question.CorrectOption is not { } correct)
            throw new ArgumentException("A multiple-choice question needs a correct option.", nameof(question));

        var rows = participants
            .Select((p, index) => (Participant: p, Index: index, Answer: byPlayer.GetValueOrDefault(p.Id)))
            .Select(r => (r.Participant, r.Index, r.Answer, IsCorrect: r.Answer is null ? (bool?)null : r.Answer.Option == correct))
            .ToList();

        var ordered = rows
            .OrderBy(r => r.IsCorrect switch { true => 0, false => 1, null => 2 })
            .ThenBy(r => r.Answer?.ElapsedMs ?? long.MaxValue)
            .ThenBy(r => r.Answer?.Sequence ?? int.MaxValue)
            .ThenBy(r => r.Index)
            .ToList();

        var ranking = ordered.Select((r, i) => new AnswerResult
        {
            PlayerId = r.Participant.Id,
            Name = r.Participant.Name,
            Rank = i + 1,
            IsCorrect = r.IsCorrect,
            ElapsedMs = r.Answer?.ElapsedMs,
        }).ToList();

        var winners = ordered
            .Where(r => r.IsCorrect == true)
            .Take(Math.Max(1, winnersPerRound))
            .Select(r => r.Participant.Id)
            .ToList();

        return new RoundOutcome(ranking, winners);
    }

    private static RoundOutcome EvaluateEstimation(
        RoundKind kind, QuestionDto question, IReadOnlyList<Participant> participants,
        Dictionary<Guid, AnswerEntry> byPlayer, Guid? defenderId)
    {
        if (question.CorrectNumber is not { } correct)
            throw new ArgumentException("An estimation question needs a correct number.", nameof(question));

        var isBattle = kind == RoundKind.Battle;

        var rows = participants
            .Select((p, index) =>
            {
                var answer = byPlayer.GetValueOrDefault(p.Id);
                decimal? distance = answer?.Number is { } n ? Math.Abs(n - correct) : null;
                // Battle: on equal distance (or when nobody answered) the defender comes first.
                var roleKey = isBattle && p.Id != defenderId ? 1 : 0;
                return (Participant: p, Index: index, Answer: answer, Distance: distance, RoleKey: roleKey);
            })
            .ToList();

        var ordered = rows
            .OrderBy(r => r.Distance is null ? 1 : 0)
            .ThenBy(r => r.Distance ?? 0m)
            .ThenBy(r => isBattle ? r.RoleKey : 0)
            .ThenBy(r => r.Answer?.ElapsedMs ?? long.MaxValue)
            .ThenBy(r => r.Answer?.Sequence ?? int.MaxValue)
            .ThenBy(r => isBattle ? r.RoleKey : r.Index)
            .ToList();

        var ranking = ordered.Select((r, i) => new AnswerResult
        {
            PlayerId = r.Participant.Id,
            Name = r.Participant.Name,
            Rank = i + 1,
            IsCorrect = r.Distance is null ? null : r.Distance == 0m,
            ElapsedMs = r.Answer?.ElapsedMs,
            Distance = r.Distance,
        }).ToList();

        var winners = kind == RoundKind.Battle
            ? ordered.Take(1).Select(r => r.Participant.Id).ToList()
            : ordered.Select(r => r.Participant.Id).ToList();

        return new RoundOutcome(ranking, winners);
    }
}
