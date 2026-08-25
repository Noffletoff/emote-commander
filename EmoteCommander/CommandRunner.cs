using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Dalamud.Game.Command;
using Dalamud.Plugin.Services;

namespace EmoteCommander;

/// <summary>
/// Owns the user's commands and the sequence they run:
/// apply preset -> redraw -> wait for it to finish -> perform the emote.
///
/// The wait is not optional. The game caches a loaded .pap by game path, so
/// firing early replays the previous animation and looks exactly like a broken
/// plugin.
/// </summary>
public sealed class CommandRunner : IDisposable
{
    private readonly Config _config;
    private readonly PenumbraBridge _penumbra;
    private readonly EmoteCatalogue _emotes;
    private readonly EmotePlayer _player;
    private readonly ICommandManager _commands;
    private readonly IChatGui _chat;
    private readonly IPluginLog _log;

    private readonly HashSet<string> _registered = new(StringComparer.OrdinalIgnoreCase);

    public CommandRunner(Config config, PenumbraBridge penumbra, EmoteCatalogue emotes,
                         EmotePlayer player, ICommandManager commands, IChatGui chat,
                         IPluginLog log)
    {
        _config = config;
        _penumbra = penumbra;
        _emotes = emotes;
        _player = player;
        _commands = commands;
        _chat = chat;
        _log = log;
    }

    public void RegisterAll()
    {
        foreach (var preset in _config.Presets)
            Register(preset);
    }

    /// <summary>
    /// Register one preset's command. Refuses names already taken, which is how
    /// a vanilla emote command can never be captured - the game owns those, so
    /// AddHandler returns false.
    /// </summary>
    public bool Register(Preset preset)
    {
        string name;
        try
        {
            name = "/" + Preset.Normalise(preset.Command);
        }
        catch (ArgumentException ex)
        {
            _log.Warning($"not registering preset: {ex.Message}");
            return false;
        }

        if (_registered.Contains(name))
            return true;

        var ok = _commands.AddHandler(name, new CommandInfo((_, _) => Fire(preset))
        {
            HelpMessage = $"Emote Commander: {preset.ModName}",
            // Hidden from /xlhelp and the installed-plugins page on purpose.
            // These are the user's own commands, listed in the plugin's own
            // window; with a dozen presets they would swamp both.
            ShowInHelp = false,
        });

        if (!ok)
        {
            _log.Warning($"'{name}' is already taken by the game or another plugin.");
            return false;
        }

        _registered.Add(name);
        return true;
    }

    public void Unregister(string command)
    {
        var name = "/" + command.TrimStart('/');
        if (_registered.Remove(name))
            _commands.RemoveHandler(name);
    }

    /// <summary>Whether this command name could be registered right now.</summary>
    public bool IsNameAvailable(string command)
    {
        try
        {
            var name = "/" + Preset.Normalise(command);
            return !_registered.Contains(name);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Outcome of an import, for the UI or chat to report.</summary>
    public sealed record ImportResult(int Added, int Total, List<string> Messages)
    {
        public bool Ok => Added > 0;
    }

    /// <summary>
    /// Add presets from a share code. A preset already bound to the same
    /// command is replaced, so re-importing a corrected code does the sensible
    /// thing rather than silently doing nothing.
    ///
    /// Throws FormatException for a malformed code - the one exception callers
    /// need to handle.
    /// </summary>
    public ImportResult ImportCode(string code)
    {
        // Be generous about what is pasted. People paste the whole chat line,
        // a Discord message, or a mod description with prose around the code -
        // all of which contain a perfectly good code. Only fall back to strict
        // decoding when no marker is found at all, so the error message for
        // genuine rubbish stays useful.
        var found = ShareCode.ExtractFromText(code);
        var incoming = found.Count > 0
            ? found.SelectMany(ShareCode.Decode).ToList()
            : (IReadOnlyList<Preset>)ShareCode.Decode(code);
        var messages = new List<string>();
        var added = 0;

        foreach (var preset in incoming)
        {
            if (string.IsNullOrWhiteSpace(preset.ModDirectory))
            {
                messages.Add($"{preset.SlashCommand}: no mod recorded, skipped.");
                continue;
            }

            var known = _penumbra.ModList().ContainsKey(preset.ModDirectory);
            if (!known)
                messages.Add($"{preset.SlashCommand}: '{preset.ModName}' is not installed - "
                           + "the command is saved but will not work until it is.");

            var existing = _config.ByCommand(preset.Command);
            if (existing is not null)
            {
                Unregister(existing.Command);
                _config.Presets.Remove(existing);
            }

            _config.Presets.Add(preset);
            if (Register(preset))
            {
                added++;
                messages.Add($"{preset.SlashCommand} -> {preset.ModName}");
            }
            else
            {
                messages.Add($"{preset.SlashCommand}: name is already taken, not registered.");
            }
        }

        _config.Save();
        return new ImportResult(added, incoming.Count, messages);
    }

    public void Fire(Preset preset) => _ = FireAsync(preset);

    public async Task FireAsync(Preset preset)
    {
        try
        {
            if (!_penumbra.Available)
            {
                _chat.PrintError("[EC] Penumbra is not available.");
                return;
            }

            var emote = _emotes.ByRowId(preset.EmoteRowId);
            if (emote is null)
            {
                _chat.PrintError($"[EC] {preset.SlashCommand}: no emote is set for this preset.");
                return;
            }

            if (_config.AlwaysWinConflicts)
                RaiseAboveConflicts(preset);

            var failed = _penumbra.ApplySettings(preset.ModDirectory, preset.Settings);
            if (failed.Count > 0)
                _chat.PrintError(
                    $"[EC] {preset.SlashCommand}: could not set {string.Join(", ", failed)} " +
                    $"- the mod's options may have changed since this preset was made.");

            if (!await _penumbra.RedrawAndAwaitAsync(_config.RedrawTimeoutMs).ConfigureAwait(false))
                _chat.PrintError($"[EC] {preset.SlashCommand}: redraw did not finish in time; " +
                                 $"the animation may be the previous one.");

            if (!_player.Perform(emote, out var problem))
            {
                _chat.PrintError($"[EC] {problem}");
                return;
            }

            if (_config.VerifyAfterFire)
                VerifyWinner(preset);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "firing preset failed");
            _chat.PrintError($"[EC] {preset.SlashCommand} failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Put this preset's mod one above the highest-priority mod that also
    /// claims its emote.
    ///
    /// One above, rather than a large fixed number, so the user's own ordering
    /// among everything else is left intact - the aim is to win this specific
    /// conflict, not to shove the mod to the top of the pile forever.
    /// Does nothing when it already wins.
    /// </summary>
    private void RaiseAboveConflicts(Preset preset)
    {
        if (string.IsNullOrWhiteSpace(preset.EmotePapPath))
            return;

        var conflicts = _penumbra.Conflicts(preset.EmotePapPath, preset.ModDirectory);
        if (conflicts.Count == 0)
            return;

        var highest = conflicts.Max(c => c.Priority);
        var mine = _penumbra.ModPriority(preset.ModDirectory) ?? 0;
        if (mine > highest)
            return;

        if (_penumbra.SetPriority(preset.ModDirectory, highest + 1))
        {
            // Say so in chat, not just the log. This changes the user's
            // Penumbra setup, so it should never happen invisibly.
            var names = string.Join(", ", conflicts.Select(c => $"{c.Name} ({c.Priority})"));
            _chat.Print($"[EC] Raised '{preset.ModName}' priority {mine} -> {highest + 1} "
                      + $"to beat: {names}");
        }
    }

    /// <summary>
    /// Confirm the emote path really resolved to this preset's mod. Without
    /// this, a higher-priority mod silently wins and the user sees the wrong
    /// animation with no explanation.
    /// </summary>
    private void VerifyWinner(Preset preset)
    {
        if (string.IsNullOrWhiteSpace(preset.EmotePapPath))
            return;

        var resolved = _penumbra.PlayerResourcePaths();
        if (!resolved.TryGetValue(preset.EmotePapPath, out var files) || files.Count == 0)
            return;   // not loaded yet; nothing useful to say

        var mine = files.Any(f =>
            f.Contains(preset.ModDirectory, StringComparison.OrdinalIgnoreCase));

        if (!mine)
        {
            var winner = Path.GetFileName(files.First());
            _chat.PrintError(
                $"[EC] {preset.SlashCommand}: another mod is overriding this emote " +
                $"(it resolved to {winner}). Tick 'raise priority' on this preset, " +
                $"or disable the conflicting mod.");
        }
    }

    public void Dispose()
    {
        foreach (var name in _registered)
            _commands.RemoveHandler(name);
        _registered.Clear();
    }
}
