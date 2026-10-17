using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cs2ServerControl.Services;

namespace Cs2ServerControl.Install;

public sealed record AdminRow(Admin Admin) {
    public string Name => Admin.Name;
    public string SteamIdText => Admin.SteamId.ToString(CultureInfo.InvariantCulture);
}

/// <summary>A human on the server who is no admin yet.</summary>
public sealed record OnlinePlayerRow(string Name, ulong SteamId);

/// <summary>
/// The Admins card of the Install screen: who may grab players and use the
/// admin toys in game. Kept in CounterStrikeSharp's admins.json; a running
/// server reloads it at once. Players on the server can be made admin with
/// one click, their SteamID comes with the live feed.
/// </summary>
public sealed partial class AdminsViewModel : ObservableObject {
    private readonly ServerConnection _connection;
    private readonly Dispatcher _dispatcher;
    private IReadOnlyList<(string Name, ulong SteamId)> _online = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddCommand))]
    private string _nameText = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddCommand))]
    private string _steamIdText = "";

    [ObservableProperty]
    private string? _error;

    public AdminsViewModel(ServerConnection connection) {
        _connection = connection;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _connection.Feed.SnapshotReceived += (_, snapshot) => _dispatcher.BeginInvoke(() => UpdateOnline(snapshot));
        Load();
    }

    public ObservableCollection<AdminRow> Admins { get; } = [];
    public ObservableCollection<OnlinePlayerRow> OnlinePlayers { get; } = [];

    public string RuleText => Admins.Count == 0
        ? "Nobody is listed, so everybody on the LAN counts as admin."
        : "Only the admins below may grab players in game.";

    public void Load() {
        Admins.Clear();
        foreach (Admin admin in AdminList.Load(_connection.Paths)) {
            Admins.Add(new AdminRow(admin));
        }
        OnPropertyChanged(nameof(RuleText));
        RefreshOnline();
    }

    private bool CanAdd() {
        return NameText.Trim().Length > 0 && SteamIdText.Trim().Length > 0;
    }

    [RelayCommand(CanExecute = nameof(CanAdd))]
    private async Task Add() {
        ulong? steamId = AdminList.ParseSteamId(SteamIdText);
        if (steamId == null) {
            Error = "That is no SteamID. Paste the number (7656...), a steamcommunity.com/profiles/ link, STEAM_0:1:1234 or [U:1:1234].";
            return;
        }
        if (await SaveAsync([.. Admins.Select(row => row.Admin), new Admin(NameText.Trim(), steamId.Value)])) {
            NameText = "";
            SteamIdText = "";
        }
    }

    [RelayCommand]
    private Task MakeAdmin(OnlinePlayerRow? player) {
        if (player == null) {
            return Task.CompletedTask;
        }
        return SaveAsync([.. Admins.Select(row => row.Admin), new Admin(player.Name, player.SteamId)]);
    }

    [RelayCommand]
    private Task Remove(AdminRow? row) {
        if (row == null) {
            return Task.CompletedTask;
        }
        return SaveAsync(Admins.Select(admin => admin.Admin).Where(admin => admin != row.Admin).ToList());
    }

    private async Task<bool> SaveAsync(IReadOnlyList<Admin> admins) {
        try {
            AdminList.Save(_connection.Paths, admins);
        } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
            Error = "Could not write admins.json: " + exception.Message;
            return false;
        }
        Error = null;
        Load();
        if (_connection.IsRunning) {
            // CounterStrikeSharp reads the file only on start and on this command.
            await _connection.QueryAsync("css_reloadadmins");
        }
        return true;
    }

    private void UpdateOnline(LiveSnapshot snapshot) {
        List<(string Name, ulong SteamId)> humans = snapshot.Players
            .Where(player => !player.IsBot && player.SteamId != 0)
            .Select(player => (player.Name, player.SteamId))
            .ToList();
        if (humans.SequenceEqual(_online)) {
            return;
        }
        _online = humans;
        RefreshOnline();
    }

    private void RefreshOnline() {
        var adminIds = Admins.Select(row => row.Admin.SteamId).ToHashSet();
        OnlinePlayers.Clear();
        foreach ((string name, ulong steamId) in _online.Where(player => !adminIds.Contains(player.SteamId))) {
            OnlinePlayers.Add(new OnlinePlayerRow(name, steamId));
        }
    }
}
