using System.Windows;
using QuizGame.Contracts;
using QuizGame.Host.ViewModels;
using QuizGame.Host.Views;

namespace QuizGame.Host.Services;

/// <summary>WPF implementation of <see cref="IQuestionBankDialogService"/>.</summary>
public sealed class WpfQuestionBankDialogService : IQuestionBankDialogService
{
    public QuestionDto? ShowQuestionEditor(
        IReadOnlyList<CategoryDto> categories,
        QuestionDto? existing,
        Func<QuestionUpsertRequest, Task<QuestionDto>> onSave)
    {
        var vm = new QuestionEditorViewModel(categories, existing, onSave);
        var dialog = new QuestionEditorDialog(vm) { Owner = GetOwner() };
        return dialog.ShowDialog() == true ? vm.SavedQuestion : null;
    }

    public bool ShowCategoryManager(IQuestionBankApi api)
    {
        var vm = new CategoryManagerViewModel(api, this);
        var dialog = new CategoryManagerDialog(vm) { Owner = GetOwner() };
        dialog.ShowDialog();
        return vm.HasChanges;
    }

    public bool ConfirmDelete(string title, string message)
    {
        var owner = GetOwner();
        var result = owner is not null
            ? MessageBox.Show(owner, message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No)
            : MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        return result == MessageBoxResult.Yes;
    }

    private static Window? GetOwner() =>
        Application.Current?.MainWindow is { IsVisible: true } main ? main : null;
}
