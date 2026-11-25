using QuizGame.Contracts;

namespace QuizGame.Server.Services;

/// <summary>
/// Storage and queries for the question bank: categories and questions (plan §5.1, §6.2, §7.1 QuestionService).
/// EF Core in production, in-memory for local dev without a database. All methods are async I/O (plan convention).
/// </summary>
public interface IQuestionBank
{
    // ---- Categories ----
    Task<IReadOnlyList<CategoryDto>> ListCategoriesAsync(CancellationToken ct = default);
    Task<CategoryResult> CreateCategoryAsync(string name, CancellationToken ct = default);
    Task<CategoryResult> RenameCategoryAsync(int id, string name, CancellationToken ct = default);
    Task<CategoryDeleteResult> DeleteCategoryAsync(int id, CancellationToken ct = default);

    // ---- Questions ----
    Task<PagedResult<QuestionDto>> ListQuestionsAsync(QuestionQuery query, CancellationToken ct = default);
    Task<QuestionDto?> GetQuestionAsync(Guid id, CancellationToken ct = default);
    Task<QuestionResult> CreateQuestionAsync(QuestionUpsertRequest request, Guid ownerId, CancellationToken ct = default);
    Task<QuestionResult> UpdateQuestionAsync(Guid id, QuestionUpsertRequest request, CancellationToken ct = default);

    /// <summary>Soft delete (IsActive=false) so past rounds keep their reference (plan §6.2). False if not found.</summary>
    Task<bool> SoftDeleteQuestionAsync(Guid id, CancellationToken ct = default);
}

/// <summary>Filters for the paged question list (plan §6.2 GET /questions).</summary>
public sealed record QuestionQuery(
    int? CategoryId,
    QuestionType? Type,
    int? Difficulty,
    string? Search,
    int Page,
    int PageSize);

public enum CategoryError { NameInvalid, NameTaken, NotFound }

public sealed record CategoryResult(CategoryDto? Category, CategoryError? Error)
{
    public bool Ok => Error is null;
    public static CategoryResult Success(CategoryDto category) => new(category, null);
    public static CategoryResult Fail(CategoryError error) => new(null, error);
}

public enum CategoryDeleteResult { Deleted, NotFound, InUse }

public enum QuestionError { NotFound, Invalid, CategoryNotFound }

public sealed record QuestionResult(QuestionDto? Question, QuestionError? Error, string? Detail)
{
    public bool Ok => Error is null;
    public static QuestionResult Success(QuestionDto question) => new(question, null, null);
    public static QuestionResult Fail(QuestionError error, string detail) => new(null, error, detail);
}
