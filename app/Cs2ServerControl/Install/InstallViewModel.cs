using System.Collections.ObjectModel;
using System.Net.Http;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cs2ServerControl.Services;
using Cs2ServerControl.Shell;

namespace Cs2ServerControl.Install;

/// <summary>
/// The Install screen: what the old install and map update scripts did with
/// PowerShell, now inside the app, with the installer's log on screen.
/// </summary>
public sealed partial class InstallViewModel : ObservableObject, IScreen, IInstallPrompts {
    private const int MaxLogLines = 3000;

    private readonly ServerConnection _connection;
    private readonly IDialogService _dialogs;
    private readonly Dispatcher _dispatcher;
    private CancellationTokenSource? _cancellation;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStart))]
    private bool _isBusy;

    [ObservableProperty]
    private string _resultText = "";

    [ObservableProperty]
    private bool _hasFailed;

    public InstallViewModel(ServerConnection connection, IDialogService dialogs) {
        _connection = connection;
        _dialogs = dialogs;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _connection.PropertyChanged += (_, e) => {
            if (e.PropertyName == nameof(ServerConnection.IsRunning)) {
                OnPropertyChanged(nameof(IsServerRunning));
            }
        };
    }

    public ObservableCollection<InstallMessage> Log { get; } = [];
    public bool CanStart => !IsBusy;
    public bool IsServerRunning => _connection.IsRunning;
    public bool IsServerInstalled => _connection.Paths.IsServerInstalled;

    public IReadOnlyList<string> StatusItems => [InstallStateText];

    private string InstallStateText {
        get {
            if (IsBusy) {
                return "Installing...";
            }
            return IsServerInstalled ? "Server installed" : "Server not installed yet";
        }
    }
    public string StatusRight => "Downloads are cached in tools\\downloads";

    public void OnShown() {
        OnPropertyChanged(nameof(IsServerInstalled));
    }

    public void OnHidden() {
    }

    /// <summary>Everything: SteamCMD, HLDS, mods, plugins, maps. Safe to run again as a repair.</summary>
    [RelayCommand]
    public Task<bool> InstallServer() {
        if (_connection.IsRunning) {
            ShowResult("Stop the server first: the install replaces files the running server has open.", failed: true);
            return Task.FromResult(false);
        }
        return RunAsync("Install", (installer, token) => installer.InstallServerAsync(token));
    }

    /// <summary>The map pack, the market maps, custom-maps and the map lists; shown on the Maps screen.</summary>
    [RelayCommand]
    public Task<bool> UpdateMaps() {
        return RunAsync("Map update", (installer, token) => installer.InstallMapsAsync(token));
    }

    /// <summary>Installs one map from the market; the names of the installed maps, empty when it failed.</summary>
    public async Task<IReadOnlyList<string>> InstallMarketMap(MarketMap map) {
        IReadOnlyList<string> installed = [];
        await RunAsync($"Install of {map.Name}", async (installer, token) => installed = await installer.InstallMarketMapAsync(map, token));
        return installed;
    }

    public async Task<IReadOnlyList<string>> RemoveMarketMap(MarketMap map) {
        IReadOnlyList<string> removed = [];
        await RunAsync($"Removing {map.Name}", (installer, _) => {
            removed = installer.RemoveMarketMap(map.ModId);
            return Task.CompletedTask;
        });
        return removed;
    }

    /// <summary>Copies server-files into the server and compiles the fun plugins again.</summary>
    [RelayCommand]
    public Task<bool> RebuildPlugins() {
        return RunAsync("Plugin rebuild", (installer, token) => installer.InstallFunFilesAsync(token));
    }

    [RelayCommand]
    private void Cancel() {
        _cancellation?.Cancel();
    }

    public async Task<AdminAccount?> AskAdminAccountAsync(CancellationToken cancellation) {
        var modal = new AdminAccountModal();
        await _dispatcher.InvokeAsync(() => _dialogs.ShowAsync(modal)).Task.Unwrap();
        return modal.Result;
    }

    /// <summary>One job at a time for both screens: they share tools\work and the log. True when it finished without an error.</summary>
    private async Task<bool> RunAsync(string title, Func<PackageInstaller, CancellationToken, Task> job) {
        if (IsBusy) {
            return false;
        }

        IsBusy = true;
        HasFailed = false;
        ResultText = $"{title} is running...";
        Log.Clear();
        _cancellation = new CancellationTokenSource();
        var progress = new Progress<InstallMessage>(AddLogLine);
        var installer = new PackageInstaller(_connection.Paths, progress, this);
        try {
            // Off the UI thread: downloads, archives and SteamCMD take minutes.
            await Task.Run(() => job(installer, _cancellation.Token));
            ShowResult($"{title} done.", failed: false);
            return true;
        } catch (OperationCanceledException) {
            ShowResult($"{title} cancelled. Running it again continues where it stopped.", failed: true);
        } catch (InstallException exception) {
            ShowResult($"{title} failed: {exception.Message}", failed: true);
        } catch (Exception exception) when (exception is System.IO.IOException or UnauthorizedAccessException or HttpRequestException or System.Text.Json.JsonException) {
            ShowResult($"{title} failed: {exception.Message}", failed: true);
        } finally {
            _cancellation.Dispose();
            _cancellation = null;
            IsBusy = false;
            OnPropertyChanged(nameof(IsServerInstalled));
        }
        return false;
    }

    private void ShowResult(string text, bool failed) {
        ResultText = text;
        HasFailed = failed;
        AddLogLine(new InstallMessage(failed ? InstallMessageKind.Problem : InstallMessageKind.Step, text));
    }

    private void AddLogLine(InstallMessage message) {
        Log.Add(message);
        while (Log.Count > MaxLogLines) {
            Log.RemoveAt(0);
        }
    }
}

/// <summary>Asks for the admin of the AMX Mod X menus, once per install.</summary>
public sealed partial class AdminAccountModal : ModalViewModel {
    [ObservableProperty]
    private string _name = "";

    [ObservableProperty]
    private string _password = "";

    [ObservableProperty]
    private string? _error;

    public AdminAccount? Result { get; private set; }

    [RelayCommand]
    private void Save() {
        if (Name.Trim().Length == 0 || Password.Length == 0) {
            Error = "Name and password are both needed.";
            return;
        }
        if (Name.Contains('"') || Password.Contains('"')) {
            Error = "Quotes are not allowed, users.ini has no way to escape them.";
            return;
        }
        Result = new AdminAccount(Name.Trim(), Password);
        Close();
    }
}
