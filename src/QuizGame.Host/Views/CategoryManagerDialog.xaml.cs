using System.Windows;
using QuizGame.Host.ViewModels;

namespace QuizGame.Host.Views;

public partial class CategoryManagerDialog : Window
{
    private readonly CategoryManagerViewModel _viewModel;

    public CategoryManagerDialog(CategoryManagerViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        Loaded += CategoryManagerDialog_Loaded;
    }

    private void CategoryManagerDialog_Loaded(object sender, RoutedEventArgs e)
    {
        if (_viewModel.LoadCommand.CanExecute(null))
            _viewModel.LoadCommand.Execute(null);
    }

    protected override void OnClosed(EventArgs e)
    {
        Loaded -= CategoryManagerDialog_Loaded;
        base.OnClosed(e);
    }
}
