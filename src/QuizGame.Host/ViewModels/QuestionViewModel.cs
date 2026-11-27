using System.Diagnostics;
using System.Globalization;
using System.Windows.Threading;
using QuizGame.Contracts;

namespace QuizGame.Host.ViewModels;

/// <summary>Shared by the host and projector; all methods run on the UI thread.</summary>
public sealed class QuestionViewModel : ViewModelBase, IDisposable
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private QuestionForHost? _round;
    private RoundClosedDto? _result;
    private DateTimeOffset _serverTime = DateTimeOffset.UtcNow;
    private long _anchor = Stopwatch.GetTimestamp();
    private string _countdown = "";
    private string _connectionStatus = "Not connected";
    private string _duelSummary = "";
    private double _timeRemainingFraction;

    public QuestionViewModel() => _timer.Tick += (_, _) => UpdateCountdown();

    public string DuelSummary { get => _duelSummary; private set => Set(ref _duelSummary, value); }
    public string Text => _round?.Question.Text ?? (_result is null ? "Waiting for a question…" : "Round results");
    public string Prompt => _round?.Question.Type == QuestionType.Estimation
        ? $"Enter your estimate on your phone{(_round.Question.Unit is { Length: > 0 } unit ? $" ({unit})" : "")}" : "";
    public IReadOnlyList<string> Options => _round?.Question.Options.Select(option =>
        $"{option.Label} — {option.Text}{(_result?.CorrectOption == option.Label ? "   ✓ Correct" : "")}").ToArray() ?? [];
    public string Countdown { get => _countdown; private set => Set(ref _countdown, value); }
    public double TimeRemainingFraction { get => _timeRemainingFraction; private set => Set(ref _timeRemainingFraction, value); }
    public string ConnectionStatus { get => _connectionStatus; set => Set(ref _connectionStatus, value); }
    public string CorrectAnswer => _result is null ? "" : $"Correct answer: {_result.CorrectOption?.ToString() ?? _result.CorrectNumber?.ToString(CultureInfo.CurrentCulture) ?? "—"}";
    public IReadOnlyList<string> Ranking => _result?.Ranking.Select(row =>
    {
        var baseText = $"{row.Rank}. {row.Name}";
        if (_result.Kind == RoundKind.Expansion)
        {
            return baseText +
                (row.Distance is { } distance ? $" — distance {distance}" : " — no answer") +
                (_result.WinnerPlayerIds.Contains(row.PlayerId) ? "   ★ Winner" : "");
        }

        var correctness = row.IsCorrect switch
        {
            true => row.ElapsedMs is { } ms ? $" — ✓ Correct ({ms} ms)" : " — ✓ Correct",
            false => " — ✗ Incorrect",
            null => " — no answer",
        };

        if (_result.Kind == RoundKind.Duel)
        {
            var outcome = row.WonTerritoryIndex is { } won ? $"   ⚔ Attack landed (tile #{won + 1})" : "";
            var bonus = row.BonusAwarded is > 0 ? $"   +{row.BonusAwarded} bonus" : "";
            return baseText + correctness + outcome + bonus;
        }

        var claimed = row.WonTerritoryIndex is { } wonTile ? $"   ★ Won tile #{wonTile + 1}" : "";
        return baseText + correctness + claimed;
    }).ToArray() ?? [];

    public void SyncClock(DateTimeOffset utc, TimeSpan latency = default)
    {
        _serverTime = utc + latency;
        _anchor = Stopwatch.GetTimestamp();
        UpdateCountdown();
    }

    public void Start(QuestionForHost round, string? duelSummary = null)
    {
        _round = round;
        _result = null;
        DuelSummary = duelSummary ?? "";
        _timer.Start();
        Refresh();
    }

    public void Close(RoundClosedDto result)
    {
        if (_round is not null && _round.RoundId != result.RoundId) return;
        _result = result;
        _timer.Stop();
        Refresh();
    }

    public void ApplyState(RoomStateDto state)
    {
        SyncClock(state.ServerUtcNow);
        Reset();
        if (state.HostQuestion is { } question)
        {
            string? duelSummary = null;
            if (state.CurrentRound is { Kind: RoundKind.Duel, AttackerId: { } att, DefenderId: { } def, TargetTerritoryIndex: { } target })
            {
                var attacker = state.Players.FirstOrDefault(p => p.Id == att)?.Name ?? "Attacker";
                var defender = state.Players.FirstOrDefault(p => p.Id == def)?.Name ?? "Defender";
                var tile = state.Map?.Territories.FirstOrDefault(t => t.Index == target);
                duelSummary = tile?.IsBase == true
                    ? $"{attacker} attacks {defender}'s base (Tile #{target + 1}, {tile.Health} HP)"
                    : $"{attacker} attacks {defender} (Tile #{target + 1})";
            }
            Start(question, duelSummary);
        }
        else if (state.LastRoundResult is { } result)
        {
            Close(result);
        }
    }

    public void Reset()
    {
        _timer.Stop();
        _round = null;
        _result = null;
        DuelSummary = "";
        Refresh();
    }

    private void Refresh()
    {
        Raise(nameof(Text));
        Raise(nameof(Prompt));
        Raise(nameof(Options));
        Raise(nameof(CorrectAnswer));
        Raise(nameof(Ranking));
        UpdateCountdown();
    }

    private void UpdateCountdown()
    {
        TimeRemainingFraction = 0;
        if (_result is not null) Countdown = "Round closed";
        else if (_round is null) Countdown = "";
        else
        {
            var remaining = _round.ServerDeadlineUtc - (_serverTime + Stopwatch.GetElapsedTime(_anchor));
            TimeRemainingFraction = Math.Clamp(remaining.TotalSeconds / Math.Max(1, _round.Question.TimeLimitSeconds), 0, 1);
            Countdown = remaining <= TimeSpan.Zero ? "Time is up — waiting for results" : $"{Math.Ceiling(remaining.TotalSeconds)} s";
        }
    }

    public void Dispose() => _timer.Stop();
}
