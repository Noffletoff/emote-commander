using System;
using System.Collections.Generic;
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

    /// <summary>What is currently selected in that mod, for the player's collection.</summary>
    public Dictionary<string, List<string>>? CurrentSettings(string modDir)
    {
        var collection = PlayerCollection();
        if (collection is null) return null;

        var (ec, settings) = _getCurrentSettings!.Invoke(collection.Value, modDir, string.Empty);
        if (ec is not PenumbraApiEc.Success || settings is null)
        {
            _log.Debug($"current settings for {modDir}: {ec}");
            return null;
        }
        return settings.Value.Item3;
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

            if (ec is not PenumbraApiEc.Success)
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
        if (ec is not PenumbraApiEc.Success)
            _log.Warning($"priority {modDir} -> {priority}: {ec}");
        return ec is PenumbraApiEc.Success;
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
