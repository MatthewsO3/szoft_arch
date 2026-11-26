using System.Windows.Input;
using QuizGame.Contracts;
using QuizGame.Host.Services;

namespace QuizGame.Host.ViewModels;

/// <summary>
/// Host controls for automatic phase progression and valid manual transitions.
/// </summary>
public sealed class PhaseControlViewModel : ViewModelBase
{
    private readonly IRoomControlClient _client;
    private readonly Action<string>? _onError;
    private readonly Dictionary<int, Guid?> _territoryOwners = new();

    private bool _hasRoom;
    private bool _isConnected;
    private GamePhase _phase = GamePhase.Lobby;
    private bool _isRoundOpen;
    private bool _isBusy;
    private int _minPlayers = 2;
    private int _suggestedMaxRounds = 10;
    private int _totalPlayerCount;
    private int _connectedPlayerCount;
    private int _conquestRoundsCompleted;
    private int _totalTerritories;
    private int _ownedTerritories;
    private string _activePickerName = "";
    private bool _hasUnsavedSettings;

    public PhaseControlViewModel(IRoomControlClient client, Action<string>? onError = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _onError = onError;

        StartGameCommand = new AsyncRelayCommand(
            _ => StartGameAsync(),
            _ => CanStartGame,
            ex => _onError?.Invoke(HostErrorFormatter.Format(ex)));

        NextPhaseCommand = new AsyncRelayCommand(
            _ => AdvancePhaseAsync(),
            _ => CanAdvancePhase,
            ex => _onError?.Invoke(HostErrorFormatter.Format(ex)));

        EndGameCommand = new AsyncRelayCommand(
            _ => EndGameAsync(),
            _ => CanEndGame,
            ex => _onError?.Invoke(HostErrorFormatter.Format(ex)));
    }

    public ICommand StartGameCommand { get; }
    public ICommand NextPhaseCommand { get; }
    public ICommand EndGameCommand { get; }

    public bool HasRoom
    {
        get => _hasRoom;
        private set => Set(ref _hasRoom, value);
    }

    public bool IsConnected
    {
        get => _isConnected;
        private set => Set(ref _isConnected, value);
    }

    public GamePhase Phase
    {
        get => _phase;
        private set => Set(ref _phase, value);
    }

    public bool IsRoundOpen
    {
        get => _isRoundOpen;
        private set => Set(ref _isRoundOpen, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set => Set(ref _isBusy, value);
    }

    public int MinPlayers
    {
        get => _minPlayers;
        private set => Set(ref _minPlayers, value);
    }

    public int SuggestedMaxRounds
    {
        get => _suggestedMaxRounds;
        private set => Set(ref _suggestedMaxRounds, value);
    }

    public int TotalPlayerCount
    {
        get => _totalPlayerCount;
        private set => Set(ref _totalPlayerCount, value);
    }

    public int ConnectedPlayerCount
    {
        get => _connectedPlayerCount;
        private set => Set(ref _connectedPlayerCount, value);
    }

    public int ConquestRoundsCompleted
    {
        get => _conquestRoundsCompleted;
        private set => Set(ref _conquestRoundsCompleted, value);
    }

    public int TotalTerritories
    {
        get => _totalTerritories;
        private set => Set(ref _totalTerritories, value);
    }

    public int OwnedTerritories
    {
        get => _ownedTerritories;
        private set => Set(ref _ownedTerritories, value);
    }

    public string ActivePickerName
    {
        get => _activePickerName;
        private set => Set(ref _activePickerName, value);
    }

    public bool HasUnsavedSettings
    {
        get => _hasUnsavedSettings;
        private set => Set(ref _hasUnsavedSettings, value);
    }

    public string CurrentPhaseLabel => Phase switch
    {
        GamePhase.Lobby => "Lobby",
        GamePhase.BaseSelection => "Base selection",
        GamePhase.Conquest => "Conquest",
        GamePhase.Results => "Results",
        _ => Phase.ToString()
    };

    public string PhaseBannerText => HasRoom ? $"Phase: {CurrentPhaseLabel}" : "";

    public bool IsLobby => Phase == GamePhase.Lobby;
    public bool IsBaseSelection => Phase == GamePhase.BaseSelection;
    public bool IsConquest => Phase == GamePhase.Conquest;
    public bool IsExpansion => Phase == GamePhase.Expansion;
    public bool IsDuel => Phase == GamePhase.Duel;
    public bool IsResults => Phase == GamePhase.Results;

    public bool IsLobbyCompleted => Phase > GamePhase.Lobby;
    public bool IsBaseSelectionCompleted => Phase > GamePhase.BaseSelection;
    public bool IsConquestCompleted => Phase > GamePhase.Conquest;
    public bool IsExpansionCompleted => Phase > GamePhase.Expansion;
    public bool IsDuelCompleted => Phase > GamePhase.Duel;

    public bool CanStartGame =>
        HasRoom && IsConnected && !IsBusy && !IsRoundOpen &&
        Phase == GamePhase.Lobby && ConnectedPlayerCount >= MinPlayers;

    private bool CanTransition => HasRoom && IsConnected && !IsBusy && !IsRoundOpen
        && string.IsNullOrEmpty(ActivePickerName);

    public bool CanAdvancePhase => CanTransition && Phase switch
    {
        GamePhase.BaseSelection => TotalPlayerCount > 0 && OwnedTerritories >= TotalPlayerCount,
        GamePhase.Conquest => true,
        GamePhase.Expansion => AllTerritoriesOwned,
        _ => false
    };

    public bool CanEndGame =>
        HasRoom && IsConnected && !IsBusy && !IsRoundOpen && Phase == GamePhase.Duel;

    public bool AllTerritoriesOwned =>
        TotalTerritories > 0 && OwnedTerritories >= TotalTerritories;

    public bool ReachedSuggestedMaxRounds =>
        Phase == GamePhase.Conquest && SuggestedMaxRounds > 0 && ConquestRoundsCompleted >= SuggestedMaxRounds;

    public bool SuggestEndConquest =>
        Phase == GamePhase.Conquest && (AllTerritoriesOwned || ReachedSuggestedMaxRounds);

    public string ConquestSuggestionText
    {
        get
        {
            if (Phase != GamePhase.Conquest)
                return "";
            if (AllTerritoriesOwned)
                return "All territories are owned — advancing automatically to Duel.";
            if (ReachedSuggestedMaxRounds)
                return $"Completed {ConquestRoundsCompleted}/{SuggestedMaxRounds} rounds — advancing automatically to Expansion.";
            return "";
        }
    }

    public string StatusGuidance
    {
        get
        {
            if (!HasRoom)
                return "";
            if (!IsConnected)
                return "Connection lost — waiting to reconnect before changing phases.";
            if (IsRoundOpen)
                return "A question round is open. Close it or wait for answers before changing phases.";
            if (!string.IsNullOrWhiteSpace(ActivePickerName))
                return $"{ActivePickerName} is picking a territory…";

            return Phase switch
            {
                GamePhase.Lobby when ConnectedPlayerCount < MinPlayers =>
                    $"Waiting for players ({ConnectedPlayerCount}/{MinPlayers} connected to start)."
                    + (HasUnsavedSettings ? " Apply settings to save pending changes." : ""),
                GamePhase.Lobby =>
                    $"Ready to start ({ConnectedPlayerCount}/{TotalPlayerCount} connected, minimum {MinPlayers})."
                    + (HasUnsavedSettings ? " Apply or revert pending settings before starting." : ""),
                GamePhase.BaseSelection =>
                    "Players pick their bases in turn. Conquest starts automatically after the last pick.",
                GamePhase.Conquest when TotalTerritories > 0 =>
                    $"Conquest: {OwnedTerritories}/{TotalTerritories} territories owned. Advances automatically after {SuggestedMaxRounds} rounds or when the map is full. Next phase ends Conquest early.",
                GamePhase.Conquest =>
                    $"Conquest advances automatically after {SuggestedMaxRounds} rounds. Next phase ends Conquest early.",
                GamePhase.Expansion =>
                    "Send estimation questions until every territory is owned. Duel starts automatically after the final tile pick.",
                GamePhase.Duel =>
                    "The active player nominates a target, then send a multiple-choice question. Results appear automatically at the round limit or when one base holder remains. End game finishes Duel early.",
                GamePhase.Results =>
                    "Game finished — final standings are shown.",
                _ => ""
            };
        }
    }

    public Task StartGameAsync() =>
        RunTransitionAsync(() => CanStartGame, () => _client.StartGameAsync());

    public Task AdvancePhaseAsync() =>
        RunTransitionAsync(() => CanAdvancePhase, () => _client.AdvancePhaseAsync((GamePhase)((int)Phase + 1)));

    public Task EndGameAsync() =>
        RunTransitionAsync(() => CanEndGame, () => _client.AdvancePhaseAsync(GamePhase.Results));

    public void SetConnectionState(bool isConnected)
    {
        IsConnected = isConnected;
        NotifyAll();
    }

    public void SetRoomOpened(RoomSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        HasRoom = true;
        Phase = GamePhase.Lobby;
        IsRoundOpen = false;
        IsBusy = false;
        MinPlayers = settings.MinPlayers;
        SuggestedMaxRounds = settings.ConquestRounds;
        ConquestRoundsCompleted = 0;
        ActivePickerName = "";
        HasUnsavedSettings = false;
        ApplyMapSnapshot(null);
        NotifyAll();
    }

    public void ApplyRoomState(RoomStateDto state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var enteringConquest = state.Phase == GamePhase.Conquest && Phase != GamePhase.Conquest;

        HasRoom = true;
        Phase = state.Phase;
        IsRoundOpen = state.CurrentRound is not null;
        MinPlayers = state.Settings.MinPlayers;
        SuggestedMaxRounds = state.Settings.ConquestRounds;
        TotalPlayerCount = state.Players.Count;
        ConnectedPlayerCount = state.Players.Count(p => p.Connected);
        ActivePickerName = state.CurrentPick is { } pick
            ? state.Players.FirstOrDefault(p => p.Id == pick.PickerPlayerId)?.Name ?? "A player"
            : "";

        if (state.Phase != GamePhase.Conquest || enteringConquest)
            ConquestRoundsCompleted = 0;

        ApplyMapSnapshot(state.Map);
        NotifyAll();
    }

    public void ApplyPhaseChanged(GamePhase newPhase)
    {
        var enteringConquest = newPhase == GamePhase.Conquest && Phase != GamePhase.Conquest;
        Phase = newPhase;
        if (enteringConquest)
            ConquestRoundsCompleted = 0;
        ActivePickerName = "";
        NotifyAll();
    }

    public void ApplySettings(RoomSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        MinPlayers = settings.MinPlayers;
        SuggestedMaxRounds = settings.ConquestRounds;
        NotifyAll();
    }

    public void SetHasUnsavedSettings(bool hasUnsaved)
    {
        HasUnsavedSettings = hasUnsaved;
        NotifyAll();
    }

    public void SetPlayerCounts(int total, int connected)
    {
        TotalPlayerCount = Math.Max(0, total);
        ConnectedPlayerCount = Math.Max(0, connected);
        NotifyAll();
    }

    public void SetRoundOpen(bool isOpen)
    {
        IsRoundOpen = isOpen;
        if (isOpen)
            ActivePickerName = "";
        NotifyAll();
    }

    public void RecordRoundClosed(RoundClosedDto round)
    {
        ArgumentNullException.ThrowIfNull(round);
        IsRoundOpen = false;
        if (Phase == GamePhase.Conquest)
            ConquestRoundsCompleted++;
        NotifyAll();
    }

    public void ApplyMapUpdated(MapUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);
        foreach (var t in update.ChangedTerritories)
            _territoryOwners[t.Index] = t.OwnerPlayerId;
        ActivePickerName = "";
        RecomputeTerritoryCounts();
        NotifyAll();
    }

    public void ApplyPickWaiting(PlayerSummary picker)
    {
        ArgumentNullException.ThrowIfNull(picker);
        ActivePickerName = picker.Name;
        NotifyAll();
    }

    public void ClearPicker()
    {
        ActivePickerName = "";
        NotifyAll();
    }

    public void ApplyGameFinished(GameFinishedDto finished)
    {
        ArgumentNullException.ThrowIfNull(finished);
        ApplyMapSnapshot(finished.FinalMap);
        Phase = GamePhase.Results;
        IsRoundOpen = false;
        ActivePickerName = "";
        NotifyAll();
    }

    public void ClearRoom()
    {
        HasRoom = false;
        IsConnected = false;
        Phase = GamePhase.Lobby;
        IsRoundOpen = false;
        IsBusy = false;
        MinPlayers = 2;
        SuggestedMaxRounds = 10;
        TotalPlayerCount = 0;
        ConnectedPlayerCount = 0;
        ConquestRoundsCompleted = 0;
        ActivePickerName = "";
        HasUnsavedSettings = false;
        ApplyMapSnapshot(null);
        NotifyAll();
    }

    private async Task RunTransitionAsync(Func<bool> canRun, Func<Task> action)
    {
        if (!canRun()) return;
        IsBusy = true;
        NotifyAll();
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            _onError?.Invoke(HostErrorFormatter.Format(ex));
        }
        finally
        {
            IsBusy = false;
            NotifyAll();
        }
    }

    private void ApplyMapSnapshot(MapDto? map)
    {
        _territoryOwners.Clear();
        if (map is not null)
        {
            foreach (var t in map.Territories)
                _territoryOwners[t.Index] = t.OwnerPlayerId;
        }

        RecomputeTerritoryCounts();
    }

    private void RecomputeTerritoryCounts()
    {
        TotalTerritories = _territoryOwners.Count;
        OwnedTerritories = _territoryOwners.Values.Count(owner => owner.HasValue);
    }

    private void NotifyAll()
    {
        Raise(nameof(CurrentPhaseLabel));
        Raise(nameof(PhaseBannerText));
        Raise(nameof(IsLobby));
        Raise(nameof(IsBaseSelection));
        Raise(nameof(IsConquest));
        Raise(nameof(IsExpansion));
        Raise(nameof(IsDuel));
        Raise(nameof(IsResults));
        Raise(nameof(IsLobbyCompleted));
        Raise(nameof(IsBaseSelectionCompleted));
        Raise(nameof(IsConquestCompleted));
        Raise(nameof(IsExpansionCompleted));
        Raise(nameof(IsDuelCompleted));
        Raise(nameof(CanStartGame));
        Raise(nameof(CanAdvancePhase));
        Raise(nameof(CanEndGame));
        Raise(nameof(AllTerritoriesOwned));
        Raise(nameof(ReachedSuggestedMaxRounds));
        Raise(nameof(SuggestEndConquest));
        Raise(nameof(ConquestSuggestionText));
        Raise(nameof(StatusGuidance));

        ((AsyncRelayCommand)StartGameCommand).RaiseCanExecuteChanged();
        ((AsyncRelayCommand)NextPhaseCommand).RaiseCanExecuteChanged();
        ((AsyncRelayCommand)EndGameCommand).RaiseCanExecuteChanged();
    }
}
