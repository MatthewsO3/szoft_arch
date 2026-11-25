using QuizGame.Contracts;
using QuizGame.Server.Domain.Evaluation;

namespace QuizGame.Server.Domain;

public enum StartRoundError
{
    None,
    RoundActive,
    InvalidRound,
    NoPlayers,
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
    /// <summary>What a player's phone may see: never the correct answer, never the question text (D11).</summary>
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

public sealed record ClosedRound(RoundClosedDto Result, RoundRecord Record);

/// <summary>Progress of the round in progress, for resending it to a reconnecting client.</summary>
public sealed record OpenRoundView(RoundInfo Round, int Answered, int Total);

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

    /// <summary>
    /// Seam for the map (M3): returns the owner of a territory, or null if it is free / unknown.
    /// Battle rounds need it to find the defender. Until the map exists nothing is owned.
    /// </summary>
    public Func<int, Guid?> TerritoryOwner { get; set; } = _ => null;

    public bool HasOpenRound
    {
        get { lock (_gate) return _round is not null; }
    }

    /// <summary>
    /// Opens a question round (plan §4.2 sub-state machine: Idle → QuestionOpen).
    /// Phase gating (BaseOrder only in BaseSelection, Grab/Battle only in Conquest) arrives with the
    /// phase machine in M3; until then a round may start in any phase while none is open.
    /// </summary>
    public StartRoundResult TryStartRound(QuestionDto question, SendQuestionRequest request, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (_round is not null)
                return Fail(StartRoundError.RoundActive);
            if (_players.Count == 0)
                return Fail(StartRoundError.NoPlayers);

            var malformed = ValidateQuestion(question);
            if (malformed is not null)
                return Fail(StartRoundError.InvalidRound, malformed);

            var expected = request.Kind == RoundKind.Grab ? QuestionType.MultipleChoice : QuestionType.Estimation;
            if (question.Type != expected)
                return Fail(StartRoundError.InvalidRound, $"A {request.Kind} round needs a {expected} question.");

            List<PlayerState> participants = _players.ToList();
            Guid? attacker = null, defender = null;
            int? target = null;

            if (request.Kind == RoundKind.Battle)
            {
                if (request.AttackerId is not { } attackerId || request.TargetTerritoryIndex is not { } targetIndex)
                    return Fail(StartRoundError.InvalidRound, "A battle needs an attacker and a target territory.");

                var owner = TerritoryOwner(targetIndex);
                if (owner is not { } defenderId)
                    return Fail(StartRoundError.InvalidRound, "The target territory has no owner to defend it.");
                if (defenderId == attackerId)
                    return Fail(StartRoundError.InvalidRound, "A player cannot attack their own territory.");

                var attackerState = _players.FirstOrDefault(p => p.Id == attackerId);
                var defenderState = _players.FirstOrDefault(p => p.Id == defenderId);
                if (attackerState is null || defenderState is null)
                    return Fail(StartRoundError.InvalidRound, "The attacker or defender is not in the room.");

                attacker = attackerId;
                defender = defenderId;
                target = targetIndex;
                participants = [defenderState, attackerState];
            }

            var seconds = question.TimeLimitSeconds > 0 ? question.TimeLimitSeconds : Settings.DefaultQuestionSeconds;
            seconds = Math.Max(1, seconds);

            var info = new RoundInfo(
                Guid.NewGuid(), request.Kind, question, now, now.AddSeconds(seconds),
                participants.Select(p => p.Id).ToList(), attacker, defender, target);

            _round = new ActiveRound
            {
                Info = info,
                Participants = participants.Select(p => new Participant(p.Id, p.Name)).ToList(),
            };
            _lastActivity = now;
            return new StartRoundResult(info, StartRoundError.None);
        }

        static StartRoundResult Fail(StartRoundError error, string? detail = null) => new(null, error, detail);
    }

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
    /// Records a player's answer. The first answer is final (D7). <paramref name="receivedAt"/> must be stamped
    /// by the caller at hub-method entry, never taken from the client (plan §7.2, §7.3).
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
            var outcome = RoundEvaluator.Evaluate(
                info.Kind, info.Question, round.Participants, round.Entries(),
                Settings.WinnersPerRound, info.DefenderId);

            var dto = new RoundClosedDto
            {
                RoundId = info.RoundId,
                Kind = info.Kind,
                Ranking = outcome.Ranking,
                CorrectOption = info.Question.CorrectOption,
                CorrectNumber = info.Question.CorrectNumber,
                WinnerPlayerIds = outcome.Winners,
            };

            var answers = outcome.Ranking
                .Where(r => round.HasAnswered(r.PlayerId))
                .Select(r =>
                {
                    var entry = round.Entries().First(e => e.PlayerId == r.PlayerId);
                    return new AnswerRecord(r.PlayerId, entry.Option, entry.Number, round.ReceivedAt(r.PlayerId),
                        r.IsCorrect, r.Distance, r.Rank);
                })
                .ToList();

            var record = new RoundRecord(
                info.RoundId, ++_roundSeq, info.Kind, info.Question.Id, info.StartedAt, info.Deadline, now,
                info.AttackerId, info.DefenderId, info.TargetTerritoryIndex,
                outcome.Winners.Count > 0 ? outcome.Winners[0] : null, answers);

            _round = null;
            _lastActivity = now;
            return new ClosedRound(dto, record);
        }
    }
}
