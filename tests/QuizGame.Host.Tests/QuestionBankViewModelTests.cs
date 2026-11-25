using QuizGame.Contracts;
using QuizGame.Host.ViewModels;

namespace QuizGame.Host.Tests;

public class QuestionBankViewModelTests
{
    [Fact]
    public async Task EnsureLoadedAsync_LoadsOnceLazilyAfterAttach()
    {
        var api = new FakeQuestionBankApi
        {
            GetCategoriesHandler = () => [new(1, "History"), new(2, "Space")],
            GetQuestionsHandler = q => new PagedResult<QuestionDto>(
                [FakeQuestionBankApi.SampleQuestion(categoryId: 1), FakeQuestionBankApi.SampleEstimationQuestion(categoryId: 2)],
                q.Page,
                q.PageSize,
                2)
        };
        var dialogs = new FakeQuestionBankDialogService();
        var vm = new QuestionBankViewModel(dialogs);

        // EnsureLoaded before AttachApi is a no-op
        await vm.EnsureLoadedAsync();
        Assert.Empty(api.Queries);

        // AttachApi must not trigger eager network calls
        vm.AttachApi(api);
        Assert.Empty(api.Queries);
        Assert.True(vm.IsAttached);

        await vm.EnsureLoadedAsync();
        await vm.EnsureLoadedAsync();

        Assert.Single(api.Queries);
        Assert.Equal(3, vm.CategoryFilters.Count); // All + 2 categories
        Assert.Equal(2, vm.Questions.Count);

        Assert.Equal("History", vm.Questions[0].CategoryName);
        Assert.Equal("Multiple choice", vm.Questions[0].TypeDisplay);
        Assert.Equal("1 – Easy", vm.Questions[0].DifficultyDisplay);
        Assert.Equal("20 s", vm.Questions[0].TimeLimitDisplay);
        Assert.Equal("B: Paris", vm.Questions[0].AnswerSummary);

        Assert.Equal("Space", vm.Questions[1].CategoryName);
        Assert.Equal("Estimation", vm.Questions[1].TypeDisplay);
        Assert.Equal("384400 km", vm.Questions[1].AnswerSummary);
    }

    [Fact]
    public async Task ChangingFiltersOrPageSize_ResetsPageToOneAndQueriesServer()
    {
        var api = new FakeQuestionBankApi
        {
            GetCategoriesHandler = () => [new(5, "Science")],
            GetQuestionsHandler = q => new PagedResult<QuestionDto>([], q.Page, q.PageSize, 50)
        };
        var vm = new QuestionBankViewModel(new FakeQuestionBankDialogService());
        vm.AttachApi(api);
        await vm.EnsureLoadedAsync();

        await vm.ChangePageAsync(3);
        Assert.Equal(3, vm.Page);

        vm.SelectedCategoryFilter = vm.CategoryFilters.Single(c => c.Value == 5);
        await vm.CurrentLoadTask;
        Assert.Equal(1, vm.Page);
        Assert.Equal(5, api.Queries[^1].CategoryId);

        await vm.ChangePageAsync(2);
        vm.SelectedTypeFilter = vm.TypeFilters.Single(t => t.Value == QuestionType.Estimation);
        await vm.CurrentLoadTask;
        Assert.Equal(1, vm.Page);
        Assert.Equal(QuestionType.Estimation, api.Queries[^1].Type);

        vm.SelectedDifficultyFilter = vm.DifficultyFilters.Single(d => d.Value == 3);
        await vm.CurrentLoadTask;
        Assert.Equal(3, api.Queries[^1].Difficulty);

        vm.SelectedPageSize = 50;
        await vm.CurrentLoadTask;
        Assert.Equal(50, api.Queries[^1].PageSize);
    }

    [Fact]
    public async Task SearchAndClearFilters_UpdateQueriesAndResetState()
    {
        var api = new FakeQuestionBankApi
        {
            GetCategoriesHandler = () => [new(1, "History")],
            GetQuestionsHandler = q => new PagedResult<QuestionDto>([], q.Page, q.PageSize, 40)
        };
        var vm = new QuestionBankViewModel(new FakeQuestionBankDialogService());
        vm.AttachApi(api);
        await vm.EnsureLoadedAsync();

        vm.SearchText = "  Danube  ";
        await vm.SearchAsync();
        Assert.Equal("Danube", api.Queries[^1].Search);

        vm.SelectedTypeFilter = vm.TypeFilters[1];
        await vm.CurrentLoadTask;

        await vm.ClearFiltersAsync();
        Assert.Equal("", vm.SearchText);
        Assert.Null(vm.SelectedCategoryFilter.Value);
        Assert.Null(vm.SelectedTypeFilter.Value);
        Assert.Null(vm.SelectedDifficultyFilter.Value);
        Assert.Equal(1, vm.Page);
        Assert.Null(api.Queries[^1].Search);
        Assert.Null(api.Queries[^1].Type);
    }

    [Fact]
    public async Task AddEditDeleteAndCategories_WorkWithDialogService()
    {
        var q1 = FakeQuestionBankApi.SampleQuestion(categoryId: 1);
        var currentItems = new List<QuestionDto> { q1 };
        var api = new FakeQuestionBankApi
        {
            GetCategoriesHandler = () => [new(1, "History")],
            GetQuestionsHandler = q => new PagedResult<QuestionDto>(currentItems.ToList(), q.Page, q.PageSize, currentItems.Count),
            GetQuestionHandler = id => q1
        };
        var dialogs = new FakeQuestionBankDialogService();
        var vm = new QuestionBankViewModel(dialogs);
        vm.AttachApi(api);
        await vm.EnsureLoadedAsync();

        // Add cancelled -> no extra reload
        dialogs.EditorResult = null;
        await vm.AddQuestionAsync();
        Assert.Single(api.Queries);

        // Add saved -> invokes CreateQuestionAsync & reloads
        dialogs.EditorHandler = (_, _, onSave) =>
        {
            var created = onSave(new QuestionUpsertRequest { CategoryId = 1, Text = "New Q", Difficulty = 1, TimeLimitSeconds = 20 }).GetAwaiter().GetResult();
            currentItems.Add(created);
            return created;
        };
        await vm.AddQuestionAsync();
        Assert.Equal(2, api.Queries.Count);
        Assert.Equal(2, vm.Questions.Count);

        // Edit selected -> fetches full question via GetQuestionAsync & reloads on save
        vm.SelectedQuestion = vm.Questions[0];
        dialogs.EditorHandler = (_, existing, onSave) =>
        {
            Assert.Same(q1, existing);
            return onSave(new QuestionUpsertRequest { CategoryId = 1, Text = "Updated", Difficulty = 2, TimeLimitSeconds = 25 }).GetAwaiter().GetResult();
        };
        await vm.EditSelectedQuestionAsync();
        Assert.Equal([q1.Id], api.LoadedQuestionIds);
        Assert.Equal(3, api.Queries.Count);

        // Delete cancelled -> nothing deleted
        dialogs.ConfirmResult = false;
        await vm.DeleteSelectedQuestionAsync();
        Assert.Empty(api.DeletedQuestionIds);

        // Delete confirmed -> deletes and reloads
        dialogs.ConfirmResult = true;
        await vm.DeleteSelectedQuestionAsync();
        Assert.Equal([q1.Id], api.DeletedQuestionIds);
        Assert.Equal(4, api.Queries.Count);
    }

    [Fact]
    public async Task DeleteLastItemOnPageTwo_DecrementsPageToOne()
    {
        var totalCount = 11;
        var qPage2 = FakeQuestionBankApi.SampleQuestion(categoryId: 1);
        var api = new FakeQuestionBankApi
        {
            GetCategoriesHandler = () => [new(1, "History")],
            GetQuestionsHandler = q => q.Page == 2
                ? new PagedResult<QuestionDto>([qPage2], 2, 10, totalCount)
                : new PagedResult<QuestionDto>([FakeQuestionBankApi.SampleQuestion(categoryId: 1)], 1, 10, totalCount),
            DeleteQuestionHandler = _ => totalCount--
        };
        var dialogs = new FakeQuestionBankDialogService { ConfirmResult = true };
        var vm = new QuestionBankViewModel(dialogs) { SelectedPageSize = 10 };
        vm.AttachApi(api);
        await vm.EnsureLoadedAsync();

        await vm.ChangePageAsync(2);
        Assert.Equal(2, vm.Page);
        vm.SelectedQuestion = vm.Questions[0];

        await vm.DeleteSelectedQuestionAsync();

        Assert.Equal(1, vm.Page);
        Assert.Equal(1, api.Queries[^1].Page);
    }

    [Fact]
    public async Task ManageCategories_WhenDeletedCategoryWasSelected_ResetsFilterToAll()
    {
        var categories = new List<CategoryDto> { new(1, "History"), new(2, "Temp") };
        var api = new FakeQuestionBankApi
        {
            GetCategoriesHandler = () => categories.ToList(),
            GetQuestionsHandler = q => new PagedResult<QuestionDto>([], q.Page, q.PageSize, 0)
        };
        var dialogs = new FakeQuestionBankDialogService { CategoryManagerResult = true };
        var vm = new QuestionBankViewModel(dialogs);
        vm.AttachApi(api);
        await vm.EnsureLoadedAsync();

        vm.SelectedCategoryFilter = vm.CategoryFilters.Single(c => c.Value == 2);
        await vm.CurrentLoadTask;
        Assert.Equal(2, vm.SelectedCategoryFilter.Value);

        // Simulate category 2 being deleted inside CategoryManager
        categories.RemoveAll(c => c.Id == 2);
        await vm.ManageCategoriesAsync();

        Assert.Null(vm.SelectedCategoryFilter.Value);
        Assert.Equal(1, vm.Page);
        Assert.Null(api.Queries[^1].CategoryId);
    }

    [Fact]
    public async Task AddQuestionWithoutCategories_ShowsHelpfulError()
    {
        var api = new FakeQuestionBankApi { GetCategoriesHandler = () => [] };
        var vm = new QuestionBankViewModel(new FakeQuestionBankDialogService());
        vm.AttachApi(api);

        await vm.AddQuestionAsync();

        Assert.Contains("Create at least one category", vm.ErrorMessage);
    }

    [Fact]
    public async Task Reset_ClearsStateAndDetachesApi()
    {
        var api = new FakeQuestionBankApi
        {
            GetCategoriesHandler = () => [new(1, "History")],
            GetQuestionsHandler = q => new PagedResult<QuestionDto>([FakeQuestionBankApi.SampleQuestion(categoryId: 1)], 1, 20, 1)
        };
        var vm = new QuestionBankViewModel(new FakeQuestionBankDialogService());
        vm.AttachApi(api);
        await vm.EnsureLoadedAsync();

        vm.SearchText = "France";
        vm.Reset();

        Assert.False(vm.IsAttached);
        Assert.Empty(vm.Questions);
        Assert.Single(vm.CategoryFilters);
        Assert.Equal("", vm.SearchText);
        Assert.Equal(1, vm.Page);
        Assert.Equal(0, vm.TotalCount);
    }
}
