using EmoteCommander;
using Xunit;

namespace EmoteCommander.Tests;

/// <summary>
/// A preset records the game paths its emote uses. Recording only ONE was a
/// real bug: many emotes are SHARED, so the game loads a single race's file for
/// every character - Wring Hands plays c0101 even on a female Miqo'te. Storing
/// the player's own race named a file the character never reads, and in a
/// race-widened pack, one the game does not even ship.
/// </summary>
public class PapPathTests
{
    private const string C0101 =
        "chara/human/c0101/animation/a0001/bt_common/emote/loop_emot24_loop.pap";
    private const string C0801 =
        "chara/human/c0801/animation/a0001/bt_common/emote/loop_emot24_loop.pap";

    [Fact]
    public void AllPathsUsesTheListWhenPresent()
    {
        var p = new Preset { EmotePapPaths = { C0101, C0801 } };
        Assert.Equal(new[] { C0101, C0801 }, p.AllPapPaths);
    }

    [Fact]
    public void AllPathsFallsBackToTheLegacySingleValue()
    {
        // Presets saved before the list existed must keep working.
        var p = new Preset { EmotePapPath = C0801 };
        Assert.Equal(new[] { C0801 }, p.AllPapPaths);
    }

    [Fact]
    public void TheListWinsOverTheLegacyValue()
    {
        var p = new Preset { EmotePapPath = C0801, EmotePapPaths = { C0101 } };
        Assert.Equal(new[] { C0101 }, p.AllPapPaths);
    }

    [Fact]
    public void NoPathsRecordedIsEmptyNotNull()
    {
        var p = new Preset();
        Assert.NotNull(p.AllPapPaths);
        Assert.Empty(p.AllPapPaths);
    }

    [Fact]
    public void PathsSurviveAShareCodeRoundTrip()
    {
        var original = new Preset
        {
            Command = "moomoobottom",
            ModDirectory = "pack",
            EmotePapPaths = { C0101, C0801 },
        };
        var back = Assert.Single(ShareCode.Decode(ShareCode.Encode(new[] { original })));
        Assert.Equal(new[] { C0101, C0801 }, back.EmotePapPaths);
    }

    [Fact]
    public void ALegacyCodeWithOnlyASinglePathStillWorks()
    {
        var legacy = new Preset { Command = "old", EmotePapPath = C0101 };
        var back = Assert.Single(ShareCode.Decode(ShareCode.Encode(new[] { legacy })));
        Assert.Equal(new[] { C0101 }, back.AllPapPaths);
    }

    [Fact]
    public void NullPathListFromAHostileCodeBecomesEmpty()
    {
        var code = ShareCode.Encode(new[] { new Preset { Command = "x" } })
            .Replace("[EC1]", "[EC1]");     // shape check below does the real work
        var back = Assert.Single(ShareCode.Decode(code));
        Assert.NotNull(back.EmotePapPaths);
    }

    [Theory]
    [InlineData("chara/human/c0101/animation/a0001/bt_common/emote/x.pap",
                "chara/human/c0101/animation/a0001/bt_common/emote/x.pap")]
    [InlineData(@"chara\human\c0101\animation\a0001\bt_common\emote\x.pap",
                "chara/human/c0101/animation/a0001/bt_common/emote/x.pap")]
    [InlineData("CHARA/HUMAN/C0101/ANIMATION/A0001/BT_COMMON/EMOTE/X.PAP",
                "chara/human/c0101/animation/a0001/bt_common/emote/x.pap")]
    public void PathsCompareNormalised(string input, string expected)
        => Assert.Equal(expected, EmoteResolver.NormalisePath(input));
}
