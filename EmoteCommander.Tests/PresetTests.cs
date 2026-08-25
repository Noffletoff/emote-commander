using EmoteCommander;
using Xunit;

namespace EmoteCommander.Tests;

public class PresetTests
{
    [Theory]
    [InlineData("/ohgod", "ohgod")]
    [InlineData("ohgod", "ohgod")]
    [InlineData("  OhGod  ", "ohgod")]
    [InlineData("/OhGod", "ohgod")]
    [InlineData("//ohgod", "ohgod")]
    public void NormaliseStripsSlashesWhitespaceAndCase(string input, string expected)
        => Assert.Equal(expected, Preset.Normalise(input));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("/")]
    [InlineData(" / ")]
    [InlineData(null)]
    public void NormaliseRejectsEmptyCommands(string? input)
        => Assert.Throws<ArgumentException>(() => Preset.Normalise(input!));

    [Theory]
    [InlineData("has space")]
    [InlineData("has\ttab")]
    public void NormaliseRejectsWhitespaceInsideCommands(string input)
        => Assert.Throws<ArgumentException>(() => Preset.Normalise(input));

    [Fact]
    public void FindLocatesPresetCaseInsensitively()
    {
        var presets = new List<Preset>
        {
            new() { Command = "ohgod" },
            new() { Command = "moan" },
        };

        Assert.NotNull(Preset.Find(presets, "OhGod"));
        Assert.Equal("ohgod", Preset.Find(presets, "OhGod")!.Command);
        Assert.NotNull(Preset.Find(presets, "/ohgod"));
        Assert.Null(Preset.Find(presets, "nope"));
    }

    [Fact]
    public void FindToleratesRubbishInput()
    {
        var presets = new List<Preset> { new() { Command = "ohgod" } };
        Assert.Null(Preset.Find(presets, ""));
        Assert.Null(Preset.Find(presets, null));
        Assert.Null(Preset.Find(new List<Preset>(), "ohgod"));
    }

    [Fact]
    public void SlashCommandRendersWithLeadingSlash()
        => Assert.Equal("/ohgod", new Preset { Command = "ohgod" }.SlashCommand);

    [Fact]
    public void NewPresetHasEmptySettingsNotNull()
    {
        var p = new Preset();
        Assert.NotNull(p.Settings);
        Assert.Empty(p.Settings);
    }
}
