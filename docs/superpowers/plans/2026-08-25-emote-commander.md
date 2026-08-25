# Emote Commander Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A Dalamud plugin where a user-defined slash command applies a saved Penumbra preset, redraws, and performs the vanilla emote that preset's mod replaces — so the custom animation plays and sync partners see it.

**Architecture:** Five isolated units — preset store (JSON config), Penumbra bridge (all IPC), emote resolver (pure path→emote mapping over Lumina sheets), command runner (registration + fire sequence), and an ImGui config window. Only the bridge knows Penumbra exists; only the runner knows the game exists. The resolver and store are pure and unit-testable without the game running.

**Tech Stack:** C#, .NET 10, Dalamud API 15 (`Dalamud.NET.Sdk`), Penumbra.Api (IPC subscribers), Lumina Excel sheets via `IDataManager`, xUnit for the pure units.

**Spec:** `docs/superpowers/specs/2026-08-25-emote-commander-design.md`

## Global Constraints

- **.NET 10** — Dalamud 15.0.3.2 targets `net10.0`. The SDK is NOT currently installed on this machine; install before Task 1.
- **No `/cpose`, no pose-index manipulation, ever.** No code path may write pose state. Sit-pose mods are out of scope by design.
- **Never register or intercept a vanilla emote command.** Only user-defined command names.
- **Only fire emotes the character already owns**, through the game's normal emote execution. No locked-emote substitution, no `Timeline.BaseOverride`, no `PlayTimeline`.
- **No temporary Penumbra mods.** Persistent collection settings only, or sync will not carry it.
- **Never change mod priority silently.** The +1 priority bump is per-preset, opt-in, and states what it overrides.
- **No dependency on BypassEmote** in any form.
- Plugin name in code: `EmoteCommander`. Working name — rename before any public release.

---

### Task 1: Scaffold that loads in game

Proves the toolchain end to end before any logic exists. If .NET 10 or the Dalamud SDK is wrong, this is where it surfaces — not at Task 8.

**Files:**
- Create: `EmoteCommander/EmoteCommander.csproj`
- Create: `EmoteCommander/Plugin.cs`
- Create: `EmoteCommander/EmoteCommander.json`
- Create: `EmoteCommander.sln`

**Interfaces:**
- Consumes: nothing
- Produces: `Plugin` class implementing `IDalamudPlugin`, DI-injected `IDalamudPluginInterface`, `ICommandManager`, `IPluginLog`, `IDataManager`, `IClientState`, `IChatGui`

- [x] **Step 1: Install the .NET 10 SDK** — DONE 2026-08-25

Installed **SDK 10.0.400** (runtime 10.0.11) user-local via Microsoft's official
`dotnet-install.ps1`, to keep it out of Program Files and avoid needing admin.

**The SDK is NOT on PATH.** `dotnet` resolves to `C:\Program Files\dotnet\dotnet.exe`,
which has runtimes only and no SDK. Always invoke the full path:

```
C:\Users\noffl\AppData\Local\Microsoft\dotnet\dotnet.exe
```

Or set it for a session: `$env:PATH = "$env:LOCALAPPDATA\Microsoft\dotnet;$env:PATH"`.
Uninstall is deleting `%LOCALAPPDATA%\Microsoft\dotnet`.

- [ ] **Step 2: Write the csproj**

```xml
<Project Sdk="Dalamud.NET.Sdk/13.1.0">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Version>0.1.0.0</Version>
    <Nullable>enable</Nullable>
    <LangVersion>latest</LangVersion>
    <AssemblyName>EmoteCommander</AssemblyName>
    <RootNamespace>EmoteCommander</RootNamespace>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Penumbra.Api" Version="1.6.1" />
  </ItemGroup>
</Project>
```

If `Dalamud.NET.Sdk/13.1.0` is rejected, list available versions and take the highest that resolves — the SDK version tracks Dalamud, not our plugin.

- [ ] **Step 3: Write the manifest**

```json
{
  "Name": "Emote Commander",
  "Author": "Noffletoff",
  "Punchline": "Bind your own slash commands to your animation mods.",
  "Description": "Applies a saved Penumbra preset, redraws, and performs the emote that mod replaces.",
  "ApplicableVersion": "any",
  "Tags": ["penumbra", "emote", "animation"]
}
```

- [ ] **Step 4: Write the minimal plugin**

```csharp
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace EmoteCommander;

public sealed class Plugin : IDalamudPlugin
{
    private readonly IPluginLog _log;

    public Plugin(IDalamudPluginInterface pi, IPluginLog log)
    {
        _log = log;
        _log.Information("Emote Commander loaded.");
    }

    public void Dispose() => _log.Information("Emote Commander unloaded.");
}
```

- [ ] **Step 5: Build**

Run: `dotnet build -c Debug`
Expected: build succeeds, produces `bin/Debug/EmoteCommander.dll`

- [ ] **Step 6: Load it in game**

Copy the build output to `%AppData%\XIVLauncher\devPlugins\EmoteCommander\`, then in game run `/xlplugins` → Dev Tools → installed dev plugins, and enable it.

Expected: `/xllog` shows `Emote Commander loaded.`

- [ ] **Step 7: Commit**

```bash
git init
git add EmoteCommander.sln EmoteCommander/
git commit -m "feat: scaffold plugin that loads in game"
```

---

### Task 2: Emote resolver

Pure logic, no game, no Penumbra. Unit tested. Maps a mod's redirected game paths to the emote command that plays them.

**Files:**
- Create: `EmoteCommander/EmoteResolver.cs`
- Create: `EmoteCommander.Tests/EmoteCommander.Tests.csproj`
- Test: `EmoteCommander.Tests/EmoteResolverTests.cs`

**Interfaces:**
- Consumes: nothing
- Produces: `static string? EmoteResolver.TimelineKeyFromPath(string gamePath)` returning e.g. `"emote/loop_emot24_loop"` or `null`; `static bool EmoteResolver.IsPoseFamily(string timelineKey)`

- [ ] **Step 1: Write the failing tests**

```csharp
using EmoteCommander;
using Xunit;

public class EmoteResolverTests
{
    [Fact]
    public void ExtractsTimelineKeyFromEmotePath()
    {
        var p = "chara/human/c0801/animation/a0001/bt_common/emote/loop_emot24_loop.pap";
        Assert.Equal("emote/loop_emot24_loop", EmoteResolver.TimelineKeyFromPath(p));
    }

    [Fact]
    public void IgnoresNonEmotePaths()
    {
        Assert.Null(EmoteResolver.TimelineKeyFromPath(
            "chara/human/c0801/animation/f0002/nonresident/nf_ohgod.pap"));
        Assert.Null(EmoteResolver.TimelineKeyFromPath(
            "chara/equipment/e0001/model/c0101e0001_top.mdl"));
    }

    [Fact]
    public void IsCaseInsensitiveAndSlashAgnostic()
    {
        var p = @"CHARA\HUMAN\C0801\ANIMATION\A0001\BT_COMMON\EMOTE\POSE01_LOOP.PAP";
        Assert.Equal("emote/pose01_loop", EmoteResolver.TimelineKeyFromPath(p));
    }

    [Theory]
    [InlineData("emote/s_pose03_loop", true)]
    [InlineData("emote/pose01_start", true)]
    [InlineData("emote/j_pose01_loop", true)]
    [InlineData("emote/loop_emot24_loop", false)]
    public void FlagsPoseFamilyEmotes(string key, bool expected)
        => Assert.Equal(expected, EmoteResolver.IsPoseFamily(key));
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test EmoteCommander.Tests`
Expected: FAIL — `EmoteResolver` does not exist

- [ ] **Step 3: Implement**

```csharp
using System.Text.RegularExpressions;

namespace EmoteCommander;

public static partial class EmoteResolver
{
    [GeneratedRegex(@"animation/a\d{4}/bt_common/emote/([a-z0-9_]+)\.pap$",
                    RegexOptions.IgnoreCase)]
    private static partial Regex EmotePath();

    // Pose-family emotes need /cpose to reach a specific pose index. Driving
    // that programmatically is out of scope and unsafe, so they are excluded
    // at the resolver rather than anywhere later.
    [GeneratedRegex(@"^emote/([a-z]_)?pose\d{2}_(start|loop)$", RegexOptions.IgnoreCase)]
    private static partial Regex PoseFamily();

    public static string? TimelineKeyFromPath(string gamePath)
    {
        if (string.IsNullOrWhiteSpace(gamePath)) return null;
        var norm = gamePath.Replace('\\', '/').ToLowerInvariant();
        var m = EmotePath().Match(norm);
        return m.Success ? $"emote/{m.Groups[1].Value}" : null;
    }

    public static bool IsPoseFamily(string timelineKey)
        => !string.IsNullOrEmpty(timelineKey) && PoseFamily().IsMatch(timelineKey);
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test EmoteCommander.Tests`
Expected: PASS, 6 tests

- [ ] **Step 5: Commit**

```bash
git add EmoteCommander/EmoteResolver.cs EmoteCommander.Tests/
git commit -m "feat: map emote pap paths to timeline keys, excluding pose family"
```

---

### Task 3: Emote lookup from Lumina sheets

Turns a timeline key into a real emote the player can perform. Reads the game's own sheets at runtime rather than shipping a table.

**Files:**
- Create: `EmoteCommander/EmoteCatalogue.cs`

**Interfaces:**
- Consumes: `EmoteResolver.TimelineKeyFromPath`
- Produces: `record EmoteEntry(ushort RowId, string Name, string TextCommand, string TimelineKey)`; `EmoteCatalogue(IDataManager data)`; `IReadOnlyList<EmoteEntry> All()`; `EmoteEntry? ByTimelineKey(string key)`; `EmoteEntry? ByRowId(ushort id)`

- [ ] **Step 1: Implement the catalogue**

```csharp
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace EmoteCommander;

public sealed record EmoteEntry(ushort RowId, string Name, string TextCommand, string TimelineKey);

public sealed class EmoteCatalogue
{
    private readonly List<EmoteEntry> _all = new();
    private readonly Dictionary<string, EmoteEntry> _byKey = new(StringComparer.OrdinalIgnoreCase);

    public EmoteCatalogue(IDataManager data, IPluginLog log)
    {
        var emotes = data.GetExcelSheet<Emote>();
        if (emotes is null) { log.Error("Emote sheet unavailable"); return; }

        foreach (var e in emotes)
        {
            var cmd = e.TextCommand.ValueNullable?.Command.ExtractText();
            if (string.IsNullOrWhiteSpace(cmd)) continue;

            // ActionTimeline[0] is the emote's own timeline; its Key is the
            // same string the pap path yields, e.g. "emote/loop_emot24_loop".
            var key = e.ActionTimeline.FirstOrDefault().ValueNullable?.Key.ExtractText();
            if (string.IsNullOrWhiteSpace(key)) continue;

            var name = e.Name.ExtractText();
            var entry = new EmoteEntry((ushort)e.RowId, name, cmd, key);
            _all.Add(entry);
            _byKey.TryAdd(key, entry);
        }
        log.Information($"Emote catalogue: {_all.Count} emotes, {_byKey.Count} timeline keys");
    }

    public IReadOnlyList<EmoteEntry> All() => _all;
    public EmoteEntry? ByTimelineKey(string key) => _byKey.GetValueOrDefault(key);
    public EmoteEntry? ByRowId(ushort id) => _all.FirstOrDefault(e => e.RowId == id);
}
```

- [ ] **Step 2: Wire into Plugin and log the count**

Add to `Plugin`'s constructor:

```csharp
var catalogue = new EmoteCatalogue(data, log);
```

- [ ] **Step 3: Verify in game**

Rebuild, reload the dev plugin, check `/xllog`.
Expected: a line like `Emote catalogue: 200+ emotes, 200+ timeline keys`. If the count is 0, the sheet column names differ in this Lumina version — dump one row's fields and correct before continuing.

- [ ] **Step 4: Commit**

```bash
git add EmoteCommander/EmoteCatalogue.cs EmoteCommander/Plugin.cs
git commit -m "feat: build emote catalogue from Lumina sheets"
```

---

### Task 4: Preset store

Pure config, no game. Unit tested.

**Files:**
- Create: `EmoteCommander/Preset.cs`
- Create: `EmoteCommander/Config.cs`
- Test: `EmoteCommander.Tests/ConfigTests.cs`

**Interfaces:**
- Consumes: nothing
- Produces: `record Preset` with `Command`, `ModDirectory`, `ModName`, `Settings` (`Dictionary<string, List<string>>` group name → selected option names), `EmoteRowId`, `EmoteOverridden`, `RaisePriority`; `Config : IPluginConfiguration` with `List<Preset> Presets`, `bool VerifyAfterFire`, and `Preset? ByCommand(string)`

- [ ] **Step 1: Write the failing tests**

```csharp
using EmoteCommander;
using Xunit;

public class ConfigTests
{
    [Fact]
    public void FindsPresetByCommandCaseInsensitively()
    {
        var c = new Config();
        c.Presets.Add(new Preset { Command = "ohgod" });
        Assert.NotNull(c.ByCommand("OhGod"));
        Assert.Null(c.ByCommand("nope"));
    }

    [Fact]
    public void CommandNamesAreStoredWithoutLeadingSlash()
    {
        Assert.Equal("ohgod", Preset.Normalise("/ohgod"));
        Assert.Equal("ohgod", Preset.Normalise("  OhGod "));
    }

    [Fact]
    public void RejectsEmptyCommandNames()
    {
        Assert.Throws<ArgumentException>(() => Preset.Normalise("  "));
        Assert.Throws<ArgumentException>(() => Preset.Normalise("/"));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test EmoteCommander.Tests`
Expected: FAIL — `Preset` does not exist

- [ ] **Step 3: Implement**

```csharp
using Dalamud.Configuration;

namespace EmoteCommander;

public sealed class Preset
{
    public string Command { get; set; } = "";
    public string ModDirectory { get; set; } = "";
    public string ModName { get; set; } = "";
    public Dictionary<string, List<string>> Settings { get; set; } = new();
    public ushort EmoteRowId { get; set; }
    public bool EmoteOverridden { get; set; }
    public bool RaisePriority { get; set; }

    public static string Normalise(string command)
    {
        var s = (command ?? "").Trim().TrimStart('/').Trim();
        if (s.Length == 0) throw new ArgumentException("command name is empty", nameof(command));
        return s.ToLowerInvariant();
    }
}

public sealed class Config : IPluginConfiguration
{
    public int Version { get; set; } = 1;
    public List<Preset> Presets { get; set; } = new();
    public bool VerifyAfterFire { get; set; } = true;

    public Preset? ByCommand(string command)
        => Presets.FirstOrDefault(p =>
               string.Equals(p.Command, command?.TrimStart('/'), StringComparison.OrdinalIgnoreCase));
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test EmoteCommander.Tests`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add EmoteCommander/Preset.cs EmoteCommander/Config.cs EmoteCommander.Tests/ConfigTests.cs
git commit -m "feat: preset model and plugin config"
```

---

### Task 5: Penumbra bridge — read

Everything that reads from Penumbra. Confirms the IPC signatures before anything depends on them.

**Files:**
- Create: `EmoteCommander/PenumbraBridge.cs`

**Interfaces:**
- Consumes: nothing
- Produces: `PenumbraBridge(IDalamudPluginInterface pi, IPluginLog log)`; `bool Available`; `Dictionary<string,string> ModList()` (directory → name); `Dictionary<string, (string[] Options, GroupType Type)> AvailableSettings(string modDir)`; `Dictionary<string, List<string>> CurrentSettings(string modDir)`; `Dictionary<string,string> PlayerResourcePaths()` (game path → resolved local file)

- [ ] **Step 1: Implement the read side**

```csharp
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Penumbra.Api.IpcSubscribers;

namespace EmoteCommander;

public sealed class PenumbraBridge : IDisposable
{
    private readonly IPluginLog _log;
    private readonly GetModList _getModList;
    private readonly GetAvailableModSettings _getAvailable;
    private readonly GetCurrentModSettings _getCurrent;
    private readonly GetPlayerResourcePaths _getPaths;

    public bool Available { get; private set; }

    public PenumbraBridge(IDalamudPluginInterface pi, IPluginLog log)
    {
        _log = log;
        try
        {
            _getModList  = new GetModList(pi);
            _getAvailable = new GetAvailableModSettings(pi);
            _getCurrent  = new GetCurrentModSettings(pi);
            _getPaths    = new GetPlayerResourcePaths(pi);
            _ = _getModList.Invoke();
            Available = true;
        }
        catch (Exception ex)
        {
            _log.Warning($"Penumbra IPC unavailable: {ex.Message}");
            Available = false;
        }
    }

    public Dictionary<string, string> ModList() => _getModList.Invoke();

    public void Dispose() { }
}
```

- [ ] **Step 2: Log the mod list on load**

In `Plugin`'s constructor, after constructing the bridge:

```csharp
if (bridge.Available)
    log.Information($"Penumbra: {bridge.ModList().Count} mods");
else
    log.Warning("Penumbra not found - Emote Commander needs it.");
```

- [ ] **Step 3: Verify in game**

Rebuild, reload, check `/xllog`.
Expected: `Penumbra: N mods` where N matches your Penumbra mod count.

**If a subscriber name or generic signature does not compile**, open the installed `Penumbra.Api.dll` and correct it — the names confirmed present are `GetModList`, `GetAvailableModSettings`, `GetCurrentModSettings`, `GetCurrentModSettingsWithTemp`, `GetPlayerResourcePaths`, `TrySetModSetting`, `TrySetModSettings`, `TrySetModPriority`, `RedrawObject`, and the `CreatedCharacterBase` event.

- [ ] **Step 4: Commit**

```bash
git add EmoteCommander/PenumbraBridge.cs EmoteCommander/Plugin.cs
git commit -m "feat: penumbra bridge read side"
```

---

### Task 6: Penumbra bridge — apply, redraw, await

The load-bearing sequence. Firing before the redraw completes plays the old cached pap and is indistinguishable from a broken plugin.

**Files:**
- Modify: `EmoteCommander/PenumbraBridge.cs`

**Interfaces:**
- Consumes: Task 5's bridge
- Produces: `bool ApplySettings(string modDir, Dictionary<string, List<string>> settings)`; `Task<bool> RedrawAndAwaitAsync(int timeoutMs = 3000)`

- [ ] **Step 1: Add apply + redraw-await**

```csharp
    private readonly TrySetModSettings _setSettings;
    private readonly RedrawObject _redraw;
    private readonly CreatedCharacterBase _createdBase;
    private TaskCompletionSource<bool>? _redrawTcs;

    // in the constructor, alongside the read subscribers:
    //   _setSettings = new TrySetModSettings(pi);
    //   _redraw      = new RedrawObject(pi);
    //   _createdBase = CreatedCharacterBase.Subscriber(pi, OnCreatedBase);

    private void OnCreatedBase(nint gameObject, string collection, nint drawObject)
        => _redrawTcs?.TrySetResult(true);

    public bool ApplySettings(string modDir, Dictionary<string, List<string>> settings)
    {
        var ok = true;
        foreach (var (group, options) in settings)
        {
            var ec = _setSettings.Invoke(ApiCollection, modDir, string.Empty, group, options);
            if (ec != PenumbraApiEc.Success)
            {
                _log.Warning($"set {modDir}/{group} -> {ec}");
                ok = false;
            }
        }
        return ok;
    }

    public async Task<bool> RedrawAndAwaitAsync(int timeoutMs = 3000)
    {
        _redrawTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _redraw.Invoke(0, RedrawType.Redraw);            // 0 = local player
        var done = await Task.WhenAny(_redrawTcs.Task, Task.Delay(timeoutMs));
        var ok = done == _redrawTcs.Task;
        if (!ok) _log.Warning("redraw did not signal completion within timeout");
        _redrawTcs = null;
        return ok;
    }
```

`ApiCollection` is the collection the player is in; obtain it via `GetCollectionForObject` for object index 0 and cache it per fire.

- [ ] **Step 2: Add a temporary debug command to exercise it**

```csharp
commands.AddHandler("/ecdebug", new CommandInfo((_, args) =>
{
    Task.Run(async () =>
    {
        var ok = await bridge.RedrawAndAwaitAsync();
        chat.Print($"redraw completed: {ok}");
    });
}) { HelpMessage = "Emote Commander: debug redraw" });
```

- [ ] **Step 3: Verify in game**

Run `/ecdebug`.
Expected: character redraws and chat prints `redraw completed: True` promptly. If it prints `False`, `CreatedCharacterBase` is not firing for the local player — log every invocation with its `gameObject` pointer and match against the local player's address before proceeding.

- [ ] **Step 4: Commit**

```bash
git add EmoteCommander/PenumbraBridge.cs EmoteCommander/Plugin.cs
git commit -m "feat: apply settings, redraw, await completion"
```

---

### Task 7: Perform the emote

The other load-bearing piece. It must produce a real, broadcast emote — not a local animation write.

**Files:**
- Create: `EmoteCommander/EmotePlayer.cs`

**Interfaces:**
- Consumes: `EmoteEntry`
- Produces: `EmotePlayer(IPluginLog log)`; `bool Perform(EmoteEntry emote)`

- [ ] **Step 1: Implement using the game's own emote execution**

```csharp
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.UI;

namespace EmoteCommander;

public sealed class EmotePlayer
{
    private readonly IPluginLog _log;
    public EmotePlayer(IPluginLog log) => _log = log;

    public unsafe bool Perform(EmoteEntry emote)
    {
        // Pose-family emotes need /cpose to select an index. Never reachable.
        if (EmoteResolver.IsPoseFamily(emote.TimelineKey))
        {
            _log.Warning($"refusing pose-family emote {emote.TimelineKey}");
            return false;
        }

        var agent = AgentEmote.Instance();
        if (agent == null) { _log.Error("AgentEmote unavailable"); return false; }

        agent->ExecuteEmote(emote.RowId);
        return true;
    }
}
```

- [ ] **Step 2: Extend the debug command to perform an emote**

```csharp
// /ecdebug <emote row id>
var entry = catalogue.ByRowId(ushort.Parse(args.Trim()));
if (entry is not null) player.Perform(entry);
```

- [ ] **Step 3: Verify it broadcasts**

Run `/ecdebug <id>` for an owned, non-pose emote.
Expected: your character performs it. **Then confirm with another player or an alt that they see it** — this is the one thing that cannot be verified alone, and the whole design depends on it.

- [ ] **Step 4: If `ExecuteEmote` does not broadcast, fall back to the chat route**

PuppetMaster (installed locally) performs emotes by sending the command through
chat — `ChatHandler.DoCommandAsync` / `SendMessage`. GagSpeak does the same.
That route is proven to broadcast, since remote-triggered emotes being visible to
others is the entire point of both plugins.

Use the `motion` suffix so firing a command does not print an emote line in chat
every time:

```
/wringhands motion
```

`EmoteEntry.TextCommand` already holds the command; append `" motion"`. Chat
injection is the more heavily scrutinised of the two approaches — GagSpeak's own
documentation notes it implements "many conditions to maximize the safety of this
usage" — so prefer `ExecuteEmote` and only fall back if it fails to broadcast.

- [ ] **Step 5: Adopt the motion suffix regardless**

Whichever route is used, suppress the chat line. Firing a command should play an
animation, not announce it.

- [ ] **Step 4: Commit**

```bash
git add EmoteCommander/EmotePlayer.cs EmoteCommander/Plugin.cs
git commit -m "feat: perform emotes via the game's own execution"
```

---

### Task 8: Command runner

**Files:**
- Create: `EmoteCommander/CommandRunner.cs`

**Interfaces:**
- Consumes: `Config`, `PenumbraBridge`, `EmoteCatalogue`, `EmotePlayer`
- Produces: `CommandRunner(...)`; `void Register(Preset p)`; `void Unregister(string command)`; `void RegisterAll()`; `Task FireAsync(Preset p)`

- [ ] **Step 1: Implement registration and the fire sequence**

```csharp
public async Task FireAsync(Preset p)
{
    var emote = _catalogue.ByRowId(p.EmoteRowId);
    if (emote is null) { _chat.PrintError($"[EC] {p.Command}: emote not found"); return; }

    if (!_bridge.ApplySettings(p.ModDirectory, p.Settings))
        _chat.PrintError($"[EC] {p.Command}: some settings failed to apply");

    if (!await _bridge.RedrawAndAwaitAsync())
        _chat.PrintError($"[EC] {p.Command}: redraw timed out, playing anyway");

    _player.Perform(emote);
}
```

Registration refuses a command name that already exists in Dalamud, so a vanilla emote command can never be captured:

```csharp
public void Register(Preset p)
{
    var name = "/" + Preset.Normalise(p.Command);
    if (!_commands.AddHandler(name, new CommandInfo((_, _) => _ = FireAsync(p))
        { HelpMessage = $"Emote Commander: {p.ModName}", ShowInHelp = true }))
        _log.Warning($"command {name} already taken - not registered");
    else
        _registered.Add(name);
}
```

- [ ] **Step 2: Verify in game**

Hand-write one preset into the config JSON, reload, run its command.
Expected: options apply, character redraws, emote plays, custom animation shows.

- [ ] **Step 3: Commit**

```bash
git add EmoteCommander/CommandRunner.cs EmoteCommander/Plugin.cs
git commit -m "feat: command registration and fire sequence"
```

---

### Task 9: Conflict detection and verification

Turns "why is the wrong animation playing" into a one-line answer.

**Files:**
- Modify: `EmoteCommander/PenumbraBridge.cs`
- Modify: `EmoteCommander/CommandRunner.cs`

**Interfaces:**
- Consumes: `PlayerResourcePaths()`
- Produces: `string? WhoOwns(string gamePath)` returning the resolved local file path or null; `bool RaisePriorityAbove(string modDir, int target)`

- [ ] **Step 1: Add post-fire verification**

After `Perform`, when `Config.VerifyAfterFire` is set:

```csharp
var expectedGamePath = p.EmotePapPath;          // stored when the preset was saved
var resolved = _bridge.WhoOwns(expectedGamePath);
if (resolved is not null && !resolved.Contains(p.ModDirectory, StringComparison.OrdinalIgnoreCase))
    _chat.PrintError($"[EC] {p.Command}: another mod is overriding this emote " +
                     $"(resolved to {Path.GetFileName(resolved)}). " +
                     $"Enable 'raise priority' on this preset, or disable the conflicting mod.");
```

- [ ] **Step 2: Implement the opt-in priority bump**

Only when `p.RaisePriority` is set, and only before the redraw:

```csharp
if (p.RaisePriority)
    _bridge.RaisePriorityAbove(p.ModDirectory, conflictingPriority + 1);
```

- [ ] **Step 3: Verify in game**

Enable two mods claiming the same emote, fire the preset for the lower-priority one.
Expected: the override message names the winning file. Enable `raise priority`, fire again, expect the correct animation and no message.

- [ ] **Step 4: Commit**

```bash
git add EmoteCommander/PenumbraBridge.cs EmoteCommander/CommandRunner.cs
git commit -m "feat: detect and report emote path conflicts, optional priority bump"
```

---

### Task 10: Config UI

**Files:**
- Create: `EmoteCommander/Ui/ConfigWindow.cs`
- Modify: `EmoteCommander/Plugin.cs`

**Interfaces:**
- Consumes: everything above
- Produces: `/emotecommander` opens the window

- [ ] **Step 1: Build the window**

Layout, top to bottom:
1. **Mod dropdown** — `bridge.ModList()`, searchable
2. **Option groups** for the selected mod — `AvailableSettings(modDir)`, combo per single-select group, checkboxes per multi-select group, pre-filled from `CurrentSettings`
3. **Emote** — auto-filled from the mod's redirects via `EmoteResolver` + `EmoteCatalogue`, shown as a dropdown of all emotes so it can be overridden; sets `EmoteOverridden` when changed by hand
4. **Command name** field, with live validation (not empty, not already taken)
5. **Raise priority** checkbox, with the conflicting mod named beside it when one exists
6. **Save** / **Delete**, and a list of existing presets

- [ ] **Step 2: Verify in game**

Create a preset entirely through the UI, save, run its command.
Expected: works without touching the config file by hand. Pose-family emotes are absent from the dropdown.

- [ ] **Step 3: Commit**

```bash
git add EmoteCommander/Ui/ConfigWindow.cs EmoteCommander/Plugin.cs
git commit -m "feat: config window for building presets"
```

---

---

### Task 11: Share codes

Pure logic, no game. A preset must survive a round trip through a string that
can be pasted into a Discord message or a Penumbra description.

**Files:**
- Create: `EmoteCommander/ShareCode.cs`
- Test: `EmoteCommander.Tests/ShareCodeTests.cs`
- Modify: `EmoteCommander.Tests/EmoteCommander.Tests.csproj` (link `ShareCode.cs`)

**Interfaces:**
- Consumes: `Preset`
- Produces: `static string ShareCode.Encode(IEnumerable<Preset>)`; `static IReadOnlyList<Preset> ShareCode.Decode(string)`; `static IReadOnlyList<string> ShareCode.ExtractFromText(string)`

Format: `[EC1]<base64 of gzipped utf8 json>[/EC1]`. The version digit is in the
marker so a later format can be recognised and refused cleanly rather than
throwing.

- [ ] **Step 1: Write the failing tests**

```csharp
using EmoteCommander;
using Xunit;

namespace EmoteCommander.Tests;

public class ShareCodeTests
{
    private static Preset Sample() => new()
    {
        Command = "ohgod",
        ModDirectory = "noff_smoking_idle",
        ModName = "Noff Smoking Idle",
        EmoteRowId = 42,
        EmotePapPath = "chara/human/c0801/animation/a0001/bt_common/emote/loop_emot24_loop.pap",
        RaisePriority = true,
        Settings = { ["Face Pap"] = new() { "FMiqo Face Pap" }, ["Bulge"] = new() { "On" } },
    };

    [Fact]
    public void RoundTripsAPreset()
    {
        var back = ShareCode.Decode(ShareCode.Encode(new[] { Sample() }));
        var p = Assert.Single(back);
        Assert.Equal("ohgod", p.Command);
        Assert.Equal("noff_smoking_idle", p.ModDirectory);
        Assert.Equal((ushort)42, p.EmoteRowId);
        Assert.True(p.RaisePriority);
        Assert.Equal(new[] { "FMiqo Face Pap" }, p.Settings["Face Pap"]);
        Assert.Equal(new[] { "On" }, p.Settings["Bulge"]);
    }

    [Fact]
    public void RoundTripsSeveralPresets()
    {
        var two = new[] { Sample(), new Preset { Command = "moan", ModDirectory = "x" } };
        Assert.Equal(2, ShareCode.Decode(ShareCode.Encode(two)).Count);
    }

    [Fact]
    public void EncodedCodeIsWrappedInVersionedMarkers()
    {
        var code = ShareCode.Encode(new[] { Sample() });
        Assert.StartsWith("[EC1]", code);
        Assert.EndsWith("[/EC1]", code);
    }

    [Fact]
    public void ExtractsCodesEmbeddedInProse()
    {
        var text = "Cool mod.\n\nCommands:\n" + ShareCode.Encode(new[] { Sample() })
                 + "\n\nDisable to revert.";
        var found = ShareCode.ExtractFromText(text);
        Assert.Single(found);
        Assert.Single(ShareCode.Decode(found[0]));
    }

    [Fact]
    public void ExtractsNothingFromTextWithoutCodes()
    {
        Assert.Empty(ShareCode.ExtractFromText("just a normal description"));
        Assert.Empty(ShareCode.ExtractFromText(""));
    }

    [Theory]
    [InlineData("")]
    [InlineData("nonsense")]
    [InlineData("[EC1]not-base64[/EC1]")]
    [InlineData("[EC9]cGF5bG9hZA==[/EC9]")]   // unknown version
    public void DecodeThrowsFormatExceptionOnRubbish(string code)
        => Assert.Throws<FormatException>(() => ShareCode.Decode(code));
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `& "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" test`
Expected: FAIL — `ShareCode` does not exist

- [ ] **Step 3: Implement `ShareCode.cs`**

Explicit usings (the file is linked into the test project). Encode: serialise
the list to JSON, gzip, base64, wrap in `[EC1]...[/EC1]`. Decode: match the
marker, reject an unknown version with `FormatException`, base64-decode,
gunzip, deserialise; wrap any parsing failure in `FormatException` so callers
have exactly one thing to catch. `ExtractFromText`: regex out every
`\[EC(\d+)\](.*?)\[/EC\1\]` occurrence, non-greedy, `Singleline`.

- [ ] **Step 4: Run tests to verify they pass**

Expected: all green, including the earlier 31.

- [ ] **Step 5: Commit**

```bash
git add EmoteCommander/ShareCode.cs EmoteCommander.Tests/ShareCodeTests.cs EmoteCommander.Tests/EmoteCommander.Tests.csproj
git commit -m "feat: share codes for exporting and importing presets"
```

---

### Task 12: Discover presets in mod descriptions

**Files:**
- Modify: `EmoteCommander/PenumbraBridge.cs` (read a mod's description)
- Create: `EmoteCommander/PresetImporter.cs`
- Modify: `EmoteCommander/Ui/ConfigWindow.cs` (import review panel)

**Interfaces:**
- Consumes: `ShareCode.ExtractFromText`, `PenumbraBridge.ModList`, `EmoteResolver.IsPoseFamily`
- Produces: `record DiscoveredPreset(Preset Preset, string SourceModName, bool CommandTaken, string? Problem)`; `IReadOnlyList<DiscoveredPreset> PresetImporter.Scan()`

- [ ] **Step 1: Read mod descriptions**

`GetModList` gives directory → name. Read `meta.json` from
`<GetModDirectory()>/<modDir>/meta.json` and take its `Description` field.
Missing or unreadable `meta.json` is normal for some mods — skip silently, do
not log per mod or it will spam.

- [ ] **Step 2: Implement the scan**

For each mod: extract codes from its description, decode, and for each preset
attach the source mod name and any problem — command name already taken, mod
directory does not match the mod the code was found in, referenced option group
no longer exists, or the emote is pose-family. Presets with a problem are still
returned so the UI can explain rather than silently drop them.

- [ ] **Step 3: Import review panel**

A list of discovered presets, each with source mod, proposed command (editable
inline), and its problem if any. Individual checkboxes, an Add Selected button,
and nothing registered until pressed. Presets with a blocking problem cannot be
ticked.

- [ ] **Step 4: Verify in game**

Paste a code into one of your mods' Penumbra descriptions, reopen the window,
scan.
Expected: the preset appears naming that mod, with a sensible command, and adds
correctly. Then edit the description to a mangled code and confirm it is
reported rather than crashing the scan.

- [ ] **Step 5: Commit**

```bash
git add EmoteCommander/PresetImporter.cs EmoteCommander/PenumbraBridge.cs EmoteCommander/Ui/ConfigWindow.cs
git commit -m "feat: discover presets embedded in mod descriptions"
```

---

## Self-review notes

- **Spec coverage:** preset store (T4), Penumbra bridge (T5/T6/T9), emote resolver (T2/T3), command runner (T8), UI (T10), conflicts (T9), both spikes front-loaded (T6 redraw-await, T7 broadcast).
- **Constraint coverage:** pose-family excluded in T2 and re-checked in T7; vanilla commands protected by `AddHandler` returning false in T8; no temporary-mod IPC used anywhere; priority bump gated behind `RaisePriority`.
- **Known soft spot:** exact Penumbra IPC generic signatures and the Lumina `Emote` sheet column names are confirmed by *name* from the installed DLLs but not by full signature. T3 Step 3 and T5 Step 3 exist specifically to catch that early, and both say what to do if it does not compile.
- **Task 7 Step 3 is the real gate.** If `ExecuteEmote` does not broadcast, tasks 8–10 are wasted work. Do not skip the second-player check.
