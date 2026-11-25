using QuizGame.Contracts;

namespace QuizGame.Host.Services;

/// <summary>Filter and paging parameters for <c>GET /api/questions</c> (api-contract §1).</summary>
public sealed record QuestionQuery(
    int? CategoryId = null,
    QuestionType? Type = null,
    int? Difficulty = null,
    string? Search = null,
    int Page = 1,
    int PageSize = 20);

/// <summary>REST operations for managing question-bank categories and questions.</summary>
public interface IQuestionBankApi
{
    Task<IReadOnlyList<CategoryDto>> GetCategoriesAsync(CancellationToken ct = default);
    Task<CategoryDto> CreateCategoryAsync(string name, CancellationToken ct = default);
    Task<CategoryDto> UpdateCategoryAsync(int id, string name, CancellationToken ct = default);
    Task DeleteCategoryAsync(int id, CancellationToken ct = default);

    Task<PagedResult<QuestionDto>> GetQuestionsAsync(QuestionQuery query, CancellationToken ct = default);
    Task<QuestionDto> GetQuestionAsync(Guid id, CancellationToken ct = default);
    Task<QuestionDto> CreateQuestionAsync(QuestionUpsertRequest request, CancellationToken ct = default);
    Task<QuestionDto> UpdateQuestionAsync(Guid id, QuestionUpsertRequest request, CancellationToken ct = default);
    Task DeleteQuestionAsync(Guid id, CancellationToken ct = default);
}
