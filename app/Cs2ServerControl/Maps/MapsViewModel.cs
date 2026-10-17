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
    public bool IsInNoMode => Entry.ModeIds.Count == 0;

    public string ModesText {
        get {
            if (Entry.ModeIds.Count == 0) {
                return "In no map list";
            }
            return string.Join(", ", Entry.ModeIds.Select(GameModes.TitleOf));
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
/// play it and its picture. Pick goes to the Server draft, Next to the
/// running server.
/// </summary>
public sealed partial class MapsViewModel : ObservableObject, IScreen {
    private readonly ServerConnection _connection;
    private readonly IDialogService _dialogs;
    private readonly MapThumbnails _thumbnails;
    private readonly Func<string, string, bool> _pickOnServer;
    private List<MapLibraryRowViewModel> _allRows = [];

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

        List<MapFilter> filters = [new("All maps", _ => true)];
        foreach (GameMode mode in GameModes.All) {
            filters.Add(new MapFilter(mode.Title, row => row.Entry.ModeIds.Contains(mode.Id)));
        }
        filters.Add(new MapFilter("Missing picture", row => !row.HasPicture));
        filters.Add(new MapFilter("In no map list", row => row.IsInNoMode));
        Filters = filters;
        _selectedFilter = filters[0];
    }

    public IReadOnlyList<MapFilter> Filters { get; }
    public ObservableCollection<MapLibraryRowViewModel> Rows { get; } = [];
    public bool IsServerRunning => _connection.IsRunning;

    public IReadOnlyList<string> StatusItems => [Summary];
    public string StatusRight => "Pictures from tools\\previews";

    public void OnShown() {
        _ = RefreshAsync();
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

        _allRows = entries.Select(entry => new MapLibraryRowViewModel(entry, _thumbnails.Get(entry.Name))).ToList();
        int withoutPicture = _allRows.Count(row => !row.HasPicture);
        int inNoList = _allRows.Count(row => row.IsInNoMode);
        Summary = $"{_allRows.Count} maps · {inNoList} in no map list · {withoutPicture} without picture";
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
