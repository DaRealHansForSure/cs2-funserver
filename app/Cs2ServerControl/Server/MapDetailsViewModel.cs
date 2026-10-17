using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cs2ServerControl.Services;
using Cs2ServerControl.Shell;

namespace Cs2ServerControl.Server;

/// <summary>
/// The map details dialog: screenshot, a top-down overview of the real map
/// geometry and facts from the .bsp, the nav file and the game logs.
/// </summary>
public sealed partial class MapDetailsViewModel : ModalViewModel {
    private const int OverviewSize = 520;
    private const int PreviewWidth = 640;

    private readonly ServerConnection _connection;
    private readonly Action? _pickMap;
    private readonly Func<Task> _setNextMap;

    [ObservableProperty]
    private ImageSource? _overview;

    [ObservableProperty]
    private bool _isLoading = true;

    [ObservableProperty]
    private string? _overviewMessage;

    public MapDetailsViewModel(ServerConnection connection, string mapName, string modeTitle, Action? pickMap, Func<Task> setNextMap) {
        _connection = connection;
        _pickMap = pickMap;
        _setNextMap = setNextMap;
        MapName = mapName;
        PickText = $"Pick for {modeTitle}";
        Preview = new MapThumbnails(connection.Paths, PreviewWidth).Get(mapName);
        _ = LoadAsync();
    }

    public string MapName { get; }
    public string PickText { get; }
    /// <summary>False for a map no mode with a tile plays, there is nothing to pick it for.</summary>
    public bool CanPick => _pickMap != null;
    public ImageSource? Preview { get; }
    public bool HasPreview => Preview != null;
    public bool IsServerRunning => _connection.IsRunning;
    public ObservableCollection<MapStat> Stats { get; } = [];

    [RelayCommand]
    private void Pick() {
        _pickMap?.Invoke();
        Close();
    }

    [RelayCommand]
    private async Task SetNextMap() {
        await _setNextMap();
        Close();
    }

    private async Task LoadAsync() {
        BspWireframe? wireframe = null;
        string bspFile = _connection.Paths.MapFile(MapName);
        try {
            if (File.Exists(bspFile)) {
                wireframe = await BspWireframeLoader.LoadAsync(bspFile);
                Overview = RenderOverview(wireframe);
            } else {
                OverviewMessage = $"{MapName}.bsp is not installed";
            }
        } catch (Exception exception) when (exception is IOException or BspFormatException or UnauthorizedAccessException) {
            OverviewMessage = "The map file could not be read";
        }

        IReadOnlyList<MapStat> stats = await MapDetailsReader.ReadAsync(_connection.Paths, MapName, wireframe);
        foreach (MapStat stat in stats) {
            Stats.Add(stat);
        }
        IsLoading = false;
    }

    private static ImageSource RenderOverview(BspWireframe wireframe) {
        // Token colors, read here because the renderer is a plain service.
        Color Read(string key) => (Color)Application.Current.FindResource(key);
        return MapOverviewRenderer.Render(wireframe, OverviewSize, Read("ViewportBackgroundColor"), Read("WireLowColor"), Read("WireHighColor"));
    }
}
