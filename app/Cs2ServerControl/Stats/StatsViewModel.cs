using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cs2ServerControl.Services;
using Cs2ServerControl.Shell;

namespace Cs2ServerControl.Stats;

public sealed record LeaderboardRow(int Rank, PlayerStats Stats) {
    public string KillDeathText => Stats.KillDeathRatio.ToString("0.00", CultureInfo.InvariantCulture);
    public string WeaponText => WeaponNames.Display(Stats.FavoriteWeapon == "-" ? null : Stats.FavoriteWeapon);
}

public sealed record MapPlayRow(MapPlay Play) {
    public string StartText => Play.StartedAt.ToString("HH:mm", CultureInfo.InvariantCulture);
    public string ModeTitle => Play.ModeId == null ? "-" : GameModes.TitleOf(Play.ModeId);

    public string DurationText {
        get {
            if (Play.IsRunning) {
                return StatsViewModel.FormatDuration(Play.Duration) + " (now)";
            }
            if (Play.Duration == TimeSpan.Zero) {
                return "-";
            }
            return StatsViewModel.FormatDuration(Play.Duration);
        }
    }
}

public sealed record StatsPeriod(string Title, TimeSpan? Length);

/// <summary>
/// The evening in numbers, read from the logs the server writes anyway:
/// leaderboard, maps played, and how far the current map is.
/// </summary>
public sealed partial class StatsViewModel : ObservableObject, IScreen {
    // A LAN evening rarely lasts longer, and older logs belong to another party.
    private static readonly TimeSpan EveningLength = TimeSpan.FromHours(12);
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(10);

    private readonly ServerConnection _connection;
    private readonly StatsReader _reader;
    private readonly DispatcherTimer _refreshTimer;
    private DateTime? _mapStartedAt;
    private bool _isRefreshing;

    [ObservableProperty]
    private StatsPeriod _selectedPeriod;

    [ObservableProperty]
    private bool _hideBots = true;

    [ObservableProperty]
    private string _summary = "";

    /// <summary>0 to 100: how much of the map's time limit is used up.</summary>
    [ObservableProperty]
    private double _mapProgress;

    [ObservableProperty]
    private string _mapProgressText = "";

    public StatsViewModel(ServerConnection connection) {
        _connection = connection;
        _reader = new StatsReader(connection.Paths);
        _selectedPeriod = Periods[0];
        _refreshTimer = new DispatcherTimer(RefreshInterval, DispatcherPriority.Background, async (_, _) => await RefreshAsync(), Dispatcher.CurrentDispatcher);
        _connection.PropertyChanged += (_, e) => {
            if (e.PropertyName is nameof(ServerConnection.TimeLeft) or nameof(ServerConnection.IsRunning)) {
                UpdateMapProgress();
            }
        };
    }

    public IReadOnlyList<StatsPeriod> Periods { get; } = [
        new("This map", null),
        new("Last 12 hours", EveningLength),
    ];

    public bool IsThisMap {
        get => SelectedPeriod == Periods[0];
        set {
            if (value) {
                SelectedPeriod = Periods[0];
            }
        }
    }

    public bool IsEvening {
        get => SelectedPeriod == Periods[1];
        set {
            if (value) {
                SelectedPeriod = Periods[1];
            }
        }
    }

    public ObservableCollection<LeaderboardRow> Leaderboard { get; } = [];
    public ObservableCollection<MapPlayRow> MapsPlayed { get; } = [];

    public IReadOnlyList<string> StatusItems => [Summary];
    public string StatusRight => "From the game logs";

    /// <summary>Reading logs costs disk time, so only while somebody looks.</summary>
    public void OnShown() {
        _refreshTimer.Start();
        _ = RefreshAsync();
    }

    public void OnHidden() {
        _refreshTimer.Stop();
    }

    public static string FormatDuration(TimeSpan duration) {
        if (duration.TotalHours >= 1) {
            return $"{(int)duration.TotalHours} h {duration.Minutes:00} min";
        }
        return $"{duration.Minutes} min";
    }

    partial void OnSelectedPeriodChanged(StatsPeriod value) {
        OnPropertyChanged(nameof(IsThisMap));
        OnPropertyChanged(nameof(IsEvening));
        _ = RefreshAsync();
    }

    partial void OnHideBotsChanged(bool value) {
        _ = RefreshAsync();
    }

    [RelayCommand]
    private async Task RefreshAsync() {
        if (_isRefreshing) {
            return;
        }
        _isRefreshing = true;
        try {
            DateTime now = DateTime.Now;
            DateTime? since = SelectedPeriod.Length == null ? null : now - SelectedPeriod.Length.Value;
            bool isRunning = _connection.IsRunning;

            (IReadOnlyList<PlayerStats> players, IReadOnlyList<MapPlay> plays) = await Task.Run(() =>
                (_reader.ReadLeaderboard(since), _reader.ReadMapsPlayed(now - EveningLength, now, isRunning)));

            List<PlayerStats> shown = players.Where(player => !HideBots || !player.IsBot).ToList();
            Leaderboard.Clear();
            for (int index = 0; index < shown.Count; index++) {
                Leaderboard.Add(new LeaderboardRow(index + 1, shown[index]));
            }

            MapsPlayed.Clear();
            foreach (MapPlay play in plays.Reverse()) {
                MapsPlayed.Add(new MapPlayRow(play));
            }

            MapPlay? running = plays.LastOrDefault(play => play.IsRunning);
            _mapStartedAt = running?.StartedAt;

            int kills = players.Sum(player => player.Kills);
            int humans = players.Count(player => !player.IsBot);
            int bots = players.Count(player => player.IsBot);
            Summary = $"{kills} kills, {humans} humans and {bots} bots in {SelectedPeriod.Title.ToLowerInvariant()}, {plays.Count} maps tonight";
            UpdateMapProgress();
        } finally {
            _isRefreshing = false;
        }
    }

    /// <summary>
    /// Elapsed map time against elapsed plus amx_timeleft. Without a time
    /// limit (or before the first answer) the bar stays empty.
    /// </summary>
    private void UpdateMapProgress() {
        if (!_connection.IsRunning) {
            MapProgress = 0;
            MapProgressText = "Server stopped";
            return;
        }
        if (_mapStartedAt == null || !TryParseTimeLeft(_connection.TimeLeft, out TimeSpan left)) {
            MapProgress = 0;
            MapProgressText = "No time limit known for this map";
            return;
        }

        TimeSpan elapsed = DateTime.Now - _mapStartedAt.Value;
        double total = elapsed.TotalSeconds + left.TotalSeconds;
        MapProgress = total <= 0 ? 0 : Math.Clamp(elapsed.TotalSeconds / total * 100, 0, 100);
        MapProgressText = $"{_connection.CurrentMap}: {FormatDuration(elapsed)} played, {_connection.TimeLeft} left, then {_connection.NextMap ?? "-"}";
    }

    /// <summary>amx_timeleft looks like "11:28" (minutes:seconds).</summary>
    internal static bool TryParseTimeLeft(string? text, out TimeSpan left) {
        left = TimeSpan.Zero;
        if (string.IsNullOrEmpty(text)) {
            return false;
        }
        string[] parts = text.Split(':');
        if (parts.Length != 2) {
            return false;
        }
        if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int minutes)) {
            return false;
        }
        if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int seconds)) {
            return false;
        }
        left = new TimeSpan(0, minutes, seconds);
        return true;
    }
}
