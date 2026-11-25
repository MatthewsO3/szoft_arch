using QuizGame.Contracts;
using QuizGame.Server.Services;
using Xunit;

namespace QuizGame.Domain.Tests;

/// <summary>
/// Unit tests for the type-specific question rules (plan §5.2): a multiple-choice question has 2–4 options with
/// exactly one correct and no number; an estimation question has a number and no options.
/// </summary>
public class QuestionValidationTests
{
    private static QuestionUpsertRequest MultipleChoice(params (char Label, string Text, bool Correct)[] options) => new()
    {
        CategoryId = 1,
        Type = QuestionType.MultipleChoice,
        Text = "Which one?",
        Difficulty = 1,
        TimeLimitSeconds = 20,
        Options = options.Select(o => new OptionUpsert(o.Label, o.Text, o.Correct)).ToList(),
    };

    private static QuestionUpsertRequest Estimation(decimal? correctNumber = 42, string? unit = null) => new()
    {
        CategoryId = 1,
        Type = QuestionType.Estimation,
        Text = "How many?",
        Difficulty = 1,
        TimeLimitSeconds = 20,
        CorrectNumber = correctNumber,
        Unit = unit,
    };

    [Fact]
    public void Valid_multiple_choice_passes()
    {
        var r = MultipleChoice(('A', "yes", true), ('B', "no", false));
        Assert.Null(QuestionValidation.Validate(r));
    }

    [Fact]
    public void Valid_four_option_multiple_choice_passes()
    {
        var r = MultipleChoice(('A', "a", false), ('B', "b", true), ('C', "c", false), ('D', "d", false));
        Assert.Null(QuestionValidation.Validate(r));
    }

    [Fact]
    public void Valid_estimation_passes()
    {
        Assert.Null(QuestionValidation.Validate(Estimation(100, "km")));
    }

    [Fact]
    public void Estimation_without_number_fails()
    {
        Assert.NotNull(QuestionValidation.Validate(Estimation(correctNumber: null)));
    }

    [Fact]
    public void Estimation_with_options_fails()
    {
        var r = Estimation() with { Options = [new OptionUpsert('A', "x", true)] };
        Assert.NotNull(QuestionValidation.Validate(r));
    }

    [Fact]
    public void Multiple_choice_with_a_number_fails()
    {
        var r = MultipleChoice(('A', "a", true), ('B', "b", false)) with { CorrectNumber = 5 };
        Assert.NotNull(QuestionValidation.Validate(r));
    }

    [Fact]
    public void Multiple_choice_with_a_unit_fails()
    {
        var r = MultipleChoice(('A', "a", true), ('B', "b", false)) with { Unit = "km" };
        Assert.NotNull(QuestionValidation.Validate(r));
    }

    [Theory]
    [InlineData(0)] // no correct option
    [InlineData(2)] // two correct options
    public void Multiple_choice_must_have_exactly_one_correct(int correctCount)
    {
        var options = new[]
        {
            ('A', "a", correctCount >= 1),
            ('B', "b", correctCount >= 2),
            ('C', "c", false),
        };
        Assert.NotNull(QuestionValidation.Validate(MultipleChoice(options)));
    }

    [Fact]
    public void Multiple_choice_with_one_option_fails()
    {
        Assert.NotNull(QuestionValidation.Validate(MultipleChoice(('A', "only", true))));
    }

    [Fact]
    public void Multiple_choice_with_five_options_fails()
    {
        var r = MultipleChoice(
            ('A', "a", true), ('B', "b", false), ('C', "c", false), ('D', "d", false), ('E', "e", false));
        Assert.NotNull(QuestionValidation.Validate(r));
    }

    [Fact]
    public void Multiple_choice_with_duplicate_labels_fails()
    {
        var r = MultipleChoice(('A', "a", true), ('A', "b", false));
        Assert.NotNull(QuestionValidation.Validate(r));
    }

    [Fact]
    public void Multiple_choice_with_invalid_label_fails()
    {
        var r = MultipleChoice(('A', "a", true), ('Z', "b", false));
        Assert.NotNull(QuestionValidation.Validate(r));
    }

    [Fact]
    public void Lowercase_option_labels_are_accepted()
    {
        var r = MultipleChoice(('a', "a", true), ('b', "b", false));
        Assert.Null(QuestionValidation.Validate(r));
    }

    [Fact]
    public void Option_with_blank_text_fails()
    {
        var r = MultipleChoice(('A', "a", true), ('B', "   ", false));
        Assert.NotNull(QuestionValidation.Validate(r));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_question_text_fails(string text)
    {
        var r = Estimation() with { Text = text };
        Assert.NotNull(QuestionValidation.Validate(r));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    public void Difficulty_out_of_range_fails(int difficulty)
    {
        var r = Estimation() with { Difficulty = difficulty };
        Assert.NotNull(QuestionValidation.Validate(r));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void Difficulty_within_range_passes(int difficulty)
    {
        var r = Estimation() with { Difficulty = difficulty };
        Assert.Null(QuestionValidation.Validate(r));
    }

    [Theory]
    [InlineData(QuestionValidation.MinTimeLimitSeconds - 1)]
    [InlineData(QuestionValidation.MaxTimeLimitSeconds + 1)]
    public void Time_limit_out_of_range_fails(int seconds)
    {
        var r = Estimation() with { TimeLimitSeconds = seconds };
        Assert.NotNull(QuestionValidation.Validate(r));
    }

    [Fact]
    public void Nonpositive_category_fails()
    {
        var r = Estimation() with { CategoryId = 0 };
        Assert.NotNull(QuestionValidation.Validate(r));
    }

    [Fact]
    public void Question_text_over_the_limit_fails()
    {
        var r = Estimation() with { Text = new string('x', QuestionValidation.MaxTextLength + 1) };
        Assert.NotNull(QuestionValidation.Validate(r));
    }
}
