using System.IO;
using System.Windows;
using System.Windows.Threading;
using Cs2ServerControl.Services;
using Cs2ServerControl.Shell;

namespace Cs2ServerControl;

public partial class App : Application {
    // One window per PC: a second start (the exe double-clicked while the
    // window is open) brings the open window forward instead.
    private const string WindowMutexName = "Cs2ServerControl.Window";

    private ServerConnection? _connection;
    private Mutex? _windowMutex;

    protected override void OnStartup(StartupEventArgs e) {
        base.OnStartup(e);
        AppCommand command = AppCommand.Parse(e.Args);

        _windowMutex = new Mutex(initiallyOwned: true, WindowMutexName, out bool isFirstWindow);
        if (!isFirstWindow) {
            WindowActivator.ActivateOtherInstance();
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnUnhandledException;

        try {
            PackagePaths paths = PackagePaths.Locate();
            AppSettings settings = AppSettings.Load();
            _connection = new ServerConnection(paths, settings, Dispatcher);

            var shell = new ShellViewModel(_connection);
            var window = new MainWindow(shell, settings);
            MainWindow = window;
            window.Show();
            shell.RunStartupCommand(command);
        } catch (Exception exception) {
            // Without a window there is nothing to keep running for. Staying
            // alive would hold the exe and the one-window lock, so the next
            // start would silently bring nothing forward.
            string logFile = WriteErrorLog(exception);
            MessageBox.Show($"The window could not start: {exception.GetBaseException().Message}\n\nDetails: {logFile}",
                "CS2 Server Control", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e) {
        _connection?.Dispose();
        _windowMutex?.Dispose();
        base.OnExit(e);
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e) {
        // The server keeps running without this window, so a bug here should
        // say what happened instead of closing silently. The full exception
        // goes into a log, the dialog only has room for the first message.
        string logFile = WriteErrorLog(e.Exception);
        Exception innermost = e.Exception.GetBaseException();
        string message = string.IsNullOrWhiteSpace(innermost.Message) ? innermost.GetType().Name : innermost.Message;
        ConfirmDialog.ShowMessage(MainWindow, "Unexpected error", $"{message}\n\nDetails: {logFile}");
        e.Handled = true;
    }

    private static string WriteErrorLog(Exception exception) {
        string logFile = Path.Combine(Path.GetDirectoryName(AppSettings.SettingsFile)!, "error.log");
        try {
            Directory.CreateDirectory(Path.GetDirectoryName(logFile)!);
            File.AppendAllText(logFile, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}\r\n{exception}\r\n\r\n");
        } catch (Exception writeError) when (writeError is IOException or UnauthorizedAccessException) {
            // Without a log the dialog still shows the message.
        }
        return logFile;
    }
}
