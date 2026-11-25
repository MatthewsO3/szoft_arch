using QuizGame.Contracts;
using QuizGame.Host.Services;
using QuizGame.Host.ViewModels;

namespace QuizGame.Host.Tests;

public class CategoryManagerViewModelTests
{
    [Fact]
    public async Task LoadAddRenameDelete_LifecycleUpdatesCollectionAndHasChanges()
    {
        var categories = new List<CategoryDto> { new(1, "History") };
        var api = new FakeQuestionBankApi
        {
            GetCategoriesHandler = () => categories.ToList(),
            CreateCategoryHandler = name => new CategoryDto(2, name),
            UpdateCategoryHandler = (id, name) => new CategoryDto(id, name),
        };
        var dialogs = new FakeQuestionBankDialogService { ConfirmResult = true };
        var vm = new CategoryManagerViewModel(api, dialogs);

        await vm.LoadAsync();
        Assert.Single(vm.Categories);
        Assert.False(vm.HasChanges);

        // Validation on empty name
        vm.NewCategoryName = "   ";
        await vm.AddAsync();
        Assert.Contains("category name", vm.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.False(vm.HasChanges);

        // Add
        vm.NewCategoryName = "  Science  ";
        await vm.AddAsync();
        Assert.Equal(2, vm.Categories.Count);
        Assert.Equal("", vm.NewCategoryName);
        Assert.Equal("Science", vm.SelectedCategory?.Name);
        Assert.Equal("Science", vm.RenameCategoryName);
        Assert.True(vm.HasChanges);

        // Rename
        vm.RenameCategoryName = "Physics";
        await vm.RenameAsync();
        Assert.Equal("Physics", vm.Categories[1].Name);
        Assert.Equal("Physics", vm.SelectedCategory?.Name);

        // Delete cancelled
        dialogs.ConfirmResult = false;
        await vm.DeleteAsync();
        Assert.Equal(2, vm.Categories.Count);

        // Delete confirmed
        dialogs.ConfirmResult = true;
        await vm.DeleteAsync();
        Assert.Single(vm.Categories);
        Assert.Null(vm.SelectedCategory);
    }

    [Fact]
    public async Task ServerErrorOnDelete_SurfacesErrorMessageWithoutRemovingCategory()
    {
        var api = new FakeQuestionBankApi
        {
            GetCategoriesHandler = () => [new(1, "History")],
            DeleteCategoryHandler = _ => throw new ApiException("Category has active questions.", 409)
        };
        var dialogs = new FakeQuestionBankDialogService { ConfirmResult = true };
        var vm = new CategoryManagerViewModel(api, dialogs);

        await vm.LoadAsync();
        vm.SelectedCategory = vm.Categories[0];

        await vm.DeleteAsync();

        Assert.Single(vm.Categories);
        Assert.Equal("Category has active questions.", vm.ErrorMessage);
        Assert.False(vm.HasChanges);
    }
}
