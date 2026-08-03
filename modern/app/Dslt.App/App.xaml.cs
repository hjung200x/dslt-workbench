using System.Windows;
using Dslt.App.ViewModels;
using Dslt.App.Services;
using Dslt.Managed.Core.Services;

namespace Dslt.App;

public partial class App : Application
{
    private IProcessingEngine? _engine;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _engine = ProcessingEngineFactory.Create();
        var window = new MainWindow
        {
            DataContext = new MainWindowViewModel(_engine, new WpfWorkspaceFileService()),
        };
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _engine?.Dispose();
        base.OnExit(e);
    }
}
