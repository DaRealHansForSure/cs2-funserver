using System.IO;
using Cs2ServerControl.Install;
using Cs2ServerControl.Services;
using Xunit;

namespace Cs2ServerControl.Tests;

public class BotSettingsStoreTests : IDisposable {
    private readonly string _rootDir = Path.Combine(Path.GetTempPath(), "cs16-bots-tests-" + Guid.NewGuid().ToString("N"));
    private readonly PackagePaths _paths;

    public BotSettingsStoreTests() {
        _paths = new PackagePaths(_rootDir);
        Directory.CreateDirectory(Path.GetDirectoryName(_paths.ServerBaseConfigFile)!);
        File.WriteAllLines(_paths.ServerBaseConfigFile, [
            "mp_timelimit 25",
            BotSettingsStore.StartMarker,
            "bot_difficulty \"1\"",
            "// a comment inside the block",
            "bot_quota \"8\"",
            "bot_chatter \"normal\"",
            BotSettingsStore.EndMarker,
            "bot_quota \"99\"",
        ]);
    }

    public void Dispose() {
        FileTree.DeleteFolderQuietly(_rootDir);
    }

    [Fact]
    public void TheDefaultsAreTheBlockInBaseCfg() {
        IReadOnlyDictionary<string, string> defaults = BotSettingsStore.ReadDefaults(_paths);

        Assert.Equal(3, defaults.Count);
        Assert.Equal("8", defaults["bot_quota"]);
        Assert.Equal("normal", defaults["bot_chatter"]);
    }

    [Fact]
    public void SavesOnlyWhatDiffersAndNeverTouchesBaseCfg() {
        string baseBefore = File.ReadAllText(_paths.ServerBaseConfigFile);
        IReadOnlyDictionary<string, string> defaults = BotSettingsStore.ReadDefaults(_paths);

        BotSettingsStore.SaveCustom(_paths, [new("bot_difficulty", "1"), new("bot_quota", "12"), new("bot_chatter", "radio")], defaults);

        IReadOnlyDictionary<string, string> custom = BotSettingsStore.ReadCustom(_paths, defaults.Keys);
        Assert.Equal(2, custom.Count);
        Assert.Equal("12", custom["bot_quota"]);
        Assert.Equal("radio", custom["bot_chatter"]);
        Assert.Equal(baseBefore, File.ReadAllText(_paths.ServerBaseConfigFile));
    }

    [Fact]
    public void BackToTheDefaultsRemovesTheFile() {
        IReadOnlyDictionary<string, string> defaults = BotSettingsStore.ReadDefaults(_paths);
        BotSettingsStore.SaveCustom(_paths, [new("bot_quota", "12")], defaults);
        Assert.True(File.Exists(_paths.LocalBotConfigFile));

        BotSettingsStore.SaveCustom(_paths, [new("bot_difficulty", "1"), new("bot_quota", "8"), new("bot_chatter", "normal")], defaults);

        Assert.False(File.Exists(_paths.LocalBotConfigFile));
        Assert.Empty(BotSettingsStore.ReadCustom(_paths, defaults.Keys));
    }

    [Fact]
    public void AValueChangedBackDropsOnlyItsLine() {
        IReadOnlyDictionary<string, string> defaults = BotSettingsStore.ReadDefaults(_paths);
        BotSettingsStore.SaveCustom(_paths, [new("bot_quota", "12"), new("bot_chatter", "radio")], defaults);

        BotSettingsStore.SaveCustom(_paths, [new("bot_quota", "8"), new("bot_chatter", "radio")], defaults);

        IReadOnlyDictionary<string, string> custom = BotSettingsStore.ReadCustom(_paths, defaults.Keys);
        Assert.Equal(["bot_chatter"], custom.Keys);
    }
}
