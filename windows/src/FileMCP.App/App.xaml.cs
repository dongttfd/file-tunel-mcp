using FileMCP.Core;
using System.Windows;

namespace FileMCP.App;

public partial class App : System.Windows.Application
{
    private MainWindow? _window;
    private DesktopSingleInstanceCoordinator? _singleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstance = new DesktopSingleInstanceCoordinator("FileMCP.Desktop.v1");
        if (!_singleInstance.IsPrimary)
        {
            try { _singleInstance.SignalPrimaryAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult(); } catch { }
            _singleInstance.Dispose();
            _singleInstance = null;
            Shutdown(0);
            return;
        }

        _window = new MainWindow();
        MainWindow = _window;
        SessionEnding += (_, _) => _window.ShutdownForSystemSession();
        _singleInstance.StartActivationListener(() =>
            Dispatcher.BeginInvoke(new Action(() => _window?.ActivateFromSecondaryLaunch())));
        _window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstance?.Dispose();
        _singleInstance = null;
        base.OnExit(e);
    }
}
