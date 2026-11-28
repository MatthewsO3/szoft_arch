namespace QuizGame.Host.Services;

/// <summary>Creates typed REST API clients for a target server URL.</summary>
public interface IApiClientFactory
{
    ApiClient Create(string baseUrl);
}
