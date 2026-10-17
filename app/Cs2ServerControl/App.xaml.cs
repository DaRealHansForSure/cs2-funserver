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

        if (!command.HasWindow) {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _ = RunFastDownloadAsync(command);
            return;
        }

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
            // Left over from a window that closed during a nav build.
            _connection.Process.StopNavHelpers();

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
                "CS 1.6 Server Control", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    /// <summary>
    /// The hidden download server mode: no window, serves until hlds.exe ends
    /// (or forever without a process to watch), then the process exits.
    /// </summary>
    private async Task RunFastDownloadAsync(AppCommand command) {
        using var stop = new CancellationTokenSource();
        if (command.WatchProcessId != null) {
            _ = StopWhenProcessEndsAsync(command.WatchProcessId.Value, stop);
        }

        try {
            await new FastDownloadServer(command.Root!, command.Port).RunAsync(stop.Token);
        } catch (System.Net.Sockets.SocketException) {
            // The port is taken: another download server already runs.
        }
        Shutdown();
    }

    private static async Task StopWhenProcessEndsAsync(int processId, CancellationTokenSource stop) {
        try {
            using System.Diagnostics.Process process = System.Diagnostics.Process.GetProcessById(processId);
            await process.WaitForExitAsync();
        } catch (ArgumentException) {
            // Already gone.
        }
        stop.Cancel();
    }

    protected override void OnExit(ExitEventArgs e) {
        // A nav build still running would keep going on the invisible desktop.
        _connection?.Process.StopNavHelpers();
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
