using System.Windows;
using QuizGame.Host.ViewModels;

namespace QuizGame.Host;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
    }

    // PasswordBox.Password is deliberately not bindable, so it is pushed into the view model here.
    private void PasswordBox_PasswordChanged(object sender, RoutedEventArgs e) =>
        _viewModel.Password = ((System.Windows.Controls.PasswordBox)sender).Password;

    protected override async void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        await _viewModel.ShutdownAsync();
    }
}
