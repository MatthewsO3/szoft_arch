using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using QuizGame.Contracts;
using QuizGame.Server.Auth;
using QuizGame.Server.Services;

namespace QuizGame.Server.Controllers;

/// <summary>Question-bank category management (plan §6.2). Game master only.</summary>
[ApiController]
[Route("api/categories")]
[Authorize(Policy = Policies.GameMasterOnly)]
public sealed class CategoriesController(IQuestionBank bank) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<CategoryDto>>> List(CancellationToken ct) =>
        Ok(await bank.ListCategoriesAsync(ct));

    [HttpPost]
    public async Task<ActionResult<CategoryDto>> Create([FromBody] CategoryUpsertRequest request, CancellationToken ct)
    {
        var result = await bank.CreateCategoryAsync(request.Name, ct);
        return result.Ok
            ? CreatedAtAction(nameof(List), new { id = result.Category!.Id }, result.Category)
            : CategoryError(result.Error!.Value);
    }

    [HttpPut("{id:int}")]
    public async Task<ActionResult<CategoryDto>> Rename(int id, [FromBody] CategoryUpsertRequest request, CancellationToken ct)
    {
        var result = await bank.RenameCategoryAsync(id, request.Name, ct);
        return result.Ok ? Ok(result.Category) : CategoryError(result.Error!.Value);
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        var result = await bank.DeleteCategoryAsync(id, ct);
        return result switch
        {
            CategoryDeleteResult.Deleted => NoContent(),
            CategoryDeleteResult.NotFound => ApiProblem.Create(404, ErrorCodes.CategoryNotFound, "Category not found."),
            _ => ApiProblem.Create(409, ErrorCodes.CategoryInUse, "The category still has questions and cannot be deleted."),
        };
    }

    private static ObjectResult CategoryError(CategoryError error) => error switch
    {
        Services.CategoryError.NameInvalid => ApiProblem.Create(400, ErrorCodes.InvalidCategory, "Category name must be 1–64 characters."),
        Services.CategoryError.NameTaken => ApiProblem.Create(409, ErrorCodes.CategoryNameTaken, "A category with that name already exists."),
        _ => ApiProblem.Create(404, ErrorCodes.CategoryNotFound, "Category not found."),
    };
}
