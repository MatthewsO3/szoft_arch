using QuizGame.Contracts;
using QuizGame.Server.Domain.Map;

namespace QuizGame.Server.Domain;

public enum PhaseError
{
    None,
    InvalidPhase,
    InvalidTransition,
    NotEnoughPlayers,
    RoundInProgress,
    BaseSelectionIncomplete,
    InvalidSettings,
}

public readonly record struct StartGameResult(PhaseError Error, MapDto? Map = null)
{
    public bool Ok => Error == PhaseError.None;
}

public readonly record struct PhaseResult(PhaseError Error, GamePhase Phase)
{
    public bool Ok => Error == PhaseError.None;
}

public enum PickError
{
    None,
    InvalidPhase,
    NoActivePick,
    NotYourPick,
    InvalidPick,
}

/// <summary>A base pick that is waiting for a player. <see cref="Seq"/> identifies it so that a stale timeout is ignored.</summary>
public sealed record PickView(
    long Seq, PlayerSummary Picker, DateTimeOffset Deadline, IReadOnlyList<int> ValidTerritoryIndices, bool Relaxed);

/// <summary>What the map looks like after a base was assigned or released, and what happens next.</summary>
public sealed record MapChange(
    IReadOnlyList<TerritoryDto> Changed,
    IReadOnlyList<ScoreEntry> Scoreboard,
    PickView? NextPick,
    bool PickingFinished);

public readonly record struct PickResult(PickError Error, MapChange? Change = null)
{
    public bool Ok => Error == PickError.None;
}

/// <summary>Map, base selection and phase transitions of a room (plan §4.2, §4.4). Everything runs under the room lock.</summary>
public sealed partial class GameRoom
{
    private sealed record PickState(
        long Seq, Guid PlayerId, DateTimeOffset Deadline, IReadOnlyList<int> Valid, bool Relaxed);

    private GameMap? _map;
    private bool _baseOrderEstablished;
    private readonly List<Guid> _pickQueue = [];
    private PickState? _pick;
    private long _pickSeq;

    /// <summary>Randomness for auto-picks; tests replace it with a seeded instance.</summary>
    public Random PickRandom { get; set; } = Random.Shared;

    public MapDto? GetMap()
    {
        lock (_gate) return _map?.ToDto();
    }

    public Guid? OwnerOf(int territoryIndex)
    {
        lock (_gate) return _map?.OwnerOf(territoryIndex);
    }

    /// <summary>Score table, best first: score, then territories, then earlier base rank, then join order.</summary>
    public IReadOnlyList<ScoreEntry> Scoreboard()
    {
        lock (_gate) return ScoreboardCore();
    }

    private IReadOnlyList<ScoreEntry> ScoreboardCore()
    {
        if (_map is not { } map) return [];
        return _players
            .Select((p, order) => (Player: p, Order: order, Score: MapService.ScoreOf(map, p.Id), Count: map.OwnedBy(p.Id).Count()))
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.Count)
            .ThenBy(x => x.Player.BaseRank ?? int.MaxValue)
            .ThenBy(x => x.Order)
            .Select(x => new ScoreEntry(x.Player.Id, x.Player.Name, x.Score, x.Count))
            .ToList();
    }

    // ---- settings and phases ----

    /// <summary>Replaces the room settings. Lobby only: the map and the values are fixed once the game starts.</summary>
    public PhaseError TryUpdateSettings(RoomSettings settings)
    {
        lock (_gate)
        {
            if (_phase != GamePhase.Lobby) return PhaseError.InvalidPhase;
            if (!RoomSettingsRules.IsValid(settings)) return PhaseError.InvalidSettings;
            Settings = settings;
            return PhaseError.None;
        }
    }

    /// <summary>Lobby → BaseSelection: needs <c>MinPlayers</c> connected players; generates the map (plan §4.2).</summary>
    public StartGameResult TryStartGame(DateTimeOffset now)
    {
        lock (_gate)
        {
            if (_phase != GamePhase.Lobby) return new StartGameResult(PhaseError.InvalidPhase);
            var connected = _players.Count(p => p.IsConnected);
            if (_players.Count == 0 || connected < Math.Max(1, Settings.MinPlayers))
                return new StartGameResult(PhaseError.NotEnoughPlayers);

            _map = MapService.Create(Settings, _players.Count);
            _phase = GamePhase.BaseSelection;
            _lastActivity = now;
            return new StartGameResult(PhaseError.None, _map.ToDto());
        }
    }

    /// <summary>
    /// BaseSelection → Conquest (all bases picked, no round open) and Conquest → Results (no round open).
    /// Lobby → BaseSelection is <see cref="TryStartGame"/>; anything else is an invalid transition (no skipping, no going back).
    /// </summary>
    public PhaseResult TryAdvancePhase(GamePhase target, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (target is not (GamePhase.Conquest or GamePhase.Results) || (int)target != (int)_phase + 1)
                return new PhaseResult(PhaseError.InvalidTransition, _phase);
            if (_round is not null)
                return new PhaseResult(PhaseError.RoundInProgress, _phase);
            if (target == GamePhase.Conquest && !BasesCompleteCore)
                return new PhaseResult(PhaseError.BaseSelectionIncomplete, _phase);

            _phase = target;
            _lastActivity = now;
            return new PhaseResult(PhaseError.None, _phase);
        }
    }

    private bool BasesCompleteCore => _baseOrderEstablished && _pick is null && _pickQueue.Count == 0;

    /// <summary>Final standings and map for <c>GameFinished</c>; ties on score share a rank.</summary>
    public GameFinishedDto BuildGameFinished()
    {
        lock (_gate)
        {
            var board = ScoreboardCore();
            var standings = new List<FinalStanding>();
            for (var i = 0; i < board.Count; i++)
            {
                var rank = i > 0 && board[i].Score == board[i - 1].Score ? standings[i - 1].Rank : i + 1;
                standings.Add(new FinalStanding
                {
                    Rank = rank, PlayerId = board[i].PlayerId, Name = board[i].Name,
                    Score = board[i].Score, TerritoryCount = board[i].TerritoryCount,
                });
            }

            return new GameFinishedDto(standings, _map?.ToDto() ?? new MapDto(0, 0, []));
        }
    }

    // ---- base selection ----

    public PickView? CurrentPickView()
    {
        lock (_gate) return PickViewCore();
    }

    private PickView? PickViewCore() =>
        _pick is { } pick && _players.FirstOrDefault(p => p.Id == pick.PlayerId) is { } picker
            ? new PickView(pick.Seq, picker.ToSummary(), pick.Deadline, pick.Valid, pick.Relaxed)
            : null;

    private static CurrentPickDto PickDto(PickState pick, Guid? viewer) => new()
    {
        PickerPlayerId = pick.PlayerId,
        PickDeadlineUtc = pick.Deadline,
        ValidTerritoryIndices = viewer == pick.PlayerId ? pick.Valid : [],
    };

    /// <summary>The base-order round is closed: players pick in ranking order, the first one right away.</summary>
    private void BeginBasePickingCore(IReadOnlyList<Guid> order, DateTimeOffset now)
    {
        var rank = 0;
        foreach (var id in order)
        {
            var player = _players.FirstOrDefault(p => p.Id == id);
            if (player is null) continue;
            player.BaseRank = ++rank;
            _pickQueue.Add(id);
        }

        _baseOrderEstablished = true;
        AdvancePickCore(now);
    }

    /// <summary>Makes the next player in the queue the current picker (skipping players who left).</summary>
    private void AdvancePickCore(DateTimeOffset now)
    {
        _pick = null;
        if (_map is null) return;

        while (_pickQueue.Count > 0)
        {
            var id = _pickQueue[0];
            _pickQueue.RemoveAt(0);
            if (_players.All(p => p.Id != id)) continue;

            var valid = MapService.ValidBasePicks(_map, Settings.DisallowAdjacentBases);
            if (valid.Indices.Count == 0) continue; // map is full: nothing left to pick

            var seconds = Math.Max(1, Settings.PickTimeoutSeconds);
            _pick = new PickState(++_pickSeq, id, now.AddSeconds(seconds), valid.Indices, valid.Relaxed);
            return;
        }
    }

    /// <summary>The current picker chooses a base. The territory must be one of the valid ones.</summary>
    public PickResult TryPick(Guid playerId, int territoryIndex, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (_phase != GamePhase.BaseSelection) return new PickResult(PickError.InvalidPhase);
            if (_pick is not { } pick) return new PickResult(PickError.NoActivePick);
            if (pick.PlayerId != playerId) return new PickResult(PickError.NotYourPick);
            if (!pick.Valid.Contains(territoryIndex)) return new PickResult(PickError.InvalidPick);

            return new PickResult(PickError.None, AssignBaseCore(pick, territoryIndex, now));
        }
    }

    /// <summary>Pick timeout: gives the waiting player a random valid territory so the game cannot stall (plan §4.2).</summary>
    public PickResult TryAutoPick(long pickSeq, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (_phase != GamePhase.BaseSelection || _pick is not { } pick || pick.Seq != pickSeq)
                return new PickResult(PickError.NoActivePick);

            var index = MapService.AutoPick(pick.Valid, PickRandom);
            if (index is null) return new PickResult(PickError.NoActivePick);
            return new PickResult(PickError.None, AssignBaseCore(pick, index.Value, now));
        }
    }

    private MapChange AssignBaseCore(PickState pick, int index, DateTimeOffset now)
    {
        var territory = _map!.Get(index)!;
        territory.OwnerId = pick.PlayerId;
        territory.IsBase = true;
        territory.Value = Settings.BaseValue;
        _lastActivity = now;

        AdvancePickCore(now);
        return new MapChange([territory.ToDto()], ScoreboardCore(), PickViewCore(), _pick is null && _pickQueue.Count == 0);
    }

    /// <summary>
    /// A player was kicked or left: their territories become free again and, if they were up to pick,
    /// the next player is. Returns null if nothing on the map or in the pick queue changed.
    /// </summary>
    public MapChange? HandlePlayerRemoved(Guid playerId, DateTimeOffset now)
    {
        lock (_gate)
        {
            var freed = new List<TerritoryDto>();
            if (_map is { } map)
            {
                foreach (var t in map.OwnedBy(playerId).ToList())
                {
                    t.OwnerId = null;
                    t.IsBase = false;
                    t.Value = Settings.TerritoryValue;
                    freed.Add(t.ToDto());
                }
            }

            _pickQueue.RemoveAll(id => id == playerId);
            var pickerLeft = _pick?.PlayerId == playerId;
            if (pickerLeft)
            {
                AdvancePickCore(now);
            }
            else if (freed.Count > 0 && _pick is { } pending && _map is { } current)
            {
                // Freed cells become pickable for whoever is up now (their open PickRequested keeps its old list).
                var valid = MapService.ValidBasePicks(current, Settings.DisallowAdjacentBases);
                _pick = pending with { Valid = valid.Indices, Relaxed = valid.Relaxed };
            }

            if (freed.Count == 0 && !pickerLeft) return null;

            return new MapChange(freed, ScoreboardCore(), PickViewCore(), _baseOrderEstablished && _pick is null && _pickQueue.Count == 0);
        }
    }
}

/// <summary>A territory as it is persisted (plan §5.1 Territories, §7.4).</summary>
public sealed record TerritoryRecord(int Index, int Row, int Col, int Value, bool IsBase, Guid? OwnerPlayerId);
