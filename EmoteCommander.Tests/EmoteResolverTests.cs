using EmoteCommander;
using Xunit;

namespace EmoteCommander.Tests;

public class EmoteResolverTests
{
    [Fact]
    public void ExtractsTimelineKeyFromEmotePath()
    {
        var p = "chara/human/c0801/animation/a0001/bt_common/emote/loop_emot24_loop.pap";
        Assert.Equal("emote/loop_emot24_loop", EmoteResolver.TimelineKeyFromPath(p));
    }

    [Fact]
    public void IgnoresNonEmotePaths()
    {
        // A face animation library - real, and must never be mistaken for the
        // body emote that calls it.
        Assert.Null(EmoteResolver.TimelineKeyFromPath(
            "chara/human/c0801/animation/f0002/nonresident/nf_ohgod.pap"));
        Assert.Null(EmoteResolver.TimelineKeyFromPath(
            "chara/equipment/e0001/model/c0101e0001_top.mdl"));
        Assert.Null(EmoteResolver.TimelineKeyFromPath(""));
        Assert.Null(EmoteResolver.TimelineKeyFromPath("   "));
    }

    [Fact]
    public void IsCaseInsensitiveAndSlashAgnostic()
    {
        // Penumbra reports paths with either separator and inconsistent case.
        var p = @"CHARA\HUMAN\C0801\ANIMATION\A0001\BT_COMMON\EMOTE\POSE01_LOOP.PAP";
        Assert.Equal("emote/pose01_loop", EmoteResolver.TimelineKeyFromPath(p));
    }

    [Fact]
    public void MatchesAnyRaceAndAnimationSet()
    {
        Assert.Equal("emote/loop_emot24_loop", EmoteResolver.TimelineKeyFromPath(
            "chara/human/c0101/animation/a0001/bt_common/emote/loop_emot24_loop.pap"));
        Assert.Equal("emote/loop_emot24_loop", EmoteResolver.TimelineKeyFromPath(
            "chara/human/c1801/animation/a0001/bt_common/emote/loop_emot24_loop.pap"));
    }

    [Theory]
    // Pose-family emotes need /cpose to reach a specific index. Excluded by
    // design - driving pose changes programmatically is out of scope.
    [InlineData("emote/s_pose03_loop", true)]
    [InlineData("emote/s_pose01_start", true)]
    [InlineData("emote/pose01_loop", true)]
    [InlineData("emote/pose01_start", true)]
    [InlineData("emote/j_pose01_loop", true)]
    [InlineData("emote/b_pose01_loop", true)]
    [InlineData("emote/l_pose01_start", true)]
    // Ordinary emotes are fine.
    [InlineData("emote/loop_emot24_loop", false)]
    [InlineData("emote/loop_emot01_loop", false)]
    [InlineData("", false)]
    public void FlagsPoseFamilyEmotes(string key, bool expected)
        => Assert.Equal(expected, EmoteResolver.IsPoseFamily(key));

    [Fact]
    public void PoseFamilyDetectionSurvivesTheResolver()
    {
        // The two functions have to agree: a sit-pose pap must resolve to a key
        // that IsPoseFamily then rejects, or a preset could be built on one.
        var key = EmoteResolver.TimelineKeyFromPath(
            "chara/human/c0801/animation/a0001/bt_common/emote/s_pose03_loop.pap");
        Assert.NotNull(key);
        Assert.True(EmoteResolver.IsPoseFamily(key!));
    }
}
