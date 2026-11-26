using System.Collections.ObjectModel;
using System.Windows.Media;
using QuizGame.Contracts;

namespace QuizGame.Host.ViewModels;

/// <summary>One projected map cell.</summary>
public sealed class MapCellItem(int index, int row, int col) : ViewModelBase
{
    private static readonly Brush FreeBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x2A, 0x2F, 0x42)));

    public int Index { get; } = index;
    public int Row { get; } = row;
    public int Col { get; } = col;

    private int _value;
    private bool _isBase;
    private string _ownerName = "";
    private Brush _fill = FreeBrush;
    private Brush _textBrush = Brushes.White;

    public int Value { get => _value; private set => Set(ref _value, value); }
    public bool IsBase { get => _isBase; private set => Set(ref _isBase, value); }
    public string OwnerName { get => _ownerName; private set => Set(ref _ownerName, value); }
    public Brush Fill { get => _fill; private set => Set(ref _fill, value); }
    public Brush TextBrush { get => _textBrush; private set => Set(ref _textBrush, value); }

    public string Marker => IsBase ? "★" : "";
    public string Caption => OwnerName.Length > 0 ? OwnerName : Value.ToString();

    public void Apply(TerritoryDto dto, PlayerSummary? owner)
    {
        Value = dto.Value;
        IsBase = dto.IsBase;
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
public sealed class MapViewModel : ViewModelBase
{
    private readonly Dictionary<Guid, PlayerSummary> _players = [];
    private readonly Dictionary<int, MapCellItem> _cells = [];
    private readonly Dictionary<int, TerritoryDto> _territories = [];

    private int _rows;
    private int _cols;
    private GamePhase _phase = GamePhase.Lobby;
    private string _pickerName = "";
    private bool _finished;
    private string _connectionStatus = "";

    /// <summary>Raised when the projector map should be brought up (base selection starts).</summary>
    public event Action? OpenRequested;

    public ObservableCollection<MapCellItem> Cells { get; } = [];
    public ObservableCollection<MapScoreRow> Scores { get; } = [];

    public int Rows { get => _rows; private set => Set(ref _rows, value); }
    public int Cols { get => _cols; private set => Set(ref _cols, value); }
    public bool HasMap => Cells.Count > 0;
    public string ConnectionStatus { get => _connectionStatus; set => Set(ref _connectionStatus, value); }

    public string Title => _finished ? "Final map" : _phase switch
    {
        GamePhase.Lobby => "Waiting for the game to start",
        GamePhase.BaseSelection => "Base selection",
        GamePhase.Conquest => "Conquest",
        GamePhase.Results => "Final map",
        _ => "",
    };

    public string Status
    {
        get
        {
            if (_finished || _phase == GamePhase.Results) return "Game over";
            if (_phase == GamePhase.Lobby) return HasMap ? "" : "Players are joining…";
            if (_phase == GamePhase.BaseSelection)
            {
                if (_pickerName.Length > 0) return $"{_pickerName} is picking a base…";
                return AllBasesPlaced ? "All bases are placed" : "Waiting for the base-order round…";
            }
            return "";
        }
    }

    private bool AllBasesPlaced =>
        _players.Count > 0 && _territories.Values.Count(t => t.IsBase && t.OwnerPlayerId is not null) >= _players.Count;

    public void ApplyRoomState(RoomStateDto state)
    {
        _players.Clear();
        foreach (var p in state.Players) _players[p.Id] = p;
        _phase = state.Phase;
        _finished = state.Phase == GamePhase.Results;
        SetPicker(state.CurrentPick?.PickerPlayerId);
        ApplyMap(state.Map);
        ApplyScores(state.Scoreboard);
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
        if (phase != GamePhase.BaseSelection) SetPicker(null);
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
        SetPicker(null);
        RaiseHeader();
    }

    public void ApplyPickWaiting(PlayerSummary picker)
    {
        _players[picker.Id] = picker;
        SetPicker(picker.Id);
        RaiseHeader();
    }

    public void ClearPicker()
    {
        SetPicker(null);
        RaiseHeader();
    }

    public void ApplyGameFinished(GameFinishedDto finished)
    {
        _finished = true;
        _phase = GamePhase.Results;
        SetPicker(null);
        ApplyMap(finished.FinalMap);
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
        SetPicker(null);
        RaiseHeader();
    }

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
            _cells[t.Index] = cell;
            Cells.Add(cell);
        }
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
        var position = 1;
        foreach (var s in scoreboard)
        {
            _players.TryGetValue(s.PlayerId, out var p);
            var brush = new SolidColorBrush(ParseColor(p?.ColorHex));
            brush.Freeze();
            Scores.Add(new MapScoreRow(position++, s.Name, s.Score, s.TerritoryCount, brush));
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
