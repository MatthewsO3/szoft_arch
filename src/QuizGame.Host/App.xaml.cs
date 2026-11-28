using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using QuizGame.Host.Services;
using QuizGame.Host.ViewModels;

namespace QuizGame.Host;

/// <summary>Application entry point and DI composition root (plan §9.1).</summary>
public partial class App : Application
{
    private ServiceProvider? _services;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _services = BuildServiceProvider();
        var mainWindow = _services.GetRequiredService<MainWindow>();
        MainWindow = mainWindow;
        mainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _services?.Dispose();
        base.OnExit(e);
    }

    private static ServiceProvider BuildServiceProvider()
    {
        var services = new ServiceCollection();

        services.AddSingleton<GameConnection>();
        services.AddSingleton<IRoomControlClient>(sp => sp.GetRequiredService<GameConnection>());
        services.AddSingleton<IApiClientFactory, ApiClientFactory>();
        services.AddSingleton<IQuestionBankDialogService, WpfQuestionBankDialogService>();
        services.AddSingleton<IClipboardService, WpfClipboardService>();

        services.AddSingleton<QuestionViewModel>();
        services.AddSingleton<MapViewModel>();
        services.AddSingleton<QuestionBankViewModel>();
        services.AddSingleton<MainViewModel>();

        services.AddSingleton<MainWindow>();
        services.AddTransient<QuestionWindow>();
        services.AddTransient<MapWindow>();
        services.AddSingleton<Func<QuestionWindow>>(sp => () => sp.GetRequiredService<QuestionWindow>());
        services.AddSingleton<Func<MapWindow>>(sp => () => sp.GetRequiredService<MapWindow>());

        return services.BuildServiceProvider();
    }
}
