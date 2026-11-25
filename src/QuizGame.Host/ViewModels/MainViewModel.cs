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
    private readonly Action<string> _copyToClipboard;
    private ApiClient? _api;
    private string? _token;

    private string _serverUrl = "http://localhost:5084";
    private string _username = "admin";
    private string _errorMessage = "";
    private string _connectionStatus = "Not connected";
    private string _roomCode = "";
    private string _joinUrl = "";
    private string _copyRoomCodeText = "Copy";
    private bool _loggedIn;
    private int _selectedTabIndex;
    private int _copyFeedbackVersion;

    public MainViewModel() : this(new WpfQuestionBankDialogService(), text => System.Windows.Clipboard.SetText(text))
    {
    }

    public MainViewModel(IQuestionBankDialogService questionBankDialogs, Action<string>? copyToClipboard = null)
    {
        _copyToClipboard = copyToClipboard ?? (_ => { });
        QuestionBank = new QuestionBankViewModel(questionBankDialogs);

        LoginCommand = new AsyncRelayCommand(_ => LoginAsync(), onError: ShowError);
        LogoutCommand = new AsyncRelayCommand(_ => LogoutAsync(), onError: ShowError);
        CreateRoomCommand = new AsyncRelayCommand(_ => CreateRoomAsync(), onError: ShowError);
        CloseRoomCommand = new AsyncRelayCommand(_ => CloseRoomAsync(), _ => HasRoom, ShowError);
        KickCommand = new AsyncRelayCommand(p => KickAsync((PlayerItem)p!), p => p is PlayerItem, ShowError);
        CopyRoomCodeCommand = new RelayCommand(_ => CopyRoomCode(), _ => HasRoom);

        _connection.RoomStateReceived += s => OnUi(() => ApplyState(s));
        _connection.PlayerJoined += p => OnUi(() => Upsert(p));
        _connection.PlayerConnectionChanged += p => OnUi(() => Upsert(p));
        _connection.PlayerLeft += p => OnUi(() => Remove(p.Id));
        _connection.RoomClosed += () => OnUi(ClearRoom);
        _connection.ErrorReceived += e => OnUi(() => ErrorMessage = e.Message);
        _connection.StatusChanged += s => OnUi(() => ConnectionStatus = s);
    }

    public ObservableCollection<PlayerItem> Players { get; } = [];
    public QuestionBankViewModel QuestionBank { get; }

    public ICommand LoginCommand { get; }
    public ICommand LogoutCommand { get; }
    public ICommand CreateRoomCommand { get; }
    public ICommand CloseRoomCommand { get; }
    public ICommand KickCommand { get; }
    public ICommand CopyRoomCodeCommand { get; }

    public string ServerUrl { get => _serverUrl; set => Set(ref _serverUrl, value); }
    public string Username { get => _username; set => Set(ref _username, value); }
    public string Password { get; set; } = "";

    public string ErrorMessage { get => _errorMessage; set => Set(ref _errorMessage, value); }
    public string ConnectionStatus { get => _connectionStatus; set => Set(ref _connectionStatus, value); }
    public string CopyRoomCodeText { get => _copyRoomCodeText; private set => Set(ref _copyRoomCodeText, value); }

    public string RoomCode
    {
        get => _roomCode;
        private set
        {
            if (!Set(ref _roomCode, value)) return;
            _copyFeedbackVersion++;
            CopyRoomCodeText = "Copy";
            Raise(nameof(HasRoom));
            ((AsyncRelayCommand)CloseRoomCommand).RaiseCanExecuteChanged();
            ((RelayCommand)CopyRoomCodeCommand).RaiseCanExecuteChanged();
        }
    }

    public string JoinUrl { get => _joinUrl; private set => Set(ref _joinUrl, value); }

    public bool HasRoom => RoomCode.Length > 0;
    public bool ShowLogin => !_loggedIn;
    public bool ShowRoom => _loggedIn;
    public string PlayerCountText => $"Players ({Players.Count})";

    public int SelectedTabIndex
    {
        get => _selectedTabIndex;
        set
        {
            if (!Set(ref _selectedTabIndex, value)) return;
            if (value == 1 && _loggedIn)
                _ = QuestionBank.EnsureLoadedAsync();
        }
    }

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
            QuestionBank.AttachApi(api);
        }
        catch
        {
            api.Dispose();
            throw;
        }

        SelectedTabIndex = 0;
        ConnectionStatus = "Logged in";
        SetLoggedIn(true);
    }

    private async Task LogoutAsync()
    {
        await CloseRoomAsync();
        QuestionBank.Reset();
        SelectedTabIndex = 0;
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

    private void CopyRoomCode()
    {
        if (!HasRoom) return;
        ErrorMessage = "";
        try
        {
            _copyToClipboard(RoomCode);
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            return;
        }

        CopyRoomCodeText = "Copied!";
        _ = ResetCopyFeedbackAsync(++_copyFeedbackVersion);
    }

    private async Task ResetCopyFeedbackAsync(int version)
    {
        await Task.Delay(1500);
        if (version == _copyFeedbackVersion)
            CopyRoomCodeText = "Copy";
    }

    private void ClearRoom()
    {
        RoomCode = "";
        JoinUrl = "";
        Players.Clear();
        Raise(nameof(PlayerCountText));
    }

    private void ApplyState(RoomStateDto state)
    {
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

    public Task ShutdownAsync() => _connection.StopAsync();
}
