using System.Windows;
using System.Windows.Input;

namespace QuizGame.Host;

public partial class MapWindow : Window
{
    private bool _fullscreen;
    private WindowState _previousState;

    public MapWindow() => InitializeComponent();

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.F11 && !(e.Key == Key.Escape && _fullscreen)) return;
        e.Handled = true;
        if (!_fullscreen)
        {
            _previousState = WindowState;
            WindowState = WindowState.Normal;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Maximized;
        }
        else
        {
            WindowState = WindowState.Normal;
            WindowStyle = WindowStyle.SingleBorderWindow;
            ResizeMode = ResizeMode.CanResize;
            WindowState = _previousState;
        }
        _fullscreen = !_fullscreen;
    }
}
