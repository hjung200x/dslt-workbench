using System.Windows;
using Dslt.App.ViewModels;
using Dslt.App.Services;
using Dslt.Managed.Core.Services;

namespace Dslt.App;

public partial class App : Application
{
    private IProcessingEngine? _engine;
    private volatile bool _isExiting;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _engine = new UnavailableProcessingEngine("Native core initializing...");
        var window = new MainWindow
        {
            DataContext = new MainWindowViewModel(_engine, new WpfWorkspaceFileService()),
        };
        MainWindow = window;
        window.Show();
        _ = InitializeEngineAsync(window);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _isExiting = true;
        _engine?.Dispose();
        _engine = null;
        base.OnExit(e);
    }

    private async Task InitializeEngineAsync(MainWindow window)
    {
        IProcessingEngine engine;
        try
        {
            engine = await Task.Run(ProcessingEngineFactory.Create).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            engine = new UnavailableProcessingEngine($"Native core initialization failed: {error.Message}");
        }

        if (_isExiting || Dispatcher.HasShutdownStarted)
        {
            engine.Dispose();
            return;
        }

        try
        {
            await Dispatcher.InvokeAsync(() =>
            {
                if (_isExiting || !window.IsLoaded)
                {
                    engine.Dispose();
                    return;
                }

                var previousEngine = _engine;
                _engine = engine;
                window.DataContext = new MainWindowViewModel(engine, new WpfWorkspaceFileService());
                previousEngine?.Dispose();
            });
        }
        catch
        {
            engine.Dispose();
        }
    }
}
