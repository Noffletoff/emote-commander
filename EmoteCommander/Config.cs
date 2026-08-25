using System;
using System.Collections.Generic;
using Dalamud.Configuration;
using Dalamud.Plugin;

namespace EmoteCommander;

public sealed class Config : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    public List<Preset> Presets { get; set; } = new();

    /// <summary>
    /// After firing, check the emote path actually resolved to this preset's
    /// mod and complain if another mod won. Cheap, and turns "why is the wrong
    /// animation playing" into a one-line answer.
    /// </summary>
    public bool VerifyAfterFire { get; set; } = true;

    /// <summary>
    /// When a command's emote is claimed by another mod too, raise this mod's
    /// priority to one above the highest conflicting mod before firing.
    ///
    /// Global rather than per-command: it describes how you want your mod setup
    /// to behave, not a property of one command.
    /// </summary>
    public bool AlwaysWinConflicts { get; set; }

    /// <summary>How long to wait for a redraw before firing anyway.</summary>
    public int RedrawTimeoutMs { get; set; } = 5000;

    [NonSerialized] private IDalamudPluginInterface? _pi;

    public void Initialise(IDalamudPluginInterface pi) => _pi = pi;

    public void Save() => _pi?.SavePluginConfig(this);

    public Preset? ByCommand(string? command) => Preset.Find(Presets, command);
}
