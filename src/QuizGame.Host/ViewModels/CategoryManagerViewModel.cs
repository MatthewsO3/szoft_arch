using System.Collections.ObjectModel;
using System.Windows.Input;
using QuizGame.Contracts;
using QuizGame.Host.Services;

namespace QuizGame.Host.ViewModels;

/// <summary>View model for creating, renaming, and deleting question-bank categories.</summary>
public sealed class CategoryManagerViewModel : ViewModelBase
{
    public const int MaxCategoryNameLength = 100;

    private readonly IQuestionBankApi _api;
    private readonly IQuestionBankDialogService _dialogs;

    private CategoryDto? _selectedCategory;
    private string _newCategoryName = "";
    private string _renameCategoryName = "";
    private string _errorMessage = "";
    private bool _isLoading;

    public CategoryManagerViewModel(IQuestionBankApi api, IQuestionBankDialogService dialogs)
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));

        LoadCommand = new AsyncRelayCommand(_ => LoadAsync(), _ => !IsLoading, ex => ErrorMessage = ex.Message);
        AddCommand = new AsyncRelayCommand(_ => AddAsync(), _ => !IsLoading, ex => ErrorMessage = ex.Message);
        RenameCommand = new AsyncRelayCommand(_ => RenameAsync(), _ => !IsLoading && SelectedCategory is not null, ex => ErrorMessage = ex.Message);
        DeleteCommand = new AsyncRelayCommand(_ => DeleteAsync(), _ => !IsLoading && SelectedCategory is not null, ex => ErrorMessage = ex.Message);
    }

    public ObservableCollection<CategoryDto> Categories { get; } = [];

    public CategoryDto? SelectedCategory
    {
        get => _selectedCategory;
        set
        {
            if (!Set(ref _selectedCategory, value)) return;
            RenameCategoryName = value?.Name ?? "";
            RaiseActionStates();
        }
    }

    public string NewCategoryName
    {
        get => _newCategoryName;
        set => Set(ref _newCategoryName, value);
    }

    public string RenameCategoryName
    {
        get => _renameCategoryName;
        set => Set(ref _renameCategoryName, value);
    }

    public string ErrorMessage
    {
        get => _errorMessage;
        set => Set(ref _errorMessage, value);
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (Set(ref _isLoading, value))
                RaiseActionStates();
        }
    }

    public bool HasChanges { get; private set; }

    public ICommand LoadCommand { get; }
    public ICommand AddCommand { get; }
    public ICommand RenameCommand { get; }
    public ICommand DeleteCommand { get; }

    public async Task LoadAsync()
    {
        ErrorMessage = "";
        IsLoading = true;
        try
        {
            var items = await _api.GetCategoriesAsync();
            var previousId = SelectedCategory?.Id;

            Categories.Clear();
            foreach (var item in items) Categories.Add(item);

            SelectedCategory = previousId is int id
                ? Categories.FirstOrDefault(c => c.Id == id)
                : null;
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    public async Task AddAsync()
    {
        ErrorMessage = "";
        var trimmed = NewCategoryName.Trim();
        if (!ValidateName(trimmed, out var error))
        {
            ErrorMessage = error;
            return;
        }

        IsLoading = true;
        try
        {
            var created = await _api.CreateCategoryAsync(trimmed);
            Categories.Add(created);
            NewCategoryName = "";
            SelectedCategory = created;
            HasChanges = true;
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    public async Task RenameAsync()
    {
        if (SelectedCategory is null) return;

        ErrorMessage = "";
        var trimmed = RenameCategoryName.Trim();
        if (!ValidateName(trimmed, out var error))
        {
            ErrorMessage = error;
            return;
        }

        IsLoading = true;
        try
        {
            var updated = await _api.UpdateCategoryAsync(SelectedCategory.Id, trimmed);
            var index = Categories.IndexOf(SelectedCategory);
            if (index >= 0)
                Categories[index] = updated;
            SelectedCategory = updated;
            HasChanges = true;
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    public async Task DeleteAsync()
    {
        if (SelectedCategory is null) return;

        var target = SelectedCategory;
        if (!_dialogs.ConfirmDelete("Delete category", $"Delete category \"{target.Name}\"?"))
            return;

        ErrorMessage = "";
        IsLoading = true;
        try
        {
            await _api.DeleteCategoryAsync(target.Id);
            Categories.Remove(target);
            SelectedCategory = null;
            HasChanges = true;
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    private static bool ValidateName(string trimmed, out string error)
    {
        if (trimmed.Length == 0)
        {
            error = "Enter a category name.";
            return false;
        }

        if (trimmed.Length > MaxCategoryNameLength)
        {
            error = $"Category name must be at most {MaxCategoryNameLength} characters.";
            return false;
        }

        error = "";
        return true;
    }

    private void RaiseActionStates()
    {
        ((AsyncRelayCommand)LoadCommand).RaiseCanExecuteChanged();
        ((AsyncRelayCommand)AddCommand).RaiseCanExecuteChanged();
        ((AsyncRelayCommand)RenameCommand).RaiseCanExecuteChanged();
        ((AsyncRelayCommand)DeleteCommand).RaiseCanExecuteChanged();
    }
}
