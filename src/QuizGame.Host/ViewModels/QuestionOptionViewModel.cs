namespace QuizGame.Host.ViewModels;

/// <summary>Editable option row (A–D) in <see cref="QuestionEditorViewModel"/>.</summary>
public sealed class QuestionOptionViewModel(char label, string text = "", bool isCorrect = false) : ViewModelBase
{
    private string _text = text;
    private bool _isCorrect = isCorrect;

    public char Label { get; } = label;

    public bool IsRequired => Label is 'A' or 'B';

    public string LabelText => IsRequired ? $"{Label} *" : $"{Label}";

    public string Text
    {
        get => _text;
        set => Set(ref _text, value);
    }

    public bool IsCorrect
    {
        get => _isCorrect;
        set => Set(ref _isCorrect, value);
    }
}
