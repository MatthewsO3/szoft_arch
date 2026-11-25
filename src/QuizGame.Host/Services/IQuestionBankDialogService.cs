using QuizGame.Contracts;

namespace QuizGame.Host.Services;

/// <summary>Modal dialogs used by the question-bank UI, abstracted so ViewModels stay unit-testable.</summary>
public interface IQuestionBankDialogService
{
    QuestionDto? ShowQuestionEditor(
        IReadOnlyList<CategoryDto> categories,
        QuestionDto? existing,
        Func<QuestionUpsertRequest, Task<QuestionDto>> onSave);

    bool ShowCategoryManager(IQuestionBankApi api);

    bool ConfirmDelete(string title, string message);
}
