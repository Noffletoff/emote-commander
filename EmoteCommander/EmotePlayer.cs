using System;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;

namespace EmoteCommander;

/// <summary>
/// Performs an emote through the game's own execution path - the same route
/// taken when you click an emote in the UI.
///
/// This must be a REAL emote, not a local animation write. Writing
/// Timeline.BaseOverride would render on this client only and be invisible to
/// everyone else, which defeats the entire point: the server broadcasts the
/// emote, and other players' Penumbra dresses it in the synced animation.
/// </summary>
public sealed class EmotePlayer
{
    private readonly IPluginLog _log;

    public EmotePlayer(IPluginLog log) => _log = log;

    /// <summary>
    /// Perform an emote. Returns false and explains why if it was refused.
    /// </summary>
    public unsafe bool Perform(EmoteEntry emote, out string? problem)
    {
        problem = null;

        // Belt and braces: pose-family emotes are already absent from the
        // catalogue, but this is the last point before the game is touched and
        // the rule is a safety one, so it is enforced here too.
        if (EmoteResolver.IsPoseFamily(emote.TimelineKey))
        {
            problem = $"{emote.TextCommand} is a pose emote and cannot be driven by a command.";
            _log.Warning(problem);
            return false;
        }

        var agent = AgentEmote.Instance();
        if (agent is null)
        {
            problem = "The game's emote agent is unavailable right now.";
            return false;
        }

        try
        {
            agent->ExecuteEmote(emote.RowId);
            return true;
        }
        catch (Exception ex)
        {
            problem = $"Could not perform {emote.TextCommand}: {ex.Message}";
            _log.Error(problem);
            return false;
        }
    }
}
