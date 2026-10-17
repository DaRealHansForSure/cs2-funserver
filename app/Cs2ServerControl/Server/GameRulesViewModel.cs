using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cs2ServerControl.Install;
using Cs2ServerControl.Options;
using Cs2ServerControl.Services;

namespace Cs2ServerControl.Server;

public sealed record ModeChoice(string Id, string Title);

/// <summary>One rule of the shown mode. Empty text = the mode's default, shown as the placeholder.</summary>
public sealed partial class GameRuleRowViewModel : ObservableObject {
    private readonly Action<GameRuleRowViewModel> _changed;
    private bool _isLoading;

    [ObservableProperty]
    private string _text;

    [ObservableProperty]
    private OptionChoice? _selectedChoice;

    public GameRuleRowViewModel(GameRule rule, string text, string? modeDefault, Action<GameRuleRowViewModel> changed) {
        Rule = rule;
        _text = text;
        _changed = changed;
        Placeholder = modeDefault == null ? "Mode default" : $"Mode default: {modeDefault}";

        if (rule.Kind == GameRuleKind.OnOff) {
            string defaultText = modeDefault == "1" ? "on" : "off";
            Choices = [new OptionChoice("", $"Mode default ({defaultText})"), new OptionChoice("1", "On"), new OptionChoice("0", "Off")];
            _isLoading = true;
            SelectedChoice = Choices.FirstOrDefault(choice => choice.Value == text) ?? Choices[0];
            _isLoading = false;
        }
    }

    public GameRule Rule { get; }
    public string Label => Rule.Label;
    public string Hint => Rule.Hint;
    public string Placeholder { get; }
    public bool IsOnOff => Rule.Kind == GameRuleKind.OnOff;
    public bool IsText => !IsOnOff;
    public IReadOnlyList<OptionChoice> Choices { get; } = [];

    partial void OnTextChanged(string value) {
        if (!_isLoading) {
            _changed(this);
        }
    }

    partial void OnSelectedChoiceChanged(OptionChoice? value) {
        if (!_isLoading && value != null) {
            Text = value.Value;
        }
    }
}

/// <summary>
/// Round time, buy time and the other game rules, per mode: a value set here
/// goes into server\cstrike\local_&lt;mode&gt;.cfg, which fun_modes runs after
/// local.cfg only in that mode, so GunGame keeps its own buy time while
/// Classic gets a longer round. Part of the Server screen's draft; every
/// mode keeps its typed values until Apply or Discard.
/// </summary>
public sealed partial class GameRulesViewModel : ObservableObject {
    private readonly ServerConnection _connection;
    private readonly Dictionary<string, Dictionary<string, string>> _saved = [];
    private readonly Dictionary<string, Dictionary<string, string>> _drafts = [];
    private readonly Dictionary<string, IReadOnlyDictionary<string, string>> _defaults = [];

    [ObservableProperty]
    private ModeChoice _selectedMode;

    [ObservableProperty]
    private bool _isChanged;

    public GameRulesViewModel(ServerConnection connection) {
        _connection = connection;
        Modes = MapModes.AllModes.Select(modeId => new ModeChoice(modeId, GameModes.TitleOf(modeId))).ToList();
        _selectedMode = Modes[0];
        Load();
    }

    public IReadOnlyList<ModeChoice> Modes { get; }
    public ObservableCollection<GameRuleRowViewModel> Rows { get; } = [];
    public string FileHint => $"Empty = mode default · saved in cstrike\\local_{SelectedMode.Id}.cfg";

    /// <summary>Reads every mode's file and defaults again and drops all drafts.</summary>
    public void Load() {
        foreach (ModeChoice mode in Modes) {
            var settings = new LocalSettings(_connection.Paths.LocalModeConfigFile(mode.Id));
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (GameRule rule in GameRules.All) {
                string? value = settings.Get(rule.Cvar);
                if (value != null) {
                    values[rule.Cvar] = value;
                }
            }
            _saved[mode.Id] = values;
            _drafts[mode.Id] = new Dictionary<string, string>(values, StringComparer.OrdinalIgnoreCase);
            _defaults[mode.Id] = GameRules.ReadModeDefaults(_connection.Paths, mode.Id);
        }
        ShowMode();
        IsChanged = false;
    }

    /// <summary>The Server screen shows the rules of the mode picked there.</summary>
    public void FollowMode(string modeId) {
        ModeChoice? mode = Modes.FirstOrDefault(choice => choice.Id == modeId);
        if (mode != null && mode != SelectedMode) {
            SelectedMode = mode;
        }
    }

    /// <summary>
    /// Writes every mode whose rules changed and, when the server runs that
    /// mode, sends the values right away; a cleared value sends the mode
    /// default back. Null when it worked, else what is wrong.
    /// </summary>
    public async Task<string?> ApplyAsync() {
        if (!IsChanged) {
            return null;
        }

        var normalized = new Dictionary<string, Dictionary<string, string>>();
        foreach ((string modeId, Dictionary<string, string> draft) in _drafts) {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (GameRule rule in GameRules.All) {
                string text = draft.TryGetValue(rule.Cvar, out string? typed) ? typed : "";
                if (!GameRules.TryNormalize(rule, text, out string value, out string? error)) {
                    return $"{GameModes.TitleOf(modeId)}: {error}";
                }
                if (value.Length > 0) {
                    values[rule.Cvar] = value;
                }
            }
            normalized[modeId] = values;
        }

        try {
            foreach ((string modeId, Dictionary<string, string> values) in normalized) {
                if (SameValues(values, _saved[modeId])) {
                    continue;
                }
                var settings = new LocalSettings(_connection.Paths.LocalModeConfigFile(modeId));
                foreach (GameRule rule in GameRules.All) {
                    if (values.TryGetValue(rule.Cvar, out string? value)) {
                        settings.Set(rule.Cvar, value);
                    } else {
                        settings.Remove(rule.Cvar);
                    }
                }
                settings.Save();
            }
        } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
            return "Could not write the game rules: " + exception.Message;
        }

        string? activeMode = _connection.ActiveModeId;
        if (_connection.IsRunning && activeMode != null && normalized.TryGetValue(activeMode, out Dictionary<string, string>? active)) {
            IReadOnlyDictionary<string, string> defaults = _defaults[activeMode];
            List<string> commands = [];
            foreach (GameRule rule in GameRules.All) {
                string? value = active.TryGetValue(rule.Cvar, out string? set) ? set : defaults.GetValueOrDefault(rule.Cvar);
                if (value != null) {
                    commands.Add($"{rule.Cvar} \"{value}\"");
                }
            }
            await _connection.QueryAsync(string.Join(";", commands));
        }

        Load();
        return null;
    }

    /// <summary>Empties every field of the shown mode, so its own values apply again once Apply removes them.</summary>
    [RelayCommand]
    private void ResetToDefaults() {
        foreach (GameRuleRowViewModel row in Rows) {
            if (row.IsOnOff) {
                row.SelectedChoice = row.Choices[0];
            } else {
                row.Text = "";
            }
        }
    }

    partial void OnSelectedModeChanged(ModeChoice value) {
        ShowMode();
        OnPropertyChanged(nameof(FileHint));
    }

    private void ShowMode() {
        Rows.Clear();
        string modeId = SelectedMode.Id;
        Dictionary<string, string> draft = _drafts[modeId];
        IReadOnlyDictionary<string, string> defaults = _defaults[modeId];
        foreach (GameRule rule in GameRules.All) {
            string text = draft.TryGetValue(rule.Cvar, out string? typed) ? typed : "";
            Rows.Add(new GameRuleRowViewModel(rule, text, defaults.GetValueOrDefault(rule.Cvar), OnRowChanged));
        }
    }

    private void OnRowChanged(GameRuleRowViewModel row) {
        Dictionary<string, string> draft = _drafts[SelectedMode.Id];
        if (row.Text.Trim().Length == 0) {
            draft.Remove(row.Rule.Cvar);
        } else {
            draft[row.Rule.Cvar] = row.Text;
        }
        IsChanged = _drafts.Any(entry => !SameValues(entry.Value, _saved[entry.Key]));
    }

    private static bool SameValues(IReadOnlyDictionary<string, string> left, IReadOnlyDictionary<string, string> right) {
        return left.Count == right.Count
            && left.All(entry => right.TryGetValue(entry.Key, out string? other) && other.Trim() == entry.Value.Trim());
    }
}
