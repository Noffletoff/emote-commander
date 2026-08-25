using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Penumbra.Api.Enums;
using Penumbra.Api.IpcSubscribers;

namespace EmoteCommander;

/// <summary>
/// The only place that talks to Penumbra.
///
/// Everything else depends on this class rather than on Penumbra directly, so
/// an IPC change is contained to one file. Construction is defensive: Penumbra
/// may be absent, disabled, or a version whose IPC does not match, and none of
/// those should stop the plugin loading - they should make <see cref="Available"/>
/// false and produce a clear message.
/// </summary>
public sealed class PenumbraBridge : IDisposable
{
    private readonly IPluginLog _log;

    private readonly GetModList? _getModList;
    private readonly GetModDirectory? _getModDirectory;
    private readonly GetAvailableModSettings? _getAvailableSettings;
    private readonly GetCurrentModSettings? _getCurrentSettings;
    private readonly GetPlayerResourcePaths? _getPlayerResourcePaths;
    private readonly GetCollectionForObject? _getCollectionForObject;
    private readonly TrySetModSettings? _setModSettings;
    private readonly TrySetModPriority? _setModPriority;
    private readonly RedrawObject? _redraw;
    // CreatedCharacterBase is a static factory; Subscriber() hands back a
    // disposable subscription rather than an instance of it.
    private readonly IDisposable? _characterBaseCreated;

    private TaskCompletionSource<bool>? _redrawCompleted;

    /// <summary>False when Penumbra is missing or its IPC did not match.</summary>
    public bool Available { get; }

    /// <summary>Why it is unavailable, for the UI to show.</summary>
    public string? Unavailable { get; }

    public PenumbraBridge(IDalamudPluginInterface pi, IPluginLog log)
    {
        _log = log;
        try
        {
            _getModList = new GetModList(pi);
            _getModDirectory = new GetModDirectory(pi);
            _getAvailableSettings = new GetAvailableModSettings(pi);
            _getCurrentSettings = new GetCurrentModSettings(pi);
            _getPlayerResourcePaths = new GetPlayerResourcePaths(pi);
            _getCollectionForObject = new GetCollectionForObject(pi);
            _setModSettings = new TrySetModSettings(pi);
            _setModPriority = new TrySetModPriority(pi);
            _redraw = new RedrawObject(pi);
            _characterBaseCreated = CreatedCharacterBase.Subscriber(pi, OnCharacterBaseCreated);

            // Cheapest call that proves Penumbra is actually answering rather
            // than merely present.
            _ = _getModList.Invoke();

            Available = true;
        }
        catch (Exception ex)
        {
            Available = false;
            Unavailable = ex.Message;
            _log.Warning($"Penumbra IPC unavailable: {ex.Message}");
        }
    }

    /// <summary>Installed mods as directory name to display name.</summary>
    public Dictionary<string, string> ModList()
        => Available ? _getModList!.Invoke() : new Dictionary<string, string>();

    /// <summary>Penumbra's mod root directory on disk.</summary>
    public string ModDirectoryRoot()
        => Available ? _getModDirectory!.Invoke() : string.Empty;

    /// <summary>
    /// The collection actually applied to the local player. Presets must be
    /// applied to this rather than to whatever collection is selected in
    /// Penumbra's UI, or the change lands somewhere the character never reads.
    /// </summary>
    public Guid? PlayerCollection()
    {
        if (!Available) return null;
        var (valid, _, collection) = _getCollectionForObject!.Invoke(0);
        return valid ? collection.Id : null;
    }

    /// <summary>Option groups of a mod, and the options within each.</summary>
    public IReadOnlyDictionary<string, (string[] Options, GroupType Type)> AvailableSettings(string modDir)
    {
        if (!Available)
            return new Dictionary<string, (string[], GroupType)>();

        return _getAvailableSettings!.Invoke(modDir, string.Empty)
               ?? new Dictionary<string, (string[], GroupType)>();
    }

    /// <summary>A mod's state in the player's collection.</summary>
    public sealed record ModState(bool Enabled, int Priority,
                                  Dictionary<string, List<string>> Settings);

    /// <summary>
    /// Enabled, priority and selected options in one call. Everything else
    /// reads through this so a mod's state is never fetched twice or, worse,
    /// fetched inconsistently.
    /// </summary>
    public ModState? State(string modDir)
    {
        var collection = PlayerCollection();
        if (collection is null) return null;

        var (ec, settings) = _getCurrentSettings!.Invoke(collection.Value, modDir, string.Empty);
        if (ec is not PenumbraApiEc.Success || settings is null)
        {
            _log.Debug($"state for {modDir}: {ec}");
            return null;
        }

        var (enabled, priority, options, _) = settings.Value;
        return new ModState(enabled, priority, options);
    }

    /// <summary>What is currently selected in that mod, for the player's collection.</summary>
    public Dictionary<string, List<string>>? CurrentSettings(string modDir)
        => State(modDir)?.Settings;

    /// <summary>This mod's priority in the player's collection, or null.</summary>
    public int? ModPriority(string modDir) => State(modDir)?.Priority;

    /// <summary>
    /// Other mods that also redirect this game path and could actually beat us
    /// for it, with their priorities.
    ///
    /// ENABLED ONLY. A disabled mod redirects nothing, so counting it would
    /// raise priority to clear an obstacle that is not there - which is exactly
    /// what happened before this check existed.
    /// </summary>
    public IReadOnlyList<(string Directory, string Name, int Priority)> Conflicts(
        string gamePath, string exceptModDir)
    {
        var found = new List<(string, string, int)>();
        if (!Available || string.IsNullOrWhiteSpace(gamePath)) return found;

        foreach (var (dir, name) in ModList())
        {
            if (string.Equals(dir, exceptModDir, StringComparison.OrdinalIgnoreCase))
                continue;

            var claims = ModFilePaths(dir)
                .Any(p => string.Equals(p, gamePath, StringComparison.OrdinalIgnoreCase));
            if (!claims) continue;

            var state = State(dir);
            if (state is null || !state.Enabled)
                continue;

            found.Add((dir, name, state.Priority));
        }
        return found;
    }

    /// <summary>
    /// Game path to the file it currently resolves to for the player, after all
    /// priority resolution. This is ground truth for "which mod actually won".
    /// </summary>
    /// <remarks>
    /// A game path can map to more than one resolved file, hence the set.
    /// </remarks>
    public Dictionary<string, HashSet<string>> PlayerResourcePaths()
    {
        if (!Available) return new();
        var all = _getPlayerResourcePaths!.Invoke();
        // Keyed by object index; index 0 is the player.
        return all.TryGetValue(0, out var paths) ? paths : new();
    }

    /// <summary>
    /// Every game path a mod redirects, across its default files and all option
    /// groups.
    ///
    /// Read from the mod's own json on disk rather than over IPC, because
    /// Penumbra exposes no "list this mod's files" call. A Penumbra mod folder
    /// holds default_mod.json plus one group_*.json per option group; the
    /// redirects are the KEYS of each "Files" object.
    ///
    /// Never throws - a malformed or missing mod folder yields nothing, since
    /// this runs across every installed mod and one bad mod must not break the
    /// scan.
    /// </summary>
    public IReadOnlyList<string> ModFilePaths(string modDir)
    {
        var root = ModDirectoryRoot();
        if (string.IsNullOrEmpty(root)) return Array.Empty<string>();

        var folder = Path.Combine(root, modDir);
        if (!Directory.Exists(folder)) return Array.Empty<string>();

        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var file in Directory.EnumerateFiles(folder, "*.json",
                                                          SearchOption.TopDirectoryOnly))
            {
                var name = Path.GetFileName(file);
                var isDefault = name.Equals("default_mod.json", StringComparison.OrdinalIgnoreCase);
                var isGroup = name.StartsWith("group_", StringComparison.OrdinalIgnoreCase);
                if (!isDefault && !isGroup) continue;

                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                CollectFiles(doc.RootElement, paths);
            }
        }
        catch (Exception ex)
        {
            _log.Debug($"reading files of '{modDir}': {ex.Message}");
        }
        return paths.ToList();
    }

    private static void CollectFiles(JsonElement element, HashSet<string> into)
    {
        if (element.ValueKind is not JsonValueKind.Object) return;

        // default_mod.json: { "Files": { gamePath: localPath } }
        if (element.TryGetProperty("Files", out var files)
            && files.ValueKind is JsonValueKind.Object)
        {
            foreach (var entry in files.EnumerateObject())
                into.Add(entry.Name);
        }

        // group_*.json: { "Options": [ { "Files": {...} }, ... ] }
        if (element.TryGetProperty("Options", out var options)
            && options.ValueKind is JsonValueKind.Array)
        {
            foreach (var option in options.EnumerateArray())
                CollectFiles(option, into);
        }
    }

    // ---------------------------------------------------------------- write

    /// <summary>
    /// Apply every option selection in a preset. Returns the group names that
    /// failed, so the caller can say which rather than just "something broke" -
    /// a renamed or removed option group is the expected cause.
    /// </summary>
    public IReadOnlyList<string> ApplySettings(
        string modDir, IReadOnlyDictionary<string, List<string>> settings)
    {
        var failed = new List<string>();
        var collection = PlayerCollection();
        if (collection is null)
        {
            failed.AddRange(settings.Keys);
            return failed;
        }

        foreach (var (group, options) in settings)
        {
            // Signature order per the compiler: modName is the LAST argument,
            // not the third as the read-side calls might suggest.
            var ec = _setModSettings!.Invoke(
                collection.Value, modDir, group, options, string.Empty);

            // NothingChanged means it already had that value - a success as
            // far as the caller is concerned, and the normal result when the
            // same command is fired twice.
            if (ec is not (PenumbraApiEc.Success or PenumbraApiEc.NothingChanged))
            {
                _log.Warning($"apply {modDir} / '{group}' -> {ec}");
                failed.Add(group);
            }
        }
        return failed;
    }

    /// <summary>Raise this mod's priority, for the opt-in conflict override.</summary>
    public bool SetPriority(string modDir, int priority)
    {
        var collection = PlayerCollection();
        if (collection is null) return false;

        var ec = _setModPriority!.Invoke(collection.Value, modDir, priority, string.Empty);
        if (ec is not (PenumbraApiEc.Success or PenumbraApiEc.NothingChanged))
        {
            _log.Warning($"priority {modDir} -> {priority}: {ec}");
            return false;
        }
        return true;
    }

    /// <summary>
    /// Redraw the player and wait for it to finish.
    ///
    /// The wait is the load-bearing part: the game caches a loaded .pap by game
    /// path, so firing the emote before the redraw completes plays the OLD
    /// animation - indistinguishable from the plugin being broken. Penumbra
    /// signals completion via CreatedCharacterBase.
    ///
    /// A missed signal degrades to the timeout rather than hanging.
    /// </summary>
    public async Task<bool> RedrawAndAwaitAsync(int timeoutMs = 5000)
    {
        if (!Available) return false;

        var tcs = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _redrawCompleted = tcs;
        try
        {
            _redraw!.Invoke(0, RedrawType.Redraw);
            var finished = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs))
                                     .ConfigureAwait(false);
            if (finished != tcs.Task)
            {
                _log.Warning($"redraw did not signal completion within {timeoutMs}ms");
                return false;
            }
            return true;
        }
        finally
        {
            _redrawCompleted = null;
        }
    }

    private void OnCharacterBaseCreated(nint gameObject, Guid collection, nint drawObject)
        => _redrawCompleted?.TrySetResult(true);

    public void Dispose()
    {
        _characterBaseCreated?.Dispose();
    }
}
