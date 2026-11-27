namespace QuizGame.Host.Services;

/// <summary>Default <see cref="IApiClientFactory"/> creating <see cref="ApiClient"/> instances.</summary>
public sealed class ApiClientFactory : IApiClientFactory
{
    public ApiClient Create(string baseUrl) => new(baseUrl);
}
