using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cs2ServerControl.Install;
using Cs2ServerControl.Server;
using Cs2ServerControl.Services;
using Cs2ServerControl.Shell;

namespace Cs2ServerControl.Maps;

public sealed partial class MapLibraryRowViewModel : ObservableObject {
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPicture))]
    private ImageSource? _thumbnail;

    public MapLibraryRowViewModel(MapLibraryEntry entry, ImageSource? thumbnail) {
        Entry = entry;
        _thumbnail = thumbnail;
    }

    public MapLibraryEntry Entry { get; }
    public string Name => Entry.Name;
    public bool HasNav => Entry.HasNav;
    public bool HasPicture => Thumbnail != null;
    public bool IsInNoMode => Entry.ModeIds.Count == 0;

    public string ModesText {
        get {
            if (!Entry.IsPlayable) {
                return $"Not playable, needs {string.Join(", ", Entry.MissingFiles!)}";
            }
            if (Entry.ModeIds.Count == 0) {
                return "In no map list";
            }
            return string.Join(", ", Entry.ModeIds.Select(GameModes.TitleOf));
        }
    }

    public string SourceText {
        get {
            switch (Entry.Source) {
                case MapSource.Stock:
                    return "Stock";
                case MapSource.MapPack:
                    return "Map pack";
                default:
                    return "Custom";
            }
        }
    }

    public string NavText {
        get {
            if (HasNav) {
                return "Present";
            }
            return Entry.IsNavOutdated ? "Outdated" : "Missing";
        }
    }
    public string SizeText => (Entry.SizeBytes / 1024.0 / 1024.0).ToString("0.0", CultureInfo.InvariantCulture) + " MB";

    /// <summary>The first mode with a tile on the Server screen, where Pick takes the map.</summary>
    public string? PickModeId => Entry.ModeIds.FirstOrDefault(modeId => GameModes.All.Any(mode => mode.Id == modeId));
    public bool CanPick => PickModeId != null;
}

/// <summary>What the Show list narrows the table to.</summary>
public sealed record MapFilter(string Title, Func<MapLibraryRowViewModel, bool> Matches);

/// <summary>
/// The Maps screen: every installed map in one table, with the modes that
/// play it, its nav file and picture, the custom-maps folder, and the
/// market tab to install more maps from GameBanana. Map updates and market
/// installs run here, through the same installer and log as the Install
/// screen. Pick goes to the Server draft, nav files to the Bots queue.
/// </summary>
public sealed partial class MapsViewModel : ObservableObject, IScreen {
    private readonly ServerConnection _connection;
    private readonly IDialogService _dialogs;
    private readonly MapThumbnails _thumbnails;
    private readonly Func<string, string, bool> _pickOnServer;
    private readonly Action _queueMissingNavs;
    private readonly InstallViewModel _install;
    private List<MapLibraryRowViewModel> _allRows = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInstalledTab))]
    private bool _isMarketTab;

    [ObservableProperty]
    private MapFilter _selectedFilter;

    [ObservableProperty]
    private string _searchText = "";

    [ObservableProperty]
    private string _summary = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanGenerateThumbnails))]
    private bool _isGenerating;

    [ObservableProperty]
    private string _generateProgress = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanGenerateThumbnails), nameof(GenerateThumbnailsText))]
    private int _missingPictureCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(QueueNavText), nameof(CanQueueNavs))]
    private int _missingNavCount;

    public MapsViewModel(ServerConnection connection, IDialogService dialogs, InstallViewModel install,
            Func<string, string, bool> pickOnServer, Action queueMissingNavs) {
        _connection = connection;
        _dialogs = dialogs;
        _pickOnServer = pickOnServer;
        _queueMissingNavs = queueMissingNavs;
        _install = install;
        _thumbnails = new MapThumbnails(connection.Paths);
        Market = new MarketViewModel(connection, dialogs, install, AfterMapsChangedAsync);

        List<MapFilter> filters = [new("All maps", _ => true)];
        foreach (string modeId in MapModes.AllModes) {
            filters.Add(new MapFilter(GameModes.TitleOf(modeId), row => row.Entry.ModeIds.Contains(modeId)));
        }
        // Like the Bots queue: only maps in a map list get a nav file built.
        filters.Add(new MapFilter("Missing navigation", row => !row.HasNav && !row.IsInNoMode));
        filters.Add(new MapFilter("Missing picture", row => !row.HasPicture));
        filters.Add(new MapFilter("In no map list", row => row.IsInNoMode));
        filters.Add(new MapFilter("Custom maps", row => row.Entry.Source == MapSource.Custom));
        filters.Add(new MapFilter("Not playable", row => !row.Entry.IsPlayable));
        Filters = filters;
        _selectedFilter = filters[0];
    }

    public MarketViewModel Market { get; }

    /// <summary>The shared installer: its log and progress show in the activity card here.</summary>
    public InstallViewModel Install => _install;

    public bool IsInstalledTab {
        get => !IsMarketTab;
        set => IsMarketTab = !value;
    }

    public IReadOnlyList<MapFilter> Filters { get; }
    public ObservableCollection<MapLibraryRowViewModel> Rows { get; } = [];
    public ObservableCollection<WaitingMap> WaitingMaps { get; } = [];
    public bool IsServerRunning => _connection.IsRunning;
    public bool CanGenerateThumbnails => !IsGenerating && MissingPictureCount > 0;
    public string GenerateThumbnailsText => $"Generate missing pictures ({MissingPictureCount})";
    public bool CanQueueNavs => MissingNavCount > 0;
    public string QueueNavText => $"Queue missing navigation ({MissingNavCount})";
    public string CustomMapsDir => Path.Combine(_connection.Paths.RootDir, "custom-maps");

    public IReadOnlyList<string> StatusItems => [Summary];
    public string StatusRight => "Pictures from tools\\previews";

    public void OnShown() {
        _ = RefreshAsync();
        if (IsMarketTab) {
            Market.OnShown();
        }
    }

    public void OnHidden() {
    }

    partial void OnIsMarketTabChanged(bool value) {
        if (value) {
            Market.OnShown();
        }
    }

    partial void OnSelectedFilterChanged(MapFilter value) {
        ApplyFilter();
    }

    partial void OnSearchTextChanged(string value) {
        ApplyFilter();
    }

    [RelayCommand]
    private async Task RefreshAsync() {
        PackagePaths paths = _connection.Paths;
        (IReadOnlyList<MapLibraryEntry> entries, IReadOnlyList<WaitingMap> waiting) = await Task.Run(() =>
            (MapLibrary.Scan(paths), MapLibrary.ScanCustomMaps(CustomMapsDir, paths.MapsDir)));

        _allRows = entries.Select(entry => new MapLibraryRowViewModel(entry, _thumbnails.Get(entry.Name))).ToList();
        WaitingMaps.Clear();
        foreach (WaitingMap map in waiting) {
            WaitingMaps.Add(map);
        }
        UpdateCounts();
        ApplyFilter();
    }

    [RelayCommand]
    private async Task ShowDetails(MapLibraryRowViewModel? row) {
        if (row == null) {
            return;
        }
        string? modeId = row.PickModeId;
        Action? pick = null;
        if (modeId != null) {
            pick = () => _pickOnServer(modeId, row.Name);
        }
        var details = new MapDetailsViewModel(_connection, row.Name, GameModes.TitleOf(modeId), pick, () => SetNextMap(row));
        await _dialogs.ShowAsync(details);
    }

    [RelayCommand]
    private void PickOnServer(MapLibraryRowViewModel? row) {
        string? modeId = row?.PickModeId;
        if (row == null || modeId == null) {
            return;
        }
        _pickOnServer(modeId, row.Name);
    }

    [RelayCommand]
    private async Task SetNextMap(MapLibraryRowViewModel? row) {
        if (row == null || !_connection.IsRunning) {
            return;
        }
        await _connection.RunAsync($"nextlevel {row.Name}");
        _connection.NextMap = row.Name;
    }

    /// <summary>
    /// One map after the other on the UI thread: WPF renders there, and the
    /// .bsp reading in between runs in the background.
    /// </summary>
    [RelayCommand]
    private async Task GenerateThumbnails() {
        if (IsGenerating) {
            return;
        }
        IsGenerating = true;
        try {
            Color Read(string key) => (Color)Application.Current.FindResource(key);
            var generator = new ThumbnailGenerator(_connection.Paths, Read("ViewportBackgroundColor"), Read("WireLowColor"), Read("WireHighColor"));
            List<MapLibraryRowViewModel> missing = _allRows.Where(row => !row.HasPicture).ToList();
            int failed = 0;
            for (int index = 0; index < missing.Count; index++) {
                MapLibraryRowViewModel row = missing[index];
                GenerateProgress = $"Rendering {index + 1} of {missing.Count}: {row.Name}";
                if (await generator.GenerateAsync(row.Name)) {
                    row.Thumbnail = _thumbnails.Get(row.Name);
                } else {
                    failed++;
                }
            }
            GenerateProgress = failed == 0 ? $"{missing.Count} pictures rendered" : $"{missing.Count - failed} pictures rendered, {failed} maps could not be read";
            UpdateCounts();
        } finally {
            IsGenerating = false;
        }
    }

    [RelayCommand]
    private void QueueMissingNavs() {
        _queueMissingNavs();
    }

    /// <summary>Map pack, market maps and custom-maps again, right here with the log in the activity card.</summary>
    [RelayCommand]
    public async Task UpdateMaps() {
        await _install.UpdateMaps();
        foreach (MapLibraryRowViewModel row in _allRows) {
            MapThumbnails.Forget(row.Name);
        }
        await AfterMapsChangedAsync();
    }

    /// <summary>After an update, install or remove: the table, the counts and the market badges follow.</summary>
    private async Task AfterMapsChangedAsync() {
        await RefreshAsync();
        Market.RefreshInstalledState();
    }

    [RelayCommand]
    private void OpenCustomMapsFolder() {
        Directory.CreateDirectory(CustomMapsDir);
        Process.Start(new ProcessStartInfo { FileName = CustomMapsDir, UseShellExecute = true });
    }

    private void UpdateCounts() {
        MissingPictureCount = _allRows.Count(row => !row.HasPicture);
        MissingNavCount = _allRows.Count(row => !row.HasNav && !row.IsInNoMode);
        string waitingText = WaitingMaps.Count == 0 ? "" : $" · {WaitingMaps.Count} waiting in custom-maps";
        Summary = $"{_allRows.Count} maps · {MissingNavCount} without navigation · {MissingPictureCount} without picture{waitingText}";
    }

    private void ApplyFilter() {
        string search = SearchText.Trim();
        Rows.Clear();
        foreach (MapLibraryRowViewModel row in _allRows) {
            bool matchesSearch = search.Length == 0 || row.Name.Contains(search, StringComparison.OrdinalIgnoreCase);
            if (matchesSearch && SelectedFilter.Matches(row)) {
                Rows.Add(row);
            }
        }
    }
}
