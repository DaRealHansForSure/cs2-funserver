using System.Collections.ObjectModel;
using System.Net;
using System.Text.RegularExpressions;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cs2ServerControl.Services;

public enum ConsoleLineKind {
    Normal,
    Plugin,
    Kill,
    Command,
}

public sealed partial record ConsoleLine(string Text, ConsoleLineKind Kind) {
    public static ConsoleLine FromServer(string text) {
        if (text.StartsWith("[Fun]", StringComparison.Ordinal) || PluginTag().IsMatch(text)) {
            return new ConsoleLine(text, ConsoleLineKind.Plugin);
        }
        if (text.Contains("\" killed \"", StringComparison.Ordinal)) {
            return new ConsoleLine(text, ConsoleLineKind.Kill);
        }
        return new ConsoleLine(text, ConsoleLineKind.Normal);
    }

    /// <summary>
    /// The engine echoes every RCON request into the log, password included,
    /// and the app polls every 2 seconds. Those lines are noise and must not
    /// put the password on screen; the app shows its own "] command" instead.
    /// </summary>
    public static bool IsRconEcho(string text) {
        return RconEcho().IsMatch(text);
    }

    [GeneratedRegex(@"\[[\w\-]+\.amxx\]")]
    private static partial Regex PluginTag();

    // "Rcon from 1.2.3.4:5:", "rcon 123 "pw" status", "L ...: Rcon: "rcon 123 ..."
    // and the '" from "1.2.3.4:5"' tail of that log line.
    [GeneratedRegex(@"^(Rcon from |rcon \d+ ""|L .*?: Rcon: ""rcon |"" from ""[\d.:]+""$|L .*?: Bad Rcon: )")]
    private static partial Regex RconEcho();
}

/// <summary>
/// Everything the screens share about the running server: process state,
/// RCON, the console log and the polled status values. Lives on the UI
/// thread; background events are marshalled through the dispatcher.
/// </summary>
public sealed partial class ServerConnection : ObservableObject, IDisposable {
    public const int MaxConsoleLines = 2000;

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _pollTimer;
    private readonly ConsoleLogTail _logTail;
    private bool _isPolling;
    private bool _triedLocalConfigPassword;
    private bool _didAttachSetup;
    private readonly ServerQuery _query = new();

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private DateTime? _startedAt;

    [ObservableProperty]
    private bool _isRconConnected;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string? _currentMap;

    [ObservableProperty]
    private string? _nextMap;

    [ObservableProperty]
    private string? _activeModeId;

    [ObservableProperty]
    private int _playerCount;

    [ObservableProperty]
    private int _maxPlayers = ServerProcess.DefaultMaxPlayers;

    [ObservableProperty]
    private int _botCount;

    [ObservableProperty]
    private string? _serverName;

    [ObservableProperty]
    private bool _isTvRunning;

    /// <summary>"12:34" from AMXX's amx_timeleft, null without a time limit.</summary>
    [ObservableProperty]
    private string? _timeLeft;

    public ServerConnection(PackagePaths paths, AppSettings settings, Dispatcher dispatcher) {
        Paths = paths;
        Settings = settings;
        _dispatcher = dispatcher;
        Process = new ServerProcess(paths);
        LanAddress = ServerProcess.ResolveLanAddress(settings.LanAddress);

        Rcon = new RconClient {
            Server = new IPEndPoint(LanAddress, ServerProcess.GamePort),
            Password = settings.RconPassword,
        };
        Rcon.ConnectionChanged += (_, _) => _dispatcher.BeginInvoke(() => IsRconConnected = Rcon.IsConnected);

        Feed = new LiveFeedListener();
        Feed.Start();
        Feed.SnapshotReceived += OnSnapshotReceived;

        _logTail = new ConsoleLogTail(paths.ConsoleLogFile);
        _logTail.LinesReceived += (_, lines) => _dispatcher.BeginInvoke(() => AddServerLines(lines));

        _pollTimer = new DispatcherTimer(PollInterval, DispatcherPriority.Background, async (_, _) => await PollAsync(), dispatcher);
        _pollTimer.Start();
        _ = PollAsync();
    }

    public PackagePaths Paths { get; }
    public AppSettings Settings { get; }
    public ServerProcess Process { get; }
    public RconClient Rcon { get; }
    public LiveFeedListener Feed { get; }
    public IPAddress LanAddress { get; private set; }
    public string GameAddress => $"{LanAddress}:{ServerProcess.GamePort}";
    private IPEndPoint QueryEndpoint => new(LanAddress, ServerProcess.GamePort);

    public ObservableCollection<ConsoleLine> ConsoleLines { get; } = [];

    /// <summary>Raised on the UI thread for every batch of new log lines.</summary>
    public event EventHandler<IReadOnlyList<string>>? ServerLinesReceived;

    /// <summary>Raised on the UI thread when the current map changed.</summary>
    public event EventHandler? MapChanged;

    public void Dispose() {
        _pollTimer.Stop();
        _logTail.Dispose();
        Feed.Dispose();
    }

    /// <summary>
    /// Sends a command, shows "] command" and the reply in the console.
    /// Returns the reply, or null when the server did not take it.
    /// </summary>
    public async Task<string?> RunAsync(string command) {
        AddConsoleLine(new ConsoleLine("] " + command, ConsoleLineKind.Command));
        try {
            string reply = await SendWithFallbackAsync(command);
            foreach (string line in RconReply.SplitLines(reply)) {
                AddConsoleLine(ConsoleLine.FromServer(line));
            }
            return reply;
        } catch (RconException exception) {
            AddConsoleLine(new ConsoleLine("RCON: " + exception.Message, ConsoleLineKind.Normal));
            return null;
        }
    }

    /// <summary>Sends a command without showing it, for polling. Null on failure.</summary>
    public async Task<string?> QueryAsync(string command) {
        try {
            return await SendWithFallbackAsync(command);
        } catch (RconException) {
            return null;
        }
    }

    /// <summary>
    /// Only while stopped: a running server stays bound to its address, and
    /// RCON and the query would then talk to an address it does not listen on.
    /// Empty picks the adapter with the default gateway.
    /// </summary>
    public bool ChangeLanAddress(string chosenAddress) {
        if (IsRunning) {
            return false;
        }
        Settings.LanAddress = chosenAddress;
        Settings.Save();
        LanAddress = ServerProcess.ResolveLanAddress(chosenAddress);
        Rcon.Server = new IPEndPoint(LanAddress, ServerProcess.GamePort);
        Rcon.Reset();
        OnPropertyChanged(nameof(LanAddress));
        OnPropertyChanged(nameof(GameAddress));
        return true;
    }

    /// <summary>
    /// Only while stopped: telling a running server the new password would
    /// write it into the console and the logs. The next start uses it.
    /// </summary>
    public bool RenewRconPassword() {
        if (IsRunning) {
            return false;
        }
        string password = AppSettings.CreatePassword();
        Settings.RconPassword = password;
        Settings.Save();
        // Before the first install there is no cstrike folder; the first start writes it.
        if (Paths.IsServerInstalled) {
            Process.WriteLocalConfigPassword(password);
        }
        Rcon.Password = password;
        Rcon.Reset();
        return true;
    }

    /// <summary>Starts on the given map when it is installed, else on the first map of the mode.</summary>
    public async Task StartAsync(string modeId, string? preferredMap = null) {
        if (IsRunning || IsBusy) {
            return;
        }

        bool isPreferredInstalled = preferredMap != null && System.IO.File.Exists(Paths.MapFile(preferredMap));
        string? startMap = isPreferredInstalled ? preferredMap : Process.FirstMapOfMode(modeId);
        if (startMap == null) {
            AddConsoleLine(new ConsoleLine($"No installed map for mode {modeId}. Run Update maps on the Install screen.", ConsoleLineKind.Normal));
            return;
        }

        IsBusy = true;
        try {
            Process.Start(modeId, startMap, Settings.MaxPlayers, LanAddress, Settings.RconPassword);
            Rcon.Password = Settings.RconPassword;
            Rcon.Reset();
            ActiveModeId = modeId;
            AddConsoleLine(new ConsoleLine($"Starting hlds.exe ({modeId}, {startMap}) on {GameAddress}", ConsoleLineKind.Normal));
        } catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or System.IO.IOException) {
            AddConsoleLine(new ConsoleLine(exception.Message, ConsoleLineKind.Normal));
        } finally {
            IsBusy = false;
        }
        await PollAsync();
    }

    public async Task StopAsync() {
        if (!IsRunning || IsBusy) {
            return;
        }

        IsBusy = true;
        try {
            await RunAsync("quit");
            await Process.WaitForExitOrKillAsync();
            Rcon.Reset();
        } finally {
            IsBusy = false;
        }
        await PollAsync();
    }

    public async Task RestartAsync(string modeId) {
        string restartMode = ActiveModeId ?? modeId;
        await StopAsync();
        await StartAsync(restartMode, CurrentMap);
    }

    public void AddConsoleLine(ConsoleLine line) {
        ConsoleLines.Add(line);
        while (ConsoleLines.Count > MaxConsoleLines) {
            ConsoleLines.RemoveAt(0);
        }
    }

    private void AddServerLines(IReadOnlyList<string> lines) {
        foreach (string line in lines) {
            if (ConsoleLine.IsRconEcho(line)) {
                continue;
            }
            AddConsoleLine(ConsoleLine.FromServer(line));

            Match mode = ActiveModePattern().Match(line);
            if (mode.Success) {
                ActiveModeId = mode.Groups["mode"].Value;
            }
        }
        ServerLinesReceived?.Invoke(this, lines);
    }

    private async Task<string> SendWithFallbackAsync(string command) {
        try {
            return await Rcon.SendAsync(command);
        } catch (RconException exception) when (exception.Message.Contains("password", StringComparison.OrdinalIgnoreCase) && !_triedLocalConfigPassword) {
            // A server started outside this window uses the password in
            // local.cfg. Try that one once, then keep using whatever worked.
            _triedLocalConfigPassword = true;
            string? localPassword = Process.ReadLocalConfigPassword();
            if (localPassword == null || localPassword == Rcon.Password) {
                throw;
            }
            Rcon.Password = localPassword;
            return await Rcon.SendAsync(command);
        }
    }

    private void OnSnapshotReceived(object? sender, LiveSnapshot snapshot) {
        _dispatcher.BeginInvoke(() => {
            if (!string.IsNullOrEmpty(snapshot.ModeId)) {
                ActiveModeId = snapshot.ModeId;
            }
            SetCurrentMap(snapshot.MapName);
        });
    }

    private void SetCurrentMap(string? mapName) {
        if (string.IsNullOrEmpty(mapName) || string.Equals(mapName, CurrentMap, StringComparison.OrdinalIgnoreCase)) {
            return;
        }
        CurrentMap = mapName;
        MapChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task PollAsync() {
        if (_isPolling) {
            return;
        }

        _isPolling = true;
        try {
            bool wasRunning = IsRunning;
            IsRunning = Process.IsRunning;
            IsTvRunning = Process.IsTvRunning;
            StartedAt = IsRunning ? Process.StartedAt : null;

            if (!IsRunning) {
                if (wasRunning) {
                    Process.StopFastDownload();
                    Process.StopTv();
                    Rcon.Reset();
                }
                PlayerCount = 0;
                BotCount = 0;
                return;
            }

            if (!wasRunning || !_didAttachSetup) {
                await AttachSetupAsync();
            }

            ServerInfo? info = await _query.QueryInfoAsync(QueryEndpoint);
            if (info == null) {
                return;
            }
            ServerName = info.Name;
            PlayerCount = info.Players;
            MaxPlayers = info.MaxPlayers;
            BotCount = info.Bots;
            SetCurrentMap(info.Map);

            IReadOnlyDictionary<string, string>? rules = await _query.QueryRulesAsync(QueryEndpoint);
            if (rules != null) {
                // fun_nextmap and fun_timeleft come from the FunServer plugin, which marks them for the server browser.
                if (rules.TryGetValue("fun_nextmap", out string? nextMap) && nextMap.Length > 0) {
                    NextMap = nextMap;
                }
                TimeLeft = rules.TryGetValue("fun_timeleft", out string? timeLeft) ? timeLeft : null;
            }
        } finally {
            _isPolling = false;
        }
    }

    /// <summary>
    /// Once per server start: when nothing told the window the mode yet (a
    /// server started outside it), the mode file says which one runs. No RCON
    /// call, because the server writes every RCON command into its logs.
    /// </summary>
    private Task AttachSetupAsync() {
        _didAttachSetup = true;
        if (ActiveModeId != null) {
            return Task.CompletedTask;
        }
        try {
            if (System.IO.File.Exists(Paths.ActiveModeFile)) {
                string modeId = System.IO.File.ReadAllText(Paths.ActiveModeFile).Trim();
                if (modeId.Length > 0) {
                    ActiveModeId = modeId;
                }
            }
        } catch (System.IO.IOException) {
            // Unknown mode; the Server screen then shows none as active.
        }
        return Task.CompletedTask;
    }

    [GeneratedRegex(@"Active mode:\s*(?<mode>\w+)")]
    private static partial Regex ActiveModePattern();
}
