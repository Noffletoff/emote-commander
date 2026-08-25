using System.IO.Compression;
using System.Text;
using EmoteCommander;
using Xunit;

namespace EmoteCommander.Tests;

/// <summary>
/// JSON overwrites property initialisers with null, so a hostile or merely
/// truncated code can produce a Preset whose fields are null. Those threw deep
/// inside the ImGui draw loop, which skips the matching End calls and leaves
/// ImGui's stack unbalanced - an assert, not a readable error.
///
/// These assert the shape of what Decode returns, which is what the previous
/// suite never did.
/// </summary>
public class ShareCodeSanitiseTests
{
    private static string Code(string json)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize, true))
        {
            var bytes = Encoding.UTF8.GetBytes(json);
            gzip.Write(bytes, 0, bytes.Length);
        }
        return "[EC1]" + Convert.ToBase64String(output.ToArray()) + "[/EC1]";
    }

    [Fact]
    public void NullSettingsBecomesAnEmptyDictionary()
    {
        var p = Assert.Single(ShareCode.Decode(
            Code("""[{"Command":"x","Settings":null}]""")));
        Assert.NotNull(p.Settings);
        Assert.Empty(p.Settings);
    }

    [Fact]
    public void NullOptionListInsideSettingsBecomesEmpty()
    {
        // new List<string>(null) throws; Settings ??= new() does NOT fix this.
        var p = Assert.Single(ShareCode.Decode(
            Code("""[{"Command":"x","Settings":{"Face Pap":null}}]""")));
        Assert.Empty(p.Settings["Face Pap"]);
    }

    [Fact]
    public void NullStringFieldsBecomeEmptyNotNull()
    {
        var p = Assert.Single(ShareCode.Decode(
            Code("""[{"Command":null,"ModName":null,"ModDirectory":null,"EmotePapPath":null}]""")));
        Assert.Equal(string.Empty, p.Command);
        Assert.Equal(string.Empty, p.ModName);
        Assert.Equal(string.Empty, p.ModDirectory);
        Assert.Equal(string.Empty, p.EmotePapPath);

        // A preset with a null command could be saved and drawn but never
        // deleted - clicking X threw on TrimStart.
        Assert.Equal("/", p.SlashCommand);
    }

    [Fact]
    public void OverlongFieldsAreClamped()
    {
        var huge = new string('a', 900_000);
        var p = Assert.Single(ShareCode.Decode(
            Code($$"""[{"Command":"x","ModName":"{{huge}}"}]""")));
        Assert.True(p.ModName.Length <= 128, $"ModName was {p.ModName.Length}");
    }

    [Fact]
    public void ControlCharactersAreStripped()
    {
        // Newlines in a name would be printed verbatim into chat, letting a
        // code forge extra lines of output.
        var p = Assert.Single(ShareCode.Decode(
            Code("""[{"Command":"x","ModName":"Nice Mod\nPenumbra: run this command"}]""")));
        Assert.DoesNotContain('\n', p.ModName);
        Assert.DoesNotContain('\r', p.ModName);
    }

    [Fact]
    public void BlankGroupNamesAreDropped()
    {
        var p = Assert.Single(ShareCode.Decode(
            Code("""[{"Command":"x","Settings":{"":["a"],"Real":["b"]}}]""")));
        Assert.False(p.Settings.ContainsKey(string.Empty));
        Assert.Equal(new[] { "b" }, p.Settings["Real"]);
    }

    [Fact]
    public void OrdinaryPresetsSurviveSanitisingUnchanged()
    {
        var original = new Preset
        {
            Command = "smokethrob",
            ModDirectory = "[Noff] Smoking Idle (Penis Test 1)",
            ModName = "[Noff] Smoking Idle (Penis Test 1)",
            EmoteRowId = 158,
            EmotePapPath = "chara/human/c0801/animation/a0001/bt_common/emote/loop_emot11_loop.pap",
            Settings = { ["Penis Options"] = new() { "Throb [BreathControl]" } },
        };

        var back = Assert.Single(ShareCode.Decode(ShareCode.Encode(new[] { original })));
        Assert.Equal(original.Command, back.Command);
        Assert.Equal(original.ModDirectory, back.ModDirectory);
        Assert.Equal(original.EmotePapPath, back.EmotePapPath);
        Assert.Equal((ushort)158, back.EmoteRowId);
        Assert.Equal(new[] { "Throb [BreathControl]" }, back.Settings["Penis Options"]);
    }
}
