using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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
    public bool HasPicture => Thumbnail != null;
    public bool IsWorkshop => Entry.WorkshopId != null;
    // Workshop maps are in no list on purpose: they are played by pick in any mode.
    public bool IsInNoMode => Entry.ModeIds.Count == 0 && !IsWorkshop;

    public string ModesText {
        get {
            if (IsWorkshop) {
                return "Workshop, any mode by pick";
            }
            if (Entry.ModeIds.Count == 0) {
                return "In no map list";
            }
            return string.Join(", ", Entry.ModeIds.Select(GameModes.TitleOf));
        }
    }

    public string SizeText => (Entry.SizeBytes / 1024.0 / 1024.0).ToString("0.0", CultureInfo.InvariantCulture) + " MB";

    /// <summary>The first mode with a tile on the Server screen, where Pick takes the map.</summary>
    public string? PickModeId => Entry.ModeIds.FirstOrDefault(modeId => GameModes.All.Any(mode => mode.Id == modeId));
    public bool CanPick => PickModeId != null || IsWorkshop;
    // nextlevel only knows the maps in the server's maps folder.
    public bool CanSetNext => !IsWorkshop;
}

/// <summary>What the Show list narrows the table to.</summary>
public sealed record MapFilter(string Title, Func<MapLibraryRowViewModel, bool> Matches);

/// <summary>
/// The Maps screen: every installed map in one table, with the modes that
/// play it and its picture, and the Workshop tab with the host's Workshop
/// maps. Pick goes to the Server draft, Next to the running server.
/// </summary>
public sealed partial class MapsViewModel : ObservableObject, IScreen {
    private readonly ServerConnection _connection;
    private readonly IDialogService _dialogs;
    private readonly MapThumbnails _thumbnails;
    private readonly Func<string, string, bool> _pickOnServer;
    private List<MapLibraryRowViewModel> _allRows = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInstalledTab))]
    private bool _isWorkshopTab;

    [ObservableProperty]
    private MapFilter _selectedFilter;

    [ObservableProperty]
    private string _searchText = "";

    [ObservableProperty]
    private string _summary = "";

    public MapsViewModel(ServerConnection connection, IDialogService dialogs, Func<string, string, bool> pickOnServer) {
        _connection = connection;
        _dialogs = dialogs;
        _pickOnServer = pickOnServer;
        _thumbnails = new MapThumbnails(connection.Paths);
        Workshop = new WorkshopViewModel(connection, dialogs, pickOnServer);
        // The game's map pictures arrive in the background on the first start.
        MapThumbnails.PictureChanged += OnPictureChanged;

        List<MapFilter> filters = [new("All maps", _ => true)];
        foreach (GameMode mode in GameModes.All) {
            filters.Add(new MapFilter(mode.Title, row => row.Entry.ModeIds.Contains(mode.Id)));
        }
        filters.Add(new MapFilter("Workshop", row => row.IsWorkshop));
        filters.Add(new MapFilter("Missing picture", row => !row.HasPicture));
        filters.Add(new MapFilter("In no map list", row => row.IsInNoMode));
        Filters = filters;
        _selectedFilter = filters[0];
    }

    public WorkshopViewModel Workshop { get; }

    public bool IsInstalledTab {
        get => !IsWorkshopTab;
        set => IsWorkshopTab = !value;
    }

    public IReadOnlyList<MapFilter> Filters { get; }
    public ObservableCollection<MapLibraryRowViewModel> Rows { get; } = [];
    public bool IsServerRunning => _connection.IsRunning;

    public IReadOnlyList<string> StatusItems => [Summary];
    public string StatusRight => "Pictures from tools\\previews";

    public void OnShown() {
        _ = RefreshAsync();
        Workshop.Refresh();
    }

    public void OnHidden() {
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
        IReadOnlyList<MapLibraryEntry> entries = await Task.Run(() => MapLibrary.Scan(paths));

        _allRows = entries.Select(entry => new MapLibraryRowViewModel(entry, PictureOf(entry))).ToList();
        UpdateSummary();
        ApplyFilter();
    }

    private void UpdateSummary() {
        int withoutPicture = _allRows.Count(row => !row.HasPicture);
        int inNoList = _allRows.Count(row => row.IsInNoMode);
        Summary = $"{_allRows.Count} maps · {inNoList} in no map list · {withoutPicture} without picture";
    }

    private void OnPictureChanged(object? sender, string mapName) {
        foreach (MapLibraryRowViewModel row in _allRows.Where(row => string.Equals(row.Name, mapName, StringComparison.OrdinalIgnoreCase))) {
            row.Thumbnail = PictureOf(row.Entry);
        }
        UpdateSummary();
    }

    /// <summary>A Workshop map has the picture from its Workshop page, saved under its item id.</summary>
    private ImageSource? PictureOf(MapLibraryEntry entry) {
        ImageSource? picture = _thumbnails.Get(entry.Name);
        if (picture == null && entry.WorkshopId != null) {
            picture = _thumbnails.Get(entry.WorkshopId);
        }
        return picture;
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
        if (row == null) {
            return;
        }
        string? modeId = row.PickModeId;
        if (modeId == null && row.IsWorkshop) {
            // A Workshop map is in no mode list; it goes into the mode the server runs.
            modeId = _connection.ActiveModeId ?? GameModes.All[0].Id;
        }
        if (modeId == null) {
            return;
        }
        _pickOnServer(modeId, row.Name);
    }

    [RelayCommand]
    private async Task SetNextMap(MapLibraryRowViewModel? row) {
        if (row == null || !row.CanSetNext || !_connection.IsRunning) {
            return;
        }
        await _connection.RunAsync($"nextlevel {row.Name}");
        _connection.NextMap = row.Name;
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
