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
public sealed class ApiClient : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;

    public ApiClient(string baseUrl)
    {
        BaseUrl = NormalizeBaseUrl(baseUrl);
        _http = new HttpClient { BaseAddress = new Uri(BaseUrl + "/"), Timeout = TimeSpan.FromSeconds(10) };
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

    public Task<AuthResponse> LoginAsync(string username, string password) =>
        SendAsync<AuthResponse>(HttpMethod.Post, "api/auth/login", new LoginRequest(username, password));

    public Task<CreateRoomResponse> CreateRoomAsync() =>
        SendAsync<CreateRoomResponse>(HttpMethod.Post, "api/rooms");

    public Task CloseRoomAsync(string roomCode) =>
        SendAsync<object>(HttpMethod.Delete, $"api/rooms/{roomCode}");

    private async Task<T> SendAsync<T>(HttpMethod method, string path, object? body = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = JsonContent.Create(body, options: Json);

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new ApiException("Cannot reach the server. Check the URL and that it is running.", 0);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
                throw new ApiException(await ReadProblemAsync(response), (int)response.StatusCode);

            if (response.Content.Headers.ContentLength is 0 || response.StatusCode == System.Net.HttpStatusCode.NoContent)
                return default!;
            return (await response.Content.ReadFromJsonAsync<T>(Json))!;
        }
    }

    private static async Task<string> ReadProblemAsync(HttpResponseMessage response)
    {
        if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            return "Too many attempts. Wait a moment and try again.";
        try
        {
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            if (doc.RootElement.TryGetProperty("detail", out var detail) && detail.GetString() is { } text)
                return text;
        }
        catch (JsonException)
        {
            // not a ProblemDetails body
        }

        return $"Request failed ({(int)response.StatusCode}).";
    }

    public void Dispose() => _http.Dispose();
}
