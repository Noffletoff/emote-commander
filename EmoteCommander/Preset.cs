// Explicit usings on purpose: this file is LINKED into the test project, which
// does not enable ImplicitUsings. Relying on the plugin project's implicit set
// would compile here and fail there.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace EmoteCommander;

/// <summary>
/// One bound command: which mod, which options to select in it, and which
/// emote to perform once they are applied.
///
/// Deliberately free of any Dalamud or Penumbra dependency - it is the unit the
/// share code serialises and the unit the tests exercise, so it must build and
/// run with the game closed.
/// </summary>
public sealed class Preset
{
    /// <summary>The command name, stored WITHOUT a leading slash.</summary>
    public string Command { get; set; } = "";

    /// <summary>Penumbra's directory name for the mod (its stable identifier).</summary>
    public string ModDirectory { get; set; } = "";

    /// <summary>Display name at the time the preset was made, for the UI only.</summary>
    public string ModName { get; set; } = "";

    /// <summary>Option group name to the option names selected within it.</summary>
    public Dictionary<string, List<string>> Settings { get; set; } = new();

    /// <summary>ActionTimeline-backed emote row to perform.</summary>
    public ushort EmoteRowId { get; set; }

    /// <summary>The emote pap path this preset expects to win, for conflict checks.</summary>
    public string EmotePapPath { get; set; } = "";

    /// <summary>True when the user overrode the auto-detected emote by hand.</summary>
    public bool EmoteOverridden { get; set; }

    /// <summary>The command as typed, with its leading slash.</summary>
    /// <remarks>Derived, so kept out of share codes and the config file.</remarks>
    [JsonIgnore]
    public string SlashCommand => "/" + Command;

    /// <summary>
    /// Canonical form of a command name: no leading slash, trimmed, lowercase.
    /// Throws when the result would be empty or contains whitespace, since
    /// neither can be registered as a chat command.
    /// </summary>
    public static string Normalise(string? command)
    {
        var s = (command ?? string.Empty).Trim().TrimStart('/').Trim();

        if (s.Length == 0)
            throw new ArgumentException("Command name is empty.", nameof(command));

        if (s.Any(char.IsWhiteSpace))
            throw new ArgumentException(
                $"Command names cannot contain whitespace: '{s}'.", nameof(command));

        return s.ToLowerInvariant();
    }

    /// <summary>
    /// The preset bound to this command, or null. Accepts the command with or
    /// without its slash and in any case. Never throws - lookup happens on the
    /// hot path when a command fires.
    /// </summary>
    public static Preset? Find(IEnumerable<Preset>? presets, string? command)
    {
        if (presets is null || string.IsNullOrWhiteSpace(command))
            return null;

        var wanted = command.Trim().TrimStart('/').Trim();
        if (wanted.Length == 0)
            return null;

        return presets.FirstOrDefault(p =>
            string.Equals(p.Command, wanted, StringComparison.OrdinalIgnoreCase));
    }
}
