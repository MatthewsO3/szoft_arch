using System.Globalization;
using System.Windows.Input;
using QuizGame.Contracts;
using QuizGame.Host.Services;

namespace QuizGame.Host.ViewModels;

/// <summary>
/// View model for viewing, validating, and editing <see cref="RoomSettings"/> (plan §9.2, §16).
/// Settings can be configured before creating a room or updated in the Lobby, and lock once the game starts.
/// </summary>
public sealed class RoomSettingsViewModel : ViewModelBase
{
    private static readonly RoomSettings DefaultSettings = new();

    private readonly IRoomControlClient _client;
    private readonly Action<RoomSettings>? _onSettingsApplied;

    private FilterChoice<MapPreset> _selectedMapPreset;
    private string _territoryValueText = "";
    private string _baseValueText = "";
    private string _baseHealthText = "";
    private string _conquestRoundsText = "";
    private string _duelRoundsText = "";
    private string _defenderBonusText = "";
    private string _drawBonusText = "";
    private string _minPlayersText = "";
    private string _pickTimeoutSecondsText = "";
    private string _defaultQuestionSecondsText = "";
    private bool _disallowAdjacentBases;

    private RoomSettings _appliedSettings = DefaultSettings;
    private bool _hasRoom;
    private GamePhase _phase = GamePhase.Lobby;
    private bool _isConnected;
    private bool _isSaving;
    private string _validationError = "";
    private string _statusMessage = "";

    public RoomSettingsViewModel(IRoomControlClient client, Action<RoomSettings>? onSettingsApplied = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _onSettingsApplied = onSettingsApplied;

        MapPresets =
        [
            new FilterChoice<MapPreset>("Small (5×5)", MapPreset.Small),
            new FilterChoice<MapPreset>("Medium (6×8)", MapPreset.Medium),
            new FilterChoice<MapPreset>("Large (8×10)", MapPreset.Large)
        ];

        _selectedMapPreset = FindPreset(DefaultSettings.MapPreset);
        LoadFields(DefaultSettings);

        ApplySettingsCommand = new AsyncRelayCommand(
            _ => ApplySettingsAsync(),
            _ => HasRoom && Phase == GamePhase.Lobby && IsConnected && !IsSaving && HasUnsavedChanges,
            ex => ValidationError = HostErrorFormatter.Format(ex));

        RevertCommand = new RelayCommand(
            _ => Revert(),
            _ => HasRoom && CanEdit && HasUnsavedChanges);

        ResetDefaultsCommand = new RelayCommand(
            _ => LoadDefaultsIntoForm(),
            _ => CanEdit && !MatchesDefaults());
    }

    public IReadOnlyList<FilterChoice<MapPreset>> MapPresets { get; }

    public ICommand ApplySettingsCommand { get; }
    public ICommand RevertCommand { get; }
    public ICommand ResetDefaultsCommand { get; }

    public FilterChoice<MapPreset> SelectedMapPreset
    {
        get => _selectedMapPreset;
        set
        {
            if (value is null)
            {
                var fallback = FindPreset(MapPreset.Medium);
                var changed = !EqualityComparer<FilterChoice<MapPreset>>.Default.Equals(_selectedMapPreset, fallback);
                _selectedMapPreset = fallback;
                Raise(nameof(SelectedMapPreset));
                if (changed) OnUserEdited();
                return;
            }

            if (!Set(ref _selectedMapPreset, value)) return;
            OnUserEdited();
        }
    }

    public string TerritoryValueText
    {
        get => _territoryValueText;
        set => SetFormField(ref _territoryValueText, value);
    }

    public string BaseValueText
    {
        get => _baseValueText;
        set => SetFormField(ref _baseValueText, value);
    }

    public string WinnersPerRoundText
    {
        get => _conquestRoundsText;
        set => SetFormField(ref _conquestRoundsText, value);
    }

    public string ConquestRoundsText
    {
        get => _conquestRoundsText;
        set => SetFormField(ref _conquestRoundsText, value);
    }

    public string DuelRoundsText
    {
        get => _duelRoundsText;
        set => SetFormField(ref _duelRoundsText, value);
    }

    public string BaseHealthText
    {
        get => _baseHealthText;
        set => SetFormField(ref _baseHealthText, value);
    }

    public string DefenderBonusText
    {
        get => _defenderBonusText;
        set => SetFormField(ref _defenderBonusText, value);
    }

    public string DrawBonusText
    {
        get => _drawBonusText;
        set => SetFormField(ref _drawBonusText, value);
    }

    public string MinPlayersText
    {
        get => _minPlayersText;
        set => SetFormField(ref _minPlayersText, value);
    }

    public string PickTimeoutSecondsText
    {
        get => _pickTimeoutSecondsText;
        set => SetFormField(ref _pickTimeoutSecondsText, value);
    }

    public string DefaultQuestionSecondsText
    {
        get => _defaultQuestionSecondsText;
        set => SetFormField(ref _defaultQuestionSecondsText, value);
    }

    public string SuggestedMaxRoundsText
    {
        get => _duelRoundsText;
        set => SetFormField(ref _duelRoundsText, value);
    }

    public bool DisallowAdjacentBases
    {
        get => _disallowAdjacentBases;
        set => SetFormField(ref _disallowAdjacentBases, value);
    }

    public RoomSettings AppliedSettings
    {
        get => _appliedSettings;
        private set => Set(ref _appliedSettings, value);
    }

    public bool HasRoom
    {
        get => _hasRoom;
        private set => Set(ref _hasRoom, value);
    }

    public GamePhase Phase
    {
        get => _phase;
        private set => Set(ref _phase, value);
    }

    public bool IsConnected
    {
        get => _isConnected;
        private set => Set(ref _isConnected, value);
    }

    public bool IsSaving
    {
        get => _isSaving;
        private set => Set(ref _isSaving, value);
    }

    public bool CanEdit => !IsSaving && (!HasRoom || Phase == GamePhase.Lobby);
    public bool IsLocked => HasRoom && Phase != GamePhase.Lobby;
    public bool ShowApplyControls => HasRoom;

    public bool HasUnsavedChanges => !TryBuildSettings(out var current, out _) || !Equals(current, AppliedSettings);

    public string ValidationError
    {
        get => _validationError;
        private set => Set(ref _validationError, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => Set(ref _statusMessage, value);
    }

    public string ModeHint => !HasRoom
        ? "These rules will be applied when you create a room."
        : Phase != GamePhase.Lobby
            ? "Settings are locked after the game starts."
            : HasUnsavedChanges
                ? "Unsaved changes — click Apply settings to update the lobby."
                : "Editable in Lobby. Changes take effect after clicking Apply settings.";

    public bool TryBuildSettings(out RoomSettings settings, out string error)
    {
        settings = DefaultSettings;

        if (!TryParsePositiveInt(TerritoryValueText, out var territoryValue))
        {
            error = "Territory value must be a positive whole number.";
            return false;
        }
        if (!TryParsePositiveInt(BaseValueText, out var baseValue))
        {
            error = "Base value must be a positive whole number.";
            return false;
        }
        if (!TryParsePositiveInt(BaseHealthText, out var baseHealth))
        {
            error = "Base health must be at least 1.";
            return false;
        }
        if (!TryParsePositiveInt(ConquestRoundsText, out var conquestRounds))
        {
            error = "Conquest rounds must be at least 1.";
            return false;
        }
        if (!TryParsePositiveInt(DuelRoundsText, out var duelRounds))
        {
            error = "Duel rounds must be at least 1.";
            return false;
        }
        if (!TryParseNonNegativeInt(DefenderBonusText, out var defenderBonus))
        {
            error = "Defender bonus must be zero or a positive whole number.";
            return false;
        }
        if (!TryParseNonNegativeInt(DrawBonusText, out var drawBonus))
        {
            error = "Draw bonus must be zero or a positive whole number.";
            return false;
        }
        if (!TryParsePositiveInt(MinPlayersText, out var minPlayers))
        {
            error = "Minimum players must be at least 1.";
            return false;
        }
        if (!TryParsePositiveInt(PickTimeoutSecondsText, out var pickTimeoutSeconds))
        {
            error = "Pick timeout must be at least 1 second.";
            return false;
        }
        if (!TryParsePositiveInt(DefaultQuestionSecondsText, out var defaultQuestionSeconds))
        {
            error = "Default question time must be at least 1 second.";
            return false;
        }

        settings = new RoomSettings
        {
            MapPreset = SelectedMapPreset.Value,
            TerritoryValue = territoryValue,
            BaseValue = baseValue,
            BaseHealth = baseHealth,
            ConquestRounds = conquestRounds,
            DuelRounds = duelRounds,
            DefenderBonus = defenderBonus,
            DrawBonus = drawBonus,
            MinPlayers = minPlayers,
            PickTimeoutSeconds = pickTimeoutSeconds,
            DefaultQuestionSeconds = defaultQuestionSeconds,
            DisallowAdjacentBases = DisallowAdjacentBases
        };
        error = "";
        return true;
    }

    public bool TryGetForRoomCreation(out RoomSettings settings)
    {
        if (!TryBuildSettings(out settings, out var error))
        {
            ValidationError = error;
            StatusMessage = "";
            return false;
        }

        ValidationError = "";
        return true;
    }

    public async Task ApplySettingsAsync()
    {
        if (!HasRoom || Phase != GamePhase.Lobby || !IsConnected || IsSaving)
            return;

        if (!TryBuildSettings(out var settings, out var error))
        {
            ValidationError = error;
            StatusMessage = "";
            return;
        }

        ValidationError = "";
        StatusMessage = "";
        IsSaving = true;
        NotifyStateAndCommands();
        try
        {
            await _client.UpdateSettingsAsync(settings);
            AppliedSettings = settings;
            LoadFields(settings);
            StatusMessage = "Settings applied.";
            _onSettingsApplied?.Invoke(settings);
        }
        catch (Exception ex)
        {
            ValidationError = HostErrorFormatter.Format(ex);
        }
        finally
        {
            IsSaving = false;
            NotifyStateAndCommands();
        }
    }

    public void Revert()
    {
        if (!HasRoom || !CanEdit)
            return;

        ValidationError = "";
        StatusMessage = "";
        LoadFields(AppliedSettings);
        NotifyStateAndCommands();
    }

    public void LoadDefaultsIntoForm()
    {
        if (!CanEdit)
            return;

        ValidationError = "";
        StatusMessage = "";
        LoadFields(DefaultSettings);
        NotifyStateAndCommands();
    }

    public void SetConnectionState(bool isConnected)
    {
        IsConnected = isConnected;
        NotifyStateAndCommands();
    }

    public void SetRoomCreated(RoomSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        HasRoom = true;
        Phase = GamePhase.Lobby;
        AppliedSettings = settings;
        ValidationError = "";
        StatusMessage = "";
        LoadFields(settings);
        NotifyStateAndCommands();
    }

    public void SyncFromRoomState(RoomStateDto state)
    {
        ArgumentNullException.ThrowIfNull(state);
        HasRoom = true;
        Phase = state.Phase;
        AppliedSettings = state.Settings;
        ValidationError = "";
        StatusMessage = "";
        LoadFields(state.Settings);
        NotifyStateAndCommands();
    }

    public void SetPhase(GamePhase phase)
    {
        if (phase != GamePhase.Lobby)
        {
            LoadFields(AppliedSettings);
            ValidationError = "";
            StatusMessage = "";
        }

        Phase = phase;
        NotifyStateAndCommands();
    }

    public void ClearRoom()
    {
        HasRoom = false;
        Phase = GamePhase.Lobby;
        IsSaving = false;
        ValidationError = "";
        StatusMessage = "";
        LoadFields(AppliedSettings);
        NotifyStateAndCommands();
    }

    public void ResetToDefaults()
    {
        HasRoom = false;
        Phase = GamePhase.Lobby;
        IsConnected = false;
        IsSaving = false;
        AppliedSettings = DefaultSettings;
        ValidationError = "";
        StatusMessage = "";
        LoadFields(DefaultSettings);
        NotifyStateAndCommands();
    }

    private void SetFormField<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (!Set(ref field, value, name)) return;
        OnUserEdited();
    }

    private void OnUserEdited()
    {
        StatusMessage = "";
        ValidationError = "";
        NotifyStateAndCommands();
    }

    private void LoadFields(RoomSettings settings)
    {
        _selectedMapPreset = FindPreset(settings.MapPreset);
        _territoryValueText = settings.TerritoryValue.ToString(CultureInfo.InvariantCulture);
        _baseValueText = settings.BaseValue.ToString(CultureInfo.InvariantCulture);
        _baseHealthText = settings.BaseHealth.ToString(CultureInfo.InvariantCulture);
        _conquestRoundsText = settings.ConquestRounds.ToString(CultureInfo.InvariantCulture);
        _duelRoundsText = settings.DuelRounds.ToString(CultureInfo.InvariantCulture);
        _defenderBonusText = settings.DefenderBonus.ToString(CultureInfo.InvariantCulture);
        _drawBonusText = settings.DrawBonus.ToString(CultureInfo.InvariantCulture);
        _minPlayersText = settings.MinPlayers.ToString(CultureInfo.InvariantCulture);
        _pickTimeoutSecondsText = settings.PickTimeoutSeconds.ToString(CultureInfo.InvariantCulture);
        _defaultQuestionSecondsText = settings.DefaultQuestionSeconds.ToString(CultureInfo.InvariantCulture);
        _disallowAdjacentBases = settings.DisallowAdjacentBases;

        Raise(nameof(SelectedMapPreset));
        Raise(nameof(TerritoryValueText));
        Raise(nameof(BaseValueText));
        Raise(nameof(BaseHealthText));
        Raise(nameof(WinnersPerRoundText));
        Raise(nameof(ConquestRoundsText));
        Raise(nameof(DuelRoundsText));
        Raise(nameof(DefenderBonusText));
        Raise(nameof(DrawBonusText));
        Raise(nameof(MinPlayersText));
        Raise(nameof(PickTimeoutSecondsText));
        Raise(nameof(DefaultQuestionSecondsText));
        Raise(nameof(SuggestedMaxRoundsText));
        Raise(nameof(DisallowAdjacentBases));
    }

    private void NotifyStateAndCommands()
    {
        Raise(nameof(CanEdit));
        Raise(nameof(IsLocked));
        Raise(nameof(ShowApplyControls));
        Raise(nameof(HasUnsavedChanges));
        Raise(nameof(ModeHint));
        RefreshCommands();
    }

    private void RefreshCommands()
    {
        ((AsyncRelayCommand)ApplySettingsCommand).RaiseCanExecuteChanged();
        ((RelayCommand)RevertCommand).RaiseCanExecuteChanged();
        ((RelayCommand)ResetDefaultsCommand).RaiseCanExecuteChanged();
    }

    private bool MatchesDefaults() =>
        TryBuildSettings(out var current, out _) && Equals(current, DefaultSettings);

    private FilterChoice<MapPreset> FindPreset(MapPreset preset) =>
        MapPresets.FirstOrDefault(p => p.Value == preset) ?? MapPresets[1];

    private static bool TryParsePositiveInt(string? raw, out int value)
    {
        if (int.TryParse((raw ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value) && value >= 1)
            return true;
        value = 0;
        return false;
    }

    private static bool TryParseNonNegativeInt(string? raw, out int value)
    {
        if (int.TryParse((raw ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value) && value >= 0)
            return true;
        value = 0;
        return false;
    }
}
