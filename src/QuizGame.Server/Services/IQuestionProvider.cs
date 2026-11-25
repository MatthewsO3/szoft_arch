using System.Collections.Concurrent;
using QuizGame.Contracts;

namespace QuizGame.Server.Services;

/// <summary>
/// Source of questions for a round. The question bank (M2-B, EF Core) provides the real implementation and
/// replaces <see cref="InMemoryQuestionProvider"/> in Program.cs; the round logic only depends on this.
/// Must return only active questions, complete with options / correct answer.
/// </summary>
public interface IQuestionProvider
{
    Task<QuestionDto?> GetActiveAsync(Guid id, CancellationToken ct = default);
}

/// <summary>Placeholder bank for tests and Development until the database-backed one exists.</summary>
public sealed class InMemoryQuestionProvider : IQuestionProvider
{
    private readonly ConcurrentDictionary<Guid, QuestionDto> _questions = new();

    public void Add(QuestionDto question) => _questions[question.Id] = question;

    public Task<QuestionDto?> GetActiveAsync(Guid id, CancellationToken ct = default) =>
        Task.FromResult(_questions.GetValueOrDefault(id));

    /// <summary>Fixed ids so the smoke test and a developer can start a round without a question bank.</summary>
    public static readonly Guid DemoMultipleChoiceId = Guid.Parse("00000000-0000-0000-0000-00000000a001");
    public static readonly Guid DemoEstimationId = Guid.Parse("00000000-0000-0000-0000-00000000a002");

    public InMemoryQuestionProvider SeedDemo()
    {
        Add(new QuestionDto
        {
            Id = DemoMultipleChoiceId,
            CategoryId = 1,
            Type = QuestionType.MultipleChoice,
            Text = "Which planet is closest to the Sun?",
            Difficulty = 1,
            TimeLimitSeconds = 10,
            Options =
            [
                new OptionDto(Guid.NewGuid(), 'A', "Venus"),
                new OptionDto(Guid.NewGuid(), 'B', "Mercury"),
                new OptionDto(Guid.NewGuid(), 'C', "Mars"),
                new OptionDto(Guid.NewGuid(), 'D', "Earth"),
            ],
            CorrectOption = 'B',
        });
        Add(new QuestionDto
        {
            Id = DemoEstimationId,
            CategoryId = 1,
            Type = QuestionType.Estimation,
            Text = "How tall is Mount Everest?",
            Difficulty = 1,
            TimeLimitSeconds = 10,
            CorrectNumber = 8849m,
            Unit = "m",
        });
        return this;
    }
}
