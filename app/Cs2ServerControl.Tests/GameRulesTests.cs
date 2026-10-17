using System.IO;
using System.Windows.Threading;
using Cs2ServerControl.Install;
using Cs2ServerControl.Server;
using Cs2ServerControl.Services;
using Xunit;

namespace Cs2ServerControl.Tests;

public class GameRulesTests : IDisposable {
    private readonly string _rootDir = Path.Combine(Path.GetTempPath(), "cs16-rules-tests-" + Guid.NewGuid().ToString("N"));
    private readonly PackagePaths _paths;

    public GameRulesTests() {
        _paths = new PackagePaths(_rootDir);
        Directory.CreateDirectory(Path.GetDirectoryName(_paths.ModeConfigFile("base"))!);
        Directory.CreateDirectory(_paths.ToolsDir);
        File.WriteAllLines(_paths.ModeConfigFile("base"), ["mp_roundtime 3", "mp_buytime 0.5", "mp_friendlyfire 0"]);
        File.WriteAllLines(_paths.ModeConfigFile("classic"), ["// classic", "mp_roundtime 2.5"]);
    }

    public void Dispose() {
        FileTree.DeleteFolderQuietly(_rootDir);
    }

    private static GameRule Rule(string cvar) {
        return GameRules.All.Single(rule => rule.Cvar == cvar);
    }

    [Theory]
    [InlineData("mp_roundtime", "2,5", "2.5")]
    [InlineData("mp_roundtime", " 4 ", "4")]
    [InlineData("mp_buytime", "-1", "-1")]
    [InlineData("mp_startmoney", "16000", "16000")]
    [InlineData("mp_timelimit", "", "")]
    public void AcceptsValidValues(string cvar, string text, string expected) {
        Assert.True(GameRules.TryNormalize(Rule(cvar), text, out string value, out string? error));
        Assert.Equal(expected, value);
        Assert.Null(error);
    }

    [Theory]
    [InlineData("mp_roundtime", "12")]
    [InlineData("mp_startmoney", "99999")]
    [InlineData("mp_freezetime", "2.5")]
    [InlineData("mp_c4timer", "soon")]
    public void RejectsInvalidValues(string cvar, string text) {
        Assert.False(GameRules.TryNormalize(Rule(cvar), text, out _, out string? error));
        Assert.NotNull(error);
    }

    [Fact]
    public void ModeDefaultsComeFromTheModeThenBase() {
        IReadOnlyDictionary<string, string> defaults = GameRules.ReadModeDefaults(_paths, "classic");

        Assert.Equal("2.5", defaults["mp_roundtime"]);
        Assert.Equal("0.5", defaults["mp_buytime"]);
        Assert.False(defaults.ContainsKey("mp_c4timer"));
    }

    [Fact]
    public void ApplyWritesOnlyTheChangedModeAndClearingRemovesTheLine() {
        var settings = new AppSettings { RconPassword = "test-only", FilePath = Path.Combine(_rootDir, "settings.json") };
        using var connection = new ServerConnection(_paths, settings, Dispatcher.CurrentDispatcher);
        var rules = new GameRulesViewModel(connection);
        Assert.Equal("Mode default: 2.5", rules.Rows.Single(row => row.Rule.Cvar == "mp_roundtime").Placeholder);

        rules.Rows.Single(row => row.Rule.Cvar == "mp_buytime").Text = "1,5";
        rules.Rows.Single(row => row.Rule.Cvar == "mp_friendlyfire").SelectedChoice = rules.Rows.Single(row => row.Rule.Cvar == "mp_friendlyfire").Choices[1];
        Assert.True(rules.IsChanged);

        Assert.Null(rules.ApplyAsync().GetAwaiter().GetResult());
        Assert.False(rules.IsChanged);
        var written = new LocalSettings(_paths.LocalModeConfigFile("classic"));
        Assert.Equal("1.5", written.Get("mp_buytime"));
        Assert.Equal("1", written.Get("mp_friendlyfire"));
        Assert.False(File.Exists(_paths.LocalModeConfigFile("gungame")));

        rules.Rows.Single(row => row.Rule.Cvar == "mp_buytime").Text = "";
        Assert.Null(rules.ApplyAsync().GetAwaiter().GetResult());
        Assert.Null(new LocalSettings(_paths.LocalModeConfigFile("classic")).Get("mp_buytime"));
    }

    [Fact]
    public void AnInvalidValueStopsApplyWithTheModeName() {
        var settings = new AppSettings { RconPassword = "test-only", FilePath = Path.Combine(_rootDir, "settings.json") };
        using var connection = new ServerConnection(_paths, settings, Dispatcher.CurrentDispatcher);
        var rules = new GameRulesViewModel(connection);

        rules.Rows.Single(row => row.Rule.Cvar == "mp_roundtime").Text = "99";

        string? error = rules.ApplyAsync().GetAwaiter().GetResult();
        Assert.StartsWith("Classic Fun: Round time", error);
        Assert.False(File.Exists(_paths.LocalModeConfigFile("classic")));
    }
}
