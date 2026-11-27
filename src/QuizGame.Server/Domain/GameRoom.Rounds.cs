using QuizGame.Contracts;
using QuizGame.Server.Domain.Evaluation;
using QuizGame.Server.Domain.Map;

namespace QuizGame.Server.Domain;

public enum StartRoundError
{
    None,
    RoundActive,
    InvalidRound,
    NoPlayers,
    /// <summary>The round kind is not allowed in the current phase.</summary>
    InvalidPhase,
    /// <summary>A territory pick is still waiting; it must finish before a round opens.</summary>
    PickInProgress,
    /// <summary>Conquest: no claims are in yet (the claim stage must run first).</summary>
    NoClaims,
    /// <summary>The phase's round budget is used up (Conquest/Duel).</summary>
    RoundsExhausted,
    /// <summary>Expansion: the map is already full, nothing left to hand out.</summary>
    MapFull,
    /// <summary>Duel: no confirmed attacker/target (nominate + confirm first).</summary>
    NoTarget,
}

public enum SubmitStatus
{
    Ok,
    NoActiveRound,
    NotParticipant,
    AlreadyAnswered,
    TooLate,
    InvalidAnswer,
}

/// <summary>Immutable view of a round that was just opened.</summary>
public sealed record RoundInfo(
    Guid RoundId,
    RoundKind Kind,
    QuestionDto Question,
    DateTimeOffset StartedAt,
    DateTimeOffset Deadline,
    IReadOnlyList<Guid> ParticipantIds,
    Guid? AttackerId,
    Guid? DefenderId,
    int? TargetTerritoryIndex)
{
    public QuestionForHost ForHost() => new(RoundId, Question, Deadline);

    /// <summary>What a player's phone may see: never the correct answer, never the question text.</summary>
    public QuestionForPlayer ForPlayer() => new()
    {
        RoundId = RoundId,
        Type = Question.Type,
        ServerDeadlineUtc = Deadline,
        Options = Question.Options,
        Unit = Question.Unit,
    };
}

public readonly record struct StartRoundResult(RoundInfo? Round, StartRoundError Error, string? Detail = null)
{
    public bool Ok => Round is not null;
}

public readonly record struct SubmitResult(SubmitStatus Status, int Answered = 0, int Total = 0, bool AllAnswered = false)
{
    public bool Ok => Status == SubmitStatus.Ok;
}

/// <summary>An answer as it is persisted (plan §5.1 Answers).</summary>
public sealed record AnswerRecord(
    Guid PlayerId, char? Option, decimal? Number, DateTimeOffset ReceivedAt, bool? IsCorrect, decimal? Distance, int Rank);

/// <summary>A closed round as it is persisted (plan §5.1 Rounds + Answers, §7.4).</summary>
public sealed record RoundRecord(
    Guid RoundId,
    int Seq,
    RoundKind Kind,
    Guid QuestionId,
    DateTimeOffset StartedAt,
    DateTimeOffset Deadline,
    DateTimeOffset ClosedAt,
    Guid? AttackerId,
    Guid? DefenderId,
    int? TargetTerritoryIndex,
    Guid? WinnerPlayerId,
    IReadOnlyList<AnswerRecord> Answers);

/// <summary>
/// A closed round plus its follow-up (plan §4.2): <see cref="Change"/> is the map/score change applied on close
/// (Conquest winners taking their claims, a Duel capture, Duel bonuses); <see cref="ExpansionPick"/> is the
/// Expansion estimation winner that now has to pick a free tile.
/// </summary>
public sealed record ClosedRound(RoundClosedDto Result, RoundRecord Record, MapChange? Change = null, PickView? ExpansionPick = null);

/// <summary>Progress of the round in progress, for resending it to a reconnecting client.</summary>
public sealed record OpenRoundView(RoundInfo Round, int Answered, int Total);

/// <summary>A duel the active player has nominated and the host has not yet confirmed (plan §4.2).</summary>
public sealed record DuelNomination(Guid AttackerId, Guid DefenderId, int TargetTerritoryIndex);

public enum NominateError
{
    None,
    InvalidPhase,
    RoundInProgress,
    NotYourTurn,
    NoMap,
    NoSuchTerritory,
    TerritoryFree,
    OwnTerritory,
    NotAdjacent,
}

public readonly record struct NominateResult(NominateError Error, DuelNomination? Nomination = null)
{
    public bool Ok => Error == NominateError.None;
}

public sealed partial class GameRoom
{
    private sealed class ActiveRound
    {
        private readonly Dictionary<Guid, (AnswerEntry Entry, DateTimeOffset ReceivedAt)> _answers = [];

        public required RoundInfo Info { get; init; }
        public required IReadOnlyList<Participant> Participants { get; init; }
        public int NextSequence { get; set; }

        public bool HasAnswered(Guid playerId) => _answers.ContainsKey(playerId);
        public int AnswerCount => _answers.Count;
        public void Add(AnswerEntry entry, DateTimeOffset receivedAt) => _answers[entry.PlayerId] = (entry, receivedAt);
        public IReadOnlyList<AnswerEntry> Entries() => _answers.Values.Select(v => v.Entry).ToList();
        public DateTimeOffset ReceivedAt(Guid playerId) => _answers[playerId].ReceivedAt;
        public AnswerEntry? EntryOf(Guid playerId) => _answers.TryGetValue(playerId, out var v) ? v.Entry : null;

        public CurrentRoundDto ToCurrentDto() => new()
        {
            RoundId = Info.RoundId,
            Kind = Info.Kind,
            QuestionType = Info.Question.Type,
            ServerDeadlineUtc = Info.Deadline,
            AttackerId = Info.AttackerId,
            DefenderId = Info.DefenderId,
            TargetTerritoryIndex = Info.TargetTerritoryIndex,
        };
    }

    private ActiveRound? _round;
    private int _roundSeq;
    private RoundClosedDto? _lastRoundResult;
    private DuelNomination? _nomination;

    public bool HasOpenRound
    {
        get { lock (_gate) return _round is not null; }
    }

    /// <summary>Authoritative host turn: true only when a question may be sent immediately.</summary>
    public bool CanSendQuestion()
    {
        lock (_gate)
        {
            if (_round is not null || _pick is not null || _pickQueue.Count > 0)
                return false;

            return _phase switch
            {
                GamePhase.Conquest => _claims.Count == 0 && _conquestRound < Settings.ConquestRounds
                    && _map is { } conquestMap && MapService.FreeTerritories(conquestMap).Count > 0,
                GamePhase.Expansion => _map is { } expansionMap && MapService.FreeTerritories(expansionMap).Count > 0,
                GamePhase.Duel => _nomination is not null && _duelRound < Settings.DuelRounds && !OneBaseHolderRemains(),
                _ => false,
            };
        }
    }

    // ---- Conquest ----

    /// <summary>Starts the claim stage of a Conquest round: everyone claims a free tile before the question opens.</summary>
    public PickView? StartConquestClaims(DateTimeOffset now)
    {
        lock (_gate)
        {
            if (_phase != GamePhase.Conquest || _round is not null || _pick is not null || _claims.Count > 0) return null;
            if (_conquestRound >= Settings.ConquestRounds) return null;
            BeginConquestClaimsCore(now);
            return PickViewCore();
        }
    }

    /// <summary>Opens the Conquest multiple-choice question once every claim is in (plan §4.2 Conquest).</summary>
    public StartRoundResult TryStartConquestRound(QuestionDto question, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (_round is not null) return Fail(StartRoundError.RoundActive);
            if (_phase != GamePhase.Conquest) return Fail(StartRoundError.InvalidPhase, "Conquest rounds run only in the Conquest phase.");
            if (_conquestRound >= Settings.ConquestRounds) return Fail(StartRoundError.RoundsExhausted, "All Conquest rounds have been played.");
            if (_pick is not null || _pickQueue.Count > 0) return Fail(StartRoundError.PickInProgress, "Players are still claiming tiles.");
            if (_claims.Count == 0) return Fail(StartRoundError.NoClaims, "No tiles were claimed this round.");
            if (question.Type != QuestionType.MultipleChoice) return Fail(StartRoundError.InvalidRound, "A Conquest round needs a multiple-choice question.");
            var malformed = ValidateQuestion(question);
            if (malformed is not null) return Fail(StartRoundError.InvalidRound, malformed);

            // Only the players who claimed a tile take part in the question.
            var participants = _players.Where(p => _claims.ContainsKey(p.Id)).ToList();
            return OpenRound(RoundKind.Conquest, question, participants, null, null, null, now);
        }
    }

    // ---- Expansion ----

    /// <summary>Opens an Expansion estimation question for everyone (plan §4.2 Expansion).</summary>
    public StartRoundResult TryStartExpansionRound(QuestionDto question, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (_round is not null) return Fail(StartRoundError.RoundActive);
            if (_phase != GamePhase.Expansion) return Fail(StartRoundError.InvalidPhase, "Expansion rounds run only in the Expansion phase.");
            if (_pick is not null) return Fail(StartRoundError.PickInProgress, "A tile pick is still in progress.");
            if (_map is not { } map || MapService.FreeTerritories(map).Count == 0) return Fail(StartRoundError.MapFull, "The map is already full.");
            if (question.Type != QuestionType.Estimation) return Fail(StartRoundError.InvalidRound, "An Expansion round needs an estimation question.");
            var malformed = ValidateQuestion(question);
            if (malformed is not null) return Fail(StartRoundError.InvalidRound, malformed);

            return OpenRound(RoundKind.Expansion, question, _players.ToList(), null, null, null, now);
        }
    }

    // ---- Duel ----

    /// <summary>
    /// The active player nominates an enemy tile to attack (plan §4.2 Duel). Duel phase only, their turn, no round open.
    /// The target must be owned by another player and adjacent to a tile the attacker owns. The host confirms it.
    /// </summary>
    public NominateResult TryNominateTarget(Guid attackerId, int targetIndex)
    {
        lock (_gate)
        {
            if (_phase != GamePhase.Duel) return new NominateResult(NominateError.InvalidPhase);
            if (_round is not null) return new NominateResult(NominateError.RoundInProgress);
            if (_pick is { Purpose: PickPurpose.DuelTarget } pick
                && (pick.PlayerId != attackerId || !pick.Valid.Contains(targetIndex)))
                return new NominateResult(NominateError.NotYourTurn);
            if (ActivePlayerCore() != attackerId) return new NominateResult(NominateError.NotYourTurn);
            if (_map is not { } map) return new NominateResult(NominateError.NoMap);
            if (map.Get(targetIndex) is not { } target) return new NominateResult(NominateError.NoSuchTerritory);
            if (target.OwnerId is not { } defenderId) return new NominateResult(NominateError.TerritoryFree);
            if (defenderId == attackerId) return new NominateResult(NominateError.OwnTerritory);
            if (!target.Neighbours.Any(n => map.Territories[n].OwnerId == attackerId))
                return new NominateResult(NominateError.NotAdjacent);

            _nomination = new DuelNomination(attackerId, defenderId, targetIndex);
            if (_pick is { Purpose: PickPurpose.DuelTarget }) _pick = null;
            return new NominateResult(NominateError.None, _nomination);
        }
    }

    public DuelNomination? CurrentNomination()
    {
        lock (_gate) return _nomination;
    }

    /// <summary>The active player at the front of the turn order (for the Duel), or null.</summary>
    public Guid? ActivePlayer()
    {
        lock (_gate) return ActivePlayerCore();
    }

    /// <summary>Opens a Duel between attacker and defender using the confirmed nomination (plan §4.2 Duel).</summary>
    public StartRoundResult TryStartDuelRound(QuestionDto question, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (_round is not null) return Fail(StartRoundError.RoundActive);
            if (_phase != GamePhase.Duel) return Fail(StartRoundError.InvalidPhase, "Duels run only in the Duel phase.");
            if (_duelRound >= Settings.DuelRounds) return Fail(StartRoundError.RoundsExhausted, "All Duel rounds have been played.");
            if (_nomination is not { } nomination) return Fail(StartRoundError.NoTarget, "No duel target was confirmed.");
            if (question.Type != QuestionType.MultipleChoice) return Fail(StartRoundError.InvalidRound, "A Duel needs a multiple-choice question.");
            var malformed = ValidateQuestion(question);
            if (malformed is not null) return Fail(StartRoundError.InvalidRound, malformed);

            var attacker = _players.FirstOrDefault(p => p.Id == nomination.AttackerId);
            var defender = _players.FirstOrDefault(p => p.Id == nomination.DefenderId);
            if (attacker is null || defender is null || _map?.Get(nomination.TargetTerritoryIndex) is null)
            {
                _nomination = null;
                return Fail(StartRoundError.InvalidRound, "The attacker, defender or target is no longer valid.");
            }

            _nomination = null;
            return OpenRound(RoundKind.Duel, question, [defender, attacker], attacker.Id, defender.Id, nomination.TargetTerritoryIndex, now);
        }
    }

    // ---- shared round open / submit / close ----

    private StartRoundResult OpenRound(
        RoundKind kind, QuestionDto question, IReadOnlyList<PlayerState> participants,
        Guid? attacker, Guid? defender, int? target, DateTimeOffset now)
    {
        if (participants.Count == 0) return Fail(StartRoundError.NoPlayers);

        var seconds = Math.Max(1, question.TimeLimitSeconds > 0 ? question.TimeLimitSeconds : Settings.DefaultQuestionSeconds);
        var info = new RoundInfo(
            Guid.NewGuid(), kind, question, now, now.AddSeconds(seconds),
            participants.Select(p => p.Id).ToList(), attacker, defender, target);

        _round = new ActiveRound
        {
            Info = info,
            Participants = participants.Select(p => new Participant(p.Id, p.Name)).ToList(),
        };
        _lastActivity = now;
        return new StartRoundResult(info, StartRoundError.None);
    }

    private static StartRoundResult Fail(StartRoundError error, string? detail = null) => new(null, error, detail);

    private static string? ValidateQuestion(QuestionDto q)
    {
        if (q.Type == QuestionType.MultipleChoice)
        {
            if (q.Options.Count is < 2 or > 4) return "A multiple-choice question needs 2–4 options.";
            if (q.CorrectOption is not { } c || q.Options.All(o => o.Label != c)) return "The correct option is not one of the options.";
        }
        else if (q.CorrectNumber is null)
        {
            return "An estimation question needs a correct number.";
        }

        return null;
    }

    /// <summary>
    /// Records a player's answer. The first answer is final. <paramref name="receivedAt"/> must be stamped by the
    /// caller at hub-method entry, never taken from the client (plan §7.2, §7.3).
    /// </summary>
    public SubmitResult TrySubmitAnswer(Guid playerId, SubmitAnswerRequest request, DateTimeOffset receivedAt, TimeSpan grace)
    {
        lock (_gate)
        {
            if (_round is not { } round || round.Info.RoundId != request.RoundId)
                return new SubmitResult(SubmitStatus.NoActiveRound);
            if (round.Participants.All(p => p.Id != playerId))
                return new SubmitResult(SubmitStatus.NotParticipant);
            if (round.HasAnswered(playerId))
                return new SubmitResult(SubmitStatus.AlreadyAnswered, round.AnswerCount, round.Participants.Count);
            if (receivedAt > round.Info.Deadline + grace)
                return new SubmitResult(SubmitStatus.TooLate);

            char? option = null;
            decimal? number = null;
            if (round.Info.Question.Type == QuestionType.MultipleChoice)
            {
                if (request.Option is not { } raw) return new SubmitResult(SubmitStatus.InvalidAnswer);
                var label = char.ToUpperInvariant(raw);
                if (round.Info.Question.Options.All(o => o.Label != label)) return new SubmitResult(SubmitStatus.InvalidAnswer);
                option = label;
            }
            else
            {
                if (request.Number is not { } value) return new SubmitResult(SubmitStatus.InvalidAnswer);
                number = value;
            }

            var elapsed = Math.Max(0, (long)(receivedAt - round.Info.StartedAt).TotalMilliseconds);
            round.Add(new AnswerEntry(playerId, option, number, elapsed, round.NextSequence++), receivedAt);
            _lastActivity = receivedAt;

            return new SubmitResult(SubmitStatus.Ok, round.AnswerCount, round.Participants.Count, AllEligibleAnswered(round));
        }
    }

    // Everybody who can still answer has answered: offline or removed players do not hold the round open.
    private bool AllEligibleAnswered(ActiveRound round) =>
        round.Participants.All(p =>
            round.HasAnswered(p.Id) || !(_players.FirstOrDefault(x => x.Id == p.Id)?.IsConnected ?? false));

    /// <summary>The round in progress with its answer progress, or null (for resending it after a reconnect).</summary>
    public OpenRoundView? GetOpenRound()
    {
        lock (_gate)
            return _round is { } r ? new OpenRoundView(r.Info, r.AnswerCount, r.Participants.Count) : null;
    }

    /// <summary>
    /// Evaluates and closes the round (QuestionOpen → Evaluating). Returns null if no such round is open,
    /// which makes it safe for the deadline timer, "all answered" and the host to race each other.
    /// </summary>
    public ClosedRound? CloseRound(Guid? roundId, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (_round is not { } round || (roundId is { } id && id != round.Info.RoundId))
                return null;

            var info = round.Info;
            var correctOption = info.Question.CorrectOption;
            var correctNumber = info.Question.CorrectNumber;

            MapChange? change = null;
            PickView? expansionPick = null;
            var ranking = new List<AnswerResult>();
            var winners = new List<Guid>();

            switch (info.Kind)
            {
                case RoundKind.Conquest:
                    (ranking, winners, change) = ResolveConquestCore(round, correctOption!.Value, now);
                    break;
                case RoundKind.Expansion:
                    (ranking, winners, expansionPick) = ResolveExpansionCore(round, correctNumber!.Value, now);
                    break;
                case RoundKind.Duel:
                    (ranking, winners, change) = ResolveDuelCore(round, correctOption!.Value, now);
                    break;
            }

            var dto = new RoundClosedDto
            {
                RoundId = info.RoundId,
                Kind = info.Kind,
                Ranking = ranking,
                CorrectOption = correctOption,
                CorrectNumber = correctNumber,
                WinnerPlayerIds = winners,
            };

            var answers = ranking
                .Where(r => round.HasAnswered(r.PlayerId))
                .Select(r =>
                {
                    var entry = round.EntryOf(r.PlayerId)!;
                    return new AnswerRecord(r.PlayerId, entry.Option, entry.Number, round.ReceivedAt(r.PlayerId),
                        r.IsCorrect, r.Distance, r.Rank);
                })
                .ToList();

            var record = new RoundRecord(
                info.RoundId, ++_roundSeq, info.Kind, info.Question.Id, info.StartedAt, info.Deadline, now,
                info.AttackerId, info.DefenderId, info.TargetTerritoryIndex,
                winners.Count > 0 ? winners[0] : null, answers);

            _round = null;
            _lastRoundResult = dto;
            _lastActivity = now;

            return new ClosedRound(dto, record, change, expansionPick);
        }
    }

    // Conquest: every claimer who answered correctly keeps their claimed tile; rotate the order; count the round.
    private (List<AnswerResult>, List<Guid>, MapChange?) ResolveConquestCore(ActiveRound round, char correct, DateTimeOffset now)
    {
        var changed = new List<TerritoryDto>();
        var winners = new List<Guid>();
        var ranking = new List<AnswerResult>();

        var rank = 0;
        foreach (var p in round.Participants)
        {
            var entry = round.EntryOf(p.Id);
            bool? isCorrect = entry is null ? null : entry.Option == correct;
            int? won = null;

            if (isCorrect == true && _claims.TryGetValue(p.Id, out var index) && _map?.Get(index) is { IsFree: true })
            {
                var t = AssignCore(index, p.Id, isBase: false);
                changed.Add(t.ToDto());
                winners.Add(p.Id);
                won = index;
            }

            ranking.Add(new AnswerResult
            {
                PlayerId = p.Id, Name = p.Name, Rank = ++rank, IsCorrect = isCorrect,
                ElapsedMs = entry?.ElapsedMs, WonTerritoryIndex = won,
            });
        }

        _claims.Clear();
        _conquestRound++;
        RotateTurnOrderCore();

        var change = new MapChange(changed, ScoreboardCore(), null, true);
        return (ranking, winners, change);
    }

    // Expansion: closest guess wins and becomes the picker of one free tile (plan §4.2 Expansion).
    private (List<AnswerResult>, List<Guid>, PickView?) ResolveExpansionCore(ActiveRound round, decimal correct, DateTimeOffset now)
    {
        var rows = round.Participants
            .Select((p, index) =>
            {
                var entry = round.EntryOf(p.Id);
                decimal? distance = entry?.Number is { } n ? Math.Abs(n - correct) : null;
                return (p, index, entry, distance);
            })
            .OrderBy(r => r.distance is null ? 1 : 0)
            .ThenBy(r => r.distance ?? 0m)
            .ThenBy(r => r.entry?.ElapsedMs ?? long.MaxValue)
            .ThenBy(r => r.entry?.Sequence ?? int.MaxValue)
            .ThenBy(r => r.index)
            .ToList();

        var ranking = rows.Select((r, i) => new AnswerResult
        {
            PlayerId = r.p.Id, Name = r.p.Name, Rank = i + 1,
            IsCorrect = r.distance is null ? null : r.distance == 0m, Distance = r.distance,
        }).ToList();

        // The closest answerer picks a free tile. If nobody answered, nobody picks this round.
        var winner = rows.FirstOrDefault(r => r.distance is not null);
        PickView? pick = null;
        var winners = new List<Guid>();
        if (winner.p is not null)
        {
            winners.Add(winner.p.Id);
            _pickQueue.Clear();
            _pickQueue.Add(winner.p.Id);
            AdvancePickCore(PickPurpose.Expansion, now);
            pick = PickViewCore();
        }

        return (ranking, winners, pick);
    }

    // Duel: attacker right -> hit/capture; defender right -> +DefenderBonus; both right -> draw, each +DrawBonus (plan §4.2/§4.3).
    private (List<AnswerResult>, List<Guid>, MapChange?) ResolveDuelCore(ActiveRound round, char correct, DateTimeOffset now)
    {
        var attackerId = round.Info.AttackerId!.Value;
        var defenderId = round.Info.DefenderId!.Value;
        var targetIndex = round.Info.TargetTerritoryIndex!.Value;

        var attackerRight = round.EntryOf(attackerId)?.Option == correct;
        var defenderRight = round.EntryOf(defenderId)?.Option == correct;

        var changed = new List<TerritoryDto>();
        var winners = new List<Guid>();
        var attackerBonus = 0;
        var defenderBonus = 0;

        if (attackerRight && defenderRight)
        {
            // Draw: no tile change; each duellist gets the draw bonus (plan §4.3).
            attackerBonus = defenderBonus = Settings.DrawBonus;
            AddBonus(attackerId, attackerBonus);
            AddBonus(defenderId, defenderBonus);
        }
        else if (attackerRight)
        {
            changed.AddRange(ApplyAttackHitCore(attackerId, defenderId, targetIndex, now));
            if (changed.Count > 0) winners.Add(attackerId);
        }
        else if (defenderRight)
        {
            // Defender holds and earns the defender bonus (plan §4.3).
            defenderBonus = Settings.DefenderBonus;
            AddBonus(defenderId, defenderBonus);
        }
        // Neither right: nothing happens.

        _duelRound++;
        RotateTurnOrderCore();

        var ranking = new List<AnswerResult>
        {
            new() { PlayerId = attackerId, Name = NameOf(attackerId), Rank = 1,
                IsCorrect = round.EntryOf(attackerId) is null ? null : attackerRight,
                WonTerritoryIndex = winners.Contains(attackerId) ? targetIndex : null,
                BonusAwarded = attackerBonus > 0 ? attackerBonus : null },
            new() { PlayerId = defenderId, Name = NameOf(defenderId), Rank = 2,
                IsCorrect = round.EntryOf(defenderId) is null ? null : defenderRight,
                BonusAwarded = defenderBonus > 0 ? defenderBonus : null },
        };

        var change = new MapChange(changed, ScoreboardCore(), null, true);
        return (ranking, winners, change);
    }

    // A successful attack: a normal tile is taken at once; a base loses one health and is captured on the third hit.
    // Capturing a player's last base frees all of that player's tiles (plan §4.2 Duel).
    private List<TerritoryDto> ApplyAttackHitCore(Guid attackerId, Guid defenderId, int targetIndex, DateTimeOffset now)
    {
        if (_map?.Get(targetIndex) is not { } target || target.OwnerId != defenderId) return [];

        var changed = new List<TerritoryDto>();

        if (target.IsBase && target.Health > 1)
        {
            target.Health--;              // base survives; lost one hit point
            changed.Add(target.ToDto());
            return changed;
        }

        // Normal tile, or a base on its last health: captured as a normal tile.
        var wasBase = target.IsBase;
        target.OwnerId = attackerId;
        target.IsBase = false;
        target.Value = Settings.TerritoryValue;
        target.Health = 1;
        changed.Add(target.ToDto());

        // Losing your last base loses everything (plan §4.2 Duel).
        if (wasBase && _map.OwnedBy(defenderId).All(t => !t.IsBase))
            changed.AddRange(FreeAllOf(defenderId));

        return changed;
    }

    private void AddBonus(Guid playerId, int amount)
    {
        if (_players.FirstOrDefault(p => p.Id == playerId) is { } p) p.BonusPoints += amount;
    }

    private string NameOf(Guid playerId) => _players.FirstOrDefault(p => p.Id == playerId)?.Name ?? "";

    /// <summary>True when one player holds every base left on the map (Duel end condition, plan §4.2).</summary>
    public bool OneBaseHolderRemains()
    {
        lock (_gate)
        {
            if (_map is not { } map) return false;
            var baseOwners = map.Territories.Where(t => t.IsBase && t.OwnerId is not null).Select(t => t.OwnerId!.Value).Distinct().ToList();
            return baseOwners.Count == 1;
        }
    }

    /// <summary>Whether the current phase's round budget is used up.</summary>
    public bool PhaseRoundsExhausted
    {
        get
        {
            lock (_gate) return _phase switch
            {
                GamePhase.Conquest => _conquestRound >= Settings.ConquestRounds,
                GamePhase.Duel => _duelRound >= Settings.DuelRounds,
                _ => false,
            };
        }
    }
}
