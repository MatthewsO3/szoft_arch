using System.Windows;

namespace QuizGame.Host.Services;

/// <summary>WPF implementation of <see cref="IClipboardService"/>.</summary>
public sealed class WpfClipboardService : IClipboardService
{
    public void SetText(string text) => Clipboard.SetText(text);
}
