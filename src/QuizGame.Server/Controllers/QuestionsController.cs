using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using QuizGame.Contracts;
using QuizGame.Server.Auth;
using QuizGame.Server.Services;

namespace QuizGame.Server.Controllers;

/// <summary>Question-bank CRUD with type-specific validation (plan §5.2, §6.2). Game master only.</summary>
[ApiController]
[Route("api/questions")]
[Authorize(Policy = Policies.GameMasterOnly)]
public sealed class QuestionsController(IQuestionBank bank) : ControllerBase
{
    /// <summary>Filtered, paged list of active questions (plan §6.2 GET /questions).</summary>
    [HttpGet]
    public async Task<ActionResult<PagedResult<QuestionDto>>> List(
        [FromQuery] int? categoryId,
        [FromQuery] QuestionType? type,
        [FromQuery] int? difficulty,
        [FromQuery] string? search,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken ct = default)
    {
        var query = new QuestionQuery(categoryId, type, difficulty, search, page, pageSize);
        return Ok(await bank.ListQuestionsAsync(query, ct));
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<QuestionDto>> Get(Guid id, CancellationToken ct)
    {
        var question = await bank.GetQuestionAsync(id, ct);
        return question is null
            ? ApiProblem.Create(404, ErrorCodes.QuestionNotFound, "Question not found.")
            : Ok(question);
    }

    [HttpPost]
    public async Task<ActionResult<QuestionDto>> Create([FromBody] QuestionUpsertRequest request, CancellationToken ct)
    {
        var result = await bank.CreateQuestionAsync(request, User.GetUserId(), ct);
        return result.Ok
            ? CreatedAtAction(nameof(Get), new { id = result.Question!.Id }, result.Question)
            : QuestionError(result);
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<QuestionDto>> Update(Guid id, [FromBody] QuestionUpsertRequest request, CancellationToken ct)
    {
        var result = await bank.UpdateQuestionAsync(id, request, ct);
        return result.Ok ? Ok(result.Question) : QuestionError(result);
    }

    /// <summary>Soft delete (IsActive=false) so past rounds keep their reference (plan §6.2).</summary>
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        return await bank.SoftDeleteQuestionAsync(id, ct)
            ? NoContent()
            : ApiProblem.Create(404, ErrorCodes.QuestionNotFound, "Question not found.");
    }

    private static ObjectResult QuestionError(QuestionResult result) => result.Error switch
    {
        Services.QuestionError.NotFound => ApiProblem.Create(404, ErrorCodes.QuestionNotFound, result.Detail ?? "Question not found."),
        Services.QuestionError.CategoryNotFound => ApiProblem.Create(400, ErrorCodes.CategoryNotFound, result.Detail ?? "Category not found."),
        _ => ApiProblem.Create(400, ErrorCodes.InvalidQuestion, result.Detail ?? "The question is invalid."),
    };
}
