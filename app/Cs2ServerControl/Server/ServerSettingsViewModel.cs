using System.ComponentModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using Cs2ServerControl.Services;

namespace Cs2ServerControl.Server;

/// <summary>
/// Server settings of this PC (name, join password, max players, fun rounds,
/// hook, CSTV). Part of the Server screen's draft: changes wait for Apply.
/// Cvars go into cfg\fun\local.cfg, which the FunServer plugin runs after
/// the mode configs on every map, and to the running server over RCON.
/// </summary>
public sealed partial class ServerSettingsViewModel : ObservableObject {
    private const string DefaultServerName = "LAN Fun Server";
    private const int MinPlayers = 2;
    private const int MinFunRoundsEvery = 1;
    private const int MaxFunRoundsEvery = 20;
    private const int MinHookSpeed = 200;
    private const int MaxHookSpeed = 3000;

    private readonly ServerConnection _connection;
    private bool _isLoading;

    [ObservableProperty]
    private string _serverName = DefaultServerName;

    [ObservableProperty]
    private string _joinPassword = "";

    [ObservableProperty]
    private string _maxPlayersText = ServerProcess.DefaultMaxPlayers.ToString(CultureInfo.InvariantCulture);

    [ObservableProperty]
    private bool _funRoundsEnabled = true;

    [ObservableProperty]
    private string _funRoundsEveryText = "3";

    [ObservableProperty]
    private string _hookSpeedText = "750";

    [ObservableProperty]
    private bool _hookAdminOnly;

    [ObservableProperty]
    private bool _isTvWanted;

    /// <summary>CSTV as saved in local.cfg, for the On/Off label.</summary>
    [ObservableProperty]
    private bool _isTvOn;

    [ObservableProperty]
    private bool _isChanged;

    [ObservableProperty]
    private string? _error;

    public ServerSettingsViewModel(ServerConnection connection) {
        _connection = connection;
        Load();
        _connection.PropertyChanged += OnConnectionChanged;
    }

    public string TvAddressText => $"On the TV PC: connect {_connection.LanAddress}:{ServerProcess.TvPort}";

    /// <summary>Max players only takes effect when cs2.exe starts, so say so while it runs.</summary>
    public bool IsMaxPlayersPending => _connection.IsRunning && ParseMaxPlayers() != _connection.MaxPlayers;

    public void Load() {
        var settings = new LocalSettings(_connection.Paths.LocalConfigFile);
        _isLoading = true;
        try {
            ServerName = settings.Get("hostname") ?? DefaultServerName;
            JoinPassword = settings.Get("sv_password") ?? "";
            FunRoundsEnabled = settings.Get("fun_rounds_enabled") != "0";
            FunRoundsEveryText = settings.Get("fun_rounds_every") ?? "3";
            HookSpeedText = settings.Get("fun_hook_speed") ?? "750";
            HookAdminOnly = settings.Get("fun_hook_admin_only") == "1";
            MaxPlayersText = _connection.Settings.MaxPlayers.ToString(CultureInfo.InvariantCulture);
            IsTvWanted = settings.Get("tv_enable") == "1";
            IsTvOn = IsTvWanted;
        } finally {
            _isLoading = false;
        }
        Error = null;
        IsChanged = false;
    }

    /// <summary>Saves and sends the settings. False with Error set when something is invalid.</summary>
    public async Task<bool> ApplyAsync() {
        if (!IsChanged) {
            return true;
        }
        if (!TryReadNumbers(out int maxPlayers, out int funRoundsEvery, out int hookSpeed)) {
            return false;
        }

        var cvars = new List<KeyValuePair<string, string>> {
            new("hostname", ServerName.Trim().Length == 0 ? DefaultServerName : ServerName.Trim()),
            new("sv_password", JoinPassword.Trim()),
            new("fun_rounds_enabled", FunRoundsEnabled ? "1" : "0"),
            new("fun_rounds_every", funRoundsEvery.ToString(CultureInfo.InvariantCulture)),
            new("fun_hook_speed", hookSpeed.ToString(CultureInfo.InvariantCulture)),
            new("fun_hook_admin_only", HookAdminOnly ? "1" : "0"),
            // CS2 starts CSTV only while a map loads, so a running server
            // broadcasts from the next map on.
            new("tv_enable", IsTvWanted ? "1" : "0"),
        };

        try {
            var settings = new LocalSettings(_connection.Paths.LocalConfigFile);
            foreach (KeyValuePair<string, string> cvar in cvars) {
                settings.Set(cvar.Key, cvar.Value);
            }
            settings.Save();
        } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
            Error = "Could not write local.cfg: " + exception.Message;
            return false;
        }

        _connection.Settings.MaxPlayers = maxPlayers;
        _connection.Settings.Save();

        if (_connection.IsRunning) {
            // Quotes are stripped like in local.cfg; the engine has no escaping.
            await _connection.QueryAsync(string.Join(";", cvars.Select(cvar => $"{cvar.Key} \"{cvar.Value.Replace("\"", "")}\"")));
        }
        IsTvOn = IsTvWanted;

        Error = null;
        IsChanged = false;
        OnPropertyChanged(nameof(IsMaxPlayersPending));
        return true;
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e) {
        base.OnPropertyChanged(e);
        bool isSetting = e.PropertyName is nameof(ServerName) or nameof(JoinPassword) or nameof(MaxPlayersText)
            or nameof(FunRoundsEnabled) or nameof(FunRoundsEveryText) or nameof(HookSpeedText) or nameof(HookAdminOnly)
            or nameof(IsTvWanted);
        if (isSetting && !_isLoading) {
            IsChanged = true;
        }
    }

    private bool TryReadNumbers(out int maxPlayers, out int funRoundsEvery, out int hookSpeed) {
        maxPlayers = ParseMaxPlayers();
        bool everyOk = int.TryParse(FunRoundsEveryText, out funRoundsEvery);
        bool speedOk = int.TryParse(HookSpeedText, out hookSpeed);

        if (maxPlayers < MinPlayers || maxPlayers > ServerProcess.MaxPlayersLimit) {
            Error = $"Max players must be between {MinPlayers} and {ServerProcess.MaxPlayersLimit}.";
            return false;
        }
        if (!everyOk || funRoundsEvery < MinFunRoundsEvery || funRoundsEvery > MaxFunRoundsEvery) {
            Error = $"Fun round every: {MinFunRoundsEvery} to {MaxFunRoundsEvery} rounds.";
            return false;
        }
        if (!speedOk || hookSpeed < MinHookSpeed || hookSpeed > MaxHookSpeed) {
            Error = $"Hook speed: {MinHookSpeed} to {MaxHookSpeed}.";
            return false;
        }
        return true;
    }

    private int ParseMaxPlayers() {
        return int.TryParse(MaxPlayersText, out int value) ? value : 0;
    }

    private void OnConnectionChanged(object? sender, PropertyChangedEventArgs e) {
        if (e.PropertyName is nameof(ServerConnection.IsRunning) or nameof(ServerConnection.MaxPlayers)) {
            OnPropertyChanged(nameof(IsMaxPlayersPending));
        }
    }
}
