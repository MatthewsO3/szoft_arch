using System.Collections.ObjectModel;
using System.Windows.Input;
using QuizGame.Contracts;
using QuizGame.Host.Services;

namespace QuizGame.Host.ViewModels;

/// <summary>View model for browsing, filtering, paging, and editing the question bank (plan §9.2).</summary>
public sealed class QuestionBankViewModel : ViewModelBase
{
    private static readonly FilterChoice<int?> AllCategoriesChoice = new("All categories", null);

    private readonly IQuestionBankDialogService _dialogs;

    private IQuestionBankApi? _api;
    private IReadOnlyList<CategoryDto> _categories = [];
    private CancellationTokenSource? _loadCts;
    private long _loadVersion;
    private bool _hasLoadedOnce;
    private bool _suppressAutoReload;

    private FilterChoice<int?> _selectedCategoryFilter = AllCategoriesChoice;
    private FilterChoice<QuestionType?> _selectedTypeFilter;
    private FilterChoice<int?> _selectedDifficultyFilter;
    private string _searchText = "";
    private int _page = 1;
    private int _selectedPageSize = 20;
    private int _totalCount;
    private QuestionItemViewModel? _selectedQuestion;
    private bool _isLoading;
    private string _errorMessage = "";

    public QuestionBankViewModel(IQuestionBankDialogService dialogs)
    {
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));

        TypeFilters =
        [
            new FilterChoice<QuestionType?>("All types", null),
            new FilterChoice<QuestionType?>("Multiple choice", QuestionType.MultipleChoice),
            new FilterChoice<QuestionType?>("Estimation", QuestionType.Estimation)
        ];
        DifficultyFilters =
        [
            new FilterChoice<int?>("All difficulties", null),
            new FilterChoice<int?>("1 – Easy", 1),
            new FilterChoice<int?>("2 – Medium", 2),
            new FilterChoice<int?>("3 – Hard", 3)
        ];
        PageSizes = [10, 20, 50];

        _selectedTypeFilter = TypeFilters[0];
        _selectedDifficultyFilter = DifficultyFilters[0];
        CategoryFilters.Add(AllCategoriesChoice);

        RefreshCommand = new AsyncRelayCommand(_ => RefreshAsync(), _ => IsAttached, ShowError);
        SearchCommand = new AsyncRelayCommand(_ => SearchAsync(), _ => IsAttached, ShowError);
        ClearFiltersCommand = new AsyncRelayCommand(_ => ClearFiltersAsync(), _ => IsAttached, ShowError);
        AddQuestionCommand = new AsyncRelayCommand(_ => AddQuestionAsync(), _ => IsAttached, ShowError);
        EditQuestionCommand = new AsyncRelayCommand(_ => EditSelectedQuestionAsync(), _ => IsAttached && SelectedQuestion is not null, ShowError);
        DeleteQuestionCommand = new AsyncRelayCommand(_ => DeleteSelectedQuestionAsync(), _ => IsAttached && SelectedQuestion is not null, ShowError);
        ManageCategoriesCommand = new AsyncRelayCommand(_ => ManageCategoriesAsync(), _ => IsAttached, ShowError);
        PreviousPageCommand = new AsyncRelayCommand(_ => ChangePageAsync(Page - 1), _ => IsAttached && Page > 1, ShowError);
        NextPageCommand = new AsyncRelayCommand(_ => ChangePageAsync(Page + 1), _ => IsAttached && Page < TotalPages, ShowError);
    }

    public ObservableCollection<FilterChoice<int?>> CategoryFilters { get; } = [];
    public IReadOnlyList<FilterChoice<QuestionType?>> TypeFilters { get; }
    public IReadOnlyList<FilterChoice<int?>> DifficultyFilters { get; }
    public IReadOnlyList<int> PageSizes { get; }
    public ObservableCollection<QuestionItemViewModel> Questions { get; } = [];

    /// <summary>Tracks the latest background reload triggered by filter changes for deterministic testing.</summary>
    public Task CurrentLoadTask { get; private set; } = Task.CompletedTask;

    public bool IsAttached => _api is not null;

    public FilterChoice<int?> SelectedCategoryFilter
    {
        get => _selectedCategoryFilter;
        set
        {
            var next = value ?? AllCategoriesChoice;
            if (!Set(ref _selectedCategoryFilter, next) || _suppressAutoReload) return;
            Page = 1;
            CurrentLoadTask = ReloadQuestionsAsync();
        }
    }

    public FilterChoice<QuestionType?> SelectedTypeFilter
    {
        get => _selectedTypeFilter;
        set
        {
            var next = value ?? TypeFilters[0];
            if (!Set(ref _selectedTypeFilter, next) || _suppressAutoReload) return;
            Page = 1;
            CurrentLoadTask = ReloadQuestionsAsync();
        }
    }

    public FilterChoice<int?> SelectedDifficultyFilter
    {
        get => _selectedDifficultyFilter;
        set
        {
            var next = value ?? DifficultyFilters[0];
            if (!Set(ref _selectedDifficultyFilter, next) || _suppressAutoReload) return;
            Page = 1;
            CurrentLoadTask = ReloadQuestionsAsync();
        }
    }

    public string SearchText
    {
        get => _searchText;
        set => Set(ref _searchText, value);
    }

    public int Page
    {
        get => _page;
        private set
        {
            var clamped = Math.Max(1, value);
            if (!Set(ref _page, clamped)) return;
            Raise(nameof(PageInfoText));
            Raise(nameof(SummaryText));
            RaisePaginationCommands();
        }
    }

    public int SelectedPageSize
    {
        get => _selectedPageSize;
        set
        {
            if (value <= 0 || !Set(ref _selectedPageSize, value)) return;
            Raise(nameof(TotalPages));
            Raise(nameof(PageInfoText));
            Raise(nameof(SummaryText));
            RaisePaginationCommands();
            if (_suppressAutoReload) return;
            Page = 1;
            CurrentLoadTask = ReloadQuestionsAsync();
        }
    }

    public int TotalCount
    {
        get => _totalCount;
        private set
        {
            if (!Set(ref _totalCount, Math.Max(0, value))) return;
            Raise(nameof(TotalPages));
            Raise(nameof(PageInfoText));
            Raise(nameof(SummaryText));
            RaisePaginationCommands();
        }
    }

    public int TotalPages => TotalCount == 0 ? 1 : (int)Math.Ceiling(TotalCount / (double)SelectedPageSize);

    public string PageInfoText => $"Page {Page} of {TotalPages}";

    public string SummaryText
    {
        get
        {
            if (TotalCount == 0 || Questions.Count == 0)
                return TotalCount == 0 ? "0 questions" : $"0 of {TotalCount} questions";

            var start = ((Page - 1) * SelectedPageSize) + 1;
            var end = start + Questions.Count - 1;
            return $"Showing {start}–{end} of {TotalCount} questions";
        }
    }

    public QuestionItemViewModel? SelectedQuestion
    {
        get => _selectedQuestion;
        set
        {
            if (Set(ref _selectedQuestion, value))
                RaiseSelectionCommands();
        }
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (!Set(ref _isLoading, value)) return;
            Raise(nameof(HasNoQuestions));
        }
    }

    public bool HasNoQuestions => !IsLoading && Questions.Count == 0;

    public string ErrorMessage
    {
        get => _errorMessage;
        private set => Set(ref _errorMessage, value);
    }

    public ICommand RefreshCommand { get; }
    public ICommand SearchCommand { get; }
    public ICommand ClearFiltersCommand { get; }
    public ICommand AddQuestionCommand { get; }
    public ICommand EditQuestionCommand { get; }
    public ICommand DeleteQuestionCommand { get; }
    public ICommand ManageCategoriesCommand { get; }
    public ICommand PreviousPageCommand { get; }
    public ICommand NextPageCommand { get; }

    public void AttachApi(IQuestionBankApi api)
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));
        _hasLoadedOnce = false;
        CancelPendingLoad();
        ErrorMessage = "";
        Raise(nameof(IsAttached));
        RaiseAllCommands();
    }

    public void Reset()
    {
        CancelPendingLoad();
        _api = null;
        _hasLoadedOnce = false;
        _categories = [];
        Questions.Clear();
        SelectedQuestion = null;

        _suppressAutoReload = true;
        try
        {
            CategoryFilters.Clear();
            CategoryFilters.Add(AllCategoriesChoice);
            SelectedCategoryFilter = AllCategoriesChoice;
            SelectedTypeFilter = TypeFilters[0];
            SelectedDifficultyFilter = DifficultyFilters[0];
            SearchText = "";
            SelectedPageSize = 20;
            Page = 1;
            TotalCount = 0;
        }
        finally
        {
            _suppressAutoReload = false;
        }

        ErrorMessage = "";
        IsLoading = false;
        Raise(nameof(HasNoQuestions));
        Raise(nameof(SummaryText));
        Raise(nameof(IsAttached));
        RaiseAllCommands();
    }

    /// <summary>Loads categories and the first page the first time the Question Bank tab is opened.</summary>
    public Task EnsureLoadedAsync()
    {
        if (_api is null || _hasLoadedOnce) return Task.CompletedTask;
        _hasLoadedOnce = true;
        CurrentLoadTask = RefreshAsync();
        return CurrentLoadTask;
    }

    public async Task RefreshAsync()
    {
        if (_api is null) return;
        ErrorMessage = "";

        try
        {
            await LoadCategoriesAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            return;
        }

        await ReloadQuestionsAsync();
    }

    public Task SearchAsync()
    {
        if (_api is null) return Task.CompletedTask;
        Page = 1;
        CurrentLoadTask = ReloadQuestionsAsync();
        return CurrentLoadTask;
    }

    public Task ClearFiltersAsync()
    {
        if (_api is null) return Task.CompletedTask;

        _suppressAutoReload = true;
        try
        {
            SelectedCategoryFilter = AllCategoriesChoice;
            SelectedTypeFilter = TypeFilters[0];
            SelectedDifficultyFilter = DifficultyFilters[0];
            SearchText = "";
            Page = 1;
        }
        finally
        {
            _suppressAutoReload = false;
        }

        CurrentLoadTask = ReloadQuestionsAsync();
        return CurrentLoadTask;
    }

    public async Task AddQuestionAsync()
    {
        if (_api is null) return;
        ErrorMessage = "";

        if (_categories.Count == 0)
        {
            try
            {
                await LoadCategoriesAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                ErrorMessage = ex.Message;
                return;
            }
        }

        if (_categories.Count == 0)
        {
            ErrorMessage = "Create at least one category (Categories…) before adding a question.";
            return;
        }

        var api = _api;
        var created = _dialogs.ShowQuestionEditor(_categories, null, req => api.CreateQuestionAsync(req));
        if (created is not null)
            await ReloadQuestionsAsync();
    }

    public async Task EditSelectedQuestionAsync()
    {
        if (_api is null || SelectedQuestion is null) return;
        ErrorMessage = "";

        if (_categories.Count == 0)
            await LoadCategoriesAsync(CancellationToken.None);

        var full = await _api.GetQuestionAsync(SelectedQuestion.Id);
        var api = _api;
        var updated = _dialogs.ShowQuestionEditor(_categories, full, req => api.UpdateQuestionAsync(full.Id, req));
        if (updated is not null)
            await ReloadQuestionsAsync();
    }

    public async Task DeleteSelectedQuestionAsync()
    {
        if (_api is null || SelectedQuestion is null) return;

        var target = SelectedQuestion;
        if (!_dialogs.ConfirmDelete("Delete question", $"Delete this question?\n\n\"{target.Text}\""))
            return;

        ErrorMessage = "";
        await _api.DeleteQuestionAsync(target.Id);
        if (Questions.Count == 1 && Page > 1)
            Page -= 1;

        await ReloadQuestionsAsync();
    }

    public async Task ManageCategoriesAsync()
    {
        if (_api is null) return;

        var changed = _dialogs.ShowCategoryManager(_api);
        if (!changed) return;

        ErrorMessage = "";
        await LoadCategoriesAsync(CancellationToken.None);
        await ReloadQuestionsAsync();
    }

    public Task ChangePageAsync(int targetPage)
    {
        if (_api is null) return Task.CompletedTask;
        var clamped = Math.Clamp(targetPage, 1, TotalPages);
        if (clamped == Page) return Task.CompletedTask;

        Page = clamped;
        CurrentLoadTask = ReloadQuestionsAsync();
        return CurrentLoadTask;
    }

    public async Task ReloadQuestionsAsync()
    {
        if (_api is null) return;

        var previousCts = _loadCts;
        var cts = new CancellationTokenSource();
        _loadCts = cts;
        var version = ++_loadVersion;
        previousCts?.Cancel();
        previousCts?.Dispose();

        var ct = cts.Token;
        IsLoading = true;
        ErrorMessage = "";

        try
        {
            var query = new QuestionQuery(
                SelectedCategoryFilter.Value,
                SelectedTypeFilter.Value,
                SelectedDifficultyFilter.Value,
                string.IsNullOrWhiteSpace(SearchText) ? null : SearchText.Trim(),
                Page,
                SelectedPageSize);

            var result = await _api.GetQuestionsAsync(query, ct);
            if (ct.IsCancellationRequested || version != _loadVersion)
                return;

            var categoryNames = new Dictionary<int, string>();
            foreach (var category in _categories)
                categoryNames[category.Id] = category.Name;

            var selectedId = SelectedQuestion?.Id;

            Questions.Clear();
            foreach (var item in result.Items)
            {
                var catName = categoryNames.TryGetValue(item.CategoryId, out var name)
                    ? name
                    : $"Category #{item.CategoryId}";
                Questions.Add(new QuestionItemViewModel(item, catName));
            }

            TotalCount = result.TotalCount;
            if (result.Page > 0)
                Page = result.Page;

            SelectedQuestion = selectedId is Guid id
                ? Questions.FirstOrDefault(q => q.Id == id)
                : null;

            Raise(nameof(SummaryText));
            Raise(nameof(HasNoQuestions));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Superseded by a newer load or Reset().
        }
        catch (Exception ex)
        {
            if (version == _loadVersion)
                ErrorMessage = ex.Message;
        }
        finally
        {
            if (version == _loadVersion)
                IsLoading = false;
        }
    }

    private async Task LoadCategoriesAsync(CancellationToken ct)
    {
        if (_api is null) return;

        var categories = await _api.GetCategoriesAsync(ct);
        var previousCategoryId = SelectedCategoryFilter.Value;

        _categories = categories;
        _suppressAutoReload = true;
        try
        {
            CategoryFilters.Clear();
            CategoryFilters.Add(AllCategoriesChoice);
            foreach (var c in categories)
                CategoryFilters.Add(new FilterChoice<int?>(c.Name, c.Id));

            var restored = previousCategoryId is int id
                ? CategoryFilters.FirstOrDefault(c => c.Value == id)
                : AllCategoriesChoice;

            if (restored is null)
            {
                SelectedCategoryFilter = AllCategoriesChoice;
                Page = 1;
            }
            else
            {
                SelectedCategoryFilter = restored;
            }
        }
        finally
        {
            _suppressAutoReload = false;
        }
    }

    private void CancelPendingLoad()
    {
        _loadVersion++;
        var cts = _loadCts;
        _loadCts = null;
        cts?.Cancel();
        cts?.Dispose();
    }

    private void ShowError(Exception ex) => ErrorMessage = ex.Message;

    private void RaisePaginationCommands()
    {
        ((AsyncRelayCommand)PreviousPageCommand).RaiseCanExecuteChanged();
        ((AsyncRelayCommand)NextPageCommand).RaiseCanExecuteChanged();
    }

    private void RaiseSelectionCommands()
    {
        ((AsyncRelayCommand)EditQuestionCommand).RaiseCanExecuteChanged();
        ((AsyncRelayCommand)DeleteQuestionCommand).RaiseCanExecuteChanged();
    }

    private void RaiseAllCommands()
    {
        ((AsyncRelayCommand)RefreshCommand).RaiseCanExecuteChanged();
        ((AsyncRelayCommand)SearchCommand).RaiseCanExecuteChanged();
        ((AsyncRelayCommand)ClearFiltersCommand).RaiseCanExecuteChanged();
        ((AsyncRelayCommand)AddQuestionCommand).RaiseCanExecuteChanged();
        ((AsyncRelayCommand)ManageCategoriesCommand).RaiseCanExecuteChanged();
        RaiseSelectionCommands();
        RaisePaginationCommands();
    }
}
