using Microsoft.AspNetCore.SignalR.Client;
using QuizGame.Contracts;

namespace QuizGame.Host.Services;

/// <summary>
/// Owns the single SignalR connection of the host app (plan §9.1). Events fire on a background thread;
/// the view model marshals them to the UI thread. After an automatic reconnect it re-attaches to the room (plan §7.5).
/// </summary>
public sealed class GameConnection : IAsyncDisposable
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

    public async Task StopAsync()
    {
        var hub = _hub;
        _hub = null;
        if (hub is not null) await hub.DisposeAsync();
    }

    public ValueTask DisposeAsync() => new(StopAsync());
}
