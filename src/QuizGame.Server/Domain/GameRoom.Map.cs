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
    PickInProgress,
    BaseSelectionIncomplete,
    PhaseNotComplete,
    InvalidSettings,
}

public readonly record struct StartGameResult(PhaseError Error, MapDto? Map = null, IReadOnlyList<Guid>? TurnOrder = null)
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

/// <summary>A pending territory choice waiting for a player. <see cref="Seq"/> identifies it so a stale timeout is ignored.</summary>
public sealed record PickView(
    long Seq, PickPurpose Purpose, PlayerSummary Picker, DateTimeOffset Deadline, IReadOnlyList<int> ValidTerritoryIndices);

/// <summary>What the map looks like after cells changed, and what pick happens next.</summary>
public sealed record MapChange(
    IReadOnlyList<TerritoryDto> Changed,
    IReadOnlyList<ScoreEntry> Scoreboard,
    PickView? NextPick,
    bool PickingFinished);

public readonly record struct PickResult(
    PickError Error, MapChange? Change = null, bool AllClaimsIn = false, DuelNomination? Nomination = null)
{
    public bool Ok => Error == PickError.None;
}

/// <summary>Map, phases, turn order and territory picks of a room (plan §4.2, §4.4). Everything runs under the room lock.</summary>
public sealed partial class GameRoom
{
    private sealed record PickState(
        long Seq, PickPurpose Purpose, Guid PlayerId, DateTimeOffset Deadline, IReadOnlyList<int> Valid);

    private GameMap? _map;

    /// <summary>Turn order (player ids), front first. Set randomly at game start, rotated right each Conquest/Duel round.</summary>
    private readonly List<Guid> _turnOrder = [];
    private bool _basesComplete;

    private readonly List<Guid> _pickQueue = [];
    private PickState? _pick;
    private long _pickSeq;

    /// <summary>Conquest claim stage: player id → the free tile they claimed this round (owned only if they then answer right).</summary>
    private readonly Dictionary<Guid, int> _claims = [];

    private int _conquestRound;   // 0 before the first Conquest round
    private int _duelRound;       // 0 before the first Duel round

    /// <summary>Randomness for auto-picks and the initial turn-order shuffle; tests replace it with a seeded instance.</summary>
    public Random PickRandom { get; set; } = Random.Shared;

    public MapDto? GetMap()
    {
        lock (_gate) return _map?.ToDto();
    }

    public Guid? OwnerOf(int territoryIndex)
    {
        lock (_gate) return _map?.OwnerOf(territoryIndex);
    }

    public IReadOnlyList<Guid> TurnOrder
    {
        get { lock (_gate) return _turnOrder.ToList(); }
    }

    /// <summary>Score table, best first: score, then territories, then turn position, then join order.</summary>
    public IReadOnlyList<ScoreEntry> Scoreboard()
    {
        lock (_gate) return ScoreboardCore();
    }

    // Score = value of owned territories + accumulated Duel bonus (plan §4.3).
    private int ScoreOfCore(Guid playerId) =>
        (_map is { } map ? MapService.ScoreOf(map, playerId) : 0)
        + (_players.FirstOrDefault(p => p.Id == playerId)?.BonusPoints ?? 0);

    private IReadOnlyList<ScoreEntry> ScoreboardCore()
    {
        if (_map is not { } map) return [];
        return _players
            .Select((p, order) => (Player: p, Order: order, Score: ScoreOfCore(p.Id), Count: map.OwnedBy(p.Id).Count()))
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

    /// <summary>
    /// Lobby → BaseSelection: needs <c>MinPlayers</c> connected players; generates the map, fixes a random turn order
    /// and starts the first base pick right away (plan §4.2).
    /// </summary>
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

            // Random turn order; it also fixes each player's 1-based base-pick position.
            var shuffled = _players.Select(p => p.Id).OrderBy(_ => PickRandom.Next()).ToList();
            _turnOrder.Clear();
            _turnOrder.AddRange(shuffled);
            for (var i = 0; i < _turnOrder.Count; i++)
                _players.First(p => p.Id == _turnOrder[i]).BaseRank = i + 1;

            // Queue every player for a base pick, in turn order; the first is asked immediately.
            _pickQueue.Clear();
            _pickQueue.AddRange(_turnOrder);
            AdvancePickCore(PickPurpose.Base, now);

            _lastActivity = now;
            return new StartGameResult(PhaseError.None, _map.ToDto(), _turnOrder.ToList());
        }
    }

    /// <summary>
    /// Forward-only phase transitions (no skipping, no going back):
    /// BaseSelection → Conquest (all bases picked), Conquest → Expansion (no round / pick pending),
    /// Expansion → Duel (map full), Duel → Results. Lobby → BaseSelection is <see cref="TryStartGame"/>.
    /// </summary>
    public PhaseResult TryAdvancePhase(GamePhase target, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (target is not (GamePhase.Conquest or GamePhase.Expansion or GamePhase.Duel or GamePhase.Results)
                || (int)target != (int)_phase + 1)
                return new PhaseResult(PhaseError.InvalidTransition, _phase);
            if (_round is not null)
                return new PhaseResult(PhaseError.RoundInProgress, _phase);
            if (_phase == GamePhase.Duel && target == GamePhase.Results
                && _pick is { Purpose: PickPurpose.DuelTarget })
            {
                _pick = null;
                _pickQueue.Clear();
            }
            if (_pick is not null || _pickQueue.Count > 0 || _claims.Count > 0)
                return new PhaseResult(PhaseError.PickInProgress, _phase);

            if (target == GamePhase.Conquest && !_basesComplete)
                return new PhaseResult(PhaseError.BaseSelectionIncomplete, _phase);
            if (target == GamePhase.Duel && _map is { } m && MapService.FreeTerritories(m).Count > 0)
                return new PhaseResult(PhaseError.PhaseNotComplete, _phase); // Expansion fills the whole map first

            _phase = target;
            _lastActivity = now;
            return new PhaseResult(PhaseError.None, _phase);
        }
    }

    /// <summary>The next phase when gameplay is complete. Never interrupts a round or territory pick.</summary>
    public GamePhase? NextAutomaticPhase()
    {
        lock (_gate)
        {
            if (_round is not null || _pick is not null || _pickQueue.Count > 0 || _claims.Count > 0)
                return null;
            var mapFull = _map is { } map && MapService.FreeTerritories(map).Count == 0;
            return _phase switch
            {
                GamePhase.BaseSelection when _basesComplete => GamePhase.Conquest,
                GamePhase.Conquest when PhaseRoundsExhausted || mapFull => GamePhase.Expansion,
                GamePhase.Expansion when mapFull => GamePhase.Duel,
                GamePhase.Duel when PhaseRoundsExhausted || OneBaseHolderRemains() || NoValidDuelTargetRemains() => GamePhase.Results,
                _ => null
            };
        }
    }

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

    // ---- picks (shared machinery for base / conquest-claim / expansion / duel-target) ----

    public PickView? CurrentPickView()
    {
        lock (_gate) return PickViewCore();
    }

    private PickView? PickViewCore() =>
        _pick is { } pick && _players.FirstOrDefault(p => p.Id == pick.PlayerId) is { } picker
            ? new PickView(pick.Seq, pick.Purpose, picker.ToSummary(), pick.Deadline, pick.Valid)
            : null;

    private CurrentPickDto? CurrentPickDtoFor(Guid? viewer) =>
        _pick is { } pick
            ? new CurrentPickDto
            {
                PickerPlayerId = pick.PlayerId,
                PickDeadlineUtc = pick.Deadline,
                ValidTerritoryIndices = viewer == pick.PlayerId ? pick.Valid : [],
                Purpose = pick.Purpose,
            }
            : null;

    /// <summary>Makes the next queued player the current picker for <paramref name="purpose"/>, skipping players who left or have no valid cell.</summary>
    private void AdvancePickCore(PickPurpose purpose, DateTimeOffset now)
    {
        _pick = null;
        if (_map is null) return;

        while (_pickQueue.Count > 0)
        {
            var id = _pickQueue[0];
            _pickQueue.RemoveAt(0);
            if (_players.All(p => p.Id != id)) continue;

            var valid = ValidPicksFor(purpose, id);
            if (valid.Count == 0) continue; // nothing to pick for this player (e.g. no free tile): skip

            var seconds = Math.Max(1, Settings.PickTimeoutSeconds);
            _pick = new PickState(++_pickSeq, purpose, id, now.AddSeconds(seconds), valid);
            return;
        }

        // Queue drained with nobody left to ask.
        if (purpose == PickPurpose.Base) _basesComplete = true;
    }

    private IReadOnlyList<int> ValidPicksFor(PickPurpose purpose, Guid playerId) => _map is not { } map ? [] : purpose switch
    {
        PickPurpose.Base => MapService.ValidBasePicks(map, Settings.DisallowAdjacentBases).Indices,
        PickPurpose.ConquestClaim => ClaimableFor(map, playerId),
        PickPurpose.Expansion => MapService.FreeTerritories(map),
        PickPurpose.DuelTarget => MapService.ValidDuelTargets(map, playerId),
        _ => [],
    };

    // Conquest claim: a free tile adjacent to the player's own, else any free tile, excluding tiles already claimed this round.
    private IReadOnlyList<int> ClaimableFor(GameMap map, Guid playerId)
    {
        var claimed = _claims.Values.ToHashSet();
        var valid = MapService.ValidAdjacentPicks(map, playerId).Where(i => !claimed.Contains(i)).ToList();
        if (valid.Count > 0) return valid;
        // Adjacent set was fully claimed; fall back to any unclaimed free tile.
        return MapService.FreeTerritories(map).Where(i => !claimed.Contains(i)).ToList();
    }

    /// <summary>The current picker chooses a territory. The territory must be one of the valid ones.</summary>
    public PickResult TryPick(Guid playerId, int territoryIndex, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (_pick is not { } pick) return new PickResult(PickError.NoActivePick);
            if (pick.PlayerId != playerId) return new PickResult(PickError.NotYourPick);
            if (!pick.Valid.Contains(territoryIndex)) return new PickResult(PickError.InvalidPick);
            return ResolvePickCore(pick, territoryIndex, now);
        }
    }

    /// <summary>Pick timeout: gives the waiting player a random valid territory so the game cannot stall (plan §4.2).</summary>
    public PickResult TryAutoPick(long pickSeq, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (_pick is not { } pick || pick.Seq != pickSeq) return new PickResult(PickError.NoActivePick);
            var index = MapService.AutoPick(pick.Valid, PickRandom);
            if (index is null) return new PickResult(PickError.NoActivePick);
            return ResolvePickCore(pick, index.Value, now);
        }
    }

    private PickResult ResolvePickCore(PickState pick, int index, DateTimeOffset now)
    {
        _lastActivity = now;
        switch (pick.Purpose)
        {
            case PickPurpose.Base:
            {
                var t = AssignCore(index, pick.PlayerId, isBase: true);
                AdvancePickCore(PickPurpose.Base, now);
                return new PickResult(PickError.None, new MapChange([t.ToDto()], ScoreboardCore(), PickViewCore(), _pick is null && _pickQueue.Count == 0));
            }
            case PickPurpose.Expansion:
            {
                var t = AssignCore(index, pick.PlayerId, isBase: false);
                _pick = null; // Expansion asks one picker per estimation round
                return new PickResult(PickError.None, new MapChange([t.ToDto()], ScoreboardCore(), null, true));
            }
            case PickPurpose.ConquestClaim:
            {
                _claims[pick.PlayerId] = index;
                AdvancePickCore(PickPurpose.ConquestClaim, now);
                var allIn = _pick is null && _pickQueue.Count == 0;
                return new PickResult(PickError.None, new MapChange([], ScoreboardCore(), PickViewCore(), allIn), allIn);
            }
            case PickPurpose.DuelTarget:
            {
                var target = _map!.Get(index)!;
                _nomination = new DuelNomination(pick.PlayerId, target.OwnerId!.Value, index);
                _pick = null;
                return new PickResult(PickError.None, Nomination: _nomination);
            }
            default:
                return new PickResult(PickError.InvalidPick);
        }
    }

    // Assigns ownership and sets base/normal value and health.
    private Territory AssignCore(int index, Guid ownerId, bool isBase)
    {
        var t = _map!.Get(index)!;
        t.OwnerId = ownerId;
        t.IsBase = isBase;
        t.Value = isBase ? Settings.BaseValue : Settings.TerritoryValue;
        t.Health = isBase ? Math.Max(1, Settings.BaseHealth) : 1;
        return t;
    }

    // ---- Conquest claim stage ----

    /// <summary>Begins a Conquest claim stage: every player in turn order claims one free tile, the first asked at once.</summary>
    private void BeginConquestClaimsCore(DateTimeOffset now)
    {
        _claims.Clear();
        _pickQueue.Clear();
        _pickQueue.AddRange(_turnOrder.Where(id => _players.Any(p => p.Id == id)));
        AdvancePickCore(PickPurpose.ConquestClaim, now);
    }

    /// <summary>Whether a Conquest claim stage is open (players still choosing tiles before the question).</summary>
    public bool ClaimsOpen
    {
        get { lock (_gate) return _pick is { Purpose: PickPurpose.ConquestClaim } || (_claims.Count > 0 && _round is null); }
    }

    /// <summary>Prompts the next Duel player who has an adjacent enemy territory to nominate.</summary>
    public PickView? StartDuelTargetPick(DateTimeOffset now)
    {
        lock (_gate)
        {
            if (_phase != GamePhase.Duel || _round is not null || _pick is not null || _nomination is not null
                || _duelRound >= Settings.DuelRounds || OneBaseHolderRemains())
                return null;

            // A player with no adjacent enemy territory cannot attack this turn, so rotate past them.
            for (var i = 0; i < _turnOrder.Count; i++)
            {
                var attacker = ActivePlayerCore();
                if (attacker is not null && ValidPicksFor(PickPurpose.DuelTarget, attacker.Value).Count > 0)
                {
                    _pickQueue.Clear();
                    _pickQueue.Add(attacker.Value);
                    AdvancePickCore(PickPurpose.DuelTarget, now);
                    return PickViewCore();
                }
                RotateTurnOrderCore();
            }
            return null;
        }
    }

    // ---- turn order ----

    /// <summary>Right-rotates the turn order: the front player moves to the back (1st → last). Plan §4.2.</summary>
    private void RotateTurnOrderCore()
    {
        if (_turnOrder.Count < 2) return;
        var front = _turnOrder[0];
        _turnOrder.RemoveAt(0);
        _turnOrder.Add(front);
    }

    private Guid? ActivePlayerCore() => _turnOrder.FirstOrDefault(id => _players.Any(p => p.Id == id)) is var id && id != Guid.Empty ? id : null;

    private bool NoValidDuelTargetRemains() =>
        _map is { } map && !_turnOrder
            .Where(id => _players.Any(p => p.Id == id))
            .Any(id => MapService.ValidDuelTargets(map, id).Count > 0);

    // ---- player removal ----

    /// <summary>
    /// A player was kicked or left: their territories become free again and, if they were up to pick,
    /// the next player is asked. Returns null if nothing on the map or in the pick queue changed.
    /// </summary>
    public MapChange? HandlePlayerRemoved(Guid playerId, DateTimeOffset now)
    {
        lock (_gate)
        {
            var freed = FreeAllOf(playerId);

            _turnOrder.Remove(playerId);
            _pickQueue.RemoveAll(id => id == playerId);
            _claims.Remove(playerId);

            // A pending duel nomination that involves the leaver is no longer valid.
            if (_nomination is { } nomination && (nomination.AttackerId == playerId || nomination.DefenderId == playerId))
                _nomination = null;

            var pickerLeft = _pick?.PlayerId == playerId;
            if (pickerLeft)
            {
                AdvancePickCore(_pick!.Purpose, now);
            }
            else if (freed.Count > 0 && _pick is { } pending)
            {
                var valid = ValidPicksFor(pending.Purpose, pending.PlayerId);
                _pick = pending with { Valid = valid };
            }

            if (freed.Count == 0 && !pickerLeft) return null;
            return new MapChange(freed, ScoreboardCore(), PickViewCore(), _pick is null && _pickQueue.Count == 0);
        }
    }

    // Frees every tile a player owns (used on removal and on losing the last base). Returns the changed cells.
    private List<TerritoryDto> FreeAllOf(Guid playerId)
    {
        var freed = new List<TerritoryDto>();
        if (_map is not { } map) return freed;
        foreach (var t in map.OwnedBy(playerId).ToList())
        {
            t.OwnerId = null;
            t.IsBase = false;
            t.Value = Settings.TerritoryValue;
            t.Health = 1;
            freed.Add(t.ToDto());
        }

        return freed;
    }
}

/// <summary>A territory as it is persisted (plan §5.1 Territories, §7.4).</summary>
public sealed record TerritoryRecord(int Index, int Row, int Col, int Value, bool IsBase, int Health, Guid? OwnerPlayerId);
