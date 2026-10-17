using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cs2ServerControl.Services;
using Cs2ServerControl.Shell;

namespace Cs2ServerControl.Options;

/// <summary>One choice of a Select: the stored value and what the list shows.</summary>
public sealed record OptionChoice(string Value, string Label);

public sealed record PortRow(string Name, string Protocol, int Port, string Purpose);

public sealed record FirewallRow(FirewallRuleState Rule) {
    public string StateText {
        get {
            if (Rule.Exists == true) {
                return "Allowed";
            }
            if (Rule.Exists == false) {
                return "Missing";
            }
            return "Unknown";
        }
    }

    public bool IsMissing => Rule.Exists == false;
}

/// <summary>
/// The app's own settings. Like every other screen a draft: picks only mark
/// the screen dirty, Apply saves them, Discard goes back. Address and
/// password only change while the server is stopped, a running server keeps
/// what it was started with.
/// </summary>
public sealed partial class OptionsViewModel : ObservableObject, IScreen {
    private readonly ServerConnection _connection;
    private bool _isLoading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    private OptionChoice _selectedAddress;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    private OptionChoice _selectedStartScreen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    private bool _isNewPasswordPending;

    [ObservableProperty]
    private string? _applyError;

    [ObservableProperty]
    private string? _passwordMessage;

    [ObservableProperty]
    private bool _isCheckingFirewall;

    [ObservableProperty]
    private string _firewallSummary = "";

    public OptionsViewModel(ServerConnection connection, IReadOnlyList<OptionChoice> screens) {
        _connection = connection;
        StartScreens = [new OptionChoice("", "The screen used last"), .. screens];
        // A placeholder until Load fills the list from the network adapters.
        _selectedAddress = new OptionChoice("", "Automatic");
        _selectedStartScreen = StartScreens[0];
        _connection.PropertyChanged += OnConnectionChanged;
        Load();
    }

    public ObservableCollection<OptionChoice> AddressChoices { get; } = [];
    public IReadOnlyList<OptionChoice> StartScreens { get; }
    public ObservableCollection<FirewallRow> FirewallRules { get; } = [];

    public IReadOnlyList<PortRow> Ports { get; } = [
        new("Game", "UDP and TCP", ServerProcess.GamePort, "Players join here, LAN browser"),
        new("Fast download", "TCP", ServerProcess.FastDownloadPort, "Maps and sounds for joining players"),
        new("TV (HLTV)", "UDP", ServerProcess.TvPort, "Spectator view for a TV or projector"),
        new("Nav helper", "UDP", NavBuilder.HelperPort, "Hidden server for nav files, this PC only"),
        new("Live feed", "UDP", LiveFeedListener.Port, "Live Map data, this PC only"),
    ];

    public bool IsServerRunning => _connection.IsRunning;
    public bool CanChangeServerOptions => !_connection.IsRunning;
    public string CurrentAddressText => $"Now: {_connection.GameAddress}";
    public string PackageFolder => _connection.Paths.RootDir;
    public string SettingsFile => AppSettings.SettingsFile;
    public string VersionText => $"Version {Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "-"}";

    // The ComboBox briefly pushes null while Load refills its list.
    public bool IsDirty => (SelectedAddress?.Value ?? _connection.Settings.LanAddress) != _connection.Settings.LanAddress
        || (SelectedStartScreen?.Value ?? _connection.Settings.StartScreen) != _connection.Settings.StartScreen
        || IsNewPasswordPending;

    public IReadOnlyList<string> StatusItems => [CurrentAddressText];
    public string StatusRight => "Saved in %APPDATA%\\Cs2ServerControl\\settings.json";

    public void OnShown() {
        if (!IsDirty) {
            Load();
        }
        _ = CheckFirewallAsync();
    }

    public void OnHidden() {
    }

    [RelayCommand]
    private void Apply() {
        ApplyError = null;
        bool needsStoppedServer = SelectedAddress.Value != _connection.Settings.LanAddress || IsNewPasswordPending;
        if (needsStoppedServer && _connection.IsRunning) {
            ApplyError = "Stop the server first: it keeps the address and password it was started with.";
            return;
        }

        if (SelectedAddress.Value != _connection.Settings.LanAddress) {
            _connection.ChangeLanAddress(SelectedAddress.Value);
        }
        if (IsNewPasswordPending) {
            _connection.RenewRconPassword();
            IsNewPasswordPending = false;
            PasswordMessage = "New password saved. The next server start uses it.";
        }
        _connection.Settings.StartScreen = SelectedStartScreen.Value;
        _connection.Settings.Save();
        Load();
    }

    [RelayCommand]
    private void Discard() {
        ApplyError = null;
        IsNewPasswordPending = false;
        Load();
    }

    [RelayCommand]
    private async Task CheckFirewallAsync() {
        if (IsCheckingFirewall) {
            return;
        }
        IsCheckingFirewall = true;
        try {
            IReadOnlyList<FirewallRuleState> rules = await Task.Run(ServerProcess.ReadFirewallRules);
            FirewallRules.Clear();
            foreach (FirewallRuleState rule in rules) {
                FirewallRules.Add(new FirewallRow(rule));
            }
            int missing = rules.Count(rule => rule.Exists == false);
            FirewallSummary = missing == 0 ? "All rules are in place." : $"{missing} of {rules.Count} rules are missing, other PCs may not get in.";
        } finally {
            IsCheckingFirewall = false;
        }
    }

    /// <summary>Right away, not part of the draft: Windows asks for administrator rights itself.</summary>
    [RelayCommand]
    private async Task RepairFirewall() {
        FirewallSummary = "Waiting for the Windows administrator prompt...";
        bool ran = await ServerProcess.AddFirewallRulesAsync();
        if (!ran) {
            FirewallSummary = "The administrator prompt was declined, nothing changed.";
            return;
        }
        await CheckFirewallAsync();
    }

    [RelayCommand]
    private void OpenFolder(string? which) {
        string? folder = FolderFor(which);
        if (folder == null) {
            return;
        }
        if (!Directory.Exists(folder)) {
            ApplyError = $"{folder} does not exist yet.";
            return;
        }
        try {
            Process.Start(new ProcessStartInfo { FileName = folder, UseShellExecute = true });
        } catch (Win32Exception exception) {
            ApplyError = exception.Message;
        }
    }

    private string? FolderFor(string? which) {
        switch (which) {
            case "package":
                return _connection.Paths.RootDir;
            case "logs":
                return _connection.Paths.AmxLogsDir;
            case "settings":
                return Path.GetDirectoryName(AppSettings.SettingsFile);
            default:
                return null;
        }
    }

    private void Load() {
        _isLoading = true;
        try {
            AddressChoices.Clear();
            IPAddress automatic = ServerProcess.FindLanAddress();
            AddressChoices.Add(new OptionChoice("", $"Automatic ({automatic})"));
            foreach (IPAddress address in ServerProcess.ListLanAddresses()) {
                AddressChoices.Add(new OptionChoice(address.ToString(), address.ToString()));
            }

            string storedAddress = _connection.Settings.LanAddress;
            OptionChoice? stored = AddressChoices.FirstOrDefault(choice => choice.Value == storedAddress);
            if (stored == null) {
                // A stored address of an adapter that is gone still shows, so Discard keeps it.
                stored = new OptionChoice(storedAddress, $"{storedAddress} (not on this PC now)");
                AddressChoices.Add(stored);
            }
            SelectedAddress = stored;
            SelectedStartScreen = StartScreens.FirstOrDefault(choice => choice.Value == _connection.Settings.StartScreen) ?? StartScreens[0];
        } finally {
            _isLoading = false;
        }
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(CurrentAddressText));
    }

    partial void OnSelectedAddressChanged(OptionChoice value) {
        if (!_isLoading) {
            ApplyError = null;
        }
    }

    private void OnConnectionChanged(object? sender, PropertyChangedEventArgs e) {
        if (e.PropertyName == nameof(ServerConnection.IsRunning)) {
            OnPropertyChanged(nameof(IsServerRunning));
            OnPropertyChanged(nameof(CanChangeServerOptions));
        }
        if (e.PropertyName is nameof(ServerConnection.GameAddress) or nameof(ServerConnection.LanAddress)) {
            OnPropertyChanged(nameof(CurrentAddressText));
        }
    }
}
