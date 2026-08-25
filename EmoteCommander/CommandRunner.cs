using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
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
    private readonly IFramework _framework;

    private readonly HashSet<string> _registered = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Only one command may run its apply/redraw/perform sequence at a time.
    ///
    /// Two overlapping fires corrupt each other: they share one redraw
    /// registration, so the second overwrites the first's and both then time
    /// out, both emotes play against a half-finished redraw, and whichever
    /// mod options were written last are what is actually on. Two presets
    /// cannot meaningfully play at once anyway.
    /// </summary>
    private readonly SemaphoreSlim _fireGate = new(1, 1);

    public CommandRunner(Config config, PenumbraBridge penumbra, EmoteCatalogue emotes,
                         EmotePlayer player, ICommandManager commands, IChatGui chat,
                         IPluginLog log, IFramework framework)
    {
        _config = config;
        _penumbra = penumbra;
        _emotes = emotes;
        _player = player;
        _commands = commands;
        _chat = chat;
        _log = log;
        _framework = framework;
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

    public void Unregister(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return;
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
        // Refuse rather than queue: queueing would leave the user watching
        // commands play out seconds after they typed them.
        if (!await _fireGate.WaitAsync(0).ConfigureAwait(false))
        {
            _chat.PrintError($"[EC] {preset.SlashCommand}: another command is still running.");
            return;
        }

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

            // Setting options on a mod that is missing or switched off succeeds
            // and changes nothing, so without this the command silently plays a
            // vanilla animation with no explanation anywhere.
            var state = _penumbra.State(preset.ModDirectory);
            if (state is null)
            {
                _chat.PrintError($"[EC] {preset.SlashCommand}: '{preset.ModName}' is not "
                               + "installed in Penumbra.");
                return;
            }
            if (!state.Enabled)
            {
                _chat.PrintError($"[EC] {preset.SlashCommand}: '{preset.ModName}' is disabled "
                               + "in Penumbra - enable it and try again.");
                return;
            }
            if (!_penumbra.ModsGloballyEnabled())
            {
                _chat.PrintError($"[EC] {preset.SlashCommand}: Penumbra's mods are turned off "
                               + "globally, so nothing would apply.");
                return;
            }

            // If the options are already exactly what this preset wants, there
            // is nothing to apply and nothing to reload - so skip the redraw
            // entirely. Repeating a command, or firing one whose animation is
            // already selected, should not cost a visible blip.
            var alreadySet = SettingsAlreadyMatch(preset, state);

            var priorityChanged = false;
            if (_config.AlwaysWinConflicts)
                priorityChanged = RaiseAboveConflicts(preset);

            if (!alreadySet)
            {
                var failed = _penumbra.ApplySettings(preset.ModDirectory, preset.Settings);
                if (failed.Count > 0)
                    _chat.PrintError(
                        $"[EC] {preset.SlashCommand}: could not set {string.Join(", ", failed)} " +
                        $"- the mod's options may have changed since this preset was made.");
            }

            // A priority change alters which file wins, so it needs the reload
            // even when the selections themselves did not move.
            if (!alreadySet || priorityChanged)
            {
                if (!await _penumbra.RedrawAndAwaitAsync(_config.RedrawTimeoutMs)
                                    .ConfigureAwait(false))
                    _chat.PrintError($"[EC] {preset.SlashCommand}: redraw did not finish in time; " +
                                     $"the animation may be the previous one.");
            }

            // BACK ONTO THE GAME'S THREAD before touching game memory.
            // ExecuteEmote is a raw call into the client; calling it from the
            // thread the await resumed on can crash the process outright, and a
            // try/catch does not catch an access violation. Everything after
            // the await must be marshalled, including chat output and the
            // Penumbra reads inside VerifyWinner.
            await _framework.RunOnFrameworkThread(() =>
            {
                if (!_player.Perform(emote, out var problem))
                {
                    _chat.PrintError($"[EC] {problem}");
                    return;
                }

                if (_config.VerifyAfterFire)
                    VerifyWinner(preset);
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "firing preset failed");
            _chat.PrintError($"[EC] {preset.SlashCommand} failed: {ex.Message}");
        }
        finally
        {
            _fireGate.Release();
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
    private bool RaiseAboveConflicts(Preset preset)
    {
        if (preset.AllPapPaths.Count == 0)
            return false;

        var conflicts = _penumbra.Conflicts(preset.AllPapPaths, preset.ModDirectory);
        if (conflicts.Count == 0)
            return false;

        var highest = conflicts.Max(c => c.Priority);
        var mine = _penumbra.ModPriority(preset.ModDirectory) ?? 0;
        if (mine > highest)
            return false;

        if (_penumbra.SetPriority(preset.ModDirectory, highest + 1))
        {
            // Say so in chat, not just the log. This changes the user's
            // Penumbra setup, so it should never happen invisibly.
            var names = string.Join(", ", conflicts.Select(c => $"{c.Name} ({c.Priority})"));
            _chat.Print($"[EC] Raised '{preset.ModName}' priority {mine} -> {highest + 1} "
                      + $"to beat: {names}");
            return true;
        }
        return false;
    }

    /// <summary>
    /// Confirm the emote path really resolved to this preset's mod. Without
    /// this, a higher-priority mod silently wins and the user sees the wrong
    /// animation with no explanation.
    /// </summary>
    /// <summary>
    /// Whether Penumbra already holds exactly the selections this preset wants.
    ///
    /// Compared as case-insensitive SETS: Penumbra does not promise an order
    /// for a multi-select group, so comparing sequences would report a false
    /// difference and reintroduce the redraw this exists to avoid.
    ///
    /// Only the groups the preset names are considered. A preset deliberately
    /// says nothing about other groups, so differences there are not its
    /// business.
    /// </summary>
    private static bool SettingsAlreadyMatch(Preset preset, PenumbraBridge.ModState state)
    {
        foreach (var (group, wanted) in preset.Settings)
        {
            if (!state.Settings.TryGetValue(group, out var current))
                return false;      // group gone, or never set - let apply report it

            var a = new HashSet<string>(wanted ?? new List<string>(),
                                        StringComparer.OrdinalIgnoreCase);
            var b = new HashSet<string>(current ?? new List<string>(),
                                        StringComparer.OrdinalIgnoreCase);
            if (!a.SetEquals(b))
                return false;
        }
        return true;
    }

    /// <summary>
    /// Game paths come from mod json written by different tools, so both
    /// separators and either case are live in the wild. Compare normalised or
    /// real conflicts go unnoticed.
    /// </summary>
    private static string Normalise(string path)
        => path.Replace('\\', '/').ToLowerInvariant();

    private void VerifyWinner(Preset preset)
    {
        if (preset.AllPapPaths.Count == 0)
            return;

        // GetPlayerResourcePaths is keyed by the ACTUAL file, whose value is the
        // set of game paths it satisfies - not the other way round. Looking a
        // game path up as a key silently missed every real conflict and warned
        // about vanilla instead.
        var resolved = _penumbra.PlayerResourcePaths();

        // Match ANY recorded path: a shared emote resolves under one race's
        // path for every character, so the player's own race is often not the
        // one actually loaded.
        var want = new HashSet<string>(preset.AllPapPaths.Select(Normalise));

        string? actual = null;
        foreach (var (file, gamePaths) in resolved)
        {
            if (gamePaths.Any(g => want.Contains(Normalise(g))))
            {
                actual = file;
                break;
            }
        }
        if (actual is null)
            return;   // not loaded yet; nothing useful to say

        // Anchor on the mod's own folder. A bare substring test matched any
        // path containing the directory name - "Eve" matching "sleeve".
        var expectedRoot = Path.Combine(_penumbra.ModDirectoryRoot(), preset.ModDirectory);
        var mine = actual.StartsWith(expectedRoot, StringComparison.OrdinalIgnoreCase);

        if (!mine)
        {
            var winner = Path.GetFileName(actual);
            _chat.PrintError(
                $"[EC] {preset.SlashCommand}: another mod is overriding this emote " +
                $"(it resolved to {winner}). Turn on 'Always win conflicts', " +
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
