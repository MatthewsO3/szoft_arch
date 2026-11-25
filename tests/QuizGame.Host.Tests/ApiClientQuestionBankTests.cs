using System.Net;
using System.Text.Json;
using QuizGame.Contracts;
using QuizGame.Host.Services;

namespace QuizGame.Host.Tests;

public class ApiClientQuestionBankTests
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    [Fact]
    public void BuildQuestionsPath_DefaultQuery_IncludesPageAndPageSizeOnly()
    {
        var path = ApiClient.BuildQuestionsPath(new QuestionQuery());
        Assert.Equal("api/questions?page=1&pageSize=20", path);
    }

    [Fact]
    public void BuildQuestionsPath_WithFiltersAndWhitespace_ClampsAndEscapes()
    {
        var query = new QuestionQuery(
            CategoryId: 4,
            Type: QuestionType.Estimation,
            Difficulty: 2,
            Search: "  c++ & c#  ",
            Page: 0,
            PageSize: -5);

        var path = ApiClient.BuildQuestionsPath(query);
        Assert.Equal("api/questions?page=1&pageSize=1&categoryId=4&type=Estimation&difficulty=2&search=c%2B%2B%20%26%20c%23", path);
    }

    [Fact]
    public void BuildQuestionsPath_WhitespaceSearch_IsOmitted()
    {
        var path = ApiClient.BuildQuestionsPath(new QuestionQuery(Search: "   ", Page: 2, PageSize: 10));
        Assert.Equal("api/questions?page=2&pageSize=10", path);
    }

    [Fact]
    public async Task CategoryEndpoints_SendExpectedRequestsAndDeserializeResponses()
    {
        var callIndex = 0;
        var handler = new StubHttpMessageHandler((_, _) =>
        {
            var resp = callIndex++ switch
            {
                0 => StubHttpMessageHandler.JsonResponse(HttpStatusCode.OK, """[{"id":1,"name":"History"}]"""),
                1 => StubHttpMessageHandler.JsonResponse(HttpStatusCode.Created, """{"id":2,"name":"Science"}"""),
                2 => StubHttpMessageHandler.JsonResponse(HttpStatusCode.OK, """{"id":2,"name":"Natural Science"}"""),
                _ => new HttpResponseMessage(HttpStatusCode.NoContent)
            };
            return Task.FromResult(resp);
        });

        using var client = new ApiClient("http://localhost:5084", handler) { Token = "gm-jwt" };

        var list = await client.GetCategoriesAsync();
        var created = await client.CreateCategoryAsync("Science");
        var updated = await client.UpdateCategoryAsync(2, "Natural Science");
        await client.DeleteCategoryAsync(2);

        Assert.Single(list);
        Assert.Equal("History", list[0].Name);
        Assert.Equal(new CategoryDto(2, "Science"), created);
        Assert.Equal(new CategoryDto(2, "Natural Science"), updated);

        Assert.Equal(4, handler.Calls.Count);
        Assert.All(handler.Calls, c => Assert.Equal("Bearer gm-jwt", c.Authorization));

        Assert.Equal((HttpMethod.Get, "/api/categories"), (handler.Calls[0].Method, handler.Calls[0].PathAndQuery));
        Assert.Equal((HttpMethod.Post, "/api/categories"), (handler.Calls[1].Method, handler.Calls[1].PathAndQuery));
        Assert.Equal("""{"name":"Science"}""", handler.Calls[1].Body);
        Assert.Equal((HttpMethod.Put, "/api/categories/2"), (handler.Calls[2].Method, handler.Calls[2].PathAndQuery));
        Assert.Equal("""{"name":"Natural Science"}""", handler.Calls[2].Body);
        Assert.Equal((HttpMethod.Delete, "/api/categories/2"), (handler.Calls[3].Method, handler.Calls[3].PathAndQuery));
    }

    [Fact]
    public async Task QuestionEndpoints_SendExpectedRequestsAndDeserializeResponses()
    {
        var qId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        var sample = FakeQuestionBankApi.SampleQuestion(qId, 3);
        var paged = new PagedResult<QuestionDto>([sample], 2, 10, 15);

        var callIndex = 0;
        var handler = new StubHttpMessageHandler((_, _) =>
        {
            var resp = callIndex++ switch
            {
                0 => StubHttpMessageHandler.JsonResponse(HttpStatusCode.OK, JsonSerializer.Serialize(paged, WebJson)),
                1 => StubHttpMessageHandler.JsonResponse(HttpStatusCode.OK, JsonSerializer.Serialize(sample, WebJson)),
                2 => StubHttpMessageHandler.JsonResponse(HttpStatusCode.Created, JsonSerializer.Serialize(sample, WebJson)),
                3 => StubHttpMessageHandler.JsonResponse(HttpStatusCode.OK, JsonSerializer.Serialize(sample, WebJson)),
                _ => new HttpResponseMessage(HttpStatusCode.NoContent)
            };
            return Task.FromResult(resp);
        });

        using var client = new ApiClient("http://localhost:5084", handler) { Token = "gm-jwt" };

        var pageResult = await client.GetQuestionsAsync(new QuestionQuery(CategoryId: 3, Type: QuestionType.MultipleChoice, Page: 2, PageSize: 10));
        var fetched = await client.GetQuestionAsync(qId);

        var upsert = new QuestionUpsertRequest
        {
            CategoryId = 3,
            Type = QuestionType.MultipleChoice,
            Text = "Capital of France?",
            Difficulty = 1,
            TimeLimitSeconds = 20,
            Options = [new('A', "Berlin", false), new('B', "Paris", true)]
        };

        var created = await client.CreateQuestionAsync(upsert);
        var updated = await client.UpdateQuestionAsync(qId, upsert);
        await client.DeleteQuestionAsync(qId);

        Assert.Equal(15, pageResult.TotalCount);
        Assert.Single(pageResult.Items);
        Assert.Equal(qId, fetched.Id);
        Assert.Equal(qId, created.Id);
        Assert.Equal(qId, updated.Id);

        Assert.Equal((HttpMethod.Get, "/api/questions?page=2&pageSize=10&categoryId=3&type=MultipleChoice"), (handler.Calls[0].Method, handler.Calls[0].PathAndQuery));
        Assert.Equal((HttpMethod.Get, $"/api/questions/{qId}"), (handler.Calls[1].Method, handler.Calls[1].PathAndQuery));
        Assert.Equal((HttpMethod.Post, "/api/questions"), (handler.Calls[2].Method, handler.Calls[2].PathAndQuery));
        Assert.Equal((HttpMethod.Put, $"/api/questions/{qId}"), (handler.Calls[3].Method, handler.Calls[3].PathAndQuery));
        Assert.Equal((HttpMethod.Delete, $"/api/questions/{qId}"), (handler.Calls[4].Method, handler.Calls[4].PathAndQuery));
    }

    [Fact]
    public async Task ErrorResponses_ExtractProblemDetailsValidationErrorsAndRateLimits()
    {
        var responses = new Queue<HttpResponseMessage>(
        [
            StubHttpMessageHandler.JsonResponse(HttpStatusCode.BadRequest, """{"title":"Bad Request","detail":"Question text is required."}"""),
            StubHttpMessageHandler.JsonResponse(HttpStatusCode.BadRequest, """{"title":"Validation failed","errors":{"Text":["Enter text."],"TimeLimit":["Out of range."]}}"""),
            StubHttpMessageHandler.JsonResponse(HttpStatusCode.Conflict, """{"title":"Category is in use"}"""),
            new HttpResponseMessage(HttpStatusCode.TooManyRequests),
            new HttpResponseMessage(HttpStatusCode.InternalServerError)
        ]);

        var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(responses.Dequeue()));
        using var client = new ApiClient("http://localhost:5084", handler);

        var ex1 = await Assert.ThrowsAsync<ApiException>(() => client.GetCategoriesAsync());
        Assert.Equal(400, ex1.StatusCode);
        Assert.Equal("Question text is required.", ex1.Message);

        var ex2 = await Assert.ThrowsAsync<ApiException>(() => client.GetCategoriesAsync());
        Assert.Equal("Enter text. Out of range.", ex2.Message);

        var ex3 = await Assert.ThrowsAsync<ApiException>(() => client.GetCategoriesAsync());
        Assert.Equal("Category is in use", ex3.Message);

        var ex4 = await Assert.ThrowsAsync<ApiException>(() => client.GetCategoriesAsync());
        Assert.Equal(429, ex4.StatusCode);
        Assert.Contains("Too many attempts", ex4.Message);

        var ex5 = await Assert.ThrowsAsync<ApiException>(() => client.GetCategoriesAsync());
        Assert.Equal(500, ex5.StatusCode);
        Assert.Equal("Request failed (500).", ex5.Message);
    }

    [Fact]
    public async Task CancelledToken_PropagatesOperationCanceledException()
    {
        var handler = new StubHttpMessageHandler((_, ct) => Task.FromCanceled<HttpResponseMessage>(ct));
        using var client = new ApiClient("http://localhost:5084", handler);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetCategoriesAsync(cts.Token));
    }
}
