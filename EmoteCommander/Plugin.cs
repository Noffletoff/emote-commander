using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace EmoteCommander;

/// <summary>
/// Task 1 scaffold: proves the toolchain end to end before any logic exists.
/// Nothing here talks to Penumbra or the game yet.
/// </summary>
public sealed class Plugin : IDalamudPlugin
{
    private readonly IPluginLog _log;

    public Plugin(IDalamudPluginInterface pluginInterface, IPluginLog log)
    {
        _log = log;
        _log.Information("Emote Commander loaded.");
    }

    public void Dispose()
    {
        _log.Information("Emote Commander unloaded.");
    }
}
