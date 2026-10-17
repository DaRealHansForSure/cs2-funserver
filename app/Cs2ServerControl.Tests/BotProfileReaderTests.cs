using Cs2ServerControl.Services;
using Xunit;

namespace Cs2ServerControl.Tests;

public class BotProfileReaderTests {
    // Shaped like ReGameDLL's BotProfile.db, including comments and tabs.
    private static readonly string[] Profile = [
        "// BotProfile.db",
        "Default",
        "\tSkill = 50",
        "\tAggression = 50",
        "End",
        "",
        "Template Rifle",
        "\tWeaponPreference = m4a1",
        "End",
        "Template Elite",
        "\tSkill = 100 // the best",
        "End",
        "Template Easy",
        "\tSkill = 5",
        "End",
        "",
        "Elite+Rifle Cliffe",
        "\tVoicePitch = 90",
        "End",
        "Easy Ulf",
        "\tSkill = 12",
        "End",
        "Rifle Gunner",
        "End",
        "Unknown Brutus",
        "End",
    ];

    [Fact]
    public void TemplateSkillIsInherited() {
        IReadOnlyDictionary<string, int> skills = BotProfileReader.ParseSkills(Profile);

        Assert.Equal(100, skills["Cliffe"]);
    }

    [Fact]
    public void OwnValueWinsOverTemplate() {
        IReadOnlyDictionary<string, int> skills = BotProfileReader.ParseSkills(Profile);

        Assert.Equal(12, skills["Ulf"]);
    }

    [Fact]
    public void TemplateWithoutSkillFallsBackToDefault() {
        IReadOnlyDictionary<string, int> skills = BotProfileReader.ParseSkills(Profile);

        Assert.Equal(50, skills["Gunner"]);
        Assert.Equal(50, skills["Brutus"]);
    }

    [Fact]
    public void NamesAreCaseInsensitive() {
        IReadOnlyDictionary<string, int> skills = BotProfileReader.ParseSkills(Profile);

        Assert.True(skills.ContainsKey("cliffe"));
        Assert.False(skills.ContainsKey("Elite"));
    }

    [Fact]
    public void ReadsTheRealFileWhenInstalled() {
        string? file = TestPaths.FindServerFile("cstrike", "BotProfile.db");
        if (file == null) {
            return;
        }

        IReadOnlyDictionary<string, int> skills = BotProfileReader.ReadSkills(file);

        Assert.True(skills.Count > 20);
        Assert.All(skills.Values, skill => Assert.InRange(skill, 0, 100));
    }
}
