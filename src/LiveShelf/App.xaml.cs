using System.Windows;
using System.Windows.Threading;

namespace LiveShelf;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += App_DispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
        TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;

        ShelvedWindowRegistry.RestoreRegisteredWindows();
    }

    private static void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        CrashLogger.Log(e.Exception);

        if (Current.MainWindow is MainWindow window)
        {
            window.ReportRuntimeError(e.Exception);
            window.RestoreShelvedWindowsForShutdown();
        }

        e.Handled = true;
        Current.Shutdown(1);
    }

    private static void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            CrashLogger.Log(exception);
        }

        ShelvedWindowRegistry.RestoreRegisteredWindows();
    }

    private static void TaskScheduler_UnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        CrashLogger.Log(e.Exception);
        e.SetObserved();

        var app = Current;
        if (app is null)
        {
            ShelvedWindowRegistry.RestoreRegisteredWindows();
            return;
        }

        app.Dispatcher.BeginInvoke(() =>
        {
            if (app.MainWindow is MainWindow window)
            {
                window.ReportRuntimeError(e.Exception);
                window.RestoreShelvedWindowsForShutdown();
            }
        });
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (MainWindow is MainWindow window)
        {
            window.RestoreShelvedWindowsForShutdown();
        }

        base.OnExit(e);
    }
}
