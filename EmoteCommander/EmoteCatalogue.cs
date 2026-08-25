using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace EmoteCommander;

/// <summary>One emote the player can perform.</summary>
/// <param name="RowId">Emote sheet row, what the game is asked to play.</param>
/// <param name="Name">Display name, e.g. "Wring Hands".</param>
/// <param name="TextCommand">Slash command, e.g. "/wringhands".</param>
/// <param name="TimelineKey">ActionTimeline key, e.g. "emote/loop_emot24_loop".</param>
public sealed record EmoteEntry(
    ushort RowId, string Name, string TextCommand, string TimelineKey);

/// <summary>
/// Every emote, read from the game's own Excel sheets at runtime.
///
/// Built from the sheets rather than a shipped table so it cannot drift from
/// the installed game version. The link that makes the whole plugin work is
/// that an Emote's ActionTimeline Key is the SAME string a mod's redirected pap
/// path yields - so a mod can be mapped to the emote that plays it without any
/// hand-maintained mapping.
/// </summary>
public sealed class EmoteCatalogue
{
    private readonly List<EmoteEntry> _all = new();
    private readonly Dictionary<string, EmoteEntry> _byTimelineKey =
        new(StringComparer.OrdinalIgnoreCase);

    public EmoteCatalogue(IDataManager data, IPluginLog log)
    {
        var sheet = data.GetExcelSheet<Emote>();
        if (sheet is null)
        {
            log.Error("Emote sheet unavailable - the catalogue will be empty.");
            return;
        }

        var skippedPose = 0;

        foreach (var emote in sheet)
        {
            var command = emote.TextCommand.ValueNullable?.Command.ExtractText();
            if (string.IsNullOrWhiteSpace(command))
                continue;

            // ActionTimeline[0] is the emote's own timeline. Some rows have
            // none (placeholders, unused ids) - those are not performable.
            var key = emote.ActionTimeline
                           .Select(t => t.ValueNullable?.Key.ExtractText())
                           .FirstOrDefault(k => !string.IsNullOrWhiteSpace(k));
            if (string.IsNullOrWhiteSpace(key))
                continue;

            // Pose-family emotes need /cpose and are out of scope entirely.
            // Excluded here so they never reach a dropdown in the first place.
            if (EmoteResolver.IsPoseFamily(key))
            {
                skippedPose++;
                continue;
            }

            var name = emote.Name.ExtractText();
            if (string.IsNullOrWhiteSpace(name))
                name = command;

            var entry = new EmoteEntry((ushort)emote.RowId, name, command, key);
            _all.Add(entry);
            _byTimelineKey.TryAdd(key, entry);
        }

        log.Information(
            $"Emote catalogue: {_all.Count} emotes, {_byTimelineKey.Count} timeline keys, " +
            $"{skippedPose} pose-family excluded.");
    }

    public IReadOnlyList<EmoteEntry> All => _all;

    /// <summary>The emote that plays this timeline, or null.</summary>
    public EmoteEntry? ByTimelineKey(string? key)
        => string.IsNullOrWhiteSpace(key) ? null : _byTimelineKey.GetValueOrDefault(key);

    public EmoteEntry? ByRowId(ushort rowId)
        => _all.FirstOrDefault(e => e.RowId == rowId);

    /// <summary>
    /// The emote a mod plays, worked out from the game paths it redirects.
    /// Returns null when the mod replaces no body emote animation.
    /// </summary>
    public EmoteEntry? FromRedirectedPaths(IEnumerable<string> gamePaths)
        => gamePaths.Select(EmoteResolver.TimelineKeyFromPath)
                    .Where(k => k is not null)
                    .Select(ByTimelineKey)
                    .FirstOrDefault(e => e is not null);
}
