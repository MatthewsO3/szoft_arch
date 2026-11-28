namespace QuizGame.Host.Services;

/// <summary>Copies plain text to the system clipboard.</summary>
public interface IClipboardService
{
    void SetText(string text);
}
