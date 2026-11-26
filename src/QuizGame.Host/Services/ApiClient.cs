using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using QuizGame.Contracts;

namespace QuizGame.Host.Services;

public sealed class ApiException(string message, int statusCode) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}

/// <summary>Typed REST client for the game server (plan §9.1). Attaches the game-master JWT.</summary>
public sealed class ApiClient : IQuestionBankApi, IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;

    public ApiClient(string baseUrl, HttpMessageHandler? handler = null)
    {
        BaseUrl = NormalizeBaseUrl(baseUrl);
        _http = handler is null
            ? new HttpClient { BaseAddress = new Uri(BaseUrl + "/"), Timeout = TimeSpan.FromSeconds(10) }
            : new HttpClient(handler, disposeHandler: false) { BaseAddress = new Uri(BaseUrl + "/"), Timeout = TimeSpan.FromSeconds(10) };
    }

    public string BaseUrl { get; }

    public string? Token
    {
        set => _http.DefaultRequestHeaders.Authorization =
            value is null ? null : new AuthenticationHeaderValue("Bearer", value);
    }

    /// <summary>Adds http:// when no scheme is given and strips trailing slashes.</summary>
    public static string NormalizeBaseUrl(string url)
    {
        url = url.Trim().TrimEnd('/');
        if (url.Length == 0) throw new ArgumentException("Enter the server URL.");
        if (!url.Contains("://")) url = "http://" + url;
        if (!Uri.TryCreate(url, UriKind.Absolute, out _)) throw new ArgumentException("The server URL is not valid.");
        return url;
    }

    /// <summary>Builds the relative query URI for <c>GET /api/questions</c>.</summary>
    public static string BuildQuestionsPath(QuestionQuery query)
    {
        var page = Math.Max(1, query.Page);
        var pageSize = Math.Max(1, query.PageSize);
        var parts = new List<string>
        {
            $"page={page}",
            $"pageSize={pageSize}"
        };

        if (query.CategoryId is int categoryId)
            parts.Add($"categoryId={categoryId}");

        if (query.Type is QuestionType type)
            parts.Add($"type={type}");

        if (query.Difficulty is int difficulty)
            parts.Add($"difficulty={difficulty}");

        if (!string.IsNullOrWhiteSpace(query.Search))
            parts.Add($"search={Uri.EscapeDataString(query.Search.Trim())}");

        return "api/questions?" + string.Join('&', parts);
    }

    public Task<AuthResponse> LoginAsync(string username, string password) =>
        SendAsync<AuthResponse>(HttpMethod.Post, "api/auth/login", new LoginRequest(username, password));

    public Task<CreateRoomResponse> CreateRoomAsync(RoomSettings? settings = null, CancellationToken ct = default) =>
        SendAsync<CreateRoomResponse>(HttpMethod.Post, "api/rooms", settings, ct);

    public Task CloseRoomAsync(string roomCode) =>
        SendAsync<object>(HttpMethod.Delete, $"api/rooms/{roomCode}");

    public async Task<IReadOnlyList<CategoryDto>> GetCategoriesAsync(CancellationToken ct = default) =>
        await SendAsync<CategoryDto[]>(HttpMethod.Get, "api/categories", ct: ct) ?? [];

    public Task<CategoryDto> CreateCategoryAsync(string name, CancellationToken ct = default) =>
        SendAsync<CategoryDto>(HttpMethod.Post, "api/categories", new CategoryNamePayload(name), ct);

    public Task<CategoryDto> UpdateCategoryAsync(int id, string name, CancellationToken ct = default) =>
        SendAsync<CategoryDto>(HttpMethod.Put, $"api/categories/{id}", new CategoryNamePayload(name), ct);

    public Task DeleteCategoryAsync(int id, CancellationToken ct = default) =>
        SendAsync<object>(HttpMethod.Delete, $"api/categories/{id}", ct: ct);

    public Task<PagedResult<QuestionDto>> GetQuestionsAsync(QuestionQuery query, CancellationToken ct = default) =>
        SendAsync<PagedResult<QuestionDto>>(HttpMethod.Get, BuildQuestionsPath(query), ct: ct);

    public Task<QuestionDto> GetQuestionAsync(Guid id, CancellationToken ct = default) =>
        SendAsync<QuestionDto>(HttpMethod.Get, $"api/questions/{id}", ct: ct);

    public Task<QuestionDto> CreateQuestionAsync(QuestionUpsertRequest request, CancellationToken ct = default) =>
        SendAsync<QuestionDto>(HttpMethod.Post, "api/questions", request, ct);

    public Task<QuestionDto> UpdateQuestionAsync(Guid id, QuestionUpsertRequest request, CancellationToken ct = default) =>
        SendAsync<QuestionDto>(HttpMethod.Put, $"api/questions/{id}", request, ct);

    public Task DeleteQuestionAsync(Guid id, CancellationToken ct = default) =>
        SendAsync<object>(HttpMethod.Delete, $"api/questions/{id}", ct: ct);

    private async Task<T> SendAsync<T>(HttpMethod method, string path, object? body = null, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = JsonContent.Create(body, options: Json);

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, ct);
        }
        catch (HttpRequestException)
        {
            throw new ApiException("Cannot reach the server. Check the URL and that it is running.", 0);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ApiException("Cannot reach the server. Check the URL and that it is running.", 0);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
                throw new ApiException(await ReadProblemAsync(response, ct), (int)response.StatusCode);

            if (response.Content.Headers.ContentLength is 0 || response.StatusCode == System.Net.HttpStatusCode.NoContent)
                return default!;
            return (await response.Content.ReadFromJsonAsync<T>(Json, ct))!;
        }
    }

    private static async Task<string> ReadProblemAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            return "Too many attempts. Wait a moment and try again.";
        try
        {
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var root = doc.RootElement;

            if (root.TryGetProperty("detail", out var detail) &&
                detail.ValueKind == JsonValueKind.String &&
                detail.GetString() is { } detailText &&
                !string.IsNullOrWhiteSpace(detailText))
            {
                return detailText;
            }

            if (root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Object)
            {
                var messages = new List<string>();
                foreach (var prop in errors.EnumerateObject())
                {
                    if (prop.Value.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in prop.Value.EnumerateArray())
                        {
                            if (item.ValueKind == JsonValueKind.String && item.GetString() is { } msg && !string.IsNullOrWhiteSpace(msg))
                                messages.Add(msg);
                        }
                    }
                    else if (prop.Value.ValueKind == JsonValueKind.String && prop.Value.GetString() is { } msg && !string.IsNullOrWhiteSpace(msg))
                    {
                        messages.Add(msg);
                    }
                }

                if (messages.Count > 0)
                    return string.Join(" ", messages);
            }

            if (root.TryGetProperty("title", out var title) &&
                title.ValueKind == JsonValueKind.String &&
                title.GetString() is { } titleText &&
                !string.IsNullOrWhiteSpace(titleText))
            {
                return titleText;
            }
        }
        catch (JsonException)
        {
            // not a ProblemDetails body
        }

        return $"Request failed ({(int)response.StatusCode}).";
    }

    public void Dispose() => _http.Dispose();

    private sealed record CategoryNamePayload(string Name);
}
