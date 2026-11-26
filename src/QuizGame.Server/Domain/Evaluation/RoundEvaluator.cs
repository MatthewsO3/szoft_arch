namespace QuizGame.Server.Domain.Evaluation;

/// <summary>A player taking part in a round, in join order.</summary>
public sealed record Participant(Guid Id, string Name);

/// <summary>An accepted answer. <see cref="ElapsedMs"/> is server receive time minus round start.</summary>
public sealed record AnswerEntry(Guid PlayerId, char? Option, decimal? Number, long ElapsedMs, int Sequence);
