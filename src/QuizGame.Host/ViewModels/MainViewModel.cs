using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Threading;
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

    private int? _baseRank = dto.BaseRank;
    public int? BaseRank
    {
        get => _baseRank;
        set
        {
            if (Set(ref _baseRank, value)) Raise(nameof(BaseRankText));
        }
    }

    public string StatusText => IsConnected ? "online" : "offline";
    public string BaseRankText => BaseRank is int rank ? $"#{rank}" : "";
}

/// <summary>Login + room lobby of the game master (this slice: create room, see players join, kick).</summary>
public sealed class MainViewModel : ViewModelBase
{
    private readonly SynchronizationContext _ui = SynchronizationContext.Current ?? new SynchronizationContext();
    private readonly GameConnection _connection;
    private readonly IApiClientFactory _apiClientFactory;
    private readonly IClipboardService _clipboard;
    private readonly DispatcherTimer _pickCountdownTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
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
    private bool _roundOpen;
    private bool _questionSent;
    private bool _canSendQuestion;
    private bool _waitOverlayDismissed;
    private DuelNominationDto? _duelNomination;
    private string _answerProgress = "";
    private int _copyFeedbackVersion;

    public MainViewModel(
        GameConnection connection,
        IApiClientFactory apiClientFactory,
        IClipboardService clipboard,
        QuestionViewModel question,
        MapViewModel map,
        QuestionBankViewModel questionBank)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _apiClientFactory = apiClientFactory ?? throw new ArgumentNullException(nameof(apiClientFactory));
        _clipboard = clipboard ?? throw new ArgumentNullException(nameof(clipboard));
        Question = question ?? throw new ArgumentNullException(nameof(question));
        Map = map ?? throw new ArgumentNullException(nameof(map));
        QuestionBank = questionBank ?? throw new ArgumentNullException(nameof(questionBank));

        PhaseControl = new PhaseControlViewModel(_connection, message => ErrorMessage = message);
        _pickCountdownTimer.Tick += (_, _) => PhaseControl.RefreshPickCountdown();
        PhaseControl.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(PhaseControl.Phase) or nameof(PhaseControl.HasRoom))
                QuestionBank.SetGamePhase(PhaseControl.HasRoom ? PhaseControl.Phase : null);
            if (e.PropertyName == nameof(PhaseControl.HasActivePickCountdown))
                SyncPickTimer();
            RaiseQuestionSelectionPrompt();
        };
        RoomSettings = new RoomSettingsViewModel(_connection, settings =>
        {
            PhaseControl.ApplySettings(settings);
            Map.ApplySettings(settings);
        });
        RoomSettings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(RoomSettings.HasUnsavedChanges))
                PhaseControl.SetHasUnsavedSettings(RoomSettings.HasUnsavedChanges);
        };

        LoginCommand = new AsyncRelayCommand(_ => LoginAsync(), onError: ShowError);
        LogoutCommand = new AsyncRelayCommand(_ => LogoutAsync(), onError: ShowError);
        CreateRoomCommand = new AsyncRelayCommand(_ => CreateRoomAsync(), onError: ShowError);
        CloseRoomCommand = new AsyncRelayCommand(_ => CloseRoomAsync(), _ => HasRoom, ShowError);
        KickCommand = new AsyncRelayCommand(p => KickAsync((PlayerItem)p!), p => p is PlayerItem, ShowError);
        SendQuestionCommand = new AsyncRelayCommand(_ => SendQuestionAsync(),
            _ => HasRoom && _connection.IsConnected && _canSendQuestion && QuestionBank.CanSendSelectedQuestion
                && (PhaseControl.Phase != GamePhase.Duel || _duelNomination is not null), ShowError);
        CloseRoundCommand = new AsyncRelayCommand(_ => _connection.CloseRoundAsync(),
            _ => HasRoom && _connection.IsConnected && _roundOpen, ShowError);
        DismissWaitForAnswersOverlayCommand = new RelayCommand(_ =>
        {
            _waitOverlayDismissed = true;
            Raise(nameof(ShowWaitForAnswersOverlay));
        });
        QuestionBank.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(QuestionBank.CanSendSelectedQuestion) or nameof(QuestionBank.SelectedQuestion))
                RefreshRoundCommands();
        };
        CopyRoomCodeCommand = new RelayCommand(_ => CopyRoomCode(), _ => HasRoom);

        _connection.RoomStateReceived += s => OnUi(() => ApplyState(s));
        _connection.PlayerJoined += p => OnUi(() => Upsert(p));
        _connection.PlayerConnectionChanged += p => OnUi(() => Upsert(p));
        _connection.PlayerLeft += p => OnUi(() => Remove(p.Id));
        _connection.RoomClosed += () => OnUi(ClearRoom);
        _connection.ErrorReceived += e => OnUi(() => ErrorMessage = HostErrorFormatter.FormatMessage(e.Message));
        _connection.StatusChanged += s => OnUi(() =>
        {
            ConnectionStatus = s;
            Question.ConnectionStatus = s;
            Map.ConnectionStatus = s;
            RoomSettings.SetConnectionState(_connection.IsConnected);
            PhaseControl.SetConnectionState(_connection.IsConnected);
            RefreshRoundCommands();
        });
        _connection.QuestionStarted += q => OnUi(() =>
        {
            var duelHeader = PhaseControl.Phase == GamePhase.Duel && _duelNomination is { } nom
                ? Map.FormatDuelHeader(nom)
                : null;
            if (duelHeader is null) Map.ClearDuelTarget();
            _duelNomination = null;
            _questionSent = true;
            _waitOverlayDismissed = false;
            Map.ClearPicker();
            Question.Start(q, duelHeader);
            _roundOpen = true;
            AnswerProgress = "";
            PhaseControl.SetRoundOpen(true);
            RefreshRoundCommands();
        });
        _connection.QuestionSelectionAvailabilityChanged += canSend => OnUi(() =>
        {
            _canSendQuestion = canSend;
            if (canSend) _questionSent = false;
            RefreshRoundCommands();
        });
        _connection.RoundClosed += r => OnUi(() =>
        {
            Map.ClearDuelTarget();
            Question.Close(r);
            _roundOpen = false;
            _waitOverlayDismissed = false;
            PhaseControl.RecordRoundClosed(r);
            RefreshRoundCommands();
        });
        _connection.AnswerCountChanged += c => OnUi(() => AnswerProgress = $"Answered {c.Answered}/{c.Total}");
        _connection.ClockSynchronized += (utc, latency) => OnUi(() =>
        {
            Question.SyncClock(utc, latency);
            Map.SyncClock(utc, latency);
            PhaseControl.SyncClock(utc, latency);
        });
        _connection.PhaseChanged += phase => OnUi(() =>
        {
            _duelNomination = null;
            RoomSettings.SetPhase(phase);
            Map.ApplyPhaseChanged(phase);
            PhaseControl.ApplyPhaseChanged(phase);
            if (phase == GamePhase.BaseSelection)
                SelectedTabIndex = 1;
            RefreshRoundCommands();
        });
        _connection.DuelNominated += nomination => OnUi(() =>
        {
            _duelNomination = nomination;
            Map.ApplyDuelNomination(nomination);
            PhaseControl.ClearPicker();
            RefreshRoundCommands();
        });
        _connection.MapUpdated += update => OnUi(() => { Map.ApplyMapUpdated(update); PhaseControl.ApplyMapUpdated(update); });
        _connection.ScoreboardUpdated += scores => OnUi(() => Map.ApplyScoreboardUpdated(scores));
        _connection.PickWaiting += waiting => OnUi(() => { Map.ApplyPickWaiting(waiting); PhaseControl.ApplyPickWaiting(waiting); });
        _connection.GameFinished += finished => OnUi(() =>
        {
            _roundOpen = false;
            _questionSent = false;
            _waitOverlayDismissed = false;
            RefreshRoundCommands();
            RoomSettings.SetPhase(GamePhase.Results);
            Map.ApplyGameFinished(finished);
            PhaseControl.ApplyGameFinished(finished);
        });
    }

    public QuestionViewModel Question { get; }
    public MapViewModel Map { get; }

    public ObservableCollection<PlayerItem> Players { get; } = [];
    public QuestionBankViewModel QuestionBank { get; }
    public RoomSettingsViewModel RoomSettings { get; }
    public PhaseControlViewModel PhaseControl { get; }

    public ICommand LoginCommand { get; }
    public ICommand LogoutCommand { get; }
    public ICommand CreateRoomCommand { get; }
    public ICommand CloseRoomCommand { get; }
    public ICommand KickCommand { get; }
    public ICommand SendQuestionCommand { get; }
    public ICommand CloseRoundCommand { get; }
    public ICommand DismissWaitForAnswersOverlayCommand { get; }
    public string AnswerProgress
    {
        get => _answerProgress;
        private set
        {
            if (!Set(ref _answerProgress, value)) return;
            Raise(nameof(WaitOverlayText));
        }
    }

    public bool HasDuelNomination => PhaseControl.Phase == GamePhase.Duel && _duelNomination is not null && !_roundOpen;
    public string DuelNominationText => _duelNomination is { } nomination
        ? $"Duel nominated: {Map.DescribeDuel(nomination)} — select a multiple-choice question to confirm."
        : "";
    public string SendQuestionButtonText => PhaseControl.Phase == GamePhase.Duel && _duelNomination is not null
        ? "Confirm duel & send question"
        : "Send selected question";

    public bool ShowWaitForAnswersOverlay => (_roundOpen || _questionSent) && !_waitOverlayDismissed;
    public string WaitOverlayText => _roundOpen
        ? string.IsNullOrWhiteSpace(AnswerProgress)
            ? "Wait for answers…"
            : $"Wait for answers… {AnswerProgress[9..]}"
        : PhaseControl.Phase == GamePhase.Expansion
            ? "Wait for the winning player to select a territory…"
            : PhaseControl.Phase == GamePhase.Duel
                ? "Wait for the active player to nominate a duel target…"
                : "Wait for the players to finish their selection…";

    private void RefreshRoundCommands()
    {
        ((AsyncRelayCommand)SendQuestionCommand).RaiseCanExecuteChanged();
        ((AsyncRelayCommand)CloseRoundCommand).RaiseCanExecuteChanged();
        RaiseQuestionSelectionPrompt();
        Raise(nameof(HasDuelNomination));
        Raise(nameof(DuelNominationText));
        Raise(nameof(SendQuestionButtonText));
        Raise(nameof(ShowWaitForAnswersOverlay));
        Raise(nameof(WaitOverlayText));
    }

    public bool ShowQuestionSelectionBanner =>
        HasRoom && _connection.IsConnected && _canSendQuestion
        && (PhaseControl.Phase is GamePhase.Conquest or GamePhase.Expansion
            || PhaseControl.Phase == GamePhase.Duel && _duelNomination is not null)
        && !_questionSent;

    private void RaiseQuestionSelectionPrompt()
    {
        Raise(nameof(ShowQuestionSelectionBanner));
    }

    private async Task SendQuestionAsync()
    {
        var selected = QuestionBank.SelectedQuestion ?? throw new InvalidOperationException("Select a question first.");
        ErrorMessage = "";
        if (!QuestionBank.CanSendSelectedQuestion)
        {
            ErrorMessage = QuestionBank.QuestionTypeGuidance;
            return;
        }

        if (PhaseControl.Phase == GamePhase.Duel)
            await _connection.ConfirmDuelAsync(selected.Id);
        else
            await _connection.SendQuestionAsync(new SendQuestionRequest { QuestionId = selected.Id });

        _questionSent = true;
        _canSendQuestion = false;
        QuestionBank.SelectedQuestion = null;
        RefreshRoundCommands();
    }
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
            Raise(nameof(HasNoRoom));
            ((AsyncRelayCommand)CloseRoomCommand).RaiseCanExecuteChanged();
            RefreshRoundCommands();
            ((RelayCommand)CopyRoomCodeCommand).RaiseCanExecuteChanged();
        }
    }

    public string JoinUrl { get => _joinUrl; private set => Set(ref _joinUrl, value); }

    public bool HasRoom => RoomCode.Length > 0;
    public bool HasNoRoom => !HasRoom;
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

    private void ShowError(Exception ex) => OnUi(() => ErrorMessage = HostErrorFormatter.Format(ex));

    private void SetLoggedIn(bool value)
    {
        _loggedIn = value;
        Raise(nameof(ShowLogin));
        Raise(nameof(ShowRoom));
    }

    private async Task LoginAsync()
    {
        ErrorMessage = "";
        var api = _apiClientFactory.Create(ServerUrl);
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
        RoomSettings.ResetToDefaults();
        PhaseControl.ClearRoom();
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
        if (!RoomSettings.TryGetForRoomCreation(out var settings)) return;

        if (HasRoom) await CloseRoomAsync();

        var room = await _api.CreateRoomAsync(settings);
        RoomCode = room.RoomCode;
        JoinUrl = room.JoinUrl;
        Players.Clear();
        Raise(nameof(PlayerCountText));
        RoomSettings.SetRoomCreated(settings);
        Map.ApplySettings(settings);
        PhaseControl.SetRoomOpened(settings);
        PhaseControl.SetPlayerCounts(0, 0);

        await _connection.StartAsync(_api.BaseUrl, _token, room.RoomCode);
        RoomSettings.SetConnectionState(_connection.IsConnected);
        PhaseControl.SetConnectionState(_connection.IsConnected);
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
            _clipboard.SetText(RoomCode);
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
        Question.Reset();
        _roundOpen = false;
        _questionSent = false;
        _canSendQuestion = false;
        _waitOverlayDismissed = false;
        _duelNomination = null;
        AnswerProgress = "";
        RoomCode = "";
        JoinUrl = "";
        Players.Clear();
        Raise(nameof(PlayerCountText));
        RoomSettings.ClearRoom();
        PhaseControl.ClearRoom();
        Map.Clear();
    }

    private void ApplyState(RoomStateDto state)
    {
        Question.ApplyState(state);
        _roundOpen = state.CurrentRound is not null;
        _canSendQuestion = state.CanSendQuestion;
        _questionSent = _roundOpen || state.CurrentPick?.Purpose == PickPurpose.Expansion;
        _waitOverlayDismissed = false;
        _duelNomination = state.CurrentDuelNomination;
        AnswerProgress = state.AnswerCount is { } count ? $"Answered {count.Answered}/{count.Total}" : "";
        Players.Clear();
        foreach (var p in state.Players) Players.Add(new PlayerItem(p));
        Raise(nameof(PlayerCountText));
        RoomSettings.SyncFromRoomState(state);
        Map.ApplyRoomState(state);
        PhaseControl.ApplyRoomState(state);
        RoomSettings.SetConnectionState(_connection.IsConnected);
        PhaseControl.SetConnectionState(_connection.IsConnected);
        SyncPlayerCounts();
        RefreshRoundCommands();
    }

    private void Upsert(PlayerSummary dto)
    {
        Map.UpsertPlayer(dto);
        var existing = Players.FirstOrDefault(p => p.Id == dto.Id);
        if (existing is null) Players.Add(new PlayerItem(dto));
        else
        {
            existing.IsConnected = dto.Connected;
            existing.BaseRank = dto.BaseRank;
        }
        Raise(nameof(PlayerCountText));
        SyncPlayerCounts();
    }

    private void Remove(Guid playerId)
    {
        Map.RemovePlayer(playerId);
        var existing = Players.FirstOrDefault(p => p.Id == playerId);
        if (existing is not null) Players.Remove(existing);
        Raise(nameof(PlayerCountText));
        SyncPlayerCounts();
    }

    private void SyncPlayerCounts() =>
        PhaseControl.SetPlayerCounts(Players.Count, Players.Count(p => p.IsConnected));

    private void SyncPickTimer()
    {
        if (PhaseControl.HasActivePickCountdown) _pickCountdownTimer.Start();
        else _pickCountdownTimer.Stop();
    }

    public Task ShutdownAsync()
    {
        _pickCountdownTimer.Stop();
        Question.Dispose();
        Map.Dispose();
        _api?.Dispose();
        _api = null;
        return _connection.StopAsync();
    }
}
