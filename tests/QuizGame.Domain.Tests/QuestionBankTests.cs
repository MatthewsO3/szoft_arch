using QuizGame.Contracts;
using QuizGame.Server.Services;
using Xunit;

namespace QuizGame.Domain.Tests;

/// <summary>
/// Behaviour of the question bank exercised through <see cref="InMemoryQuestionBank"/> (no database needed).
/// The rules tested here are the ones both the in-memory and EF implementations must honour: category
/// uniqueness and in-use protection, type validation on write, filtering/paging, and soft delete.
/// </summary>
public class QuestionBankTests
{
    private static async Task<int> SeedCategoryAsync(IQuestionBank bank, string name = "General")
    {
        var result = await bank.CreateCategoryAsync(name);
        Assert.True(result.Ok);
        return result.Category!.Id;
    }

    private static QuestionUpsertRequest Mc(int categoryId, string text = "Q?", int difficulty = 1) => new()
    {
        CategoryId = categoryId,
        Type = QuestionType.MultipleChoice,
        Text = text,
        Difficulty = difficulty,
        TimeLimitSeconds = 20,
        Options = [new OptionUpsert('A', "yes", true), new OptionUpsert('B', "no", false)],
    };

    private static QuestionUpsertRequest Est(int categoryId, string text = "How many?", int difficulty = 1) => new()
    {
        CategoryId = categoryId,
        Type = QuestionType.Estimation,
        Text = text,
        Difficulty = difficulty,
        TimeLimitSeconds = 20,
        CorrectNumber = 42,
    };

    // ---- Categories ----

    [Fact]
    public async Task Create_category_then_list_returns_it()
    {
        var bank = new InMemoryQuestionBank();
        var created = await bank.CreateCategoryAsync("Science");

        Assert.True(created.Ok);
        var all = await bank.ListCategoriesAsync();
        Assert.Contains(all, c => c.Id == created.Category!.Id && c.Name == "Science");
    }

    [Fact]
    public async Task Duplicate_category_name_is_rejected_case_insensitively()
    {
        var bank = new InMemoryQuestionBank();
        Assert.True((await bank.CreateCategoryAsync("Science")).Ok);

        var dup = await bank.CreateCategoryAsync("  science ");
        Assert.False(dup.Ok);
        Assert.Equal(CategoryError.NameTaken, dup.Error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Blank_category_name_is_rejected(string name)
    {
        var bank = new InMemoryQuestionBank();
        var result = await bank.CreateCategoryAsync(name);
        Assert.False(result.Ok);
        Assert.Equal(CategoryError.NameInvalid, result.Error);
    }

    [Fact]
    public async Task Rename_missing_category_reports_not_found()
    {
        var bank = new InMemoryQuestionBank();
        var result = await bank.RenameCategoryAsync(999, "New");
        Assert.Equal(CategoryError.NotFound, result.Error);
    }

    [Fact]
    public async Task Delete_empty_category_succeeds()
    {
        var bank = new InMemoryQuestionBank();
        var id = await SeedCategoryAsync(bank);
        Assert.Equal(CategoryDeleteResult.Deleted, await bank.DeleteCategoryAsync(id));
    }

    [Fact]
    public async Task Delete_missing_category_reports_not_found()
    {
        var bank = new InMemoryQuestionBank();
        Assert.Equal(CategoryDeleteResult.NotFound, await bank.DeleteCategoryAsync(123));
    }

    [Fact]
    public async Task Delete_category_with_questions_is_blocked()
    {
        var bank = new InMemoryQuestionBank();
        var id = await SeedCategoryAsync(bank);
        Assert.True((await bank.CreateQuestionAsync(Mc(id), Guid.NewGuid())).Ok);

        Assert.Equal(CategoryDeleteResult.InUse, await bank.DeleteCategoryAsync(id));
    }

    [Fact]
    public async Task Soft_deleted_question_still_blocks_category_delete()
    {
        var bank = new InMemoryQuestionBank();
        var id = await SeedCategoryAsync(bank);
        var created = await bank.CreateQuestionAsync(Mc(id), Guid.NewGuid());
        await bank.SoftDeleteQuestionAsync(created.Question!.Id);

        // The question row is kept (plan §6.2) so the reference stays valid: the category is still in use.
        Assert.Equal(CategoryDeleteResult.InUse, await bank.DeleteCategoryAsync(id));
    }

    // ---- Questions ----

    [Fact]
    public async Task Create_multiple_choice_maps_correct_option()
    {
        var bank = new InMemoryQuestionBank();
        var id = await SeedCategoryAsync(bank);

        var result = await bank.CreateQuestionAsync(Mc(id), Guid.NewGuid());
        Assert.True(result.Ok);
        Assert.Equal('A', result.Question!.CorrectOption);
        Assert.Equal(2, result.Question.Options.Count);
        Assert.Null(result.Question.CorrectNumber);
    }

    [Fact]
    public async Task Create_estimation_maps_number_and_has_no_options()
    {
        var bank = new InMemoryQuestionBank();
        var id = await SeedCategoryAsync(bank);

        var result = await bank.CreateQuestionAsync(Est(id) with { Unit = "kg" }, Guid.NewGuid());
        Assert.True(result.Ok);
        Assert.Equal(42, result.Question!.CorrectNumber);
        Assert.Equal("kg", result.Question.Unit);
        Assert.Empty(result.Question.Options);
        Assert.Null(result.Question.CorrectOption);
    }

    [Fact]
    public async Task Create_question_for_missing_category_is_rejected()
    {
        var bank = new InMemoryQuestionBank();
        var result = await bank.CreateQuestionAsync(Mc(777), Guid.NewGuid());
        Assert.False(result.Ok);
        Assert.Equal(QuestionError.CategoryNotFound, result.Error);
    }

    [Fact]
    public async Task Create_invalid_question_is_rejected_before_storage()
    {
        var bank = new InMemoryQuestionBank();
        var id = await SeedCategoryAsync(bank);

        var bad = Mc(id) with { Options = [new OptionUpsert('A', "x", true), new OptionUpsert('B', "y", true)] };
        var result = await bank.CreateQuestionAsync(bad, Guid.NewGuid());

        Assert.False(result.Ok);
        Assert.Equal(QuestionError.Invalid, result.Error);
        var page = await bank.ListQuestionsAsync(new QuestionQuery(id, null, null, null, 1, 20));
        Assert.Empty(page.Items);
    }

    [Fact]
    public async Task Update_replaces_fields_and_options()
    {
        var bank = new InMemoryQuestionBank();
        var id = await SeedCategoryAsync(bank);
        var created = await bank.CreateQuestionAsync(Mc(id), Guid.NewGuid());

        var updated = await bank.UpdateQuestionAsync(created.Question!.Id, Est(id, "Now estimation") with { CorrectNumber = 7 });

        Assert.True(updated.Ok);
        Assert.Equal(QuestionType.Estimation, updated.Question!.Type);
        Assert.Equal(7, updated.Question.CorrectNumber);
        Assert.Empty(updated.Question.Options);
    }

    [Fact]
    public async Task Update_missing_question_reports_not_found()
    {
        var bank = new InMemoryQuestionBank();
        var id = await SeedCategoryAsync(bank);
        var result = await bank.UpdateQuestionAsync(Guid.NewGuid(), Mc(id));
        Assert.Equal(QuestionError.NotFound, result.Error);
    }

    [Fact]
    public async Task Soft_delete_hides_question_from_get_and_list()
    {
        var bank = new InMemoryQuestionBank();
        var id = await SeedCategoryAsync(bank);
        var created = await bank.CreateQuestionAsync(Mc(id), Guid.NewGuid());
        var questionId = created.Question!.Id;

        Assert.True(await bank.SoftDeleteQuestionAsync(questionId));
        Assert.Null(await bank.GetQuestionAsync(questionId));

        var page = await bank.ListQuestionsAsync(new QuestionQuery(null, null, null, null, 1, 20));
        Assert.DoesNotContain(page.Items, q => q.Id == questionId);
    }

    [Fact]
    public async Task Soft_delete_missing_question_returns_false()
    {
        var bank = new InMemoryQuestionBank();
        Assert.False(await bank.SoftDeleteQuestionAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task List_filters_by_type_difficulty_and_search()
    {
        var bank = new InMemoryQuestionBank();
        var id = await SeedCategoryAsync(bank);
        var owner = Guid.NewGuid();
        await bank.CreateQuestionAsync(Mc(id, "Capital of France", difficulty: 1), owner);
        await bank.CreateQuestionAsync(Est(id, "Height of Everest", difficulty: 3), owner);

        var byType = await bank.ListQuestionsAsync(new QuestionQuery(null, QuestionType.Estimation, null, null, 1, 20));
        Assert.Single(byType.Items);
        Assert.Equal(QuestionType.Estimation, byType.Items[0].Type);

        var byDifficulty = await bank.ListQuestionsAsync(new QuestionQuery(null, null, 3, null, 1, 20));
        Assert.Single(byDifficulty.Items);

        var bySearch = await bank.ListQuestionsAsync(new QuestionQuery(null, null, null, "france", 1, 20));
        Assert.Single(bySearch.Items);
        Assert.Contains("France", bySearch.Items[0].Text);
    }

    [Fact]
    public async Task List_paginates_and_reports_total()
    {
        var bank = new InMemoryQuestionBank();
        var id = await SeedCategoryAsync(bank);
        var owner = Guid.NewGuid();
        for (var i = 0; i < 5; i++)
            await bank.CreateQuestionAsync(Mc(id, $"Q{i}"), owner);

        var page = await bank.ListQuestionsAsync(new QuestionQuery(null, null, null, null, Page: 1, PageSize: 2));

        Assert.Equal(5, page.TotalCount);
        Assert.Equal(2, page.Items.Count);
        Assert.Equal(1, page.Page);
        Assert.Equal(2, page.PageSize);
    }
}
