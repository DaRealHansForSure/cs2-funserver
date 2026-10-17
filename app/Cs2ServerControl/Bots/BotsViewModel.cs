using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cs2ServerControl.Services;
using Cs2ServerControl.Shell;

namespace Cs2ServerControl.Bots;

public sealed record SelectOption(string Label, string Value);

public sealed partial class ActiveBotViewModel : ObservableObject {
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(KickText))]
    private bool _isKickPending;

    public ActiveBotViewModel(string name, Team team, string teamName, string skillText) {
        Name = name;
        Team = team;
        TeamName = teamName;
        SkillText = skillText;
    }

    public string Name { get; }
    public Team Team { get; }
    public string TeamName { get; }
    public string SkillText { get; }
    public string KickText => IsKickPending ? "Undo" : "Kick";
}

/// <summary>
/// The Bots screen works as a draft: settings and actions (kick, add,
/// reconnect) only collect until Apply, Discard drops them.
/// </summary>
public sealed partial class BotsViewModel : ObservableObject, IScreen {
    public static readonly IReadOnlyList<string> DifficultyNames = ["Easy", "Normal", "Hard", "Expert"];

    private static readonly IReadOnlyList<string> DifficultyHints = [
        "React slowly and aim poorly. Good for beginners.",
        "Solid opponents that still make mistakes.",
        "Hit well and play as a team. For experienced rounds.",
        "Very fast reactions and precise aim. Pros only.",
    ];

    private static readonly TimeSpan RescanInterval = TimeSpan.FromSeconds(5);

    private readonly ServerConnection _connection;
    private readonly DispatcherTimer _rescanTimer;
    private readonly HashSet<string> _pendingKicks = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyDictionary<string, int> _skills = new Dictionary<string, int>();
    private IReadOnlyList<LivePlayer> _lastBots = [];
    private int _humanCount;
    // What the server has, not the draft: the hint must not vanish before Apply.
    private bool _savedWaitForHuman = true;
    private bool _isLoading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DifficultyHint), nameof(IsEasy), nameof(IsNormal), nameof(IsHard), nameof(IsExpert))]
    private int _difficulty = 2;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HeaderInfo))]
    private string _quotaText = "8";

    [ObservableProperty]
    private SelectOption _quotaMode;

    [ObservableProperty]
    private SelectOption _joinTeam;

    [ObservableProperty]
    private bool _waitForHuman = true;

    [ObservableProperty]
    private bool _autoVacate = true;

    [ObservableProperty]
    private bool _deferToHuman = true;

    [ObservableProperty]
    private bool _allowPistols = true;

    [ObservableProperty]
    private bool _allowShotguns = true;

    [ObservableProperty]
    private bool _allowSubMachineGuns = true;

    [ObservableProperty]
    private bool _allowRifles = true;

    [ObservableProperty]
    private bool _allowSnipers;

    [ObservableProperty]
    private bool _allowMachineGuns;

    [ObservableProperty]
    private bool _allowGrenades = true;

    [ObservableProperty]
    private SelectOption _chatter;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    private bool _areSettingsChanged;

    /// <summary>True while local_bots.cfg holds values of the host; false means the package defaults.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SettingsSourceText))]
    private bool _hasCustomSettings;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty), nameof(PendingAddsText), nameof(HasPendingAdds))]
    private int _pendingTerroristAdds;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty), nameof(PendingAddsText), nameof(HasPendingAdds))]
    private int _pendingCounterTerroristAdds;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    private bool _isReconnectPending;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    private int _pendingActionCount;

    [ObservableProperty]
    private bool _isApplying;

    [ObservableProperty]
    private string? _applyError;

    public BotsViewModel(ServerConnection connection) {
        _connection = connection;
        _quotaMode = QuotaModeOptions[0];
        _joinTeam = JoinTeamOptions[0];
        _chatter = ChatterOptions[0];

        LoadSettingsFromConfig();
        _ = LoadSkillsAsync(connection.Paths);

        Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
        _connection.Feed.SnapshotReceived += (_, snapshot) => dispatcher.BeginInvoke(() => UpdateBots(snapshot));
        _connection.PropertyChanged += OnConnectionChanged;

        _rescanTimer = new DispatcherTimer(RescanInterval, DispatcherPriority.Background, (_, _) => Refresh(), dispatcher);
        _rescanTimer.Start();
    }

    public ServerConnection Connection => _connection;

    public IReadOnlyList<SelectOption> QuotaModeOptions { get; } = [
        new("Fill", "fill"),
        new("Fixed count", "normal"),
        new("Per player", "match"),
    ];

    // Upper case on purpose: zBot's quota balance check compares against "CT"
    // and "T" exactly, lower case would make it treat the value as "any".
    public IReadOnlyList<SelectOption> JoinTeamOptions { get; } = [
        new("Both teams", "any"),
        new("CT only", "CT"),
        new("T only", "T"),
    ];

    public IReadOnlyList<SelectOption> ChatterOptions { get; } = [
        new("Normal (voice callouts)", "normal"),
        new("Radio commands only", "radio"),
        new("Minimal", "minimal"),
        new("Off", "off"),
    ];

    public ObservableCollection<ActiveBotViewModel> ActiveBots { get; } = [];

    public string SettingsSourceText => HasCustomSettings ? "Your settings (local_bots.cfg)" : "Package defaults";

    public bool IsDirty => AreSettingsChanged || PendingActionCount > 0 || IsReconnectPending
        || PendingTerroristAdds > 0 || PendingCounterTerroristAdds > 0;

    public bool HasPendingAdds => PendingTerroristAdds > 0 || PendingCounterTerroristAdds > 0;

    public string PendingAddsText {
        get {
            var parts = new List<string>();
            if (PendingTerroristAdds > 0) {
                parts.Add($"+{PendingTerroristAdds} T");
            }
            if (PendingCounterTerroristAdds > 0) {
                parts.Add($"+{PendingCounterTerroristAdds} CT");
            }
            return string.Join(", ", parts) + " on Apply";
        }
    }

    public string DifficultyHint => DifficultyHints[Difficulty];

    public bool IsEasy {
        get => Difficulty == 0;
        set => SetDifficultyIf(value, 0);
    }

    public bool IsNormal {
        get => Difficulty == 1;
        set => SetDifficultyIf(value, 1);
    }

    public bool IsHard {
        get => Difficulty == 2;
        set => SetDifficultyIf(value, 2);
    }

    public bool IsExpert {
        get => Difficulty == 3;
        set => SetDifficultyIf(value, 3);
    }

    public int Quota {
        get {
            if (int.TryParse(QuotaText, out int quota)) {
                return Math.Clamp(quota, 0, ServerProcess.MaxPlayersLimit);
            }
            return 0;
        }
    }

    public string HeaderInfo => $"{ActiveBots.Count} bots active · fill up to {Quota} players";

    public IReadOnlyList<string> StatusItems => [$"Difficulty: {DifficultyNames[Difficulty]}"];

    public string StatusRight => "Saved in modes\\base.cfg";

    public void OnShown() {
        Refresh();
    }

    public void OnHidden() {
    }

    /// <summary>The bot cvars in the order they are written and sent.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> BuildCvars() {
        return [
            new("bot_difficulty", Difficulty.ToString()),
            new("bot_quota", Quota.ToString()),
            new("bot_quota_mode", QuotaMode.Value),
            new("bot_join_team", JoinTeam.Value),
            new("bot_join_after_player", Flag(WaitForHuman)),
            new("bot_auto_vacate", Flag(AutoVacate)),
            new("bot_defer_to_human", Flag(DeferToHuman)),
            new("bot_allow_pistols", Flag(AllowPistols)),
            new("bot_allow_shotguns", Flag(AllowShotguns)),
            new("bot_allow_sub_machine_guns", Flag(AllowSubMachineGuns)),
            new("bot_allow_rifles", Flag(AllowRifles)),
            new("bot_allow_snipers", Flag(AllowSnipers)),
            new("bot_allow_machine_guns", Flag(AllowMachineGuns)),
            new("bot_allow_grenades", Flag(AllowGrenades)),
            new("bot_chatter", Chatter.Value),
        ];
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e) {
        base.OnPropertyChanged(e);
        if (!_isLoading && IsSettingProperty(e.PropertyName)) {
            AreSettingsChanged = true;
        }
    }

    private static bool IsSettingProperty(string? propertyName) {
        switch (propertyName) {
            case nameof(Difficulty):
            case nameof(QuotaText):
            case nameof(QuotaMode):
            case nameof(JoinTeam):
            case nameof(WaitForHuman):
            case nameof(AutoVacate):
            case nameof(DeferToHuman):
            case nameof(AllowPistols):
            case nameof(AllowShotguns):
            case nameof(AllowSubMachineGuns):
            case nameof(AllowRifles):
            case nameof(AllowSnipers):
            case nameof(AllowMachineGuns):
            case nameof(AllowGrenades):
            case nameof(Chatter):
                return true;
            default:
                return false;
        }
    }

    [RelayCommand]
    private async Task Apply() {
        if (IsApplying) {
            return;
        }
        IsApplying = true;
        ApplyError = null;
        try {
            if (AreSettingsChanged && !await ApplySettingsAsync()) {
                return;
            }
            await ApplyBotActionsAsync();
        } finally {
            IsApplying = false;
        }
    }

    [RelayCommand]
    private void Discard() {
        ApplyError = null;
        LoadSettingsFromConfig();
        _pendingKicks.Clear();
        foreach (ActiveBotViewModel bot in ActiveBots) {
            bot.IsKickPending = false;
        }
        PendingTerroristAdds = 0;
        PendingCounterTerroristAdds = 0;
        IsReconnectPending = false;
        UpdatePendingCount();
    }

    [RelayCommand]
    private void ToggleReconnect() {
        IsReconnectPending = !IsReconnectPending;
    }

    [RelayCommand]
    private void AddTerroristBot() {
        PendingTerroristAdds++;
    }

    [RelayCommand]
    private void AddCounterTerroristBot() {
        PendingCounterTerroristAdds++;
    }

    [RelayCommand]
    private void ToggleKick(ActiveBotViewModel? bot) {
        if (bot == null) {
            return;
        }
        bot.IsKickPending = !bot.IsKickPending;
        if (bot.IsKickPending) {
            _pendingKicks.Add(bot.Name);
        } else {
            _pendingKicks.Remove(bot.Name);
        }
        UpdatePendingCount();
    }

    private void UpdatePendingCount() {
        PendingActionCount = _pendingKicks.Count;
    }

    private async Task<bool> ApplySettingsAsync() {
        IReadOnlyList<KeyValuePair<string, string>> cvars = BuildCvars();
        try {
            // Only into the host's own file; base.cfg keeps the package defaults.
            BotSettingsStore.SaveCustom(_connection.Paths, cvars, BotSettingsStore.ReadDefaults(_connection.Paths));
            HasCustomSettings = File.Exists(_connection.Paths.LocalBotConfigFile);
        } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
            ApplyError = "Could not write local_bots.cfg: " + exception.Message;
            return false;
        }

        if (_connection.IsRunning) {
            // One RCON call for all cvars: the engine runs ";" separated commands in order.
            await _connection.QueryAsync(string.Join(";", cvars.Select(cvar => $"{cvar.Key} \"{cvar.Value}\"")));
        }
        AreSettingsChanged = false;
        _savedWaitForHuman = WaitForHuman;
        OnPropertyChanged(nameof(IsWaitingForHuman));
        return true;
    }

    private async Task ApplyBotActionsAsync() {
        if (_connection.IsRunning) {
            foreach (string name in _pendingKicks) {
                await _connection.RunAsync($"bot_kick \"{name}\"");
            }
            if (IsReconnectPending) {
                // bot_difficulty only affects bots that join after it changed.
                await _connection.RunAsync("bot_kick");
                await _connection.RunAsync($"bot_quota {Quota}");
            }
            for (int index = 0; index < PendingTerroristAdds; index++) {
                await _connection.RunAsync("bot_add_t");
            }
            for (int index = 0; index < PendingCounterTerroristAdds; index++) {
                await _connection.RunAsync("bot_add_ct");
            }
        }

        _pendingKicks.Clear();
        foreach (ActiveBotViewModel bot in ActiveBots) {
            bot.IsKickPending = false;
        }
        IsReconnectPending = false;
        PendingTerroristAdds = 0;
        PendingCounterTerroristAdds = 0;
        UpdatePendingCount();
    }

    private void SetDifficultyIf(bool isSelected, int level) {
        if (isSelected) {
            Difficulty = level;
        }
    }

    private static string Flag(bool value) {
        return value ? "1" : "0";
    }

    /// <summary>The package defaults with the host's own values from local_bots.cfg on top.</summary>
    private void LoadSettingsFromConfig() {
        IReadOnlyDictionary<string, string> defaults = BotSettingsStore.ReadDefaults(_connection.Paths);
        IReadOnlyDictionary<string, string> custom = BotSettingsStore.ReadCustom(_connection.Paths, defaults.Keys);
        var values = new Dictionary<string, string>(defaults, StringComparer.OrdinalIgnoreCase);
        foreach ((string cvar, string value) in custom) {
            values[cvar] = value;
        }
        ShowValues(values);
        HasCustomSettings = custom.Count > 0;
        AreSettingsChanged = false;
        _savedWaitForHuman = WaitForHuman;
        OnPropertyChanged(nameof(IsWaitingForHuman));
    }

    /// <summary>
    /// Back to the package defaults, as a draft like every change: Apply then
    /// removes local_bots.cfg, Discard keeps the host's values.
    /// </summary>
    [RelayCommand]
    private void ResetToDefaults() {
        ShowValues(BotSettingsStore.ReadDefaults(_connection.Paths));
        AreSettingsChanged = HasCustomSettings;
    }

    private void ShowValues(IReadOnlyDictionary<string, string> values) {
        _isLoading = true;
        try {
            if (values.TryGetValue("bot_difficulty", out string? difficulty) && int.TryParse(difficulty, out int level)) {
                Difficulty = Math.Clamp(level, 0, 3);
            }
            if (values.TryGetValue("bot_quota", out string? quota)) {
                QuotaText = quota;
            }
            QuotaMode = PickOption(QuotaModeOptions, values, "bot_quota_mode", QuotaMode);
            JoinTeam = PickOption(JoinTeamOptions, values, "bot_join_team", JoinTeam);
            Chatter = PickOption(ChatterOptions, values, "bot_chatter", Chatter);
            WaitForHuman = ReadFlag(values, "bot_join_after_player", WaitForHuman);
            AutoVacate = ReadFlag(values, "bot_auto_vacate", AutoVacate);
            DeferToHuman = ReadFlag(values, "bot_defer_to_human", DeferToHuman);
            AllowPistols = ReadFlag(values, "bot_allow_pistols", AllowPistols);
            AllowShotguns = ReadFlag(values, "bot_allow_shotguns", AllowShotguns);
            AllowSubMachineGuns = ReadFlag(values, "bot_allow_sub_machine_guns", AllowSubMachineGuns);
            AllowRifles = ReadFlag(values, "bot_allow_rifles", AllowRifles);
            AllowSnipers = ReadFlag(values, "bot_allow_snipers", AllowSnipers);
            AllowMachineGuns = ReadFlag(values, "bot_allow_machine_guns", AllowMachineGuns);
            AllowGrenades = ReadFlag(values, "bot_allow_grenades", AllowGrenades);
        } finally {
            _isLoading = false;
        }
    }

    private static SelectOption PickOption(IReadOnlyList<SelectOption> options, IReadOnlyDictionary<string, string> values, string cvar, SelectOption fallback) {
        if (!values.TryGetValue(cvar, out string? value)) {
            return fallback;
        }
        return options.FirstOrDefault(option => string.Equals(option.Value, value, StringComparison.OrdinalIgnoreCase)) ?? fallback;
    }

    private static bool ReadFlag(IReadOnlyDictionary<string, string> values, string cvar, bool fallback) {
        if (!values.TryGetValue(cvar, out string? value)) {
            return fallback;
        }
        return value != "0";
    }

    private void Refresh() {
        if (!_connection.Feed.HasLiveData) {
            UpdateBots(null);
        }
    }

    private void RefreshActiveBots() {
        var bots = _lastBots
            .OrderBy(bot => bot.Team == Team.Terrorist ? 0 : 1)
            .ThenBy(bot => bot.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        bool same = bots.Count == ActiveBots.Count
            && bots.Zip(ActiveBots).All(pair => pair.First.Name == pair.Second.Name && pair.First.Team == pair.Second.Team);
        if (same) {
            return;
        }

        ActiveBots.Clear();
        foreach (LivePlayer bot in bots) {
            ActiveBots.Add(new ActiveBotViewModel(bot.Name, bot.Team, TeamName(bot.Team), SkillText(bot.Name)) {
                IsKickPending = _pendingKicks.Contains(bot.Name),
            });
        }
        OnPropertyChanged(nameof(HeaderInfo));
    }

    private void UpdateBots(LiveSnapshot? snapshot) {
        _lastBots = snapshot == null ? [] : snapshot.Players.Where(player => player.IsBot).ToList();
        int humans = snapshot == null ? 0 : snapshot.Players.Count(player => !player.IsBot);
        if (humans != _humanCount) {
            _humanCount = humans;
            OnPropertyChanged(nameof(IsWaitingForHuman));
        }
        RefreshActiveBots();
    }

    /// <summary>
    /// CS2 keeps every bot out of the teams while "wait for a human" is on and
    /// nobody plays, added bots too; they then sit on the server unseen.
    /// </summary>
    public bool IsWaitingForHuman => _connection.IsRunning && _savedWaitForHuman && _humanCount == 0;

    /// <summary>In the background: the profile sits in the game's big package, whose directory takes a moment to read.</summary>
    private async Task LoadSkillsAsync(PackagePaths paths) {
        _skills = await Task.Run(() => BotProfileReader.ReadSkills(paths));
        // The rows built before carry "Skill -"; rebuild them with the skills.
        ActiveBots.Clear();
        RefreshActiveBots();
    }

    private string SkillText(string botName) {
        if (_skills.TryGetValue(botName, out int skill)) {
            return $"Skill {skill}";
        }
        return "Skill -";
    }

    private static string TeamName(Team team) {
        switch (team) {
            case Team.Terrorist:
                return "Terrorists";
            case Team.CounterTerrorist:
                return "Counter-Terrorists";
            default:
                return "Spectators";
        }
    }

    private void OnConnectionChanged(object? sender, PropertyChangedEventArgs e) {
        if (e.PropertyName != nameof(ServerConnection.IsRunning)) {
            return;
        }
        if (!_connection.IsRunning) {
            UpdateBots(null);
        }
        OnPropertyChanged(nameof(IsWaitingForHuman));
    }
}
