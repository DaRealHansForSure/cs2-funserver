using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cs2ServerControl.Install;
using Cs2ServerControl.Services;
using Cs2ServerControl.Shell;

namespace Cs2ServerControl.Maps;

public sealed partial class MarketRowViewModel : ObservableObject {
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanInstall), nameof(StateText))]
    private bool _isInstalled;

    /// <summary>Installed through the market (in market.csv), so the market may remove it again.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText))]
    private bool _isRemovable;

    public MarketRowViewModel(MarketMap map) {
        Map = map;
        Thumbnail = LoadCardImage(map.ImageUrl);
    }

    public MarketMap Map { get; }
    public ImageSource? Thumbnail { get; }
    public string Name => Map.Name;
    public bool CanInstall => !IsInstalled;
    public string CategoryText => Map.Category.Length == 0 ? "Map" : Map.Category;
    public string StatsText => string.Create(CultureInfo.InvariantCulture, $"{Map.Likes:N0} likes · {Map.Views:N0} views · {Map.Added:yyyy}");

    public string StateText {
        get {
            if (!IsInstalled) {
                return "";
            }
            return IsRemovable ? "Installed from the market" : "Installed (map pack or own map)";
        }
    }

    /// <summary>WPF downloads the picture by itself in the background; a broken URL simply shows no picture.</summary>
    private static ImageSource? LoadCardImage(string? url) {
        if (url == null || !Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)) {
            return null;
        }
        try {
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = uri;
            image.DecodePixelWidth = 220;
            image.EndInit();
            return image;
        } catch (Exception exception) when (exception is NotSupportedException or IOException) {
            return null;
        }
    }
}

public sealed record MarketCategoryChoice(int? Id, string Label);

public sealed record MarketSortChoice(MarketSort Sort, string Label);

/// <summary>
/// The market tab: CS 1.6 maps from GameBanana, searchable by name, by
/// category and sorted like the site. Install runs the shared installer
/// (cache, unpack, missing sprites, picture, map lists); Remove only takes
/// back maps the market installed. Nothing is fetched until the tab opens.
/// </summary>
public sealed partial class MarketViewModel : ObservableObject {
    private static readonly TimeSpan SearchDelay = TimeSpan.FromMilliseconds(500);

    // With installed maps hidden, pages keep loading until about this many
    // cards show (or a few pages were tried), so the view never looks empty.
    private const int FillTarget = 30;
    private const int MaxFillPages = 6;

    private readonly ServerConnection _connection;
    private readonly IDialogService _dialogs;
    private readonly InstallViewModel _install;
    private readonly Func<Task> _afterMapsChanged;
    private readonly DispatcherTimer _searchTimer;
    private readonly List<MarketRowViewModel> _loadedRows = [];
    private bool _hasLoadedCategories;
    private bool _hasSearched;
    private int _searchVersion;
    private int _loadedPage;
    private int _totalCount;

    [ObservableProperty]
    private MarketCategoryChoice _selectedCategory;

    [ObservableProperty]
    private MarketSortChoice _selectedSort;

    [ObservableProperty]
    private string _searchText = "";

    /// <summary>Hides maps that are already on the server, so the market shows only new ones.</summary>
    [ObservableProperty]
    private bool _hideInstalled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanLoadMore))]
    private bool _isLoading;

    [ObservableProperty]
    private string _statusText = "";

    public MarketViewModel(ServerConnection connection, IDialogService dialogs, InstallViewModel install, Func<Task> afterMapsChanged) {
        _connection = connection;
        _dialogs = dialogs;
        _install = install;
        _afterMapsChanged = afterMapsChanged;
        Categories.Add(new MarketCategoryChoice(null, "All categories"));
        _selectedCategory = Categories[0];
        _selectedSort = Sorts[0];
        _hideInstalled = connection.Settings.MarketHidesInstalled;
        _searchTimer = new DispatcherTimer(SearchDelay, DispatcherPriority.Background, (_, _) => {
            _searchTimer!.Stop();
            _ = SearchAsync();
        }, Dispatcher.CurrentDispatcher);
        _searchTimer.Stop();
        _install.PropertyChanged += OnInstallChanged;
    }

    public ObservableCollection<MarketCategoryChoice> Categories { get; } = [];
    public ObservableCollection<MarketRowViewModel> Rows { get; } = [];

    public IReadOnlyList<MarketSortChoice> Sorts { get; } = [
        new(MarketSort.MostDownloaded, "Most downloaded"),
        new(MarketSort.MostLiked, "Most liked"),
        new(MarketSort.MostViewed, "Most viewed"),
        new(MarketSort.Newest, "Newest"),
        new(MarketSort.RecentlyUpdated, "Recently updated"),
    ];

    public bool CanLoadMore => !IsLoading && _loadedRows.Count < _totalCount;
    public bool CanChangeMaps => !_install.IsBusy && _connection.Paths.IsServerInstalled;

    /// <summary>The first time the tab opens: the categories and the most downloaded maps.</summary>
    public void OnShown() {
        if (!_hasLoadedCategories) {
            _hasLoadedCategories = true;
            _ = LoadCategoriesAsync();
        }
        if (!_hasSearched) {
            _ = SearchAsync();
        }
    }

    /// <summary>After an update, install or remove elsewhere: which cards say Installed.</summary>
    public void RefreshInstalledState() {
        (HashSet<string> installedModIds, HashSet<string> marketModIds) = ReadInstalledModIds();
        foreach (MarketRowViewModel row in _loadedRows) {
            ApplyInstalledState(row, installedModIds, marketModIds);
        }
        ShowRows();
    }

    partial void OnHideInstalledChanged(bool value) {
        _connection.Settings.MarketHidesInstalled = value;
        _connection.Settings.Save();
        ShowRows();
        _ = FillAsync(_searchVersion);
    }

    partial void OnSearchTextChanged(string value) {
        // Typing restarts the timer, so only a pause sends a request.
        _searchTimer.Stop();
        _searchTimer.Start();
    }

    partial void OnSelectedCategoryChanged(MarketCategoryChoice value) {
        if (value != null) {
            _ = SearchAsync();
        }
    }

    partial void OnSelectedSortChanged(MarketSortChoice value) {
        if (value != null) {
            _ = SearchAsync();
        }
    }

    [RelayCommand]
    private Task Search() {
        return SearchAsync();
    }

    /// <summary>The next page; the view calls it too when the list is scrolled near its end.</summary>
    [RelayCommand]
    public async Task LoadMore() {
        if (!CanLoadMore) {
            return;
        }
        await LoadPageAsync(_loadedPage + 1, _searchVersion);
    }

    [RelayCommand]
    private async Task InstallMap(MarketRowViewModel? row) {
        if (row == null || !CanChangeMaps) {
            return;
        }
        IReadOnlyList<string> installed = await _install.InstallMarketMap(row.Map);
        foreach (string mapName in installed) {
            MapThumbnails.Forget(mapName);
        }
        await _afterMapsChanged();
    }

    [RelayCommand]
    private async Task RemoveMap(MarketRowViewModel? row) {
        if (row == null || !row.IsRemovable || !CanChangeMaps) {
            return;
        }

        MapListEntry? entry = MapList.LoadIfExists(MarketListFile)
            .FirstOrDefault(existing => existing.ModId == row.Map.ModId.ToString(CultureInfo.InvariantCulture));
        bool isPlayingNow = entry != null && _connection.IsRunning
            && entry.MapNames.Any(mapName => string.Equals(mapName, _connection.CurrentMap, StringComparison.OrdinalIgnoreCase));
        if (isPlayingNow) {
            StatusText = $"{_connection.CurrentMap} runs right now. Change the map first, then remove it.";
            return;
        }

        bool confirmed = await _dialogs.ConfirmAsync("Remove map",
            $"Delete {row.Name} from the server? Its map files, navigation file and picture go, it leaves every map list. Install brings it back from the download cache.",
            "Remove");
        if (!confirmed) {
            return;
        }
        IReadOnlyList<string> removed = await _install.RemoveMarketMap(row.Map);
        foreach (string mapName in removed) {
            MapThumbnails.Forget(mapName);
        }
        await _afterMapsChanged();
    }

    /// <summary>The map's GameBanana page in the browser, for the description and the comments.</summary>
    [RelayCommand]
    private void OpenPage(MarketRowViewModel? row) {
        if (row == null) {
            return;
        }
        try {
            Process.Start(new ProcessStartInfo { FileName = row.Map.PageUrl, UseShellExecute = true });
        } catch (Win32Exception exception) {
            StatusText = exception.Message;
        }
    }

    private string MarketListFile => Path.Combine(_connection.Paths.ToolsDir, "market.csv");
    private string MapListFile => Path.Combine(_connection.Paths.ToolsDir, "maps.csv");

    private async Task LoadCategoriesAsync() {
        try {
            IReadOnlyList<MarketCategory> categories = await GameBananaMarket.LoadCategoriesAsync(CancellationToken.None);
            foreach (MarketCategory category in categories) {
                Categories.Add(new MarketCategoryChoice(category.Id, $"{category.Name} ({category.MapCount:N0})"));
            }
        } catch (Exception exception) when (IsNetworkProblem(exception)) {
            // The search still works without categories; the status shows the search's own error.
        }
    }

    /// <summary>A new search from page 1; answers of an older search that arrive late are dropped.</summary>
    private async Task SearchAsync() {
        _hasSearched = true;
        _searchVersion++;
        int version = _searchVersion;
        Rows.Clear();
        _loadedRows.Clear();
        _totalCount = 0;
        _loadedPage = 0;
        await LoadPageAsync(1, version);
        await FillAsync(version);
    }

    private async Task LoadPageAsync(int page, int version) {
        IsLoading = true;
        StatusText = "Loading from GameBanana...";
        try {
            MarketPage result = await GameBananaMarket.SearchAsync(page, SelectedCategory?.Id, SearchText, SelectedSort?.Sort ?? MarketSort.MostDownloaded, CancellationToken.None);
            if (version != _searchVersion) {
                return;
            }

            (HashSet<string> installedModIds, HashSet<string> marketModIds) = ReadInstalledModIds();
            foreach (MarketMap map in result.Maps) {
                var row = new MarketRowViewModel(map);
                ApplyInstalledState(row, installedModIds, marketModIds);
                _loadedRows.Add(row);
                if (IsShown(row)) {
                    Rows.Add(row);
                }
            }
            _loadedPage = page;
            _totalCount = result.TotalCount;
            UpdateStatusText();
        } catch (Exception exception) when (IsNetworkProblem(exception)) {
            if (version == _searchVersion) {
                StatusText = $"GameBanana did not answer: {exception.Message}";
            }
        } finally {
            if (version == _searchVersion) {
                IsLoading = false;
                OnPropertyChanged(nameof(CanLoadMore));
            }
        }
    }

    /// <summary>With installed maps hidden, a page may add few cards; load on until the view is filled.</summary>
    private async Task FillAsync(int version) {
        for (int pages = 0; pages < MaxFillPages; pages++) {
            bool needsMore = HideInstalled && Rows.Count < FillTarget && CanLoadMore && version == _searchVersion;
            if (!needsMore) {
                return;
            }
            await LoadPageAsync(_loadedPage + 1, version);
        }
    }

    private bool IsShown(MarketRowViewModel row) {
        return !HideInstalled || !row.IsInstalled;
    }

    private void ShowRows() {
        Rows.Clear();
        foreach (MarketRowViewModel row in _loadedRows.Where(IsShown)) {
            Rows.Add(row);
        }
        UpdateStatusText();
        OnPropertyChanged(nameof(CanLoadMore));
    }

    private void UpdateStatusText() {
        if (_loadedRows.Count == 0) {
            StatusText = "No maps found.";
            return;
        }
        string loaded = string.Create(CultureInfo.InvariantCulture, $"{_loadedRows.Count:N0} of {_totalCount:N0} maps loaded");
        int hidden = _loadedRows.Count - Rows.Count;
        StatusText = hidden == 0 ? loaded : string.Create(CultureInfo.InvariantCulture, $"{loaded}, {hidden:N0} already installed are hidden");
    }

    /// <summary>
    /// ModIds of the map pack and of market installs, plus the maps on disk
    /// by name: a stock or own map with the same name counts as installed too.
    /// </summary>
    private (HashSet<string> Installed, HashSet<string> FromMarket) ReadInstalledModIds() {
        var fromMarket = new HashSet<string>(StringComparer.Ordinal);
        var installed = new HashSet<string>(StringComparer.Ordinal);
        try {
            foreach (MapListEntry entry in MapList.LoadIfExists(MarketListFile)) {
                fromMarket.Add(entry.ModId);
                installed.Add(entry.ModId);
            }
            foreach (MapListEntry entry in MapList.LoadIfExists(MapListFile)) {
                installed.Add(entry.ModId);
            }
        } catch (IOException) {
            // Without the lists the badges are only by map name.
        }
        return (installed, fromMarket);
    }

    private void ApplyInstalledState(MarketRowViewModel row, HashSet<string> installedModIds, HashSet<string> marketModIds) {
        string modId = row.Map.ModId.ToString(CultureInfo.InvariantCulture);
        row.IsRemovable = marketModIds.Contains(modId);
        row.IsInstalled = installedModIds.Contains(modId) || File.Exists(_connection.Paths.BspFile(row.Map.Name));
    }

    private void OnInstallChanged(object? sender, PropertyChangedEventArgs e) {
        if (e.PropertyName == nameof(InstallViewModel.IsBusy)) {
            OnPropertyChanged(nameof(CanChangeMaps));
        }
    }

    private static bool IsNetworkProblem(Exception exception) {
        return exception is HttpRequestException or JsonException or TaskCanceledException or IOException;
    }
}
