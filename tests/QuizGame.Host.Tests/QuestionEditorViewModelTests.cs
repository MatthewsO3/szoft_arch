using QuizGame.Contracts;
using QuizGame.Host.Services;
using QuizGame.Host.ViewModels;

namespace QuizGame.Host.Tests;

public class QuestionEditorViewModelTests
{
    private static readonly IReadOnlyList<CategoryDto> SampleCategories =
    [
        new(1, "History"),
        new(2, "Geography")
    ];

    [Fact]
    public void NewMode_SetsExpectedDefaults()
    {
        var vm = new QuestionEditorViewModel(SampleCategories, null, req => Task.FromResult(new QuestionDto()));

        Assert.Equal("New question", vm.DialogTitle);
        Assert.Equal("Create question", vm.SaveButtonText);
        Assert.Equal(SampleCategories[0], vm.SelectedCategory);
        Assert.True(vm.IsMultipleChoice);
        Assert.False(vm.IsEstimation);
        Assert.Equal("20", vm.TimeLimitText);
        Assert.Equal(4, vm.Options.Count);
        Assert.True(vm.Options[0].IsCorrect);
        Assert.False(vm.Options[1].IsCorrect);
        Assert.False(vm.Options[2].IsCorrect);
        Assert.False(vm.Options[3].IsCorrect);
    }

    [Fact]
    public void SettingOptionIsCorrect_EnforcesMutualExclusionAcrossAllFourOptions()
    {
        var vm = new QuestionEditorViewModel(SampleCategories, null, _ => Task.FromResult(new QuestionDto()));

        vm.Options[2].IsCorrect = true;
        Assert.False(vm.Options[0].IsCorrect);
        Assert.False(vm.Options[1].IsCorrect);
        Assert.True(vm.Options[2].IsCorrect);
        Assert.False(vm.Options[3].IsCorrect);

        vm.Options[1].IsCorrect = true;
        Assert.False(vm.Options[0].IsCorrect);
        Assert.True(vm.Options[1].IsCorrect);
        Assert.False(vm.Options[2].IsCorrect);
        Assert.False(vm.Options[3].IsCorrect);
    }

    [Fact]
    public void EditMode_PrefillsMultipleChoiceFields()
    {
        var existing = new QuestionDto
        {
            Id = Guid.NewGuid(),
            CategoryId = 2,
            Type = QuestionType.MultipleChoice,
            Text = "Capital of France?",
            Difficulty = 3,
            TimeLimitSeconds = 45,
            Options =
            [
                new(Guid.NewGuid(), 'A', "Berlin"),
                new(Guid.NewGuid(), 'B', "Paris"),
                new(Guid.NewGuid(), 'C', "Madrid")
            ],
            CorrectOption = 'B'
        };

        var vm = new QuestionEditorViewModel(SampleCategories, existing, _ => Task.FromResult(existing));

        Assert.Equal("Edit question", vm.DialogTitle);
        Assert.Equal("Save changes", vm.SaveButtonText);
        Assert.Equal(2, vm.SelectedCategory!.Id);
        Assert.Equal(QuestionType.MultipleChoice, vm.SelectedType);
        Assert.Equal("Capital of France?", vm.QuestionText);
        Assert.Equal(3, vm.SelectedDifficulty);
        Assert.Equal("45", vm.TimeLimitText);
        Assert.Equal("Berlin", vm.Options[0].Text);
        Assert.Equal("Paris", vm.Options[1].Text);
        Assert.Equal("Madrid", vm.Options[2].Text);
        Assert.Equal("", vm.Options[3].Text);
        Assert.False(vm.Options[0].IsCorrect);
        Assert.True(vm.Options[1].IsCorrect);
    }

    [Fact]
    public void EditMode_PrefillsEstimationFields()
    {
        var existing = FakeQuestionBankApi.SampleEstimationQuestion();

        var vm = new QuestionEditorViewModel(SampleCategories, existing, _ => Task.FromResult(existing));

        Assert.True(vm.IsEstimation);
        Assert.False(vm.IsMultipleChoice);
        Assert.Equal(2, vm.SelectedCategory!.Id);
        Assert.Equal("384400", vm.CorrectNumberText);
        Assert.Equal("km", vm.Unit);
    }

    [Fact]
    public void TryBuildRequest_MultipleChoiceWithTwoOptions_Succeeds()
    {
        var vm = new QuestionEditorViewModel(SampleCategories, null, _ => Task.FromResult(new QuestionDto()))
        {
            QuestionText = "  Is Earth round?  ",
            TimeLimitText = "25"
        };
        vm.SelectedDifficultyChoice = vm.Difficulties[1];
        vm.Options[0].Text = " Yes ";
        vm.Options[1].Text = " No ";
        vm.Options[0].IsCorrect = true;

        var ok = vm.TryBuildRequest(out var req, out var error);

        Assert.True(ok);
        Assert.Equal("", error);
        Assert.NotNull(req);
        Assert.Equal(QuestionType.MultipleChoice, req!.Type);
        Assert.Equal("Is Earth round?", req.Text);
        Assert.Equal(2, req.Difficulty);
        Assert.Equal(25, req.TimeLimitSeconds);
        Assert.Equal(2, req.Options.Count);
        Assert.Equal(new OptionUpsert('A', "Yes", true), req.Options[0]);
        Assert.Equal(new OptionUpsert('B', "No", false), req.Options[1]);
        Assert.Null(req.CorrectNumber);
        Assert.Null(req.Unit);
    }

    [Theory]
    [InlineData("1991", "1991")]
    [InlineData("3.14", "3.14")]
    [InlineData("3,14", "3.14")]
    [InlineData(" -42.5 ", "-42.5")]
    public void TryBuildRequest_EstimationParsesInvariantAndCommaDecimals(string input, string expectedInvariant)
    {
        var vm = new QuestionEditorViewModel(SampleCategories, null, _ => Task.FromResult(new QuestionDto()));
        vm.SelectedTypeChoice = vm.QuestionTypes[1];
        vm.QuestionText = "Value?";
        vm.CorrectNumberText = input;
        vm.Unit = "  kg  ";

        var ok = vm.TryBuildRequest(out var req, out var error);

        Assert.True(ok, error);
        Assert.NotNull(req);
        Assert.Equal(QuestionType.Estimation, req!.Type);
        Assert.Empty(req.Options);
        Assert.Equal(decimal.Parse(expectedInvariant, System.Globalization.CultureInfo.InvariantCulture), req.CorrectNumber);
        Assert.Equal("kg", req.Unit);
    }

    [Fact]
    public void TryBuildRequest_WhitespaceUnit_NormalizesToNull()
    {
        var vm = new QuestionEditorViewModel(SampleCategories, null, _ => Task.FromResult(new QuestionDto()));
        vm.SelectedTypeChoice = vm.QuestionTypes[1];
        vm.QuestionText = "Year?";
        vm.CorrectNumberText = "1989";
        vm.Unit = "   ";

        var ok = vm.TryBuildRequest(out var req, out _);

        Assert.True(ok);
        Assert.Null(req!.Unit);
    }

    [Fact]
    public void TryBuildRequest_ValidatesAllErrorConditions()
    {
        // Missing category
        var noCategoryVm = new QuestionEditorViewModel([], null, _ => Task.FromResult(new QuestionDto()));
        Assert.False(noCategoryVm.TryBuildRequest(out _, out var err));
        Assert.Contains("category", err, StringComparison.OrdinalIgnoreCase);

        // Blank question text
        var vm = new QuestionEditorViewModel(SampleCategories, null, _ => Task.FromResult(new QuestionDto()));
        Assert.False(vm.TryBuildRequest(out _, out err));
        Assert.Contains("question text", err, StringComparison.OrdinalIgnoreCase);

        // Invalid time limit
        vm.QuestionText = "Q";
        vm.TimeLimitText = "3";
        Assert.False(vm.TryBuildRequest(out _, out err));
        Assert.Contains("5 and 300", err);

        vm.TimeLimitText = "301";
        Assert.False(vm.TryBuildRequest(out _, out err));
        Assert.Contains("5 and 300", err);

        vm.TimeLimitText = "20";

        // Fewer than 2 options
        vm.Options[0].Text = "Only A";
        Assert.False(vm.TryBuildRequest(out _, out err));
        Assert.Contains("A and B", err);

        // Gap in options (A + C populated, B empty)
        vm.Options[2].Text = "Option C";
        Assert.False(vm.TryBuildRequest(out _, out err));
        Assert.Contains("without leaving blank gaps", err);

        // Correct option marked on empty option D
        vm.Options[1].Text = "Option B";
        vm.Options[2].Text = "";
        vm.Options[3].IsCorrect = true;
        Assert.False(vm.TryBuildRequest(out _, out err));
        Assert.Contains("non-empty option", err);

        // Estimation blank number & invalid number
        vm.SelectedTypeChoice = vm.QuestionTypes[1];
        Assert.False(vm.TryBuildRequest(out _, out err));
        Assert.Contains("numeric answer", err, StringComparison.OrdinalIgnoreCase);

        vm.CorrectNumberText = "1,2,3";
        Assert.False(vm.TryBuildRequest(out _, out err));
        Assert.Contains("valid number", err, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SaveCommand_SuccessSetsSavedQuestionAndClosesDialog()
    {
        var savedDto = FakeQuestionBankApi.SampleEstimationQuestion();
        QuestionUpsertRequest? sent = null;
        bool? closeResult = null;

        var vm = new QuestionEditorViewModel(SampleCategories, null, req =>
        {
            sent = req;
            return Task.FromResult(savedDto);
        });
        vm.RequestClose += res => closeResult = res;

        vm.SelectedTypeChoice = vm.QuestionTypes[1];
        vm.QuestionText = "Moon distance?";
        vm.CorrectNumberText = "384400";
        vm.Unit = "km";

        vm.SaveCommand.Execute(null);
        await Task.Delay(20);

        Assert.NotNull(sent);
        Assert.Equal(savedDto, vm.SavedQuestion);
        Assert.True(closeResult);
        Assert.Equal("", vm.ValidationError);
    }

    [Fact]
    public async Task SaveCommand_ServerErrorKeepsDialogOpenAndShowsMessage()
    {
        bool? closeResult = null;
        var vm = new QuestionEditorViewModel(
            SampleCategories,
            null,
            _ => Task.FromException<QuestionDto>(new ApiException("Duplicate question text.", 409)));
        vm.RequestClose += res => closeResult = res;

        vm.QuestionText = "Q1";
        vm.Options[0].Text = "A";
        vm.Options[1].Text = "B";

        vm.SaveCommand.Execute(null);
        await Task.Delay(20);

        Assert.Null(closeResult);
        Assert.Null(vm.SavedQuestion);
        Assert.False(vm.IsSaving);
        Assert.Equal("Duplicate question text.", vm.ValidationError);
    }

    [Fact]
    public void CancelCommand_FiresRequestCloseFalse()
    {
        bool? closeResult = null;
        var vm = new QuestionEditorViewModel(SampleCategories, null, _ => Task.FromResult(new QuestionDto()));
        vm.RequestClose += res => closeResult = res;

        vm.CancelCommand.Execute(null);

        Assert.False(closeResult);
    }
}
