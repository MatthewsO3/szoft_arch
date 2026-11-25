using System.Windows;
using QuizGame.Host.ViewModels;

namespace QuizGame.Host.Views;

public partial class QuestionEditorDialog : Window
{
    private readonly QuestionEditorViewModel _viewModel;

    public QuestionEditorDialog(QuestionEditorViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        _viewModel.RequestClose += OnRequestClose;
    }

    private void OnRequestClose(bool saved)
    {
        DialogResult = saved;
    }

    protected override void OnClosed(EventArgs e)
    {
        _viewModel.RequestClose -= OnRequestClose;
        base.OnClosed(e);
    }
}
