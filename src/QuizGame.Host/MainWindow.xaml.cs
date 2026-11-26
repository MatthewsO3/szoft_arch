using System.Windows;
using QuizGame.Host.ViewModels;

namespace QuizGame.Host;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel = new();
    private QuestionWindow? _questionWindow;
    private MapWindow? _mapWindow;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
        _viewModel.Map.OpenRequested += () => OpenMapWindow(this, new RoutedEventArgs());
    }

    // PasswordBox.Password is deliberately not bindable, so it is pushed into the view model here.
    private void PasswordBox_PasswordChanged(object sender, RoutedEventArgs e) =>
        _viewModel.Password = ((System.Windows.Controls.PasswordBox)sender).Password;

    private void OpenQuestionWindow(object sender, RoutedEventArgs e)
    {
        if (_questionWindow is null)
        {
            _questionWindow = new QuestionWindow { Owner = this, DataContext = _viewModel.Question };
            _questionWindow.Closed += (_, _) => _questionWindow = null;
            _questionWindow.Show();
        }
        else _questionWindow.Activate();
    }

    private void OpenMapWindow(object sender, RoutedEventArgs e)
    {
        if (_mapWindow is null)
        {
            _mapWindow = new MapWindow { Owner = this, DataContext = _viewModel.Map };
            _mapWindow.Closed += (_, _) => _mapWindow = null;
            _mapWindow.Show();
        }
        else _mapWindow.Activate();
    }

    protected override async void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        await _viewModel.ShutdownAsync();
    }
}
