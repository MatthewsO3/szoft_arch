using System.Windows;
using System.Windows.Media.Animation;
using QuizGame.Host.ViewModels;

namespace QuizGame.Host;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly Func<QuestionWindow> _createQuestionWindow;
    private readonly Func<MapWindow> _createMapWindow;
    private QuestionWindow? _questionWindow;
    private MapWindow? _mapWindow;

    public MainWindow(
        MainViewModel viewModel,
        Func<QuestionWindow> createQuestionWindow,
        Func<MapWindow> createMapWindow)
    {
        InitializeComponent();
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _createQuestionWindow = createQuestionWindow ?? throw new ArgumentNullException(nameof(createQuestionWindow));
        _createMapWindow = createMapWindow ?? throw new ArgumentNullException(nameof(createMapWindow));
        DataContext = _viewModel;
        _viewModel.PropertyChanged += ViewModel_PropertyChanged;
    }

    // PasswordBox.Password is deliberately not bindable, so it is pushed into the view model here.
    private void PasswordBox_PasswordChanged(object sender, RoutedEventArgs e) =>
        _viewModel.Password = ((System.Windows.Controls.PasswordBox)sender).Password;

    private void CopyRoomCode_Click(object sender, RoutedEventArgs e) =>
        PlayRoomCodeAnimation();

    private void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.RoomCode) && _viewModel.HasRoom)
            PlayRoomCodeAnimation();
    }

    private void PlayRoomCodeAnimation() =>
        ((Storyboard)FindResource("PopInAnimation")).Clone().Begin(RoomCodeText);

    private void OpenQuestionWindow(object sender, RoutedEventArgs e)
    {
        if (_questionWindow is null)
        {
            _questionWindow = _createQuestionWindow();
            _questionWindow.Owner = this;
            _questionWindow.Closed += (_, _) => _questionWindow = null;
            _questionWindow.Show();
        }
        else _questionWindow.Activate();
    }

    private void OpenMapWindow(object sender, RoutedEventArgs e)
    {
        if (_mapWindow is null)
        {
            _mapWindow = _createMapWindow();
            _mapWindow.Owner = this;
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
