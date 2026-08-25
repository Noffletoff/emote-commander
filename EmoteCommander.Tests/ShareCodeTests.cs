using EmoteCommander;
using Xunit;

namespace EmoteCommander.Tests;

public class ShareCodeTests
{
    private static Preset Sample() => new()
    {
        Command = "ohgod",
        ModDirectory = "noff_smoking_idle",
        ModName = "Noff Smoking Idle",
        EmoteRowId = 42,
        EmotePapPath = "chara/human/c0801/animation/a0001/bt_common/emote/loop_emot24_loop.pap",
        Settings =
        {
            ["Face Pap"] = new() { "FMiqo Face Pap" },
            ["Bulge"] = new() { "On" },
        },
    };

    [Fact]
    public void RoundTripsAPreset()
    {
        var back = ShareCode.Decode(ShareCode.Encode(new[] { Sample() }));
        var p = Assert.Single(back);
        Assert.Equal("ohgod", p.Command);
        Assert.Equal("noff_smoking_idle", p.ModDirectory);
        Assert.Equal("Noff Smoking Idle", p.ModName);
        Assert.Equal((ushort)42, p.EmoteRowId);
        Assert.Equal(new[] { "FMiqo Face Pap" }, p.Settings["Face Pap"]);
        Assert.Equal(new[] { "On" }, p.Settings["Bulge"]);
    }

    [Fact]
    public void RoundTripsSeveralPresets()
    {
        var two = new[] { Sample(), new Preset { Command = "moan", ModDirectory = "x" } };
        var back = ShareCode.Decode(ShareCode.Encode(two));
        Assert.Equal(2, back.Count);
        Assert.Equal("moan", back[1].Command);
    }

    [Fact]
    public void EncodedCodeIsWrappedInVersionedMarkers()
    {
        var code = ShareCode.Encode(new[] { Sample() });
        Assert.StartsWith("[EC1]", code);
        Assert.EndsWith("[/EC1]", code);
    }

    [Fact]
    public void EncodedCodeIsASingleLineSoItSurvivesPasting()
    {
        var code = ShareCode.Encode(new[] { Sample() });
        Assert.DoesNotContain('\n', code);
        Assert.DoesNotContain('\r', code);
    }

    [Fact]
    public void ExtractsCodesEmbeddedInProse()
    {
        var text = "Cool mod.\n\nCommands:\n"
                 + ShareCode.Encode(new[] { Sample() })
                 + "\n\nDisable to revert.";
        var found = ShareCode.ExtractFromText(text);
        Assert.Single(found);
        Assert.Single(ShareCode.Decode(found[0]));
    }

    [Fact]
    public void ExtractsSeveralCodesFromOneDescription()
    {
        var text = ShareCode.Encode(new[] { Sample() })
                 + " and also "
                 + ShareCode.Encode(new[] { new Preset { Command = "moan" } });
        Assert.Equal(2, ShareCode.ExtractFromText(text).Count);
    }

    [Fact]
    public void ExtractsNothingFromTextWithoutCodes()
    {
        Assert.Empty(ShareCode.ExtractFromText("just a normal description"));
        Assert.Empty(ShareCode.ExtractFromText(""));
        Assert.Empty(ShareCode.ExtractFromText(null!));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("nonsense")]
    [InlineData("[EC1]not-base64![/EC1]")]
    [InlineData("[EC1][/EC1]")]
    [InlineData("[EC9]cGF5bG9hZA==[/EC9]")]      // version we do not know
    [InlineData("[EC1]cGF5bG9hZA==[/EC2]")]      // mismatched markers
    public void DecodeThrowsFormatExceptionOnRubbish(string code)
        => Assert.Throws<FormatException>(() => ShareCode.Decode(code));

    [Fact]
    public void DecodeToleratesSurroundingWhitespace()
    {
        var code = "\n  " + ShareCode.Encode(new[] { Sample() }) + "  \n";
        Assert.Single(ShareCode.Decode(code));
    }
}
