using System.Globalization;
using QuizGame.Contracts;

namespace QuizGame.Host.ViewModels;

/// <summary>Row projection of <see cref="QuestionDto"/> for the question bank DataGrid.</summary>
public sealed class QuestionItemViewModel : ViewModelBase
{
    private bool _isSelectable = true;
    public bool IsSelectable
    {
        get => _isSelectable;
        set => Set(ref _isSelectable, value);
    }

    public QuestionItemViewModel(QuestionDto model, string categoryName)
    {
        Model = model;
        CategoryName = categoryName;
        AnswerSummary = FormatAnswerSummary(model);
    }

    public QuestionDto Model { get; }
    public Guid Id => Model.Id;
    public string Text => Model.Text;
    public int CategoryId => Model.CategoryId;
    public string CategoryName { get; }
    public QuestionType Type => Model.Type;
    public string TypeDisplay => Model.Type == QuestionType.MultipleChoice ? "Multiple choice" : "Estimation";
    public int Difficulty => Model.Difficulty;

    public string DifficultyDisplay => Model.Difficulty switch
    {
        1 => "1 – Easy",
        2 => "2 – Medium",
        3 => "3 – Hard",
        _ => Model.Difficulty.ToString(CultureInfo.InvariantCulture)
    };

    public int TimeLimitSeconds => Model.TimeLimitSeconds;
    public string TimeLimitDisplay => $"{Model.TimeLimitSeconds} s";
    public string AnswerSummary { get; }

    private static string FormatAnswerSummary(QuestionDto model)
    {
        if (model.Type == QuestionType.MultipleChoice)
        {
            if (model.CorrectOption is not char correct) return "—";
            var upper = char.ToUpperInvariant(correct);
            var match = model.Options.FirstOrDefault(o => char.ToUpperInvariant(o.Label) == upper);
            return match is not null && !string.IsNullOrWhiteSpace(match.Text)
                ? $"{upper}: {match.Text}"
                : $"{upper}";
        }

        if (model.CorrectNumber is not decimal number) return "—";
        var formatted = number.ToString("G29", CultureInfo.InvariantCulture);
        return string.IsNullOrWhiteSpace(model.Unit) ? formatted : $"{formatted} {model.Unit.Trim()}";
    }
}
