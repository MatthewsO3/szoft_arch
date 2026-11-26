using System.Net;
using System.Text;
using QuizGame.Contracts;
using QuizGame.Host.Services;

namespace QuizGame.Host.Tests;

internal sealed record RecordedHttpCall(HttpMethod Method, string PathAndQuery, string? Authorization, string? Body);

internal sealed class StubHttpMessageHandler(
    Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder) : HttpMessageHandler
{
    public List<RecordedHttpCall> Calls { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        var pathAndQuery = request.RequestUri?.PathAndQuery ?? "";
        Calls.Add(new RecordedHttpCall(request.Method, pathAndQuery, request.Headers.Authorization?.ToString(), body));
        return await responder(request, cancellationToken);
    }

    public static HttpResponseMessage JsonResponse(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}

internal sealed class FakeQuestionBankApi : IQuestionBankApi
{
    public List<QuestionQuery> Queries { get; } = [];
    public List<Guid> LoadedQuestionIds { get; } = [];
    public List<Guid> DeletedQuestionIds { get; } = [];

    public Func<IReadOnlyList<CategoryDto>> GetCategoriesHandler { get; set; } = () => [];
    public Func<string, CategoryDto> CreateCategoryHandler { get; set; } = name => new(99, name);
    public Func<int, string, CategoryDto> UpdateCategoryHandler { get; set; } = (id, name) => new(id, name);
    public Action<int> DeleteCategoryHandler { get; set; } = _ => { };

    public Func<QuestionQuery, PagedResult<QuestionDto>> GetQuestionsHandler { get; set; } =
        q => new PagedResult<QuestionDto>([], q.Page, q.PageSize, 0);

    public Func<Guid, QuestionDto> GetQuestionHandler { get; set; } =
        id => SampleQuestion(id);

    public Func<QuestionUpsertRequest, QuestionDto> CreateQuestionHandler { get; set; } =
        req => new QuestionDto
        {
            Id = Guid.NewGuid(),
            CategoryId = req.CategoryId,
            Type = req.Type,
            Text = req.Text,
            Difficulty = req.Difficulty,
            TimeLimitSeconds = req.TimeLimitSeconds,
            CorrectNumber = req.CorrectNumber,
            Unit = req.Unit
        };

    public Func<Guid, QuestionUpsertRequest, QuestionDto> UpdateQuestionHandler { get; set; } =
        (id, req) => new QuestionDto
        {
            Id = id,
            CategoryId = req.CategoryId,
            Type = req.Type,
            Text = req.Text,
            Difficulty = req.Difficulty,
            TimeLimitSeconds = req.TimeLimitSeconds,
            CorrectNumber = req.CorrectNumber,
            Unit = req.Unit
        };

    public Action<Guid> DeleteQuestionHandler { get; set; } = _ => { };

    public Task<IReadOnlyList<CategoryDto>> GetCategoriesAsync(CancellationToken ct = default) =>
        Task.FromResult(GetCategoriesHandler());

    public Task<CategoryDto> CreateCategoryAsync(string name, CancellationToken ct = default) =>
        Task.FromResult(CreateCategoryHandler(name));

    public Task<CategoryDto> UpdateCategoryAsync(int id, string name, CancellationToken ct = default) =>
        Task.FromResult(UpdateCategoryHandler(id, name));

    public Task DeleteCategoryAsync(int id, CancellationToken ct = default)
    {
        DeleteCategoryHandler(id);
        return Task.CompletedTask;
    }

    public Task<PagedResult<QuestionDto>> GetQuestionsAsync(QuestionQuery query, CancellationToken ct = default)
    {
        Queries.Add(query);
        return Task.FromResult(GetQuestionsHandler(query));
    }

    public Task<QuestionDto> GetQuestionAsync(Guid id, CancellationToken ct = default)
    {
        LoadedQuestionIds.Add(id);
        return Task.FromResult(GetQuestionHandler(id));
    }

    public Task<QuestionDto> CreateQuestionAsync(QuestionUpsertRequest request, CancellationToken ct = default) =>
        Task.FromResult(CreateQuestionHandler(request));

    public Task<QuestionDto> UpdateQuestionAsync(Guid id, QuestionUpsertRequest request, CancellationToken ct = default) =>
        Task.FromResult(UpdateQuestionHandler(id, request));

    public Task DeleteQuestionAsync(Guid id, CancellationToken ct = default)
    {
        DeletedQuestionIds.Add(id);
        DeleteQuestionHandler(id);
        return Task.CompletedTask;
    }

    public static QuestionDto SampleQuestion(Guid? id = null, int categoryId = 1) => new()
    {
        Id = id ?? Guid.NewGuid(),
        CategoryId = categoryId,
        Type = QuestionType.MultipleChoice,
        Text = "Capital of France?",
        Difficulty = 1,
        TimeLimitSeconds = 20,
        Options =
        [
            new OptionDto(Guid.NewGuid(), 'A', "Berlin"),
            new OptionDto(Guid.NewGuid(), 'B', "Paris"),
            new OptionDto(Guid.NewGuid(), 'C', "Rome")
        ],
        CorrectOption = 'B'
    };

    public static QuestionDto SampleEstimationQuestion(Guid? id = null, int categoryId = 2) => new()
    {
        Id = id ?? Guid.NewGuid(),
        CategoryId = categoryId,
        Type = QuestionType.Estimation,
        Text = "Distance from Earth to Moon?",
        Difficulty = 2,
        TimeLimitSeconds = 30,
        CorrectNumber = 384400m,
        Unit = "km"
    };
}

internal sealed class FakeQuestionBankDialogService : IQuestionBankDialogService
{
    public bool ConfirmResult { get; set; } = true;
    public bool CategoryManagerResult { get; set; }
    public QuestionDto? EditorResult { get; set; }

    public IReadOnlyList<CategoryDto>? LastCategories { get; private set; }
    public QuestionDto? LastExistingQuestion { get; private set; }
    public (string Title, string Message)? LastConfirmation { get; private set; }
    public Func<IReadOnlyList<CategoryDto>, QuestionDto?, Func<QuestionUpsertRequest, Task<QuestionDto>>, QuestionDto?>? EditorHandler { get; set; }

    public QuestionDto? ShowQuestionEditor(
        IReadOnlyList<CategoryDto> categories,
        QuestionDto? existing,
        Func<QuestionUpsertRequest, Task<QuestionDto>> onSave)
    {
        LastCategories = categories;
        LastExistingQuestion = existing;
        return EditorHandler is not null
            ? EditorHandler(categories, existing, onSave)
            : EditorResult;
    }

    public bool ShowCategoryManager(IQuestionBankApi api) => CategoryManagerResult;

    public bool ConfirmDelete(string title, string message)
    {
        LastConfirmation = (title, message);
        return ConfirmResult;
    }
}

internal sealed class FakeRoomControlClient : IRoomControlClient
{
    public bool IsConnected { get; set; } = true;

    public List<RoomSettings> UpdatedSettings { get; } = [];
    public int StartGameCalls { get; private set; }
    public List<GamePhase> AdvancePhaseTargets { get; } = [];

    public Func<RoomSettings, Task>? UpdateSettingsHandler { get; set; }
    public Func<Task>? StartGameHandler { get; set; }
    public Func<GamePhase, Task>? AdvancePhaseHandler { get; set; }

    public Task UpdateSettingsAsync(RoomSettings settings)
    {
        UpdatedSettings.Add(settings);
        return UpdateSettingsHandler is not null ? UpdateSettingsHandler(settings) : Task.CompletedTask;
    }

    public Task StartGameAsync()
    {
        StartGameCalls++;
        return StartGameHandler is not null ? StartGameHandler() : Task.CompletedTask;
    }

    public Task AdvancePhaseAsync(GamePhase targetPhase)
    {
        AdvancePhaseTargets.Add(targetPhase);
        return AdvancePhaseHandler is not null ? AdvancePhaseHandler(targetPhase) : Task.CompletedTask;
    }
}
