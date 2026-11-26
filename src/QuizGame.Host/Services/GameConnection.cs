using Microsoft.AspNetCore.SignalR.Client;
using QuizGame.Contracts;

namespace QuizGame.Host.Services;

/// <summary>
/// Owns the single SignalR connection of the host app (plan §9.1). Events fire on a background thread;
/// the view model marshals them to the UI thread. After an automatic reconnect it re-attaches to the room (plan §7.5).
/// </summary>
public sealed class GameConnection : IRoomControlClient, IAsyncDisposable
{
    private HubConnection? _hub;

    public event Action<RoomStateDto>? RoomStateReceived;
    public event Action<PlayerSummary>? PlayerJoined;
    public event Action<PlayerSummary>? PlayerConnectionChanged;
    public event Action<PlayerSummary>? PlayerLeft;
    public event Action? RoomClosed;
    public event Action<ErrorDto>? ErrorReceived;
    public event Action<string>? StatusChanged;
    public event Action<QuestionForHost>? QuestionStarted;
    public event Action<RoundClosedDto>? RoundClosed;
    public event Action<AnswerCount>? AnswerCountChanged;
    public event Action<GamePhase>? PhaseChanged;
    public event Action<MapUpdate>? MapUpdated;
    public event Action<PlayerSummary>? PickWaiting;
    public event Action<IReadOnlyList<ScoreEntry>>? ScoreboardUpdated;
    public event Action<GameFinishedDto>? GameFinished;
    public event Action<DuelNominationDto>? DuelNominated;
    public bool IsConnected => _hub?.State == HubConnectionState.Connected;
    public event Action<DateTimeOffset, TimeSpan>? ClockSynchronized;

    public async Task StartAsync(string baseUrl, string token, string roomCode)
    {
        await StopAsync();

        var hub = new HubConnectionBuilder()
            .WithUrl(baseUrl.TrimEnd('/') + HubNames.HubPath, o => o.AccessTokenProvider = () => Task.FromResult<string?>(token))
            .WithAutomaticReconnect([TimeSpan.Zero, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10)])
            .Build();

        hub.On<RoomStateDto>(HubNames.Events.RoomState, s => RoomStateReceived?.Invoke(s));
        hub.On<PlayerSummary>(HubNames.Events.PlayerJoined, p => PlayerJoined?.Invoke(p));
        hub.On<PlayerSummary>(HubNames.Events.PlayerConnectionChanged, p => PlayerConnectionChanged?.Invoke(p));
        hub.On<PlayerSummary>(HubNames.Events.PlayerLeft, p => PlayerLeft?.Invoke(p));
        hub.On(HubNames.Events.RoomClosed, () => RoomClosed?.Invoke());
        hub.On<ErrorDto>(HubNames.Events.Error, e => ErrorReceived?.Invoke(e));
        hub.On<QuestionForHost>(HubNames.Events.QuestionStarted, q => QuestionStarted?.Invoke(q));
        hub.On<RoundClosedDto>(HubNames.Events.RoundClosed, r => RoundClosed?.Invoke(r));
        hub.On<AnswerCount>(HubNames.Events.AnswerCountChanged, c => AnswerCountChanged?.Invoke(c));
        hub.On<GamePhase>(HubNames.Events.PhaseChanged, phase => PhaseChanged?.Invoke(phase));
        hub.On<MapUpdate>(HubNames.Events.MapUpdated, update => MapUpdated?.Invoke(update));
        hub.On<PlayerSummary>(HubNames.Events.PickWaiting, picker => PickWaiting?.Invoke(picker));
        hub.On<ScoreEntry[]>(HubNames.Events.ScoreboardUpdated, scores => ScoreboardUpdated?.Invoke(scores));
        hub.On<GameFinishedDto>(HubNames.Events.GameFinished, finished => GameFinished?.Invoke(finished));
        hub.On<DuelNominationDto>(HubNames.Events.DuelNominated, nomination => DuelNominated?.Invoke(nomination));

        hub.Reconnecting += _ =>
        {
            StatusChanged?.Invoke("Reconnecting…");
            return Task.CompletedTask;
        };
        hub.Reconnected += async _ =>
        {
            await hub.InvokeAsync(HubNames.Methods.HostAttach, roomCode);
            StatusChanged?.Invoke("Connected");
            await SynchronizeClockAsync(hub);
        };
        hub.Closed += _ =>
        {
            StatusChanged?.Invoke("Disconnected");
            return Task.CompletedTask;
        };

        _hub = hub;
        await hub.StartAsync();
        await hub.InvokeAsync(HubNames.Methods.HostAttach, roomCode);
        StatusChanged?.Invoke("Connected");
        await SynchronizeClockAsync(hub);
    }

    private async Task SynchronizeClockAsync(HubConnection hub)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            var utc = await hub.InvokeAsync<DateTimeOffset>(HubNames.Methods.Ping);
            if (ReferenceEquals(_hub, hub))
                ClockSynchronized?.Invoke(utc, System.Diagnostics.Stopwatch.GetElapsedTime(started) / 2);
        }
        catch { /* RoomState supplies a fallback clock sample. */ }
    }

    public Task KickAsync(Guid playerId) =>
        _hub?.InvokeAsync(HubNames.Methods.KickPlayer, playerId) ?? Task.CompletedTask;

    public Task SendQuestionAsync(SendQuestionRequest request) =>
        RequireHub().InvokeAsync(HubNames.Methods.SendQuestion, request);

    public Task ConfirmDuelAsync(Guid questionId) =>
        RequireHub().InvokeAsync(HubNames.Methods.ConfirmDuel, new ConfirmDuelRequest { QuestionId = questionId });

    public Task CloseRoundAsync() =>
        RequireHub().InvokeAsync(HubNames.Methods.CloseRound);

    public Task UpdateSettingsAsync(RoomSettings settings) =>
        RequireHub().InvokeAsync(HubNames.Methods.UpdateSettings, settings);

    public Task StartGameAsync() =>
        RequireHub().InvokeAsync(HubNames.Methods.StartGame);

    public Task AdvancePhaseAsync(GamePhase targetPhase) =>
        RequireHub().InvokeAsync(HubNames.Methods.AdvancePhase, targetPhase);

    private HubConnection RequireHub() =>
        _hub ?? throw new InvalidOperationException("Connect to a room first.");

    public async Task StopAsync()
    {
        var hub = _hub;
        _hub = null;
        if (hub is not null) await hub.DisposeAsync();
    }

    public ValueTask DisposeAsync() => new(StopAsync());
}
