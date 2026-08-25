using System.Text.RegularExpressions;

namespace EmoteCommander;

/// <summary>
/// Maps a mod's redirected game paths onto the emote that plays them.
///
/// Deliberately free of any Dalamud or Penumbra dependency so it can be unit
/// tested with the game closed. Everything here is string work.
/// </summary>
public static partial class EmoteResolver
{
    /// <summary>
    /// Body emote animations live at
    /// chara/human/&lt;race&gt;/animation/a0001/bt_common/emote/&lt;name&gt;.pap
    /// The &lt;name&gt; is the same string ActionTimeline uses as its key, minus
    /// the "emote/" prefix - which is what makes the mapping to a real emote
    /// possible without shipping a lookup table.
    /// </summary>
    [GeneratedRegex(@"animation/a\d{4}/bt_common/emote/([a-z0-9_]+)\.pap$",
                    RegexOptions.IgnoreCase)]
    private static partial Regex EmotePath();

    /// <summary>
    /// Pose-family emotes (sit, ground sit, doze, and their per-stance
    /// variants) need /cpose to select an index. Driving pose changes
    /// programmatically is out of scope and carries real risk, so these are
    /// rejected here rather than anywhere further down - a preset can never be
    /// built on one in the first place.
    /// </summary>
    [GeneratedRegex(@"^emote/([a-z]_)?pose\d{2}_(start|loop)$",
                    RegexOptions.IgnoreCase)]
    private static partial Regex PoseFamily();

    /// <summary>
    /// The ActionTimeline key a redirected game path corresponds to, e.g.
    /// "emote/loop_emot24_loop", or null if this path is not a body emote
    /// animation. Face libraries, models and textures all return null.
    /// </summary>
    public static string? TimelineKeyFromPath(string? gamePath)
    {
        if (string.IsNullOrWhiteSpace(gamePath))
            return null;

        // Penumbra reports paths with either separator and inconsistent case.
        var normalised = gamePath.Replace('\\', '/').ToLowerInvariant();

        var match = EmotePath().Match(normalised);
        return match.Success ? $"emote/{match.Groups[1].Value}" : null;
    }

    /// <summary>
    /// Canonical form of a game path for comparison.
    ///
    /// Mod json is written by several different tools, so separators and case
    /// both vary in the wild. Comparing raw strings makes real conflicts
    /// invisible - which is how a mod could visibly override an emote while the
    /// plugin reported that nothing claimed it.
    /// </summary>
    public static string NormalisePath(string? path)
        => (path ?? string.Empty).Replace('\\', '/').ToLowerInvariant();

    /// <summary>
    /// Whether this timeline key belongs to the pose family. Callers must
    /// refuse these.
    /// </summary>
    public static bool IsPoseFamily(string? timelineKey)
        => !string.IsNullOrWhiteSpace(timelineKey) && PoseFamily().IsMatch(timelineKey);
}
