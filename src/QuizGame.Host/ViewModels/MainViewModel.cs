using System.Collections.ObjectModel;
using System.Windows.Input;
using QuizGame.Contracts;
using QuizGame.Host.Services;

namespace QuizGame.Host.ViewModels;

public sealed class PlayerItem(PlayerSummary dto) : ViewModelBase
{
    public Guid Id { get; } = dto.Id;
    public string Name { get; } = dto.Name;
    public string ColorHex { get; } = dto.ColorHex;

    private bool _isConnected = dto.Connected;
    public bool IsConnected
    {
        get => _isConnected;
        set
        {
            if (Set(ref _isConnected, value)) Raise(nameof(StatusText));
        }
    }

    public string StatusText => IsConnected ? "online" : "offline";
}

/// <summary>Login + room lobby of the game master (this slice: create room, see players join, kick).</summary>
public sealed class MainViewModel : ViewModelBase
{
    private readonly SynchronizationContext _ui = SynchronizationContext.Current ?? new SynchronizationContext();
    private readonly GameConnection _connection = new();
    private ApiClient? _api;
    private string? _token;

    private string _serverUrl = "http://localhost:5084";
    private string _username = "admin";
    private string _errorMessage = "";
    private string _connectionStatus = "Not connected";
    private string _roomCode = "";
    private string _joinUrl = "";
    private bool _loggedIn;

    public MainViewModel()
    {
        LoginCommand = new AsyncRelayCommand(_ => LoginAsync(), onError: ShowError);
        LogoutCommand = new AsyncRelayCommand(_ => LogoutAsync(), onError: ShowError);
        CreateRoomCommand = new AsyncRelayCommand(_ => CreateRoomAsync(), onError: ShowError);
        CloseRoomCommand = new AsyncRelayCommand(_ => CloseRoomAsync(), _ => HasRoom, ShowError);
        KickCommand = new AsyncRelayCommand(p => KickAsync((PlayerItem)p!), p => p is PlayerItem, ShowError);

        _connection.RoomStateReceived += s => OnUi(() => ApplyState(s));
        _connection.PlayerJoined += p => OnUi(() => Upsert(p));
        _connection.PlayerConnectionChanged += p => OnUi(() => Upsert(p));
        _connection.PlayerLeft += p => OnUi(() => Remove(p.Id));
        _connection.RoomClosed += () => OnUi(ClearRoom);
        _connection.ErrorReceived += e => OnUi(() => ErrorMessage = e.Message);
        _connection.StatusChanged += s => OnUi(() => { ConnectionStatus = s; Question.ConnectionStatus = s; });
        _connection.QuestionStarted += q => OnUi(() => Question.Start(q));
        _connection.RoundClosed += r => OnUi(() => Question.Close(r));
        _connection.ClockSynchronized += (utc, latency) => OnUi(() => Question.SyncClock(utc, latency));
    }

    public QuestionViewModel Question { get; } = new();

    public ObservableCollection<PlayerItem> Players { get; } = [];

    public ICommand LoginCommand { get; }
    public ICommand LogoutCommand { get; }
    public ICommand CreateRoomCommand { get; }
    public ICommand CloseRoomCommand { get; }
    public ICommand KickCommand { get; }

    public string ServerUrl { get => _serverUrl; set => Set(ref _serverUrl, value); }
    public string Username { get => _username; set => Set(ref _username, value); }
    public string Password { get; set; } = "";

    public string ErrorMessage { get => _errorMessage; set => Set(ref _errorMessage, value); }
    public string ConnectionStatus { get => _connectionStatus; set => Set(ref _connectionStatus, value); }

    public string RoomCode
    {
        get => _roomCode;
        private set
        {
            if (!Set(ref _roomCode, value)) return;
            Raise(nameof(HasRoom));
            ((AsyncRelayCommand)CloseRoomCommand).RaiseCanExecuteChanged();
        }
    }

    public string JoinUrl { get => _joinUrl; private set => Set(ref _joinUrl, value); }

    public bool HasRoom => RoomCode.Length > 0;
    public bool ShowLogin => !_loggedIn;
    public bool ShowRoom => _loggedIn;
    public string PlayerCountText => $"Players ({Players.Count})";

    private void OnUi(Action action) => _ui.Post(_ => action(), null);

    private void ShowError(Exception ex) => OnUi(() => ErrorMessage = ex.Message);

    private void SetLoggedIn(bool value)
    {
        _loggedIn = value;
        Raise(nameof(ShowLogin));
        Raise(nameof(ShowRoom));
    }

    private async Task LoginAsync()
    {
        ErrorMessage = "";
        var api = new ApiClient(ServerUrl);
        try
        {
            var login = await api.LoginAsync(Username, Password);
            api.Token = login.Token;
            _api?.Dispose();
            _api = api;
            _token = login.Token;
            ServerUrl = api.BaseUrl;
        }
        catch
        {
            api.Dispose();
            throw;
        }

        ConnectionStatus = "Logged in";
        SetLoggedIn(true);
    }

    private async Task LogoutAsync()
    {
        await CloseRoomAsync();
        _api?.Dispose();
        _api = null;
        _token = null;
        ConnectionStatus = "Not connected";
        SetLoggedIn(false);
    }

    private async Task CreateRoomAsync()
    {
        ErrorMessage = "";
        if (_api is null || _token is null) return;

        if (HasRoom) await CloseRoomAsync();

        var room = await _api.CreateRoomAsync();
        RoomCode = room.RoomCode;
        JoinUrl = room.JoinUrl;
        Players.Clear();
        Raise(nameof(PlayerCountText));

        await _connection.StartAsync(_api.BaseUrl, _token, room.RoomCode);
    }

    private async Task CloseRoomAsync()
    {
        if (!HasRoom) return;
        var code = RoomCode;
        await _connection.StopAsync();
        ClearRoom();
        try
        {
            if (_api is not null) await _api.CloseRoomAsync(code);
        }
        catch (ApiException)
        {
            // already gone (e.g. closed by the idle cleanup): nothing to do
        }
    }

    private async Task KickAsync(PlayerItem player) => await _connection.KickAsync(player.Id);

    private void ClearRoom()
    {
        Question.Reset();
        RoomCode = "";
        JoinUrl = "";
        Players.Clear();
        Raise(nameof(PlayerCountText));
    }

    private void ApplyState(RoomStateDto state)
    {
        Question.ApplyState(state);
        Players.Clear();
        foreach (var p in state.Players) Players.Add(new PlayerItem(p));
        Raise(nameof(PlayerCountText));
    }

    private void Upsert(PlayerSummary dto)
    {
        var existing = Players.FirstOrDefault(p => p.Id == dto.Id);
        if (existing is null) Players.Add(new PlayerItem(dto));
        else existing.IsConnected = dto.Connected;
        Raise(nameof(PlayerCountText));
    }

    private void Remove(Guid playerId)
    {
        var existing = Players.FirstOrDefault(p => p.Id == playerId);
        if (existing is not null) Players.Remove(existing);
        Raise(nameof(PlayerCountText));
    }

    public Task ShutdownAsync()
    {
        Question.Dispose();
        return _connection.StopAsync();
    }
}
