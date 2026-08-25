using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
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
    private readonly IObjectTable _objects;

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

    private readonly GetEnabledState? _getEnabledState;

    private TaskCompletionSource<bool>? _redrawCompleted;
    private nint _awaitedObject;

    /// <summary>False when Penumbra is missing or its IPC did not match.</summary>
    public bool Available { get; }

    /// <summary>Why it is unavailable, for the UI to show.</summary>
    public string? Unavailable { get; }

    public PenumbraBridge(IDalamudPluginInterface pi, IPluginLog log,
                          IObjectTable objects)
    {
        _log = log;
        _objects = objects;
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
            _getEnabledState = new GetEnabledState(pi);
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
    /// ACTUAL file on disk to the set of GAME PATHS it currently satisfies for
    /// the player, after all priority resolution. Ground truth for "which mod
    /// actually won".
    /// </summary>
    /// <remarks>
    /// Note the direction: the KEY is the real file, the VALUES are game paths.
    /// It was previously documented - and used - the other way round, which
    /// made the conflict check miss every real conflict and warn about vanilla.
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
        var folder = SafeModFolder(modDir);
        if (folder is null) return Array.Empty<string>();

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

    /// <summary>
    /// The mod's folder on disk, or null if the name is not one we will touch.
    ///
    /// modDir can arrive from an imported share code, so it is untrusted: a
    /// crafted value like "..\..\Windows" would otherwise walk out of the
    /// Penumbra directory. Shared by every disk reader so the check cannot be
    /// forgotten by one of them.
    /// </summary>
    private string? SafeModFolder(string modDir)
    {
        var root = ModDirectoryRoot();
        if (string.IsNullOrEmpty(root) || string.IsNullOrWhiteSpace(modDir))
            return null;

        if (modDir.Contains("..", StringComparison.Ordinal)
            || Path.IsPathRooted(modDir)
            || modDir.IndexOfAny(new[] { '/', '\\' }) >= 0)
        {
            _log.Warning($"refusing suspicious mod directory: '{modDir}'");
            return null;
        }

        var folder = Path.Combine(root, modDir);

        // Belt and braces: whatever the string was, the resolved path must sit
        // inside the Penumbra root.
        if (!Path.GetFullPath(folder).StartsWith(Path.GetFullPath(root),
                                                 StringComparison.OrdinalIgnoreCase))
        {
            _log.Warning($"refusing mod directory outside the Penumbra root: '{modDir}'");
            return null;
        }

        return Directory.Exists(folder) ? folder : null;
    }

    /// <summary>
    /// A mod's Penumbra description, or empty.
    ///
    /// Read from the mod's own meta.json. The description is where a share code
    /// can be embedded so it travels with the mod: Penumbra owns the field, so
    /// unlike a sidecar file it is guaranteed to survive .pmp packing and
    /// installation, and it can be added to mods that have already shipped.
    ///
    /// Description is frequently NULL rather than empty in real mod folders
    /// (9 of 114 in a real library), so this must not assume a string.
    /// </summary>
    public string ModDescription(string modDir)
    {
        var folder = SafeModFolder(modDir);
        if (folder is null) return string.Empty;

        var meta = Path.Combine(folder, "meta.json");
        if (!File.Exists(meta)) return string.Empty;

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(meta));
            if (doc.RootElement.ValueKind is not JsonValueKind.Object)
                return string.Empty;
            if (!doc.RootElement.TryGetProperty("Description", out var description))
                return string.Empty;
            return description.ValueKind == JsonValueKind.String
                 ? description.GetString() ?? string.Empty
                 : string.Empty;
        }
        catch (Exception ex)
        {
            // Runs across every installed mod; one unreadable meta.json must
            // not break the scan, and must not spam the log either.
            _log.Debug($"reading description of '{modDir}': {ex.Message}");
            return string.Empty;
        }
    }

    /// <summary>
    /// The game paths a mod redirects GIVEN a particular set of option
    /// selections, rather than the union across every option.
    ///
    /// This is what makes a megapack usable. A pack with twenty option groups
    /// redirects twenty different emotes in total, so the union tells you
    /// nothing about which emote a specific command will play - the picker
    /// would list all twenty and auto-detect an arbitrary one. Resolved against
    /// the actual selection, "Crazy Riding" narrows to the one emote it
    /// replaces.
    ///
    /// Default files always count; group files count only when their option is
    /// selected.
    /// </summary>
    public IReadOnlyList<string> ModFilePathsForOptions(
        string modDir, IReadOnlyDictionary<string, List<string>> selection)
    {
        var folder = SafeModFolder(modDir);
        if (folder is null) return Array.Empty<string>();

        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var file in Directory.EnumerateFiles(folder, "*.json",
                                                          SearchOption.TopDirectoryOnly))
            {
                var name = Path.GetFileName(file);
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                var root = doc.RootElement;

                if (name.Equals("default_mod.json", StringComparison.OrdinalIgnoreCase))
                {
                    CollectFiles(root, paths);
                    continue;
                }
                if (!name.StartsWith("group_", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (root.ValueKind is not JsonValueKind.Object)
                    continue;

                var groupName = root.TryGetProperty("Name", out var gn)
                             && gn.ValueKind == JsonValueKind.String
                    ? gn.GetString() ?? string.Empty
                    : string.Empty;

                if (!selection.TryGetValue(groupName, out var chosen) || chosen is null)
                    continue;

                if (!root.TryGetProperty("Options", out var options)
                    || options.ValueKind is not JsonValueKind.Array)
                    continue;

                foreach (var option in options.EnumerateArray())
                {
                    if (option.ValueKind is not JsonValueKind.Object) continue;
                    var optionName = option.TryGetProperty("Name", out var on)
                                  && on.ValueKind == JsonValueKind.String
                        ? on.GetString() ?? string.Empty
                        : string.Empty;

                    if (chosen.Contains(optionName, StringComparer.OrdinalIgnoreCase))
                        CollectFiles(option, paths);
                }
            }
        }
        catch (Exception ex)
        {
            _log.Debug($"reading selected files of '{modDir}': {ex.Message}");
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

        // Remember WHOSE redraw we are waiting for. Penumbra raises
        // CreatedCharacterBase for every character base the game builds -
        // passers-by, minions, mounts - so without this the wait is satisfied
        // by a stranger loading nearby and the emote fires against a
        // half-finished redraw. That failure is crowd-dependent: fine at home,
        // wrong in a city.
        // Index 0 is the local player, and index 0 is exactly what we ask
        // Penumbra to redraw below - so compare against the same thing.
        _awaitedObject = _objects[0]?.Address ?? nint.Zero;
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
            // Only clear our OWN registration. A plain null would destroy a
            // later call's live registration if the two ever overlapped.
            Interlocked.CompareExchange(ref _redrawCompleted, null, tcs);
        }
    }

    private void OnCharacterBaseCreated(nint gameObject, Guid collection, nint drawObject)
    {
        var awaited = _awaitedObject;
        if (awaited != nint.Zero && gameObject != awaited)
            return;

        _redrawCompleted?.TrySetResult(true);
    }

    /// <summary>
    /// Penumbra's master "Enable Mods" switch. When it is off every call still
    /// reports success while Penumbra applies nothing, so a command would look
    /// like it worked and change nothing at all.
    /// </summary>
    public bool ModsGloballyEnabled()
    {
        if (!Available) return false;
        try { return _getEnabledState?.Invoke() ?? true; }
        catch (Exception ex)
        {
            _log.Debug($"GetEnabledState failed: {ex.Message}");
            return true;      // assume on rather than block the user
        }
    }

    public void Dispose()
    {
        // Release anything still waiting, or a fire in flight during a plugin
        // reload would perform a real, broadcast emote seconds later from a
        // dead instance.
        _redrawCompleted?.TrySetResult(false);
        _redrawCompleted = null;
        _characterBaseCreated?.Dispose();
    }
}
