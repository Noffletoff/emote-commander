using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Interface.Windowing;
using Dalamud.Bindings.ImGui;

namespace EmoteCommander.Ui;

/// <summary>
/// Build a preset: pick a mod, choose its options, name a command.
/// The emote is worked out from the mod itself and can be overridden.
/// </summary>
public sealed class ConfigWindow : Window, IDisposable
{
    private readonly Config _config;
    private readonly PenumbraBridge _penumbra;
    private readonly EmoteCatalogue _emotes;
    private readonly CommandRunner _runner;
    private readonly Dalamud.Plugin.Services.IPluginLog _log;
    private readonly PresetImporter _importer;

    // -- editor state ----------------------------------------------------
    private string _modFilter = string.Empty;
    private string? _selectedModDir;
    private string _selectedModName = string.Empty;
    private readonly Dictionary<string, List<string>> _selection = new();
    private EmoteEntry? _emote;
    private bool _emoteOverridden;
    private string _emoteFilter = string.Empty;
    private string _command = string.Empty;
    private bool _modTargetsPose;
    private string _emotePapPath = string.Empty;
    private bool _switchToEditor;
    private bool _focusCommandField;
    private string? _confirmDeleteMod;
    private string? _drawError;

    /// <summary>
    /// The preset being edited, or null when building a new one.
    ///
    /// Without this the editor had to guess from the command name, and guessed
    /// wrong both ways: renaming a command produced a duplicate because the
    /// original was never removed, and re-pointing one at a different mod made
    /// the preset clash with ITSELF so the Save button never appeared.
    /// </summary>
    private Preset? _editing;
    private bool _showAllEmotes;
    private IReadOnlyList<EmoteEntry> _modEmotes = System.Array.Empty<EmoteEntry>();
    private string? _status;

    private List<KeyValuePair<string, string>>? _modCache;

    public ConfigWindow(Config config, PenumbraBridge penumbra, EmoteCatalogue emotes,
                        CommandRunner runner, Dalamud.Plugin.Services.IPluginLog log,
                        PresetImporter importer)
        : base("Emote Commander###EmoteCommanderConfig")
    {
        _config = config;
        _penumbra = penumbra;
        _emotes = emotes;
        _runner = runner;
        _log = log;
        _importer = importer;

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(520, 420),
            MaximumSize = new Vector2(1400, 1200),
        };
    }

    public override void Draw()
    {
        // A throw anywhere below skips the matching End/Pop calls and leaves
        // ImGui's stack unbalanced for the rest of the frame - which surfaces
        // as an assert rather than an error anyone can read. Catch here so a
        // bad preset degrades to a message instead.
        try
        {
            DrawInner();
        }
        catch (Exception ex)
        {
            _drawError = ex.Message;
            _log.Error(ex, "Emote Commander window draw failed");
        }
    }

    private void DrawInner()
    {
        if (_drawError is not null)
        {
            ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f),
                "Something went wrong drawing this window:");
            ImGui.TextWrapped(_drawError);
            if (ImGui.Button("Try again"))
                _drawError = null;
            return;
        }

        if (!_penumbra.Available)
        {
            ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f),
                "Penumbra is not available. Emote Commander needs it.");
            if (_penumbra.Unavailable is not null)
                ImGui.TextWrapped(_penumbra.Unavailable);
            return;
        }

        if (ImGui.BeginTabBar("##ectabs"))
        {
            if (ImGui.BeginTabItem("Commands"))
            {
                DrawPresetList();
                ImGui.EndTabItem();
            }
            // Edit on the Commands tab loads the preset into the editor; without
            // forcing the tab across, it looked like the button did nothing.
            var editorFlags = _switchToEditor
                ? ImGuiTabItemFlags.SetSelected
                : ImGuiTabItemFlags.None;
            _switchToEditor = false;

            if (ImGui.BeginTabItem("New / Edit", editorFlags))
            {
                DrawEditor();
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem("Share"))
            {
                DrawShare();
                ImGui.EndTabItem();
            }
            ImGui.EndTabBar();
        }
    }

    // ------------------------------------------------------------ presets

    private void DrawPresetList()
    {
        var win = _config.AlwaysWinConflicts;
        if (ImGui.Checkbox("Always win conflicts", ref win))
        {
            _config.AlwaysWinConflicts = win;
            _config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                "When another mod also claims a command's emote, raise this mod's "
                + "priority to one above the highest conflicting mod before firing. "
                + "Off by default, because it changes your Penumbra priorities.");
        ImGui.Separator();

        if (_config.Presets.Count == 0)
        {
            ImGui.TextWrapped(
                "No commands yet. Use the New / Edit tab: pick one of your mods, "
                + "choose its options, give it a command name.");
            return;
        }

        // Grouped by mod: one mod usually owns several commands, and a flat
        // list stops being readable the moment you have more than a handful.
        // Deletions are collected and applied AFTER the loop - removing from
        // the list being enumerated would throw mid-frame and unbalance ImGui.
        Preset? remove = null;
        string? removeMod = null;

        var byMod = _config.Presets
            .GroupBy(p => p.ModName, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase);

        foreach (var group in byMod)
        {
            var label = $"{group.Key}  ({group.Count()})###mod_{group.Key}";
            if (!ImGui.CollapsingHeader(label, ImGuiTreeNodeFlags.DefaultOpen))
                continue;

            ImGui.PushID(group.Key);

            // Removing a whole mod's commands one row at a time is painful
            // once a megapack has contributed a dozen or more.
            var pending = string.Equals(_confirmDeleteMod, group.Key,
                                        StringComparison.OrdinalIgnoreCase);
            if (pending)
            {
                ImGui.TextColored(new Vector4(1f, 0.5f, 0.5f, 1f),
                    $"Remove all {group.Count()} commands for this mod?");
                ImGui.SameLine();
                if (ImGui.Button("Yes, remove them"))
                {
                    removeMod = group.Key;
                    _confirmDeleteMod = null;
                }
                ImGui.SameLine();
                if (ImGui.Button("Cancel"))
                    _confirmDeleteMod = null;
            }
            else if (ImGui.SmallButton($"Remove all {group.Count()}"))
            {
                _confirmDeleteMod = group.Key;
            }

            if (ImGui.BeginTable("##presets", 3,
                    ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
            {
                ImGui.TableSetupColumn("Command", ImGuiTableColumnFlags.WidthFixed, 140);
                ImGui.TableSetupColumn("Emote", ImGuiTableColumnFlags.WidthFixed, 140);
                ImGui.TableSetupColumn("##actions", ImGuiTableColumnFlags.WidthFixed, 140);

                foreach (var preset in group.OrderBy(p => p.Command, StringComparer.OrdinalIgnoreCase))
                {
                    ImGui.TableNextRow();
                    ImGui.PushID(preset.Command);

                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(preset.SlashCommand);

                    ImGui.TableNextColumn();
                    var emote = _emotes.ByRowId(preset.EmoteRowId);
                    ImGui.TextUnformatted(emote?.TextCommand ?? "(missing)");
                    if (emote is not null && ImGui.IsItemHovered())
                        ImGui.SetTooltip(emote.Name);

                    ImGui.TableNextColumn();
                    if (ImGui.Button("Test")) _runner.Fire(preset);
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip("Run this command now");
                    ImGui.SameLine();
                    if (ImGui.Button("Edit"))
                    {
                        LoadForEdit(preset);
                        _switchToEditor = true;
                        // Renaming is the usual reason to press Edit, and the
                        // field sits below the mod picker, every option group
                        // and the emote picker - well off screen on a long mod.
                        _focusCommandField = true;
                    }
                    ImGui.SameLine();
                    if (ImGui.Button("X")) remove = preset;
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip("Delete this command");

                    ImGui.PopID();
                }
                ImGui.EndTable();
            }
            ImGui.PopID();
        }

        if (remove is not null)
        {
            _runner.Unregister(remove.Command);
            _config.Presets.Remove(remove);
            _config.Save();
        }

        if (removeMod is not null)
        {
            var doomed = _config.Presets
                .Where(p => string.Equals(p.ModName, removeMod, StringComparison.OrdinalIgnoreCase))
                .ToList();

            foreach (var preset in doomed)
            {
                _runner.Unregister(preset.Command);
                _config.Presets.Remove(preset);

                // If one of them is loaded in the editor, stop editing it -
                // saving afterwards would otherwise resurrect it.
                if (ReferenceEquals(preset, _editing))
                    _editing = null;
            }
            _config.Save();
            _status = $"Removed {doomed.Count} command(s) for '{removeMod}'.";
        }
    }

    // -------------------------------------------------------------- share

    private string _importBox = string.Empty;
    private string _exportBox = string.Empty;
    private string? _importStatus;
    private bool _importFailed;

    private void DrawShare()
    {
        ImGui.TextWrapped(
            "Paste a share code to add someone else's commands, or export yours "
            + "to hand out. A code can also be put in a mod's Penumbra "
            + "description so it travels with the mod.");

        ImGui.Separator();
        DrawDiscovered();

        ImGui.Separator();
        ImGui.TextUnformatted("Import");

        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextMultiline("##importbox", ref _importBox, 8192,
            new Vector2(-1, 90));

        ImGui.BeginDisabled(_importBox.Trim().Length == 0);
        if (ImGui.Button("Import"))
            DoImport();
        ImGui.EndDisabled();

        ImGui.SameLine();
        if (ImGui.Button("Paste from clipboard"))
        {
            _importBox = ImGui.GetClipboardText() ?? string.Empty;
            _importStatus = null;
        }
        ImGui.SameLine();
        if (ImGui.Button("Clear##import"))
        {
            _importBox = string.Empty;
            _importStatus = null;
        }

        if (_importStatus is not null)
        {
            var colour = _importFailed
                ? new Vector4(1f, 0.5f, 0.5f, 1f)
                : new Vector4(0.5f, 1f, 0.5f, 1f);
            ImGui.TextColored(colour, _importFailed ? "Import failed" : "Imported");
            ImGui.TextWrapped(_importStatus);
        }

        ImGui.Separator();
        ImGui.TextUnformatted("Export");

        if (_config.Presets.Count == 0)
        {
            ImGui.TextDisabled("Nothing to export yet.");
            return;
        }

        if (ImGui.Button($"Generate code for all {_config.Presets.Count} command(s)"))
            _exportBox = ShareCode.Encode(_config.Presets);

        if (_exportBox.Length == 0)
            return;

        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextMultiline("##exportbox", ref _exportBox, 65536,
            new Vector2(-1, 90), ImGuiInputTextFlags.ReadOnly);

        if (ImGui.Button("Copy to clipboard"))
            ImGui.SetClipboardText(_exportBox);
    }

    // -- discovery from mod descriptions ---------------------------------

    private List<DiscoveredPreset>? _discovered;
    private readonly HashSet<string> _ticked = new(StringComparer.OrdinalIgnoreCase);
    private string? _scanStatus;

    private void DrawDiscovered()
    {
        ImGui.TextUnformatted("From your installed mods");
        ImGui.SameLine();
        if (ImGui.Button("Scan mod descriptions"))
            RunScan();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                "Looks through every installed mod's Penumbra description for "
                + "commands the mod author included. Nothing is added until you "
                + "tick it and press Add.");

        if (_scanStatus is not null)
            ImGui.TextDisabled(_scanStatus);

        if (_discovered is null || _discovered.Count == 0)
            return;

        var addable = _discovered.Where(d => d.CanAdd).ToList();
        var blocked = _discovered.Where(d => !d.CanAdd).ToList();

        foreach (var d in addable)
        {
            var key = d.Preset.Command + "|" + d.SourceModDirectory;
            var on = _ticked.Contains(key);
            if (ImGui.Checkbox($"{d.Preset.SlashCommand}##{key}", ref on))
            {
                if (on) _ticked.Add(key); else _ticked.Remove(key);
            }
            ImGui.SameLine();
            ImGui.TextDisabled($"-> {d.Preset.ModName}");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip($"offered by: {d.SourceModName}\nmod folder: {d.Preset.ModDirectory}");
        }

        if (addable.Count > 0)
        {
            ImGui.BeginDisabled(_ticked.Count == 0);
            if (ImGui.Button($"Add {_ticked.Count} selected"))
                AddTicked(addable);
            ImGui.EndDisabled();
            ImGui.SameLine();
            if (ImGui.Button("Select all"))
                foreach (var d in addable)
                    _ticked.Add(d.Preset.Command + "|" + d.SourceModDirectory);
        }

        // Shown, not hidden: a command that silently fails to appear is worse
        // than one that says why it cannot be added.
        if (blocked.Count > 0 && ImGui.CollapsingHeader($"Not addable ({blocked.Count})"))
        {
            foreach (var d in blocked)
            {
                ImGui.TextUnformatted(d.Preset.SlashCommand.Length > 1
                    ? d.Preset.SlashCommand : "(no command)");
                ImGui.SameLine();
                ImGui.TextDisabled($"[{d.SourceModName}] {d.Problem}");
            }
        }
    }

    private void RunScan()
    {
        _ticked.Clear();
        _discovered = _importer.Scan().ToList();
        var addable = _discovered.Count(d => d.CanAdd);
        _scanStatus = _discovered.Count == 0
            ? "No mods offer commands. Authors add them by putting a share code in the mod's description."
            : $"Found {_discovered.Count} in mod descriptions, {addable} addable.";
    }

    private void AddTicked(List<DiscoveredPreset> addable)
    {
        var added = 0;
        foreach (var d in addable)
        {
            var key = d.Preset.Command + "|" + d.SourceModDirectory;
            if (!_ticked.Contains(key)) continue;

            _config.Presets.Add(d.Preset);
            if (_runner.Register(d.Preset)) added++;
        }
        _config.Save();
        _scanStatus = $"Added {added} command(s).";
        RunScan();      // refresh so they now show as "already added"
    }

    private void DoImport()
    {
        try
        {
            var result = _runner.ImportCode(_importBox);
            _importFailed = !result.Ok;
            _importStatus = $"{result.Added} of {result.Total} added.\n"
                          + string.Join("\n", result.Messages);
            if (result.Ok)
                _importBox = string.Empty;
        }
        catch (FormatException ex)
        {
            _importFailed = true;
            _importStatus = ex.Message;
        }
    }

    // ------------------------------------------------------------- editor

    private void DrawEditor()
    {
        DrawModPicker();
        if (_selectedModDir is null)
        {
            ImGui.TextDisabled("Pick a mod to continue.");
            return;
        }

        ImGui.Separator();
        DrawOptionGroups();

        ImGui.Separator();
        DrawEmotePicker();

        ImGui.Separator();
        DrawCommandAndSave();

        if (_status is not null)
        {
            ImGui.Separator();
            ImGui.TextWrapped(_status);
        }
    }

    private void DrawModPicker()
    {
        _modCache ??= _penumbra.ModList()
                               .OrderBy(kv => kv.Value, StringComparer.OrdinalIgnoreCase)
                               .ToList();

        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##modfilter", "Filter mods...", ref _modFilter, 128);

        var shown = _modCache
            .Where(kv => _modFilter.Length == 0
                      || kv.Value.Contains(_modFilter, StringComparison.OrdinalIgnoreCase))
            .Take(200)
            .ToList();

        ImGui.SetNextItemWidth(-1);
        if (ImGui.BeginCombo("##mod", _selectedModName.Length > 0 ? _selectedModName : "Select a mod"))
        {
            foreach (var (dir, name) in shown)
            {
                if (ImGui.Selectable(name, dir == _selectedModDir))
                    SelectMod(dir, name);
            }
            ImGui.EndCombo();
        }

        if (_modTargetsPose)
        {
            ImGui.TextColored(new Vector4(1f, 0.75f, 0.3f, 1f),
                "This mod targets a sit pose.");
            ImGui.TextWrapped(
                "A command can start the emote but cannot select the pose index, "
                + "so you may get the wrong variant. Pose switching is deliberately "
                + "not automated.");
        }
    }

    private void SelectMod(string dir, string name)
    {
        // Changing mod does not stop you editing the same preset.
        _selectedModDir = dir;
        _selectedModName = name;
        _selection.Clear();
        _status = null;
        _emoteOverridden = false;

        foreach (var (group, options) in _penumbra.CurrentSettings(dir) ?? new())
            _selection[group] = new List<string>(options);

        _showAllEmotes = false;
        RefreshEmotesForSelection();

        var paths = _penumbra.ModFilePaths(dir);
        _modTargetsPose = paths.Select(EmoteResolver.TimelineKeyFromPath)
                               .Any(k => k is not null && EmoteResolver.IsPoseFamily(k));

        if (_command.Length == 0 && _emote is not null)
            _command = SuggestCommand(name);
    }

    /// <summary>
    /// Work out which emotes the CURRENT option selection replaces, and pick
    /// one if the answer is unambiguous.
    ///
    /// Resolving against the selection rather than the whole mod is what makes
    /// a megapack workable: a pack redirecting twenty emotes in total redirects
    /// exactly one for any given animation, so choosing "Crazy Riding" should
    /// narrow the emote to wring hands rather than offering all twenty.
    ///
    /// Falls back to the whole mod when nothing is selected yet, so a simple
    /// one-emote mod still auto-detects immediately.
    /// </summary>
    private void RefreshEmotesForSelection()
    {
        if (_selectedModDir is null)
        {
            _modEmotes = System.Array.Empty<EmoteEntry>();
            return;
        }

        var paths = _selection.Count > 0
            ? _penumbra.ModFilePathsForOptions(_selectedModDir, _selection)
            : _penumbra.ModFilePaths(_selectedModDir);

        // A selection that redirects nothing (all groups on "None") tells us
        // nothing - fall back rather than showing an empty picker.
        var narrowed = _emotes.AllFromRedirectedPaths(paths);
        if (narrowed.Count == 0)
        {
            paths = _penumbra.ModFilePaths(_selectedModDir);
            narrowed = _emotes.AllFromRedirectedPaths(paths);
        }

        _modEmotes = narrowed;

        // Keep a hand-picked emote; otherwise follow the selection.
        if (!_emoteOverridden && (_emote is null
            || !_modEmotes.Any(e => e.RowId == _emote.RowId)))
            _emote = _modEmotes.FirstOrDefault();

        // Record the concrete path that matched, which conflict detection and
        // the priority bump both need.
        _emotePapPath = _emote is null ? string.Empty
            : paths.FirstOrDefault(p => string.Equals(
                  EmoteResolver.TimelineKeyFromPath(p), _emote.TimelineKey,
                  StringComparison.OrdinalIgnoreCase))
              ?? string.Empty;
    }

    private static string SuggestCommand(string modName)
    {
        var cleaned = new string(modName.Where(c => char.IsLetterOrDigit(c)).ToArray());
        return cleaned.Length == 0 ? "mycommand" : cleaned.ToLowerInvariant();
    }

    private void DrawOptionGroups()
    {
        var groups = _penumbra.AvailableSettings(_selectedModDir!);
        if (groups.Count == 0)
        {
            ImGui.TextDisabled("This mod has no option groups - nothing to choose.");
            return;
        }

        ImGui.TextUnformatted("Options applied when the command runs:");

        foreach (var (group, info) in groups)
        {
            ImGui.PushID(group);
            _selection.TryGetValue(group, out var chosen);
            chosen ??= new List<string>();

            if (info.Type == Penumbra.Api.Enums.GroupType.Single)
            {
                var current = chosen.FirstOrDefault() ?? "(none)";
                ImGui.SetNextItemWidth(260);
                if (ImGui.BeginCombo(group, current))
                {
                    foreach (var option in info.Options)
                    {
                        if (ImGui.Selectable(option, option == current))
                        {
                            _selection[group] = new List<string> { option };
                            RefreshEmotesForSelection();
                        }
                    }
                    ImGui.EndCombo();
                }
            }
            else
            {
                ImGui.TextUnformatted(group);
                ImGui.Indent();
                foreach (var option in info.Options)
                {
                    var on = chosen.Contains(option);
                    if (ImGui.Checkbox(option, ref on))
                    {
                        var list = _selection.TryGetValue(group, out var l)
                                 ? l : _selection[group] = new List<string>();
                        if (on) { if (!list.Contains(option)) list.Add(option); }
                        else list.Remove(option);
                        RefreshEmotesForSelection();
                    }
                }
                ImGui.Unindent();
            }
            ImGui.PopID();
        }
    }

    private void DrawEmotePicker()
    {
        ImGui.TextUnformatted("Emote to perform:");

        // Only the emotes this mod actually replaces. Offering all ~293 lets
        // you bind a command to an emote the mod never touches, which fires a
        // vanilla animation and looks broken with nothing to explain it.
        var modEmotes = _modEmotes;
        var showAll = _showAllEmotes || modEmotes.Count == 0;

        if (modEmotes.Count == 0)
            ImGui.TextColored(new Vector4(1f, 0.75f, 0.3f, 1f),
                "This mod does not replace any body emote animation. "
                + "A command will play the vanilla emote.");

        var label = _emote is null ? "Select an emote" : $"{_emote.TextCommand}  ({_emote.Name})";
        ImGui.SetNextItemWidth(-1);
        if (ImGui.BeginCombo("##emote", label))
        {
            var source = showAll ? _emotes.All : modEmotes;

            if (showAll)
            {
                ImGui.SetNextItemWidth(-1);
                ImGui.InputTextWithHint("##emotefilter", "Filter emotes...", ref _emoteFilter, 128);
            }

            foreach (var e in source
                         .Where(e => !showAll || _emoteFilter.Length == 0
                                  || e.Name.Contains(_emoteFilter, StringComparison.OrdinalIgnoreCase)
                                  || e.TextCommand.Contains(_emoteFilter, StringComparison.OrdinalIgnoreCase))
                         .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                         .Take(300))
            {
                if (ImGui.Selectable($"{e.TextCommand}  ({e.Name})", e.RowId == _emote?.RowId))
                {
                    _emote = e;
                    _emoteOverridden = !modEmotes.Any(m => m.RowId == e.RowId);
                    _emotePapPath = PathForEmote(e);
                }
            }
            ImGui.EndCombo();
        }

        if (modEmotes.Count > 0)
        {
            var all = _showAllEmotes;
            if (ImGui.Checkbox("Show every emote", ref all))
                _showAllEmotes = all;
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(
                    $"This mod replaces {modEmotes.Count} emote(s). Ticking this lets you "
                    + "pick any emote, but one the mod does not replace will play the "
                    + "vanilla animation.");
        }

        if (_emoteOverridden)
            ImGui.TextColored(new Vector4(1f, 0.75f, 0.3f, 1f),
                "This emote is not replaced by this mod.");
    }

    /// <summary>The redirected path matching an emote, for conflict checks.</summary>
    private string PathForEmote(EmoteEntry emote)
    {
        if (_selectedModDir is null) return string.Empty;
        return _penumbra.ModFilePaths(_selectedModDir)
                   .FirstOrDefault(p => string.Equals(
                       EmoteResolver.TimelineKeyFromPath(p), emote.TimelineKey,
                       StringComparison.OrdinalIgnoreCase))
               ?? string.Empty;
    }

    private void DrawCommandAndSave()
    {
        ImGui.TextUnformatted("Command name:");
        ImGui.SetNextItemWidth(240);

        // Must be called immediately before the widget it focuses.
        if (_focusCommandField)
            ImGui.SetKeyboardFocusHere();

        ImGui.InputText("##command", ref _command, 64);

        if (_focusCommandField)
        {
            // Centre it rather than scrolling it to the very edge.
            ImGui.SetScrollHereY(0.5f);
            _focusCommandField = false;
        }

        ImGui.SameLine();
        ImGui.TextDisabled(_command.Length > 0 ? "/" + _command.TrimStart('/') : "");

        // Priority is a single global setting on the Commands tab - it
        // describes how the mod setup should behave, not one command.

        if (_editing is not null)
        {
            ImGui.TextDisabled($"Editing {_editing.SlashCommand}");
            ImGui.SameLine();
            if (ImGui.Button("Start a new command instead"))
            {
                ClearEditor();
                return;
            }
        }

        var problem = Validate();
        if (problem is not null)
        {
            ImGui.TextColored(new Vector4(1f, 0.5f, 0.5f, 1f), problem);
            return;
        }

        if (ImGui.Button(_editing is null ? "Save command" : "Save changes"))
            Save();
    }

    private void ClearEditor()
    {
        _editing = null;
        _command = string.Empty;
        _selection.Clear();
        _selectedModDir = null;
        _selectedModName = string.Empty;
        _emote = null;
        _emotePapPath = string.Empty;
        _emoteOverridden = false;
        _modTargetsPose = false;
        _modEmotes = System.Array.Empty<EmoteEntry>();
        _status = null;
    }

    private string? Validate()
    {
        if (_emote is null) return "Pick an emote.";
        try { Preset.Normalise(_command); }
        catch (ArgumentException ex) { return ex.Message; }

        var normalised = Preset.Normalise(_command);

        // Exclude the preset being edited: it is not a clash with itself.
        // Comparing on mod directory instead made re-pointing an existing
        // command at a different mod unsaveable.
        var clash = _config.Presets.FirstOrDefault(p =>
            !ReferenceEquals(p, _editing) &&
            string.Equals(p.Command, normalised, StringComparison.OrdinalIgnoreCase));
        if (clash is not null)
            return $"/{normalised} is already used by {clash.ModName}.";

        var renaming = _editing is not null
                    && !string.Equals(_editing.Command, normalised, StringComparison.OrdinalIgnoreCase);
        if ((_editing is null || renaming) && !_runner.IsNameAvailable(_command))
            return $"/{normalised} is already taken by the game or another plugin.";

        return null;
    }

    private void Save()
    {
        var normalised = Preset.Normalise(_command);

        // Remove the preset being edited even when it is being RENAMED - the
        // old name would otherwise stay registered and leave a duplicate.
        if (_editing is not null)
        {
            _runner.Unregister(_editing.Command);
            _config.Presets.Remove(_editing);
        }

        var existing = _config.Presets.FirstOrDefault(p =>
            string.Equals(p.Command, normalised, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            _runner.Unregister(existing.Command);
            _config.Presets.Remove(existing);
        }

        var preset = new Preset
        {
            Command = normalised,
            ModDirectory = _selectedModDir!,
            ModName = _selectedModName,
            Settings = _selection.ToDictionary(kv => kv.Key, kv => new List<string>(kv.Value)),
            EmoteRowId = _emote!.RowId,
            EmotePapPath = _emotePapPath,
            EmoteOverridden = _emoteOverridden,
        };

        _config.Presets.Add(preset);
        _config.Save();

        _status = _runner.Register(preset)
            ? $"Saved. Try /{normalised}."
            : $"Saved, but /{normalised} could not be registered - the name is taken.";

        // Keep editing what was just saved, so a second Save does not create a
        // duplicate of it.
        _editing = preset;
    }

    private void LoadForEdit(Preset preset)
    {
        _editing = preset;
        _selectedModDir = preset.ModDirectory;
        _selectedModName = preset.ModName;
        _command = preset.Command;
        _emote = _emotes.ByRowId(preset.EmoteRowId);
        _emoteOverridden = preset.EmoteOverridden;
        _status = null;

        _selection.Clear();
        foreach (var (group, options) in preset.Settings)
            _selection[group] = new List<string>(options);

        RefreshEmotesForSelection();
        if (preset.EmotePapPath.Length > 0)
            _emotePapPath = preset.EmotePapPath;
        var paths = _penumbra.ModFilePaths(preset.ModDirectory);
        _showAllEmotes = _emote is not null
                      && !_modEmotes.Any(m => m.RowId == _emote.RowId);
        _modTargetsPose = paths.Select(EmoteResolver.TimelineKeyFromPath)
                               .Any(k => k is not null && EmoteResolver.IsPoseFamily(k));
    }

    /// <summary>Mod list is cached; drop it when the window opens.</summary>
    public override void OnOpen() => _modCache = null;

    public void Dispose() { }
}
