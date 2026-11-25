using System.ComponentModel;
using System.Globalization;
using System.Windows.Input;
using QuizGame.Contracts;

namespace QuizGame.Host.ViewModels;

/// <summary>View model for creating or editing a single question (plan §9.2).</summary>
public sealed class QuestionEditorViewModel : ViewModelBase
{
    private static readonly char[] OptionLabels = ['A', 'B', 'C', 'D'];

    private readonly Func<QuestionUpsertRequest, Task<QuestionDto>> _onSave;
    private readonly bool _isEdit;
    private bool _updatingCorrectOption;

    private CategoryDto? _selectedCategory;
    private FilterChoice<QuestionType> _selectedTypeChoice;
    private string _questionText;
    private FilterChoice<int> _selectedDifficultyChoice;
    private string _timeLimitText;
    private string _correctNumberText;
    private string _unit;
    private string _validationError = "";
    private bool _isSaving;

    public QuestionEditorViewModel(
        IReadOnlyList<CategoryDto> categories,
        QuestionDto? existing,
        Func<QuestionUpsertRequest, Task<QuestionDto>> onSave)
    {
        _onSave = onSave ?? throw new ArgumentNullException(nameof(onSave));
        _isEdit = existing is not null;
        Categories = categories ?? [];

        QuestionTypes =
        [
            new FilterChoice<QuestionType>("Multiple choice", QuestionType.MultipleChoice),
            new FilterChoice<QuestionType>("Estimation", QuestionType.Estimation)
        ];

        Difficulties =
        [
            new FilterChoice<int>("1 – Easy", 1),
            new FilterChoice<int>("2 – Medium", 2),
            new FilterChoice<int>("3 – Hard", 3)
        ];

        _selectedCategory = existing is not null
            ? Categories.FirstOrDefault(c => c.Id == existing.CategoryId) ?? Categories.FirstOrDefault()
            : Categories.FirstOrDefault();

        _selectedTypeChoice = QuestionTypes.FirstOrDefault(t => t.Value == (existing?.Type ?? QuestionType.MultipleChoice))
            ?? QuestionTypes[0];

        _questionText = existing?.Text ?? "";
        _selectedDifficultyChoice = Difficulties.FirstOrDefault(d => d.Value == (existing?.Difficulty ?? 1))
            ?? Difficulties[0];
        _timeLimitText = (existing?.TimeLimitSeconds ?? 20).ToString(CultureInfo.InvariantCulture);

        _correctNumberText = existing?.Type == QuestionType.Estimation && existing.CorrectNumber is decimal num
            ? num.ToString("G29", CultureInfo.InvariantCulture)
            : "";
        _unit = existing?.Type == QuestionType.Estimation ? existing.Unit ?? "" : "";

        var existingCorrect = existing?.Type == QuestionType.MultipleChoice && existing.CorrectOption is char co
            ? char.ToUpperInvariant(co)
            : (char?)null;

        var options = new List<QuestionOptionViewModel>(OptionLabels.Length);
        foreach (var label in OptionLabels)
        {
            var match = existing?.Type == QuestionType.MultipleChoice
                ? existing.Options.FirstOrDefault(o => char.ToUpperInvariant(o.Label) == label)
                : null;
            var isCorrect = existing?.Type == QuestionType.MultipleChoice
                ? existingCorrect == label
                : label == 'A';
            var optionVm = new QuestionOptionViewModel(label, match?.Text ?? "", isCorrect);
            optionVm.PropertyChanged += Option_PropertyChanged;
            options.Add(optionVm);
        }

        Options = options;

        SaveCommand = new AsyncRelayCommand(_ => SaveAsync(), _ => !IsSaving, ex => ValidationError = ex.Message);
        CancelCommand = new RelayCommand(_ => RequestClose?.Invoke(false), _ => !IsSaving);
    }

    public event Action<bool>? RequestClose;

    public string DialogTitle => _isEdit ? "Edit question" : "New question";
    public string SaveButtonText => _isEdit ? "Save changes" : "Create question";

    public IReadOnlyList<CategoryDto> Categories { get; }
    public IReadOnlyList<FilterChoice<QuestionType>> QuestionTypes { get; }
    public IReadOnlyList<FilterChoice<int>> Difficulties { get; }
    public IReadOnlyList<QuestionOptionViewModel> Options { get; }

    public CategoryDto? SelectedCategory
    {
        get => _selectedCategory;
        set => Set(ref _selectedCategory, value);
    }

    public FilterChoice<QuestionType> SelectedTypeChoice
    {
        get => _selectedTypeChoice;
        set
        {
            if (!Set(ref _selectedTypeChoice, value)) return;
            Raise(nameof(SelectedType));
            Raise(nameof(IsMultipleChoice));
            Raise(nameof(IsEstimation));
            ValidationError = "";
        }
    }

    public QuestionType SelectedType => SelectedTypeChoice.Value;
    public bool IsMultipleChoice => SelectedType == QuestionType.MultipleChoice;
    public bool IsEstimation => SelectedType == QuestionType.Estimation;

    public string QuestionText
    {
        get => _questionText;
        set => Set(ref _questionText, value);
    }

    public FilterChoice<int> SelectedDifficultyChoice
    {
        get => _selectedDifficultyChoice;
        set
        {
            if (Set(ref _selectedDifficultyChoice, value))
                Raise(nameof(SelectedDifficulty));
        }
    }

    public int SelectedDifficulty => SelectedDifficultyChoice.Value;

    public string TimeLimitText
    {
        get => _timeLimitText;
        set => Set(ref _timeLimitText, value);
    }

    public string CorrectNumberText
    {
        get => _correctNumberText;
        set => Set(ref _correctNumberText, value);
    }

    public string Unit
    {
        get => _unit;
        set => Set(ref _unit, value);
    }

    public string ValidationError
    {
        get => _validationError;
        set => Set(ref _validationError, value);
    }

    public bool IsSaving
    {
        get => _isSaving;
        private set
        {
            if (!Set(ref _isSaving, value)) return;
            ((AsyncRelayCommand)SaveCommand).RaiseCanExecuteChanged();
            ((RelayCommand)CancelCommand).RaiseCanExecuteChanged();
        }
    }

    public QuestionDto? SavedQuestion { get; private set; }

    public ICommand SaveCommand { get; }
    public ICommand CancelCommand { get; }

    /// <summary>Validates form inputs and constructs the DTO payload for the API.</summary>
    public bool TryBuildRequest(out QuestionUpsertRequest? request, out string error)
    {
        request = null;

        if (SelectedCategory is null)
        {
            error = "Select a category.";
            return false;
        }

        var text = QuestionText.Trim();
        if (text.Length == 0)
        {
            error = "Enter the question text.";
            return false;
        }

        var difficulty = SelectedDifficulty;
        if (difficulty is < 1 or > 3)
        {
            error = "Difficulty must be 1, 2, or 3.";
            return false;
        }

        if (!int.TryParse(TimeLimitText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var timeLimit) ||
            timeLimit is < 5 or > 300)
        {
            error = "Time limit must be a whole number between 5 and 300 seconds.";
            return false;
        }

        if (SelectedType == QuestionType.MultipleChoice)
        {
            var seenEmpty = false;
            var populated = new List<OptionUpsert>();

            foreach (var option in Options)
            {
                var optionText = option.Text.Trim();
                if (optionText.Length == 0)
                {
                    seenEmpty = true;
                    continue;
                }

                if (seenEmpty)
                {
                    error = "Fill options in order starting from A without leaving blank gaps.";
                    return false;
                }

                populated.Add(new OptionUpsert(option.Label, optionText, option.IsCorrect));
            }

            if (populated.Count < 2)
            {
                error = "Multiple-choice questions require at least options A and B.";
                return false;
            }

            var correctCount = populated.Count(o => o.IsCorrect);
            if (correctCount != 1)
            {
                error = "Mark exactly one non-empty option as the correct answer.";
                return false;
            }

            request = new QuestionUpsertRequest
            {
                CategoryId = SelectedCategory.Id,
                Type = QuestionType.MultipleChoice,
                Text = text,
                Difficulty = difficulty,
                TimeLimitSeconds = timeLimit,
                Options = populated,
                CorrectNumber = null,
                Unit = null
            };
            error = "";
            return true;
        }

        var rawNumber = CorrectNumberText.Trim();
        if (rawNumber.Length == 0)
        {
            error = "Enter the correct numeric answer.";
            return false;
        }

        var normalized = rawNumber.Contains(',') && !rawNumber.Contains('.') && rawNumber.Count(c => c == ',') == 1
            ? rawNumber.Replace(',', '.')
            : rawNumber;

        if (!decimal.TryParse(
                normalized,
                NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture,
                out var correctNumber))
        {
            error = "Enter a valid number (for example: 1991 or 3.14).";
            return false;
        }

        var unit = string.IsNullOrWhiteSpace(Unit) ? null : Unit.Trim();
        request = new QuestionUpsertRequest
        {
            CategoryId = SelectedCategory.Id,
            Type = QuestionType.Estimation,
            Text = text,
            Difficulty = difficulty,
            TimeLimitSeconds = timeLimit,
            Options = [],
            CorrectNumber = correctNumber,
            Unit = unit
        };
        error = "";
        return true;
    }

    private async Task SaveAsync()
    {
        ValidationError = "";
        if (!TryBuildRequest(out var request, out var error))
        {
            ValidationError = error;
            return;
        }

        IsSaving = true;
        try
        {
            SavedQuestion = await _onSave(request!);
            RequestClose?.Invoke(true);
        }
        catch (Exception ex)
        {
            ValidationError = ex.Message;
        }
        finally
        {
            IsSaving = false;
        }
    }

    private void Option_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_updatingCorrectOption ||
            e.PropertyName != nameof(QuestionOptionViewModel.IsCorrect) ||
            sender is not QuestionOptionViewModel changed ||
            !changed.IsCorrect)
        {
            return;
        }

        _updatingCorrectOption = true;
        try
        {
            foreach (var option in Options)
            {
                if (!ReferenceEquals(option, changed) && option.IsCorrect)
                    option.IsCorrect = false;
            }
        }
        finally
        {
            _updatingCorrectOption = false;
        }
    }
}
