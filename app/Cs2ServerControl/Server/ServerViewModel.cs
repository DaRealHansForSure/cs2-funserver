using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cs2ServerControl.Services;
using Cs2ServerControl.Shell;

namespace Cs2ServerControl.Server;

public sealed partial class ModeTileViewModel : ObservableObject {
    private readonly Action<ModeTileViewModel> _onPicked;

    [ObservableProperty]
    private bool _isPicked;

    [ObservableProperty]
    private bool _isActive;

    public ModeTileViewModel(GameMode mode, Action<ModeTileViewModel> onPicked) {
        Mode = mode;
        _onPicked = onPicked;
    }

    public GameMode Mode { get; }
    public string Title => Mode.Title;
    public string Description => Mode.Description;

    partial void OnIsPickedChanged(bool value) {
        if (value) {
            _onPicked(this);
        }
    }
}

public sealed partial class MapRowViewModel : ObservableObject {
    private readonly Action<MapRowViewModel> _onPicked;

    /// <summary>False while the map search does not match; the row stays in the list for the draft.</summary>
    [ObservableProperty]
    private bool _isShown = true;

    [ObservableProperty]
    private bool _isPicked;

    [ObservableProperty]
    private bool _isCurrent;

    public MapRowViewModel(string name, ImageSource? thumbnail, Action<MapRowViewModel> onPicked) {
        Name = name;
        _thumbnail = thumbnail;
        _onPicked = onPicked;
    }

    public string Name { get; }
    [ObservableProperty]
    private ImageSource? _thumbnail;

    partial void OnIsPickedChanged(bool value) {
        if (value) {
            _onPicked(this);
        }
    }
}

/// <summary>
/// The Server screen. Mode and map work as a draft: picking a tile or a map
/// only marks the screen dirty, Apply switches the running server to exactly
/// that mode and map, Discard goes back to what runs. While the server is
/// stopped the picks are simply what Start uses.
/// </summary>
public sealed partial class ServerViewModel : ObservableObject, IScreen {
    public static readonly IReadOnlyList<string> QuickCommands = [
        "fun_mode gungame",
        "bot_kick",
        "sv_restart 1",
        "fun_glow @all pink",
    ];

    private readonly ServerConnection _connection;
    private readonly IDialogService _dialogs;
    private readonly MapThumbnails _thumbnails;
    private readonly DispatcherTimer _uptimeTimer;
    private readonly List<string> _history = [];
    private int _historyIndex;
    private bool _isSyncing;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MapsTitle), nameof(IsDirty))]
    private ModeTileViewModel _pickedMode;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    private MapRowViewModel? _pickedMap;

    /// <summary>True after the user picked something; until then the picks follow the server.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    private bool _hasDraft;

    [ObservableProperty]
    private string _uptimeText = "";

    [ObservableProperty]
    private string _commandText = "";

    [ObservableProperty]
    private string _mapSearchText = "";

    public ServerViewModel(ServerConnection connection, IDialogService dialogs) {
        _connection = connection;
        _dialogs = dialogs;
        _thumbnails = new MapThumbnails(connection.Paths);
        Settings = new ServerSettingsViewModel(connection);
        Settings.PropertyChanged += (_, e) => {
            if (e.PropertyName == nameof(ServerSettingsViewModel.IsChanged)) {
                OnPropertyChanged(nameof(IsDirty));
            }
        };
        Rules = new GameRulesViewModel(connection);
        Rules.PropertyChanged += (_, e) => {
            if (e.PropertyName == nameof(GameRulesViewModel.IsChanged)) {
                OnPropertyChanged(nameof(IsDirty));
            }
        };
        Round = new RoundActionsViewModel(connection);
        // A picture rendered on the Maps screen shows up here at once.
        MapThumbnails.PictureChanged += OnPictureChanged;

        foreach (GameMode mode in GameModes.All) {
            ModeTiles.Add(new ModeTileViewModel(mode, OnModePicked));
        }
        _pickedMode = ModeTiles[0];
        SyncPicksToServer();

        _connection.PropertyChanged += OnConnectionChanged;
        _uptimeTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => UpdateUptime(), Dispatcher.CurrentDispatcher);
        _uptimeTimer.Start();
        UpdateUptime();
    }

    public ServerConnection Connection => _connection;
    public ServerSettingsViewModel Settings { get; }
    public GameRulesViewModel Rules { get; }
    public RoundActionsViewModel Round { get; }
    public ObservableCollection<ModeTileViewModel> ModeTiles { get; } = [];
    public ObservableCollection<MapRowViewModel> Maps { get; } = [];
    public ObservableCollection<ConsoleLine> ConsoleLines => _connection.ConsoleLines;
    public IReadOnlyList<string> QuickCommandList => QuickCommands;

    public bool IsRunning => _connection.IsRunning;
    public string MapsTitle => $"Maps for {PickedMode.Title}";
    public string MapsInfo => $"Now: {_connection.CurrentMap ?? "-"} · Next: {_connection.NextMap ?? "-"}";
    public string ConsoleInfo => $"hlds.exe · {_connection.GameAddress}";

    /// <summary>
    /// Settings can be saved any time; mode and map only differ from a
    /// running server.
    /// </summary>
    public bool IsDirty => Settings.IsChanged || Rules.IsChanged || IsModeOrMapDirty;

    private bool IsModeOrMapDirty {
        get {
            if (!HasDraft || !_connection.IsRunning) {
                return false;
            }
            bool modeDiffers = PickedMode.Mode.Id != _connection.ActiveModeId;
            bool mapDiffers = PickedMap != null && !string.Equals(PickedMap.Name, _connection.CurrentMap, StringComparison.OrdinalIgnoreCase);
            return modeDiffers || mapDiffers;
        }
    }

    public IReadOnlyList<string> StatusItems => [
        $"{_connection.PlayerCount} / {_connection.MaxPlayers} players · {_connection.BotCount} bots",
        $"Fast download on port {ServerProcess.FastDownloadPort}",
    ];

    public string StatusRight => _connection.IsRconConnected ? "RCON connected" : "RCON disconnected";

    public void OnShown() {
    }

    public void OnHidden() {
    }

    [RelayCommand]
    private async Task Start() {
        await AskForFirewallRulesOnceAsync();
        await _connection.StartAsync(PickedMode.Mode.Id, PickedMap?.Name);
        HasDraft = false;
    }

    [RelayCommand]
    private async Task Stop() {
        await _connection.StopAsync();
    }

    [RelayCommand]
    private async Task Restart() {
        await _connection.RestartAsync(PickedMode.Mode.Id);
    }

    /// <summary>
    /// Switches the running server to the picked mode and map at once. The
    /// mode file and game_type/game_mode are set before the map change, so the
    /// new map starts in that mode and the mode plugin runs its configs there.
    /// </summary>
    [RelayCommand]
    private async Task Apply() {
        // Settings first, so a new map starts with them already in local.cfg.
        if (!await Settings.ApplyAsync()) {
            return;
        }
        // The rules too: a map change below then starts with them.
        string? rulesError = await Rules.ApplyAsync();
        if (rulesError != null) {
            Settings.Error = rulesError;
            return;
        }

        if (IsModeOrMapDirty && PickedMap != null) {
            string modeId = PickedMode.Mode.Id;
            if (modeId != _connection.ActiveModeId) {
                _connection.Process.WriteActiveMode(modeId);
                await _connection.RunAsync($"game_type {PickedMode.Mode.GameType}; game_mode {PickedMode.Mode.GameModeNumber}");
                _connection.ActiveModeId = modeId;
            }
            await _connection.RunAsync($"changelevel {PickedMap.Name}");
        }
        HasDraft = false;
        OnPropertyChanged(nameof(IsDirty));
    }

    [RelayCommand]
    private void Discard() {
        HasDraft = false;
        Settings.Load();
        Rules.Load();
        SyncPicksToServer();
        OnPropertyChanged(nameof(IsDirty));
    }

    [RelayCommand]
    private async Task SetNextMap(MapRowViewModel? row) {
        MapRowViewModel? target = row ?? PickedMap;
        if (target == null) {
            return;
        }
        await _connection.RunAsync($"nextlevel {target.Name}");
        _connection.NextMap = target.Name;
    }

    [RelayCommand]
    private async Task ShowMapDetails(MapRowViewModel? row) {
        if (row == null) {
            return;
        }
        var details = new MapDetailsViewModel(_connection, row.Name, PickedMode.Title, pickMap: () => row.IsPicked = true,
            setNextMap: () => SetNextMap(row));
        await _dialogs.ShowAsync(details);
    }

    /// <summary>
    /// The Maps screen's "Pick": the same draft as clicking the mode tile and
    /// the map row here, so Apply and Discard work as usual. False when the
    /// mode has no tile or the map is not in its cycle.
    /// </summary>
    public bool PickModeAndMap(string modeId, string mapName) {
        ModeTileViewModel? tile = ModeTiles.FirstOrDefault(candidate => candidate.Mode.Id == modeId);
        if (tile == null) {
            return false;
        }
        tile.IsPicked = true;
        MapRowViewModel? row = Maps.FirstOrDefault(candidate => string.Equals(candidate.Name, mapName, StringComparison.OrdinalIgnoreCase));
        if (row == null) {
            return false;
        }
        row.IsPicked = true;
        return true;
    }

    [RelayCommand]
    private async Task SendCommand() {
        string command = CommandText.Trim();
        if (command.Length == 0) {
            return;
        }

        CommandText = "";
        _history.Remove(command);
        _history.Add(command);
        _historyIndex = _history.Count;
        await _connection.RunAsync(command);
    }

    [RelayCommand]
    private async Task SendQuickCommand(string command) {
        await _connection.RunAsync(command);
    }

    [RelayCommand]
    private void HistoryPrevious() {
        if (_history.Count == 0) {
            return;
        }
        _historyIndex = Math.Max(0, _historyIndex - 1);
        CommandText = _history[_historyIndex];
    }

    [RelayCommand]
    private void HistoryNext() {
        if (_history.Count == 0) {
            return;
        }
        _historyIndex = Math.Min(_history.Count, _historyIndex + 1);
        CommandText = _historyIndex < _history.Count ? _history[_historyIndex] : "";
    }

    private void OnModePicked(ModeTileViewModel tile) {
        Rules.FollowMode(tile.Mode.Id);
        foreach (ModeTileViewModel other in ModeTiles.Where(other => other != tile)) {
            other.IsPicked = false;
        }
        bool modeChanged = PickedMode != tile;
        PickedMode = tile;
        if (modeChanged) {
            LoadMaps(preferredMap: null);
        }
        MarkDraft();
    }

    private void OnMapPicked(MapRowViewModel row) {
        foreach (MapRowViewModel other in Maps.Where(other => other != row)) {
            other.IsPicked = false;
        }
        PickedMap = row;
        MarkDraft();
    }

    private void MarkDraft() {
        if (!_isSyncing) {
            HasDraft = true;
        }
    }

    /// <summary>Puts the picks on what the server runs, as long as the user has no draft.</summary>
    private void SyncPicksToServer() {
        _isSyncing = true;
        try {
            ModeTileViewModel tile = ModeTiles.FirstOrDefault(candidate => candidate.Mode.Id == _connection.ActiveModeId) ?? PickedMode;
            tile.IsPicked = true;
            PickedMode = tile;
            LoadMaps(_connection.CurrentMap);
            UpdateMapMarkers();
        } finally {
            _isSyncing = false;
        }
    }

    private void LoadMaps(string? preferredMap) {
        bool wasSyncing = _isSyncing;
        _isSyncing = true;
        try {
            Maps.Clear();
            foreach (string map in MapCycleReader.Read(_connection.Paths.MapCycleFile(PickedMode.Mode.Id))) {
                Maps.Add(new MapRowViewModel(map, _thumbnails.Get(map), OnMapPicked));
            }

            ApplyMapSearch();
            PickedMap = null;
            MapRowViewModel? preferred = Maps.FirstOrDefault(row => string.Equals(row.Name, preferredMap, StringComparison.OrdinalIgnoreCase));
            MapRowViewModel? first = preferred ?? Maps.FirstOrDefault();
            if (first != null) {
                first.IsPicked = true;
            }
            UpdateMapMarkers();
        } finally {
            _isSyncing = wasSyncing;
        }
    }

    partial void OnMapSearchTextChanged(string value) {
        ApplyMapSearch();
    }

    private void ApplyMapSearch() {
        string search = MapSearchText.Trim();
        foreach (MapRowViewModel row in Maps) {
            row.IsShown = search.Length == 0 || row.Name.Contains(search, StringComparison.OrdinalIgnoreCase);
        }
    }

    private void OnPictureChanged(object? sender, string mapName) {
        foreach (MapRowViewModel row in Maps.Where(row => string.Equals(row.Name, mapName, StringComparison.OrdinalIgnoreCase))) {
            row.Thumbnail = _thumbnails.Get(mapName);
        }
    }

    private void UpdateMapMarkers() {
        foreach (MapRowViewModel row in Maps) {
            row.IsCurrent = _connection.IsRunning && string.Equals(row.Name, _connection.CurrentMap, StringComparison.OrdinalIgnoreCase);
        }
        foreach (ModeTileViewModel tile in ModeTiles) {
            tile.IsActive = _connection.IsRunning && tile.Mode.Id == _connection.ActiveModeId;
        }
    }

    private void UpdateUptime() {
        if (!_connection.IsRunning || _connection.StartedAt == null) {
            UptimeText = "Stopped";
            return;
        }
        TimeSpan uptime = DateTime.Now - _connection.StartedAt.Value;
        UptimeText = $"Running for {(int)uptime.TotalHours:00}:{uptime.Minutes:00}:{uptime.Seconds:00}";
    }

    private void OnConnectionChanged(object? sender, PropertyChangedEventArgs e) {
        switch (e.PropertyName) {
            case nameof(ServerConnection.IsRunning):
                OnPropertyChanged(nameof(IsRunning));
                OnPropertyChanged(nameof(IsDirty));
                UpdateMapMarkers();
                UpdateUptime();
                break;
            case nameof(ServerConnection.ActiveModeId):
            case nameof(ServerConnection.CurrentMap):
                if (!HasDraft) {
                    SyncPicksToServer();
                }
                UpdateMapMarkers();
                OnPropertyChanged(nameof(IsDirty));
                OnPropertyChanged(nameof(MapsInfo));
                break;
            case nameof(ServerConnection.NextMap):
                OnPropertyChanged(nameof(MapsInfo));
                break;
            case nameof(ServerConnection.StartedAt):
                UpdateUptime();
                break;
        }
    }

    /// <summary>
    /// The first start from this window offers the inbound rules once. The
    /// live feed port is never opened, it only talks to this PC.
    /// </summary>
    private async Task AskForFirewallRulesOnceAsync() {
        if (_connection.Settings.FirewallAsked) {
            return;
        }
        _connection.Settings.FirewallAsked = true;
        _connection.Settings.Save();

        if (!ServerProcess.AreFirewallRulesMissing()) {
            return;
        }
        bool add = await _dialogs.ConfirmAsync(
            "Windows Firewall",
            "Other PCs can only join when Windows Firewall lets them in. Add inbound rules for UDP/TCP 27015, TCP 27080 and the TV on UDP 27020 now? Windows asks for administrator rights.",
            "Add rules");
        if (add) {
            await ServerProcess.AddFirewallRulesAsync();
        }
    }
}
