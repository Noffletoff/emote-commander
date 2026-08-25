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
                        CommandRunner runner, Dalamud.Plugin.Services.IPluginLog log)
        : base("Emote Commander###EmoteCommanderConfig")
    {
        _config = config;
        _penumbra = penumbra;
        _emotes = emotes;
        _runner = runner;
        _log = log;

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
        Preset? remove = null;

        var byMod = _config.Presets
            .GroupBy(p => p.ModName, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase);

        foreach (var group in byMod)
        {
            var label = $"{group.Key}  ({group.Count()})###mod_{group.Key}";
            if (!ImGui.CollapsingHeader(label, ImGuiTreeNodeFlags.DefaultOpen))
                continue;

            ImGui.PushID(group.Key);
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

        var paths = _penumbra.ModFilePaths(dir);
        _modEmotes = _emotes.AllFromRedirectedPaths(paths);
        _showAllEmotes = false;
        _emote = _modEmotes.FirstOrDefault();
        _modTargetsPose = paths.Select(EmoteResolver.TimelineKeyFromPath)
                               .Any(k => k is not null && EmoteResolver.IsPoseFamily(k));

        // Remember the exact path that matched. Conflict detection and the
        // priority bump both need a concrete game path, not just an emote.
        _emotePapPath = _emote is null ? string.Empty
            : paths.FirstOrDefault(p =>
                  string.Equals(EmoteResolver.TimelineKeyFromPath(p), _emote.TimelineKey,
                                StringComparison.OrdinalIgnoreCase))
              ?? string.Empty;

        if (_command.Length == 0 && _emote is not null)
            _command = SuggestCommand(name);
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
                            _selection[group] = new List<string> { option };
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
        ImGui.InputText("##command", ref _command, 64);
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

        _emotePapPath = preset.EmotePapPath;
        var paths = _penumbra.ModFilePaths(preset.ModDirectory);
        _modEmotes = _emotes.AllFromRedirectedPaths(paths);
        _showAllEmotes = _emote is not null
                      && !_modEmotes.Any(m => m.RowId == _emote.RowId);
        _modTargetsPose = paths.Select(EmoteResolver.TimelineKeyFromPath)
                               .Any(k => k is not null && EmoteResolver.IsPoseFamily(k));
    }

    /// <summary>Mod list is cached; drop it when the window opens.</summary>
    public override void OnOpen() => _modCache = null;

    public void Dispose() { }
}
