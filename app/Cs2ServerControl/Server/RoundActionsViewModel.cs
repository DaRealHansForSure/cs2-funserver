using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cs2ServerControl.Services;

namespace Cs2ServerControl.Server;

public sealed record FunRound(string Id, string Title);

/// <summary>
/// Things a host does during a round, which run at once instead of waiting
/// for Apply: they change the game in progress, not a setting.
/// </summary>
public sealed partial class RoundActionsViewModel : ObservableObject {
    private readonly ServerConnection _connection;

    [ObservableProperty]
    private FunRound _nextFunRound;

    [ObservableProperty]
    private string _message = "";

    public RoundActionsViewModel(ServerConnection connection) {
        _connection = connection;
        _nextFunRound = FunRounds[0];
    }

    // Ids match amx_funround in fun_rounds.sma.
    public IReadOnlyList<FunRound> FunRounds { get; } = [
        new("random", "Random"),
        new("knives", "Knives only"),
        new("awp", "AWP only"),
        new("lowgrav", "Low gravity"),
        new("speed", "Speed"),
        new("headshot", "Headshots only"),
        new("deagle", "One bullet Deagle"),
        new("hewar", "HE war"),
        new("ghost", "Ghosts"),
        new("juggernaut", "Juggernaut"),
    ];

    [RelayCommand]
    private Task ForceFunRound() {
        return _connection.RunAsync($"amx_funround {NextFunRound.Id}");
    }

    [RelayCommand]
    private Task RestartRound() {
        return _connection.RunAsync("sv_restart 1");
    }

    /// <summary>
    /// Only for the current map: the mode configs decide the hook on every
    /// map start (GunGame and Zombie switch it off on purpose).
    /// </summary>
    [RelayCommand]
    private Task HookOn() {
        return _connection.RunAsync("fun_hook_enabled 1");
    }

    [RelayCommand]
    private Task HookOff() {
        return _connection.RunAsync("fun_hook_enabled 0");
    }

    [RelayCommand]
    private async Task SendMessage() {
        string text = Message.Trim().Replace("\"", "'");
        if (text.Length == 0) {
            return;
        }
        Message = "";
        await _connection.RunAsync($"amx_say \"{text}\"");
    }
}
