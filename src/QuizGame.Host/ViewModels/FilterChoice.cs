namespace QuizGame.Host.ViewModels;

/// <summary>Non-null ComboBox option wrapper so WPF displays labels cleanly even when <typeparamref name="T"/> is nullable.</summary>
public sealed record FilterChoice<T>(string Label, T Value);
