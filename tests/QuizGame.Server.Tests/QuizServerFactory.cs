using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Configuration;
using QuizGame.Contracts;

namespace QuizGame.Server.Tests;

/// <summary>
/// In-process test host for REST + SignalR integration tests using <see cref="WebApplicationFactory{TEntryPoint}"/>
/// and Development in-memory persistence.
/// </summary>
public sealed class QuizServerFactory : WebApplicationFactory<Program>
{
    public const string AdminUsername = "admin";
    public const string AdminPassword = "admin";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"] = "",
                ["Jwt:Key"] = "integration-test-signing-key-at-least-32-bytes!",
                ["Jwt:Issuer"] = "QuizGame",
                ["Jwt:Audience"] = "QuizGame",
                ["Seed:AdminUsername"] = AdminUsername,
                ["Seed:AdminPassword"] = AdminPassword,
                ["RateLimits:LoginPerMinute"] = "1000",
                ["RateLimits:JoinPerMinute"] = "1000",
            });
        });
    }

    public HttpClient CreateAuthedClient(string token)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    public async Task<TestHubClient> ConnectHubAsync(string token)
    {
        var hubUri = new Uri(Server.BaseAddress, HubNames.HubPath);
        var connection = new HubConnectionBuilder()
            .WithUrl(hubUri, options =>
            {
                options.HttpMessageHandlerFactory = _ => Server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
                options.AccessTokenProvider = () => Task.FromResult<string?>(token);
            })
            .Build();

        var client = new TestHubClient(connection);
        await connection.StartAsync();
        return client;
    }
}

/// <summary>Records SignalR hub events on a connection and lets tests await specific payloads reliably.</summary>
public sealed class TestHubClient : IAsyncDisposable
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    private readonly object _gate = new();
    private readonly List<(string Name, object? Payload)> _events = [];
    private readonly List<PendingWaiter> _waiters = [];

    private sealed record PendingWaiter(
        string Name,
        Func<object?, bool> Match,
        TaskCompletionSource<object?> Tcs,
        int AfterIndex);

    public HubConnection Connection { get; }

    public int EventCount
    {
        get { lock (_gate) return _events.Count; }
    }

    public TestHubClient(HubConnection connection)
    {
        Connection = connection;

        On<RoomStateDto>(HubNames.Events.RoomState);
        On<PlayerSummary>(HubNames.Events.PlayerJoined);
        On<PlayerSummary>(HubNames.Events.PlayerConnectionChanged);
        On<PlayerSummary>(HubNames.Events.PlayerLeft);
        On<GamePhase>(HubNames.Events.PhaseChanged);
        On<MapUpdate>(HubNames.Events.MapUpdated);
        On<ScoreEntry[]>(HubNames.Events.ScoreboardUpdated);
        On<PickRequest>(HubNames.Events.PickRequested);
        On<PickWaitingDto>(HubNames.Events.PickWaiting);
        On<JsonElement>(HubNames.Events.QuestionStarted);
        On<bool>(HubNames.Events.QuestionSelectionAvailabilityChanged);
        On<DuelNominationDto>(HubNames.Events.DuelNominated);
        On<DuelAnnouncement>(HubNames.Events.DuelAnnounced);
        On<AnswerCount>(HubNames.Events.AnswerCountChanged);
        On<RoundClosedDto>(HubNames.Events.RoundClosed);
        On<GameFinishedDto>(HubNames.Events.GameFinished);
        On<ErrorDto>(HubNames.Events.Error);
        connection.On(HubNames.Events.Kicked, () => Record(HubNames.Events.Kicked, null));
        connection.On(HubNames.Events.RoomClosed, () => Record(HubNames.Events.RoomClosed, null));
    }

    private void On<T>(string eventName) =>
        Connection.On<T>(eventName, payload => Record(eventName, payload));

    private void Record(string name, object? payload)
    {
        lock (_gate)
        {
            var index = _events.Count;
            _events.Add((name, payload));
            for (var i = _waiters.Count - 1; i >= 0; i--)
            {
                var w = _waiters[i];
                if (w.Name == name && index >= w.AfterIndex && w.Match(payload))
                {
                    _waiters.RemoveAt(i);
                    w.Tcs.TrySetResult(payload);
                }
            }
        }
    }

    public void ClearEvents()
    {
        lock (_gate) _events.Clear();
    }

    public async Task<T> WaitForAsync<T>(
        string eventName,
        Func<T, bool>? predicate = null,
        int afterIndex = 0,
        int timeoutMs = 8000)
    {
        bool Matches(object? raw, out T typed)
        {
            if (raw is T direct)
            {
                typed = direct;
                return predicate is null || predicate(typed);
            }
            if (raw is JsonElement el && el.Deserialize<T>(WebJson) is { } deserialized)
            {
                typed = deserialized;
                return predicate is null || predicate(typed);
            }
            typed = default!;
            return false;
        }

        TaskCompletionSource<object?> tcs;
        lock (_gate)
        {
            for (var i = afterIndex; i < _events.Count; i++)
            {
                var (name, payload) = _events[i];
                if (name == eventName && Matches(payload, out var matched))
                    return matched;
            }

            tcs = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Add(new PendingWaiter(eventName, raw => Matches(raw, out _), tcs, afterIndex));
        }

        using var cts = new CancellationTokenSource(timeoutMs);
        using var registration = cts.Token.Register(() =>
            tcs.TrySetException(new TimeoutException($"Timed out waiting for SignalR event '{eventName}'.")));

        var resolved = await tcs.Task;
        Matches(resolved, out var result);
        return result;
    }

    public async Task WaitForSignalAsync(string eventName, int afterIndex = 0, int timeoutMs = 8000)
    {
        TaskCompletionSource<object?> tcs;
        lock (_gate)
        {
            for (var i = afterIndex; i < _events.Count; i++)
            {
                if (_events[i].Name == eventName)
                    return;
            }

            tcs = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Add(new PendingWaiter(eventName, _ => true, tcs, afterIndex));
        }

        using var cts = new CancellationTokenSource(timeoutMs);
        using var registration = cts.Token.Register(() =>
            tcs.TrySetException(new TimeoutException($"Timed out waiting for SignalR signal '{eventName}'.")));

        await tcs.Task;
    }

    public ValueTask DisposeAsync() => Connection.DisposeAsync();
}
