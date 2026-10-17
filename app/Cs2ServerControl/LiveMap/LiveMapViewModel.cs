using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Numerics;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cs2ServerControl.Services;
using Cs2ServerControl.Shell;

namespace Cs2ServerControl.LiveMap;

public sealed partial class PlayerRowViewModel : ObservableObject {
    [ObservableProperty]
    private string _name = "";

    [ObservableProperty]
    private Team _team;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HealthText))]
    private bool _isAlive;

    [ObservableProperty]
    private bool _isBot;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HealthText))]
    private int _health;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ScoreText))]
    private int _frags;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ScoreText))]
    private int _deaths;

    [ObservableProperty]
    private bool _isSelected;

    /// <summary>The game's kill icon of the weapon in hand; null when dead or when the weapon has none.</summary>
    [ObservableProperty]
    private ImageSource? _weaponIcon;

    /// <summary>The weapon as text where there is no icon (flashbang, smoke grenade, C4).</summary>
    [ObservableProperty]
    private string _weaponText = "";

    /// <summary>The player's glow shell, shown as a ring around the team dot.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasGlow), nameof(GlowColor))]
    private PlayerGlow? _glow;

    public bool HasGlow => Glow != null && IsAlive;
    public Color GlowColor => Glow == null ? Colors.Transparent : Color.FromRgb(Glow.Value.Red, Glow.Value.Green, Glow.Value.Blue);

    public PlayerRowViewModel(int userId) {
        UserId = userId;
    }

    public int UserId { get; }
    public string HealthText => IsAlive ? $"{Health} HP" : "dead";
    public string ScoreText => $"{Frags}/{Deaths}";

    public void Update(LivePlayer player) {
        Name = player.Name;
        Team = player.Team;
        IsAlive = player.IsAlive;
        IsBot = player.IsBot;
        Health = player.Health;
        Frags = player.Frags;
        Deaths = player.Deaths;
        Glow = player.Glow;
        OnPropertyChanged(nameof(HasGlow));
    }
}

public sealed partial class KillFeedEntry : ObservableObject {
    [ObservableProperty]
    private double _opacity = 1;

    public KillFeedEntry(string killer, Team killerTeam, string victim, Team victimTeam, string weaponText,
        ImageSource? weaponIcon, ImageSource? headshotIcon, bool isHumiliation, DateTime createdAt) {
        Killer = killer;
        KillerTeam = killerTeam;
        Victim = victim;
        VictimTeam = victimTeam;
        WeaponText = weaponText;
        WeaponIcon = weaponIcon;
        HeadshotIcon = headshotIcon;
        IsHumiliation = isHumiliation;
        CreatedAt = createdAt;
    }

    public string Killer { get; }
    public Team KillerTeam { get; }
    public string Victim { get; }
    public Team VictimTeam { get; }

    /// <summary>"[AK-47]" or "[AK-47, Headshot]", shown when the game's icons are missing.</summary>
    public string WeaponText { get; }

    public ImageSource? WeaponIcon { get; }
    public ImageSource? HeadshotIcon { get; }
    public bool HasWeaponIcon => WeaponIcon != null;
    public bool HasHeadshotIcon => HeadshotIcon != null;
    public bool IsHumiliation { get; }
    public DateTime CreatedAt { get; }
    public bool HasKiller => Killer.Length > 0;
}

/// <summary>One bullet on screen; Strength goes from 1 (just fired) to 0 (gone).</summary>
public sealed record VisibleShot(System.Numerics.Vector3 Start, System.Numerics.Vector3 End, Team Team, bool HitPlayer, float Strength);

public sealed partial class LiveMapViewModel : ObservableObject, IScreen {
    private const int KillFeedSize = 6;
    private static readonly TimeSpan KillFeedLifetime = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan KillFeedFade = TimeSpan.FromSeconds(1);

    // Shots arrive in batches every snapshot (0.2 s); a bit longer keeps a
    // spray readable without the map filling up with old lines.
    private static readonly TimeSpan ShotLifetime = TimeSpan.FromSeconds(0.8);
    private const int MaxShotsShown = 400;

    // GoldSrc's player origin is the hull center: feet 36 units below, eyes 28 above.
    public const float FeetOffset = -36f;
    public const float HeadOffset = 28f;

    private readonly ServerConnection _connection;
    private readonly IDialogService _dialogs;
    private readonly KillIcons _killIcons;
    private readonly Dictionary<int, PlayerTrack> _tracks = [];
    private readonly List<(LiveShot Shot, Team Team, DateTime ReceivedAt)> _shots = [];
    private readonly DispatcherTimer _killFeedTimer;
    private LiveSnapshot? _snapshot;
    private bool _userPickedSelection;
    private int _mapLoadVersion;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection), nameof(SelectedName), nameof(SelectedTeam), nameof(SelectedTeamText),
        nameof(SelectedHealthText), nameof(SelectedHealthPercent), nameof(SelectedWeaponText), nameof(SelectedWeaponIcon), nameof(SelectedPositionText),
        nameof(SelectedGlowText), nameof(SelectedHasGlow), nameof(SelectedGlowColor))]
    private PlayerRowViewModel? _selectedPlayer;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPerspective))]
    private bool _isTopView;

    [ObservableProperty]
    private bool _showNames;

    [ObservableProperty]
    private bool _showTrails;

    /// <summary>The weapon icon in the label next to each player on the map.</summary>
    [ObservableProperty]
    private bool _showWeapons;

    /// <summary>Bullet paths from the plugin, fading out after a moment.</summary>
    [ObservableProperty]
    private bool _showShots;

    /// <summary>Debug view: zBot's walkable areas, their links and hiding spots.</summary>
    [ObservableProperty]
    private bool _showNavMesh;

    /// <summary>The nav mesh of the shown map, null when off, missing or unreadable.</summary>
    [ObservableProperty]
    private NavMesh? _navMesh;

    [ObservableProperty]
    private string? _navMeshMessage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CameraModeText))]
    private bool _isFollowing;

    [ObservableProperty]
    private BspWireframe? _wireframe;

    [ObservableProperty]
    private string? _viewportMessage = "Waiting for the server";

    [ObservableProperty]
    private string _mapName = "";

    [ObservableProperty]
    private string _mapSubtitle = "";

    [ObservableProperty]
    private bool _isVisible;

    public LiveMapViewModel(ServerConnection connection, IDialogService dialogs, KillIcons killIcons) {
        _killIcons = killIcons;
        _connection = connection;
        _dialogs = dialogs;
        _showNames = connection.Settings.ShowNames;
        _showTrails = connection.Settings.ShowTrails;
        _showShots = connection.Settings.ShowShots;
        _showWeapons = connection.Settings.ShowWeapons;
        _showNavMesh = connection.Settings.ShowNavMesh;

        Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
        _connection.Feed.SnapshotReceived += (_, snapshot) => dispatcher.BeginInvoke(() => OnSnapshot(snapshot));
        _connection.Feed.KillReceived += (_, kill) => dispatcher.BeginInvoke(() => OnKill(kill));
        _connection.Feed.ShotsReceived += (_, batch) => dispatcher.BeginInvoke(() => OnShots(batch));
        _connection.MapChanged += (_, _) => LoadMap(_connection.CurrentMap);
        _connection.PropertyChanged += OnConnectionChanged;

        _killFeedTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => Tick(), dispatcher);
        _killFeedTimer.Start();

        LoadMap(_connection.CurrentMap);
    }

    public CameraRig Camera { get; } = new();

    public ObservableCollection<PlayerRowViewModel> Players { get; } = [];
    public ObservableCollection<KillFeedEntry> KillFeed { get; } = [];

    public bool IsPerspective {
        get => !IsTopView;
        set {
            if (value) {
                IsTopView = false;
            }
        }
    }

    public string CameraModeText => IsFollowing && SelectedPlayer != null ? $"Camera follows: {SelectedPlayer.Name}" : "Camera: free";

    public bool HasLiveData => _connection.Feed.HasLiveData;

    public bool HasSelection => SelectedPlayer != null;
    public string SelectedName => SelectedPlayer?.Name ?? "";
    public Team SelectedTeam => SelectedPlayer?.Team ?? Team.Spectator;

    public string SelectedTeamText {
        get {
            if (SelectedPlayer == null) {
                return "";
            }
            string team;
            switch (SelectedPlayer.Team) {
                case Team.Terrorist:
                    team = "Terrorists";
                    break;
                case Team.CounterTerrorist:
                    team = "Counter-Terrorists";
                    break;
                default:
                    team = "Spectators";
                    break;
            }
            return SelectedPlayer.IsBot ? team + " · Bot" : team;
        }
    }

    public string SelectedHealthText => SelectedPlayer == null ? "" : $"{(SelectedPlayer.IsAlive ? SelectedPlayer.Health : 0)} HP";
    public double SelectedHealthPercent => SelectedPlayer == null || !SelectedPlayer.IsAlive ? 0 : Math.Clamp(SelectedPlayer.Health, 0, 100);

    public ImageSource? SelectedWeaponIcon => SelectedPlayer?.WeaponIcon;

    /// <summary>For the labels on the map; the icons are cut from the sprite sheet once and then cached.</summary>
    public ImageSource? WeaponIconFor(string weapon) {
        return _killIcons.ForWeapon(weapon);
    }

    public string SelectedWeaponText {
        get {
            LivePlayer? player = SelectedLivePlayer;
            if (player == null || !player.IsAlive) {
                return "-";
            }
            return WeaponNames.Display(player.Weapon);
        }
    }

    public bool SelectedHasGlow => SelectedPlayer?.HasGlow == true;
    public Color SelectedGlowColor => SelectedPlayer?.GlowColor ?? Colors.Transparent;
    public string SelectedGlowText => SelectedHasGlow ? GlowNames.Describe(SelectedPlayer!.Glow) : "-";

    public string SelectedPositionText {
        get {
            LivePlayer? player = SelectedLivePlayer;
            if (player == null) {
                return "-";
            }
            return $"{player.Origin.X:0}, {player.Origin.Y:0}, {player.Origin.Z:0}";
        }
    }

    public IReadOnlyList<string> StatusItems {
        get {
            string feed = HasLiveData ? $"UDP {_connection.Feed.PacketsPerSecond} packets/s" : "No live data";
            return [_connection.GameAddress, feed, "No delay, only on this PC"];
        }
    }

    public string StatusRight => "HelixToolkit.Wpf.SharpDX";

    private LivePlayer? SelectedLivePlayer {
        get {
            if (SelectedPlayer == null || !_tracks.TryGetValue(SelectedPlayer.UserId, out PlayerTrack? track)) {
                return null;
            }
            return track.Latest;
        }
    }

    public void OnShown() {
        IsVisible = true;
    }

    public void OnHidden() {
        IsVisible = false;
    }

    /// <summary>Interpolated players for the frame that is drawn at this moment.</summary>
    public IReadOnlyList<PlayerRenderState> GetRenderStates(DateTime now) {
        if (!HasLiveData) {
            return [];
        }
        return _tracks.Values
            .Where(track => track.Latest.Team != Team.Spectator)
            .Select(track => track.RenderState(now))
            .ToList();
    }

    /// <summary>The bullets still on screen, oldest first, with how far each has faded.</summary>
    public IReadOnlyList<VisibleShot> GetVisibleShots(DateTime now) {
        _shots.RemoveAll(entry => now - entry.ReceivedAt >= ShotLifetime);
        if (!ShowShots || !HasLiveData) {
            return [];
        }
        return _shots
            .Select(entry => new VisibleShot(entry.Shot.Start, entry.Shot.End, entry.Team, entry.Shot.HitPlayer,
                1f - (float)((now - entry.ReceivedAt).TotalSeconds / ShotLifetime.TotalSeconds)))
            .ToList();
    }

    public int? SelectedUserId => SelectedPlayer?.UserId;

    public void SelectByUserId(int userId) {
        PlayerRowViewModel? row = Players.FirstOrDefault(candidate => candidate.UserId == userId);
        if (row != null) {
            SelectPlayer(row);
        }
    }

    /// <summary>Any manual camera move ends follow mode, the user takes over.</summary>
    public void OnManualCameraAction() {
        IsFollowing = false;
    }

    [RelayCommand]
    private void SelectPlayer(PlayerRowViewModel? row) {
        _userPickedSelection = true;
        SetSelection(row);
    }

    [RelayCommand]
    private void RotateLeft() {
        OnManualCameraAction();
        Camera.RotateBy(-15f, animate: true);
    }

    [RelayCommand]
    private void RotateRight() {
        OnManualCameraAction();
        Camera.RotateBy(15f, animate: true);
    }

    [RelayCommand]
    private void Steeper() {
        OnManualCameraAction();
        Camera.PitchBy(10f, animate: true);
        IsTopView = Camera.IsTopView;
    }

    [RelayCommand]
    private void Flatter() {
        OnManualCameraAction();
        Camera.PitchBy(-10f, animate: true);
        IsTopView = Camera.IsTopView;
    }

    [RelayCommand]
    private void ZoomIn() {
        OnManualCameraAction();
        Camera.ZoomBy(-0.2f, animate: true);
    }

    [RelayCommand]
    private void ZoomOut() {
        OnManualCameraAction();
        Camera.ZoomBy(0.2f, animate: true);
    }

    [RelayCommand]
    private void ResetView() {
        OnManualCameraAction();
        Camera.Reset(IsTopView);
    }

    partial void OnIsFollowingChanged(bool value) {
        // Following nobody is not a state; the toggle snaps back.
        if (value && SelectedPlayer == null) {
            IsFollowing = false;
        }
    }

    // Colors amx_glow knows (fun_admincmds.sma).
    public IReadOnlyList<string> GlowColors { get; } = ["red", "green", "blue", "yellow", "pink", "white"];

    [ObservableProperty]
    private string _glowColor = "pink";

    // The player fun commands run at once: they act on the game in progress.
    [RelayCommand]
    private Task Glow() {
        return RunOnSelected($"amx_glow #{{0}} {GlowColor}");
    }

    /// <summary>The context menu picks the color and glows in one step.</summary>
    [RelayCommand]
    private Task GlowWith(string color) {
        if (color != "off") {
            GlowColor = color;
        }
        return RunOnSelected($"amx_glow #{{0}} {color}");
    }

    [RelayCommand]
    private Task Rocket() {
        return RunOnSelected("amx_rocket #{0}");
    }

    [RelayCommand]
    private Task Godmode() {
        return RunOnSelected("amx_godmode #{0} 1");
    }

    [RelayCommand]
    private Task Noclip() {
        return RunOnSelected("amx_noclip #{0} 1");
    }

    /// <summary>Glow, godmode and noclip off again in one go.</summary>
    [RelayCommand]
    private async Task ResetFun() {
        await RunOnSelected("amx_glow #{0} off");
        await RunOnSelected("amx_godmode #{0} 0");
        await RunOnSelected("amx_noclip #{0} 0");
    }

    private async Task RunOnSelected(string commandFormat) {
        if (SelectedPlayer == null) {
            return;
        }
        await _connection.RunAsync(string.Format(System.Globalization.CultureInfo.InvariantCulture, commandFormat, SelectedPlayer.UserId));
    }

    [RelayCommand]
    private async Task Slay() {
        if (SelectedPlayer == null) {
            return;
        }
        await _connection.RunAsync($"amx_slay #{SelectedPlayer.UserId}");
    }

    [RelayCommand]
    private async Task Kick() {
        if (SelectedPlayer == null) {
            return;
        }
        PlayerRowViewModel player = SelectedPlayer;
        if (!await _dialogs.ConfirmAsync("Kick", $"Really kick {player.Name}?", "Kick")) {
            return;
        }
        await _connection.RunAsync($"kick #{player.UserId}");
    }

    partial void OnIsTopViewChanged(bool value) {
        // The toolbar toggle pair: exactly one of 3D and top view is on.
        if (value && !Camera.IsTopView) {
            Camera.SetTopView(animate: true);
        } else if (!value && Camera.IsTopView) {
            Camera.SetPerspective(animate: true);
        }
    }

    partial void OnShowNamesChanged(bool value) {
        _connection.Settings.ShowNames = value;
        _connection.Settings.Save();
    }

    partial void OnShowNavMeshChanged(bool value) {
        _connection.Settings.ShowNavMesh = value;
        _connection.Settings.Save();
        _ = LoadNavMeshAsync();
    }

    /// <summary>Reads maps\&lt;map&gt;.nav in the background while the toggle is on.</summary>
    private async Task LoadNavMeshAsync() {
        NavMeshMessage = null;
        if (!ShowNavMesh || string.IsNullOrEmpty(MapName)) {
            NavMesh = null;
            return;
        }

        string mapName = MapName;
        string navFile = _connection.Paths.NavFile(mapName);
        if (!File.Exists(navFile)) {
            NavMesh = null;
            NavMeshMessage = $"No navigation file for {mapName} yet (Bots screen: Generate)";
            return;
        }

        try {
            NavMesh mesh = await Task.Run(() => NavMeshReader.Load(navFile));
            if (mapName == MapName) {
                NavMesh = mesh;
            }
        } catch (Exception exception) when (exception is IOException or InvalidDataException) {
            NavMesh = null;
            NavMeshMessage = $"The navigation file of {mapName} could not be read";
        }
    }

    partial void OnShowTrailsChanged(bool value) {
        _connection.Settings.ShowTrails = value;
        _connection.Settings.Save();
    }

    partial void OnShowWeaponsChanged(bool value) {
        _connection.Settings.ShowWeapons = value;
        _connection.Settings.Save();
    }

    partial void OnShowShotsChanged(bool value) {
        _connection.Settings.ShowShots = value;
        _connection.Settings.Save();
        if (!value) {
            _shots.Clear();
        }
    }

    partial void OnSelectedPlayerChanged(PlayerRowViewModel? oldValue, PlayerRowViewModel? newValue) {
        if (oldValue != null) {
            oldValue.IsSelected = false;
        }
        if (newValue != null) {
            newValue.IsSelected = true;
        } else {
            IsFollowing = false;
        }
        OnPropertyChanged(nameof(CameraModeText));
    }

    private void SetSelection(PlayerRowViewModel? row) {
        if (SelectedPlayer != row) {
            IsFollowing = false;
        }
        SelectedPlayer = row;
    }

    private void OnSnapshot(LiveSnapshot snapshot) {
        _snapshot = snapshot;
        DateTime now = DateTime.UtcNow;

        var seen = new HashSet<int>();
        foreach (LivePlayer player in snapshot.Players) {
            seen.Add(player.UserId);
            if (_tracks.TryGetValue(player.UserId, out PlayerTrack? track)) {
                track.Update(player, now);
            } else {
                _tracks[player.UserId] = new PlayerTrack(player, now);
            }
        }
        foreach (int gone in _tracks.Keys.Where(userId => !seen.Contains(userId)).ToList()) {
            _tracks.Remove(gone);
        }

        UpdatePlayerRows(snapshot);
        UpdateTitle();
        RefreshSelectionDetails();

        if (!string.Equals(MapName, snapshot.MapName, StringComparison.OrdinalIgnoreCase) && Wireframe == null) {
            LoadMap(snapshot.MapName);
        }
    }

    private void UpdatePlayerRows(LiveSnapshot snapshot) {
        // Terrorists first, then CT, then spectators; within a team by score.
        List<LivePlayer> ordered = snapshot.Players
            .OrderBy(player => TeamOrder(player.Team))
            .ThenByDescending(player => player.Frags)
            .ThenBy(player => player.Deaths)
            .ThenBy(player => player.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var rowsById = Players.ToDictionary(row => row.UserId);
        for (int index = 0; index < ordered.Count; index++) {
            LivePlayer player = ordered[index];
            if (!rowsById.TryGetValue(player.UserId, out PlayerRowViewModel? row)) {
                row = new PlayerRowViewModel(player.UserId);
                Players.Insert(Math.Min(index, Players.Count), row);
            }
            row.Update(player);
            ImageSource? icon = player.IsAlive ? _killIcons.ForWeapon(player.Weapon) : null;
            row.WeaponIcon = icon;
            row.WeaponText = player.IsAlive && icon == null ? WeaponNames.Display(player.Weapon) : "";

            int currentIndex = Players.IndexOf(row);
            if (currentIndex != index) {
                Players.Move(currentIndex, index);
            }
        }
        while (Players.Count > ordered.Count) {
            Players.RemoveAt(Players.Count - 1);
        }

        bool selectionGone = SelectedPlayer != null && !Players.Contains(SelectedPlayer);
        if (selectionGone) {
            SetSelection(null);
            _userPickedSelection = false;
        }
        if (SelectedPlayer == null && !_userPickedSelection) {
            SetSelection(PickDefaultSelection(snapshot));
        }
    }

    /// <summary>The first alive human, else the first alive bot, else nobody.</summary>
    private PlayerRowViewModel? PickDefaultSelection(LiveSnapshot snapshot) {
        LivePlayer? player = snapshot.Players.FirstOrDefault(candidate => candidate.IsAlive && !candidate.IsBot && candidate.Team != Team.Spectator)
            ?? snapshot.Players.FirstOrDefault(candidate => candidate.IsAlive && candidate.Team != Team.Spectator);
        if (player == null) {
            return null;
        }
        return Players.FirstOrDefault(row => row.UserId == player.UserId);
    }

    private static int TeamOrder(Team team) {
        switch (team) {
            case Team.Terrorist:
                return 0;
            case Team.CounterTerrorist:
                return 1;
            default:
                return 2;
        }
    }

    private void RefreshSelectionDetails() {
        OnPropertyChanged(nameof(SelectedName));
        OnPropertyChanged(nameof(SelectedTeam));
        OnPropertyChanged(nameof(SelectedTeamText));
        OnPropertyChanged(nameof(SelectedHealthText));
        OnPropertyChanged(nameof(SelectedHealthPercent));
        OnPropertyChanged(nameof(SelectedWeaponText));
        OnPropertyChanged(nameof(SelectedWeaponIcon));
        OnPropertyChanged(nameof(SelectedPositionText));
        OnPropertyChanged(nameof(SelectedGlowText));
        OnPropertyChanged(nameof(SelectedHasGlow));
        OnPropertyChanged(nameof(SelectedGlowColor));
        OnPropertyChanged(nameof(CameraModeText));
    }

    private void UpdateTitle() {
        if (_snapshot == null || !HasLiveData) {
            // Without a map the centered viewport message already says it.
            if (string.IsNullOrEmpty(MapName)) {
                MapSubtitle = "";
                return;
            }
            MapSubtitle = _connection.IsRunning ? "No live data" : "Server stopped";
            return;
        }

        string round = "-";
        if (_snapshot.RoundSecondsLeft >= 0) {
            round = $"{_snapshot.RoundSecondsLeft / 60}:{_snapshot.RoundSecondsLeft % 60:00}";
        }
        int playing = _snapshot.Players.Count;
        MapSubtitle = $"{GameModes.TitleOf(_snapshot.ModeId)} · Round {round} · {playing} / {_snapshot.MaxPlayers} players";
    }

    private void OnShots(LiveShotBatch batch) {
        if (!ShowShots || !IsVisible) {
            return;
        }
        DateTime now = DateTime.UtcNow;
        foreach (LiveShot shot in batch.Shots) {
            // The shooter's team colors the line; a shooter not in the last snapshot draws neutral.
            Team team = _tracks.TryGetValue(shot.ShooterUserId, out PlayerTrack? track) ? track.Latest.Team : Team.Spectator;
            _shots.Add((shot, team, now));
        }
        if (_shots.Count > MaxShotsShown) {
            _shots.RemoveRange(0, _shots.Count - MaxShotsShown);
        }
    }

    private void OnKill(LiveKill kill) {
        if (_snapshot == null) {
            return;
        }

        LivePlayer? killer = _snapshot.Players.FirstOrDefault(player => player.UserId == kill.KillerUserId);
        LivePlayer? victim = _snapshot.Players.FirstOrDefault(player => player.UserId == kill.VictimUserId);
        if (victim == null) {
            return;
        }

        string weapon = WeaponNames.Display(kill.Weapon);
        string weaponText = kill.IsHeadshot ? $"[{weapon}, Headshot]" : $"[{weapon}]";
        bool isWorldKill = killer == null || killer.UserId == victim.UserId;

        // The game's own kill feed icons; a fall or suicide gets the skull like in game.
        ImageSource? weaponIcon = _killIcons.ForWeapon(kill.Weapon);
        if (weaponIcon == null && isWorldKill) {
            weaponIcon = _killIcons.Skull;
        }
        ImageSource? headshotIcon = kill.IsHeadshot && weaponIcon != null ? _killIcons.Headshot : null;

        var entry = new KillFeedEntry(
            isWorldKill ? "" : killer!.Name,
            isWorldKill ? Team.Spectator : killer!.Team,
            victim.Name,
            victim.Team,
            weaponText,
            weaponIcon,
            headshotIcon,
            !isWorldKill && WeaponNames.IsKnife(kill.Weapon),
            DateTime.UtcNow);

        KillFeed.Insert(0, entry);
        while (KillFeed.Count > KillFeedSize) {
            KillFeed.RemoveAt(KillFeed.Count - 1);
        }
    }

    private void Tick() {
        DateTime now = DateTime.UtcNow;
        for (int index = KillFeed.Count - 1; index >= 0; index--) {
            TimeSpan age = now - KillFeed[index].CreatedAt;
            if (age >= KillFeedLifetime) {
                KillFeed.RemoveAt(index);
                continue;
            }
            TimeSpan left = KillFeedLifetime - age;
            KillFeed[index].Opacity = left < KillFeedFade ? left.TotalMilliseconds / KillFeedFade.TotalMilliseconds : 1;
        }

        bool feedStale = _snapshot != null && !HasLiveData;
        if (feedStale) {
            // No packet for 3 seconds: the map stays, the players go.
            _snapshot = null;
            _tracks.Clear();
            Players.Clear();
            SetSelection(null);
            _userPickedSelection = false;
            UpdateTitle();
        }
    }

    private async void LoadMap(string? mapName) {
        if (string.IsNullOrEmpty(mapName)) {
            ViewportMessage = _connection.IsRunning ? "Waiting for the map name" : "Server stopped";
            UpdateTitle();
            return;
        }

        MapName = mapName;
        UpdateTitle();
        string bspFile = _connection.Paths.BspFile(mapName);
        if (!File.Exists(bspFile)) {
            Wireframe = null;
            ViewportMessage = $"{mapName}.bsp not found";
            return;
        }

        int version = ++_mapLoadVersion;
        ViewportMessage = "Loading map...";
        try {
            BspWireframe wireframe = await BspWireframeLoader.LoadAsync(bspFile);
            if (version != _mapLoadVersion) {
                return;
            }
            Camera.Fit(wireframe.BoundsMin, wireframe.BoundsMax, IsTopView);
            Wireframe = wireframe;
            _ = LoadNavMeshAsync();
            ViewportMessage = null;
        } catch (Exception exception) when (exception is IOException or BspFormatException or UnauthorizedAccessException) {
            if (version == _mapLoadVersion) {
                Wireframe = null;
                ViewportMessage = $"Could not read {mapName}.bsp: {exception.Message}";
            }
        }
    }

    private void OnConnectionChanged(object? sender, PropertyChangedEventArgs e) {
        if (e.PropertyName == nameof(ServerConnection.IsRunning)) {
            UpdateTitle();
            if (Wireframe == null && !_connection.IsRunning) {
                ViewportMessage = "Server stopped";
            }
        }
    }
}
