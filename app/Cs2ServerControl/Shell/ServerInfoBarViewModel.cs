using System.ComponentModel;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Cs2ServerControl.Services;

namespace Cs2ServerControl.Shell;

/// <summary>
/// The strip above every screen with the facts people ask for most during a
/// LAN evening: which map, which mode, how full, what comes next.
/// </summary>
public sealed partial class ServerInfoBarViewModel : ObservableObject {
    private readonly ServerConnection _connection;
    private readonly MapThumbnails _thumbnails;

    [ObservableProperty]
    private string _uptimeText = "-";

    public ServerInfoBarViewModel(ServerConnection connection) {
        _connection = connection;
        _thumbnails = new MapThumbnails(connection.Paths);
        _connection.PropertyChanged += OnConnectionChanged;
        MapThumbnails.PictureChanged += (_, mapName) => {
            if (string.Equals(mapName, _connection.CurrentMap, StringComparison.OrdinalIgnoreCase)) {
                OnPropertyChanged(nameof(MapThumbnail));
                OnPropertyChanged(nameof(HasMapThumbnail));
            }
        };

        var timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => UpdateUptime(), Dispatcher.CurrentDispatcher);
        timer.Start();
        UpdateUptime();
    }

    public bool IsRunning => _connection.IsRunning;
    public string MapName => IsRunning ? _connection.CurrentMap ?? "-" : "Server stopped";
    public ImageSource? MapThumbnail => IsRunning && _connection.CurrentMap != null ? _thumbnails.Get(_connection.CurrentMap) : null;
    public bool HasMapThumbnail => MapThumbnail != null;
    public string ModeText => IsRunning ? GameModes.TitleOf(_connection.ActiveModeId) : "-";
    public string PlayersText => IsRunning ? $"{_connection.PlayerCount} / {_connection.MaxPlayers} ({_connection.BotCount} bots)" : "-";
    public string NextMapText => IsRunning ? _connection.NextMap ?? "-" : "-";
    public string TimeLeftText => IsRunning && !string.IsNullOrEmpty(_connection.TimeLeft) ? _connection.TimeLeft! : "-";
    public string AddressText => _connection.GameAddress;

    private void UpdateUptime() {
        if (!_connection.IsRunning || _connection.StartedAt == null) {
            UptimeText = "-";
            return;
        }
        TimeSpan uptime = DateTime.Now - _connection.StartedAt.Value;
        UptimeText = $"{(int)uptime.TotalHours:00}:{uptime.Minutes:00}:{uptime.Seconds:00}";
    }

    private void OnConnectionChanged(object? sender, PropertyChangedEventArgs e) {
        switch (e.PropertyName) {
            case nameof(ServerConnection.IsRunning):
            case nameof(ServerConnection.CurrentMap):
                OnPropertyChanged(nameof(IsRunning));
                OnPropertyChanged(nameof(MapName));
                OnPropertyChanged(nameof(MapThumbnail));
                OnPropertyChanged(nameof(HasMapThumbnail));
                OnPropertyChanged(nameof(ModeText));
                OnPropertyChanged(nameof(PlayersText));
                OnPropertyChanged(nameof(NextMapText));
                OnPropertyChanged(nameof(TimeLeftText));
                UpdateUptime();
                break;
            case nameof(ServerConnection.ActiveModeId):
                OnPropertyChanged(nameof(ModeText));
                break;
            case nameof(ServerConnection.PlayerCount):
            case nameof(ServerConnection.MaxPlayers):
            case nameof(ServerConnection.BotCount):
                OnPropertyChanged(nameof(PlayersText));
                break;
            case nameof(ServerConnection.NextMap):
                OnPropertyChanged(nameof(NextMapText));
                break;
            case nameof(ServerConnection.TimeLeft):
                OnPropertyChanged(nameof(TimeLeftText));
                break;
        }
    }
}
