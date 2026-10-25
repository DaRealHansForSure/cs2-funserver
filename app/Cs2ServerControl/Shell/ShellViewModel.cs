using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Cs2ServerControl.Bots;
using Cs2ServerControl.Install;
using Cs2ServerControl.LiveMap;
using Cs2ServerControl.Maps;
using Cs2ServerControl.Options;
using Cs2ServerControl.Server;
using Cs2ServerControl.Services;
using Cs2ServerControl.Stats;

namespace Cs2ServerControl.Shell;

/// <summary>A screen behind one rail entry, with its own status bar items.</summary>
public interface IScreen {
    /// <summary>Status bar items after the server state, left aligned.</summary>
    IReadOnlyList<string> StatusItems { get; }

    /// <summary>The right aligned status bar text.</summary>
    string StatusRight { get; }

    void OnShown();

    void OnHidden();
}

public sealed partial class ShellViewModel : ObservableObject {
    public const string LiveMapKey = "LiveMap";
    public const string ServerKey = "Server";
    public const string BotsKey = "Bots";
    public const string StatsKey = "Stats";
    public const string InstallKey = "Install";
    public const string MapsKey = "Maps";
    public const string OptionsKey = "Options";

    private static readonly TimeSpan StatusRefreshInterval = TimeSpan.FromMilliseconds(500);

    private readonly DispatcherTimer _statusTimer;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLiveMapSelected), nameof(IsServerSelected), nameof(IsBotsSelected), nameof(IsStatsSelected), nameof(IsInstallSelected),
        nameof(IsMapsSelected), nameof(IsOptionsSelected))]
    private IScreen _currentScreen;

    [ObservableProperty]
    private bool _isServerRunning;

    [ObservableProperty]
    private string _statusRight = "";

    /// <summary>What the modal layer shows over the whole window, null when nothing.</summary>
    [ObservableProperty]
    private ModalViewModel? _modal;

    public ShellViewModel(ServerConnection connection) {
        Connection = connection;
        var dialogs = new DialogService(modal => Modal = modal);
        // The icon color is a design token, so it is read here where the theme is loaded.
        var killIcons = new KillIcons(connection.Paths, (System.Windows.Media.Color)System.Windows.Application.Current.FindResource("KillIconColor"));
        LiveMap = new LiveMapViewModel(connection, dialogs, killIcons);
        Server = new ServerViewModel(connection, dialogs);
        Bots = new BotsViewModel(connection);
        Stats = new StatsViewModel(connection, dialogs, killIcons);
        Install = new InstallViewModel(connection, dialogs);
        InfoBar = new ServerInfoBarViewModel(connection);
        Maps = new MapsViewModel(connection, dialogs, PickOnServer);
        Options = new OptionsViewModel(connection, [
            new OptionChoice(LiveMapKey, "Live Map"),
            new OptionChoice(ServerKey, "Server"),
            new OptionChoice(BotsKey, "Bots"),
            new OptionChoice(MapsKey, "Maps"),
            new OptionChoice(StatsKey, "Stats"),
        ]);

        string startScreen = connection.Settings.StartScreen;
        _currentScreen = ScreenFor(string.IsNullOrEmpty(startScreen) ? connection.Settings.LastScreen : startScreen);
        _currentScreen.OnShown();

        _statusTimer = new DispatcherTimer(StatusRefreshInterval, DispatcherPriority.Background, (_, _) => RefreshStatus(), Dispatcher.CurrentDispatcher);
        _statusTimer.Start();
        RefreshStatus();
    }

    public ServerConnection Connection { get; }
    public LiveMapViewModel LiveMap { get; }
    public ServerViewModel Server { get; }
    public BotsViewModel Bots { get; }
    public StatsViewModel Stats { get; }
    public InstallViewModel Install { get; }
    public MapsViewModel Maps { get; }
    public OptionsViewModel Options { get; }
    public ServerInfoBarViewModel InfoBar { get; }

    public ObservableCollection<string> StatusItems { get; } = [];

    public bool IsLiveMapSelected {
        get => CurrentScreen == LiveMap;
        set => SelectIf(value, LiveMap);
    }

    public bool IsServerSelected {
        get => CurrentScreen == Server;
        set => SelectIf(value, Server);
    }

    public bool IsBotsSelected {
        get => CurrentScreen == Bots;
        set => SelectIf(value, Bots);
    }

    public bool IsStatsSelected {
        get => CurrentScreen == Stats;
        set => SelectIf(value, Stats);
    }

    public bool IsInstallSelected {
        get => CurrentScreen == Install;
        set => SelectIf(value, Install);
    }

    public bool IsMapsSelected {
        get => CurrentScreen == Maps;
        set => SelectIf(value, Maps);
    }

    public bool IsOptionsSelected {
        get => CurrentScreen == Options;
        set => SelectIf(value, Options);
    }

    public string CurrentScreenKey {
        get {
            if (CurrentScreen == Server) {
                return ServerKey;
            }
            if (CurrentScreen == Bots) {
                return BotsKey;
            }
            if (CurrentScreen == Stats) {
                return StatsKey;
            }
            if (CurrentScreen == Install) {
                return InstallKey;
            }
            if (CurrentScreen == Maps) {
                return MapsKey;
            }
            if (CurrentScreen == Options) {
                return OptionsKey;
            }
            return LiveMapKey;
        }
    }

    /// <summary>
    /// What the command line asked for: --install installs, --start starts
    /// the server. A package that is not installed yet always opens on the
    /// Install screen.
    /// </summary>
    public void RunStartupCommand(AppCommand command) {
        switch (command.Kind) {
            case AppCommandKind.Install:
                CurrentScreen = Install;
                _ = Install.InstallServer();
                return;
            case AppCommandKind.Start:
                if (!Connection.Paths.IsServerInstalled) {
                    CurrentScreen = Install;
                    return;
                }
                CurrentScreen = Server;
                if (!Connection.IsRunning) {
                    Server.StartCommand.Execute(null);
                }
                return;
            default:
                if (!Connection.Paths.IsServerInstalled) {
                    CurrentScreen = Install;
                }
                return;
        }
    }

    partial void OnCurrentScreenChanged(IScreen? oldValue, IScreen newValue) {
        oldValue?.OnHidden();
        newValue.OnShown();
        Connection.Settings.LastScreen = CurrentScreenKey;
        RefreshStatus();
    }

    /// <summary>The Maps screen's Pick: a draft on the Server screen, shown so Apply is in reach.</summary>
    private bool PickOnServer(string modeId, string mapName) {
        bool picked = Server.PickModeAndMap(modeId, mapName);
        CurrentScreen = Server;
        return picked;
    }

    private void SelectIf(bool isSelected, IScreen screen) {
        if (isSelected) {
            CurrentScreen = screen;
        }
    }

    private IScreen ScreenFor(string key) {
        if (key == ServerKey) {
            return Server;
        }
        if (key == BotsKey) {
            return Bots;
        }
        if (key == StatsKey) {
            return Stats;
        }
        if (key == InstallKey) {
            return Install;
        }
        if (key == MapsKey) {
            return Maps;
        }
        if (key == OptionsKey) {
            return Options;
        }
        return LiveMap;
    }

    private void RefreshStatus() {
        IsServerRunning = Connection.IsRunning;

        IReadOnlyList<string> items = CurrentScreen.StatusItems;
        bool changed = items.Count != StatusItems.Count || items.Where((item, index) => item != StatusItems[index]).Any();
        if (changed) {
            StatusItems.Clear();
            foreach (string item in items) {
                StatusItems.Add(item);
            }
        }
        StatusRight = CurrentScreen.StatusRight;
    }
}
