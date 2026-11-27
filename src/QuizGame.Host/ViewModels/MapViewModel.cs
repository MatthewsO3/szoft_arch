using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using QuizGame.Contracts;

namespace QuizGame.Host.ViewModels;

/// <summary>One projected map cell.</summary>
public sealed class MapCellItem(int index, int row, int col) : ViewModelBase
{
    private static readonly Brush FreeBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x2A, 0x2F, 0x42)));
    private static readonly Brush DuelTargetBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x6B)));

    public int Index { get; } = index;
    public int Row { get; } = row;
    public int Col { get; } = col;

    private int _value;
    private bool _isBase;
    private int _health = 1;
    private bool _isDuelTarget;
    private string _ownerName = "";
    private Brush _fill = FreeBrush;
    private Brush _textBrush = Brushes.White;

    public int Value { get => _value; private set => Set(ref _value, value); }
    public bool IsBase { get => _isBase; private set => Set(ref _isBase, value); }
    public int Health { get => _health; private set => Set(ref _health, value); }
    public string OwnerName { get => _ownerName; private set => Set(ref _ownerName, value); }
    public Brush Fill { get => _fill; private set => Set(ref _fill, value); }
    public Brush TextBrush { get => _textBrush; private set => Set(ref _textBrush, value); }

    public bool IsDuelTarget
    {
        get => _isDuelTarget;
        internal set
        {
            if (!Set(ref _isDuelTarget, value)) return;
            Raise(nameof(Marker));
            Raise(nameof(CellBorderBrush));
            Raise(nameof(CellBorderThickness));
        }
    }

    public Brush CellBorderBrush => IsDuelTarget ? DuelTargetBrush : Brushes.Transparent;
    public Thickness CellBorderThickness => IsDuelTarget ? new Thickness(5) : new Thickness(0);

    public string Marker
    {
        get
        {
            var baseMarker = IsBase ? (Health > 1 ? $"★{Health}" : "★") : "";
            if (!IsDuelTarget) return baseMarker;
            return baseMarker.Length > 0 ? $"⚔ {baseMarker}" : "⚔";
        }
    }

    public string Caption => OwnerName.Length > 0 ? OwnerName : Value.ToString();

    public void Apply(TerritoryDto dto, PlayerSummary? owner)
    {
        Value = dto.Value;
        IsBase = dto.IsBase;
        Health = dto.Health;
        OwnerName = dto.OwnerPlayerId is null ? "" : owner?.Name ?? "?";
        if (dto.OwnerPlayerId is null)
        {
            Fill = FreeBrush;
            TextBrush = Brushes.White;
        }
        else
        {
            var color = MapViewModel.ParseColor(owner?.ColorHex);
            Fill = Freeze(new SolidColorBrush(color));
            // Dark text on light colours, light text on dark ones.
            var luma = 0.299 * color.R + 0.587 * color.G + 0.114 * color.B;
            TextBrush = luma > 140 ? Brushes.Black : Brushes.White;
        }
        Raise(nameof(Marker));
        Raise(nameof(Caption));
    }

    private static Brush Freeze(SolidColorBrush brush)
    {
        brush.Freeze();
        return brush;
    }
}

public sealed record MapScoreRow(int Position, string Name, int Score, int Territories, Brush Color)
{
    public string Text => $"{Position}. {Name}";
    public string Points => $"{Score}  ({Territories})";
}

/// <summary>
/// View model of the projected map window (plan §9.2): the territory grid, scores and who is picking.
/// Fed by room state, <c>MapUpdated</c>, <c>PickWaiting</c>, <c>PhaseChanged</c> and <c>GameFinished</c>.
/// </summary>
public sealed class MapViewModel : ViewModelBase, IDisposable
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly Dictionary<Guid, PlayerSummary> _players = [];
    private readonly Dictionary<int, MapCellItem> _cells = [];
    private readonly Dictionary<int, TerritoryDto> _territories = [];

    private int _rows;
    private int _cols;
    private GamePhase _phase = GamePhase.Lobby;
    private string _pickerName = "";
    private bool _finished;
    private string _connectionStatus = "";
    private DateTimeOffset _serverTime = DateTimeOffset.UtcNow;
    private long _anchor = Stopwatch.GetTimestamp();
    private DateTimeOffset? _pickDeadlineUtc;
    private PickPurpose? _pickPurpose;
    private int _pickTimeoutSeconds = 20;
    private string _pickCountdown = "";
    private double _pickTimeRemainingFraction;
    private DuelNominationDto? _activeDuel;
    private int? _duelTargetIndex;

    public MapViewModel() => _timer.Tick += (_, _) => UpdateCountdown();

    /// <summary>Raised when the projector map should be brought up (base selection starts).</summary>
    public event Action? OpenRequested;

    public ObservableCollection<MapCellItem> Cells { get; } = [];
    public ObservableCollection<MapScoreRow> Scores { get; } = [];

    public int Rows { get => _rows; private set => Set(ref _rows, value); }
    public int Cols { get => _cols; private set => Set(ref _cols, value); }
    public bool HasMap => Cells.Count > 0;
    public string ConnectionStatus { get => _connectionStatus; set => Set(ref _connectionStatus, value); }
    public string PickCountdown { get => _pickCountdown; private set => Set(ref _pickCountdown, value); }
    public double PickTimeRemainingFraction { get => _pickTimeRemainingFraction; private set => Set(ref _pickTimeRemainingFraction, value); }
    public bool HasActivePickCountdown => _pickDeadlineUtc is not null && !_finished;

    public string Title => _finished ? "Final map" : _phase switch
    {
        GamePhase.Lobby => "Waiting for the game to start",
        GamePhase.BaseSelection => "Base selection",
        GamePhase.Conquest => "Conquest",
        GamePhase.Expansion => "Expansion",
        GamePhase.Duel => "Duel",
        GamePhase.Results => "Final map",
        _ => "",
    };

    public string Status
    {
        get
        {
            if (_finished || _phase == GamePhase.Results) return "Game over";
            if (_pickerName.Length > 0)
            {
                return _pickPurpose switch
                {
                    PickPurpose.Base => $"{_pickerName} is picking a base…",
                    PickPurpose.ConquestClaim => $"{_pickerName} is claiming a tile…",
                    PickPurpose.Expansion => $"{_pickerName} is picking a tile…",
                    PickPurpose.DuelTarget => $"{_pickerName} is choosing a duel target…",
                    _ => $"{_pickerName} is picking a territory…",
                };
            }
            if (_activeDuel is { } duel) return $"⚔ {FormatDuelHeader(duel)}";
            if (_phase == GamePhase.Lobby) return HasMap ? "" : "Players are joining…";
            if (_phase == GamePhase.BaseSelection)
                return AllBasesPlaced ? "All bases are placed" : "Waiting for the base-order round…";
            return "";
        }
    }

    private bool AllBasesPlaced =>
        _players.Count > 0 && _territories.Values.Count(t => t.IsBase && t.OwnerPlayerId is not null) >= _players.Count;

    public void SyncClock(DateTimeOffset utc, TimeSpan latency = default)
    {
        _serverTime = utc + latency;
        _anchor = Stopwatch.GetTimestamp();
        UpdateCountdown();
    }

    public void ApplySettings(RoomSettings settings)
    {
        _pickTimeoutSeconds = Math.Max(1, settings.PickTimeoutSeconds);
        UpdateCountdown();
    }

    public void ApplyRoomState(RoomStateDto state)
    {
        SyncClock(state.ServerUtcNow);
        _pickTimeoutSeconds = Math.Max(1, state.Settings.PickTimeoutSeconds);
        _players.Clear();
        foreach (var p in state.Players) _players[p.Id] = p;
        _phase = state.Phase;
        _finished = state.Phase == GamePhase.Results;
        if (state.CurrentPick is { } pick && !_finished)
        {
            SetPicker(pick.PickerPlayerId);
            _pickDeadlineUtc = pick.PickDeadlineUtc;
            _pickPurpose = pick.Purpose;
            _timer.Start();
        }
        else
        {
            ClearActivePickState();
        }
        _activeDuel = state.CurrentDuelNomination ?? (
            state.CurrentRound is { Kind: RoundKind.Duel, AttackerId: { } att, DefenderId: { } def, TargetTerritoryIndex: { } target }
                ? new DuelNominationDto(att, def, target)
                : null);
        _duelTargetIndex = _activeDuel?.TargetTerritoryIndex;
        ApplyMap(state.Map);
        ApplyScores(state.Scoreboard);
        UpdateCountdown();
        RaiseHeader();
    }

    public void UpsertPlayer(PlayerSummary player)
    {
        _players[player.Id] = player;
        RefreshOwnerLabels();
    }

    public void RemovePlayer(Guid id)
    {
        _players.Remove(id);
        RaiseHeader();
    }

    public void ApplyPhaseChanged(GamePhase phase)
    {
        _phase = phase;
        ClearActivePickState();
        _activeDuel = null;
        SetDuelTarget(null);
        UpdateCountdown();
        RaiseHeader();
        if (phase == GamePhase.BaseSelection) OpenRequested?.Invoke();
    }

    public void ApplyMapUpdated(MapUpdate update)
    {
        foreach (var t in update.ChangedTerritories)
        {
            _territories[t.Index] = t;
            if (_cells.TryGetValue(t.Index, out var cell)) cell.Apply(t, Owner(t));
        }
        // The first MapUpdated of a game carries every cell; the grid size comes from RoomState (see api-contract).
        if (Cells.Count == 0 && _territories.Count > 0) BuildCells();
        ApplyScores(update.Scoreboard);
        ClearActivePickState();
        UpdateCountdown();
        RaiseHeader();
    }

    public void ApplyScoreboardUpdated(IReadOnlyList<ScoreEntry> scoreboard) => ApplyScores(scoreboard);

    public void ApplyDuelNomination(DuelNominationDto nomination)
    {
        ClearActivePickState();
        _activeDuel = nomination;
        SetDuelTarget(nomination.TargetTerritoryIndex);
        UpdateCountdown();
        RaiseHeader();
    }

    public void ClearDuelTarget()
    {
        if (_activeDuel is null && _duelTargetIndex is null) return;
        _activeDuel = null;
        SetDuelTarget(null);
        RaiseHeader();
    }

    public string DescribeDuel(DuelNominationDto nomination)
    {
        var attacker = _players.TryGetValue(nomination.AttackerId, out var a) ? a.Name : "Attacker";
        var defender = _players.TryGetValue(nomination.DefenderId, out var d) ? d.Name : "Defender";
        var tileNum = nomination.TargetTerritoryIndex + 1;
        if (_territories.TryGetValue(nomination.TargetTerritoryIndex, out var t) && t.IsBase)
            return $"{attacker} attacks {defender}'s base (Tile #{tileNum}, {t.Health} HP)";
        return $"{attacker} attacks {defender} (Tile #{tileNum})";
    }

    public string FormatDuelHeader(DuelNominationDto nomination) => DescribeDuel(nomination);

    public void ApplyPickWaiting(PickWaitingDto waiting)
    {
        _players[waiting.Picker.Id] = waiting.Picker;
        SetPicker(waiting.Picker.Id);
        _pickDeadlineUtc = waiting.PickDeadlineUtc;
        _pickPurpose = waiting.Purpose;
        _timer.Start();
        UpdateCountdown();
        RaiseHeader();
    }

    public void ClearPicker()
    {
        ClearActivePickState();
        UpdateCountdown();
        RaiseHeader();
    }

    public void ApplyGameFinished(GameFinishedDto finished)
    {
        _finished = true;
        _phase = GamePhase.Results;
        ClearActivePickState();
        _activeDuel = null;
        SetDuelTarget(null);
        ApplyMap(finished.FinalMap);
        Scores.Clear();
        foreach (var s in finished.Standings)
        {
            _players.TryGetValue(s.PlayerId, out var p);
            var brush = new SolidColorBrush(ParseColor(p?.ColorHex));
            brush.Freeze();
            Scores.Add(new MapScoreRow(s.Rank, s.Name, s.Score, s.TerritoryCount, brush));
        }
        UpdateCountdown();
        RaiseHeader();
    }

    public void Clear()
    {
        _players.Clear();
        _territories.Clear();
        _cells.Clear();
        Cells.Clear();
        Scores.Clear();
        Rows = Cols = 0;
        _phase = GamePhase.Lobby;
        _finished = false;
        ClearActivePickState();
        _activeDuel = null;
        _duelTargetIndex = null;
        UpdateCountdown();
        RaiseHeader();
    }

    private void ClearActivePickState()
    {
        _timer.Stop();
        SetPicker(null);
        _pickDeadlineUtc = null;
        _pickPurpose = null;
    }

    private void UpdateCountdown()
    {
        if (_pickDeadlineUtc is not { } deadline || _finished)
        {
            PickTimeRemainingFraction = 0;
            PickCountdown = "";
            Raise(nameof(HasActivePickCountdown));
            return;
        }

        var remaining = deadline - (_serverTime + Stopwatch.GetElapsedTime(_anchor));
        PickTimeRemainingFraction = Math.Clamp(remaining.TotalSeconds / Math.Max(1, _pickTimeoutSeconds), 0, 1);
        PickCountdown = remaining <= TimeSpan.Zero ? "Time is up — auto-picking…" : $"{(int)Math.Ceiling(remaining.TotalSeconds)} s";
        Raise(nameof(HasActivePickCountdown));
    }

    public void Dispose() => _timer.Stop();

    private void SetPicker(Guid? id)
    {
        _pickerName = id is { } v && _players.TryGetValue(v, out var p) ? p.Name : "";
    }

    private void ApplyMap(MapDto? map)
    {
        _territories.Clear();
        if (map is null)
        {
            _cells.Clear();
            Cells.Clear();
            Rows = Cols = 0;
            return;
        }

        Rows = map.Rows;
        Cols = map.Cols;
        foreach (var t in map.Territories) _territories[t.Index] = t;
        BuildCells();
    }

    private void BuildCells()
    {
        _cells.Clear();
        Cells.Clear();
        if (_territories.Count > 0 && (Rows == 0 || Cols == 0))
        {
            // MapUpdated can arrive before RoomState: derive the grid size from the cells themselves.
            Rows = _territories.Values.Max(t => t.Row) + 1;
            Cols = _territories.Values.Max(t => t.Col) + 1;
        }
        foreach (var t in _territories.Values.OrderBy(t => t.Index))
        {
            var cell = new MapCellItem(t.Index, t.Row, t.Col);
            cell.Apply(t, Owner(t));
            cell.IsDuelTarget = _duelTargetIndex == t.Index;
            _cells[t.Index] = cell;
            Cells.Add(cell);
        }
    }

    private void SetDuelTarget(int? targetIndex)
    {
        if (_duelTargetIndex is { } previous && _cells.TryGetValue(previous, out var oldCell))
            oldCell.IsDuelTarget = false;
        _duelTargetIndex = targetIndex;
        if (targetIndex is { } next && _cells.TryGetValue(next, out var newCell))
            newCell.IsDuelTarget = true;
    }

    private void RefreshOwnerLabels()
    {
        foreach (var t in _territories.Values)
            if (t.OwnerPlayerId is not null && _cells.TryGetValue(t.Index, out var cell)) cell.Apply(t, Owner(t));
    }

    private PlayerSummary? Owner(TerritoryDto t) =>
        t.OwnerPlayerId is { } id && _players.TryGetValue(id, out var p) ? p : null;

    private void ApplyScores(IReadOnlyList<ScoreEntry> scoreboard)
    {
        Scores.Clear();
        for (var i = 0; i < scoreboard.Count; i++)
        {
            var s = scoreboard[i];
            var rank = _finished && i > 0 && s.Score == scoreboard[i - 1].Score
                ? Scores[i - 1].Position
                : i + 1;
            _players.TryGetValue(s.PlayerId, out var p);
            var brush = new SolidColorBrush(ParseColor(p?.ColorHex));
            brush.Freeze();
            Scores.Add(new MapScoreRow(rank, s.Name, s.Score, s.TerritoryCount, brush));
        }
    }

    private void RaiseHeader()
    {
        Raise(nameof(HasMap));
        Raise(nameof(Title));
        Raise(nameof(Status));
    }

    internal static Color ParseColor(string? hex)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(hex) && ColorConverter.ConvertFromString(hex) is Color c) return c;
        }
        catch (FormatException) { }
        return Color.FromRgb(0x77, 0x77, 0x77);
    }
}
