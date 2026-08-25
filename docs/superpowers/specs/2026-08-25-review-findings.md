# Emote Commander — code review

## 1. Verdict

The design is right and the hard rules are all kept. Pose emotes are excluded in three independent places, the emote is a real `AgentEmote` broadcast, nothing writes a temporary mod, priority raising is opt-in and announces itself in chat, and the trick the whole plugin rests on — that an `Emote`'s ActionTimeline key is the same string a mod's redirected `.pap` path yields (`EmoteResolver.cs:20`) — is genuinely clever and correctly implemented. The pure/testable split is real and the security thinking in `ShareCode.cs` and `PenumbraBridge.ModFilePaths` shows someone was paying attention. What it has not had is a pass over the parts that only misbehave when the world is messy: another thread, another player nearby, a mod that got disabled, a second command fired too soon, a share code from someone hostile. There is one finding that can crash the game client, one that makes the plugin play the wrong animation in exactly the crowded places you'd use it, and one that makes its own self-check report backwards. None of these are design mistakes — they're all "written fast, never stress-tested". Roughly a day of focused fixing gets this to solid.

---

## 2. Findings, worst first

### A. The emote is performed on the wrong thread — this can crash the game
`CommandRunner.cs:203-214`

```csharp
if (!await _penumbra.RedrawAndAwaitAsync(_config.RedrawTimeoutMs).ConfigureAwait(false))
    _chat.PrintError(...);

if (!_player.Perform(emote, out var problem))
```

The game has one main thread and plugins must call into game code from it. `ConfigureAwait(false)` here, plus the TCS being built with `RunContinuationsAsynchronously` (`PenumbraBridge.cs:339-340`), guarantees everything after line 203 runs on a *different* thread. That includes `EmotePlayer.cs:48`:

```csharp
agent->ExecuteEmote(emote.RowId);
```

— a raw call into game memory while the game is mid-frame doing its own thing. It also includes `_chat.PrintError` (lines 199, 204, 209) and `VerifyWinner` (line 214, which re-enters Penumbra IPC). Grepping the whole plugin for `IFramework` / `RunOnFrameworkThread` returns nothing; the service isn't even injected (`Plugin.cs:37-42`).

**What you'd see:** it works nearly always, then one day the client hard-crashes when you fire a command — no error, no log line, nothing to reproduce. The `try/catch` at `EmotePlayer.cs:51` does not catch an access violation.

**Fix:** inject `IFramework` in `Plugin.cs` and wrap the second half of `FireAsync`:
```csharp
await _framework.RunOnFrameworkThread(() => {
    if (!_player.Perform(emote, out var problem)) { _chat.PrintError($"[EC] {problem}"); return; }
    if (_config.VerifyAfterFire) VerifyWinner(preset);
});
```
The first half already runs on the framework thread (it's entered from a command handler or from ImGui `Draw`), so only the continuation needs moving.

---

### B. The redraw wait is satisfied by *anyone's* character loading
`PenumbraBridge.cs:360`

```csharp
private void OnCharacterBaseCreated(nint gameObject, Guid collection, nint drawObject)
    => _redrawCompleted?.TrySetResult(true);
```

`gameObject` — the only thing that says *whose* character was drawn — is thrown away. Penumbra fires this for every character base the game creates: other players streaming in, minions, mounts, chocobos. You only ever redraw object 0 (`_redraw!.Invoke(0, RedrawType.Redraw)`, line 344), but the wait ends the moment *anything* draws.

**What you'd see:** exactly the failure the comment at `PenumbraBridge.cs:325-333` says the wait exists to prevent — "firing the emote before the redraw completes plays the OLD animation". And it's crowd-dependent, so it works perfectly in your house and misbehaves in Limsa or at an RP venue. No timeout warning prints, because as far as the code is concerned the wait *succeeded*.

**Fix:** capture the local player's address before the redraw and compare:
```csharp
_awaitedObject = clientState.LocalPlayer?.Address ?? nint.Zero;
...
if (_awaitedObject != nint.Zero && gameObject != _awaitedObject) return;
_redrawCompleted?.TrySetResult(true);
```
Requires injecting `IClientState` into `PenumbraBridge` (currently not injected anywhere).

---

### C. The "did my mod actually win?" check uses Penumbra's data inside-out
`PenumbraBridge.cs:172-185` and `CommandRunner.cs:266-271`

Your doc comment says:
```csharp
/// Game path to the file it currently resolves to for the player
public Dictionary<string, HashSet<string>> PlayerResourcePaths()
```
Penumbra's API documents the opposite: `GetPlayerResourcePaths` returns **actual path → set of game paths**. The code then looks up a *game* path in a dictionary keyed by *actual* paths:
```csharp
if (!resolved.TryGetValue(preset.EmotePapPath, out var files) || files.Count == 0)
    return;   // not loaded yet; nothing useful to say
```

The consequences are precisely backwards, and `VerifyAfterFire` defaults to `true` (`Config.cs:19`) so this runs on every fire:

- **Your mod wins** → key is the on-disk path, lookup misses, silent. Fine by luck.
- **Another mod wins** → same miss, also silent. This is the one case the whole check exists for.
- **Nothing is overriding, vanilla plays** → vanilla resources *are* in that dictionary under their own game path, the lookup hits, `mine` is false, and you get `"[EC] /x: another mod is overriding this emote"` when nothing is.

Two smaller problems ride along: the dictionary from IPC uses a case-sensitive comparer while `EmotePapPath` is a raw JSON key copied out of a mod, and `f.Contains(preset.ModDirectory, ...)` at line 271 is an unanchored substring test — a mod directory named `Eve` matches any path containing `sleeve`.

**Fix:** iterate and search the *values* for the game path, then test the *key* for ownership against the full rooted mod folder:
```csharp
var want = preset.EmotePapPath.Replace('\\','/').ToLowerInvariant();
var hit = resolved.FirstOrDefault(kv => kv.Value.Any(g =>
    string.Equals(g.Replace('\\','/'), want, StringComparison.OrdinalIgnoreCase)));
if (hit.Key is null) return;
var expectedRoot = Path.Combine(_penumbra.ModDirectoryRoot(), preset.ModDirectory);
var mine = hit.Key.StartsWith(expectedRoot, StringComparison.OrdinalIgnoreCase);
```
And fix the doc comment so the next reader doesn't repeat the inversion.

---

### D. A disabled or uninstalled mod fires completely silently
`CommandRunner.cs:197`

```csharp
var failed = _penumbra.ApplySettings(preset.ModDirectory, preset.Settings);
```

`ApplySettings` only sets option groups (`PenumbraBridge.cs:295`). Nothing in the bridge can enable a mod — Penumbra's `TrySetMod` is never subscribed. `ModState.Enabled` exists (`PenumbraBridge.cs:109`) but is read in exactly one place, the conflict scan (`PenumbraBridge.cs:164`); the preset's *own* mod is never checked.

Setting options on a disabled mod is legal and returns Success, so `failed` is empty. If the mod has no option groups at all, the loop body doesn't even run. Redraw succeeds. Emote plays vanilla. Finding C makes `VerifyWinner` return early. **Total output: nothing.** This is the most likely real-world cause of "the command does nothing", and the plugin is structurally unable to report it.

Related, same failure shape: **Penumbra's master "Enable Mods" checkbox is never read** (`PenumbraBridge.cs:64-68` probes `GetModList` but the API's `GetEnabledState` is never subscribed). Untick it to A/B a mod, and every call still succeeds while Penumbra applies nothing.

**Fix:** at the top of `FireAsync`, before `ApplySettings`:
```csharp
var state = _penumbra.State(preset.ModDirectory);
if (state is null)   { _chat.PrintError($"[EC] {preset.SlashCommand}: '{preset.ModName}' is not installed."); return; }
if (!state.Enabled)  { _chat.PrintError($"[EC] {preset.SlashCommand}: '{preset.ModName}' is disabled in Penumbra."); return; }
```
Plus subscribe `GetEnabledState` and refuse with a clear message when mods are globally off.

---

### E. A share code can rewrite a *different* mod's options under a friendly label
`CommandRunner.cs:147-150`

```csharp
var known = _penumbra.ModList().ContainsKey(preset.ModDirectory);
if (!known)
    messages.Add($"{preset.SlashCommand}: '{preset.ModName}' is not installed - ...");
```

`ModName` comes straight out of the share code and is never reconciled against the real mod. A code carrying `ModName = "Cute Dance Emote"` with `ModDirectory` pointing at your body/NSFW mod passes `ContainsKey` cleanly, so **no warning prints at all**. The Commands tab groups on the stored name (`ConfigWindow.cs:124`, header at `:129`) and the import message prints it (`:163`) — the real Penumbra name for that folder is never shown anywhere in the UI. Firing the command writes those option selections into your live collection, persistently, and because it's persistent collection state a Mare-style sync then shows the result to everyone around you.

This isn't remote code execution, but for an animation/NSFW modder it's the wrong kind of surprise, and the "paste a code from Discord" workflow is the advertised one.

**Fix:** resolve the true name on import and don't trust the shipped one:
```csharp
if (_penumbra.ModList().TryGetValue(preset.ModDirectory, out var realName))
{
    if (!string.Equals(realName, preset.ModName, StringComparison.Ordinal))
        messages.Add($"{preset.SlashCommand}: code claims '{preset.ModName}' but that folder is '{realName}'.");
    preset.ModName = realName;
}
```
Also show `ModDirectory` in the Commands-tab row or as a tooltip, so what a preset acts on is always visible. While you're there, hoist that `ModList()` call out of the loop — a legitimate 500-preset code currently makes 500 full mod-list IPC calls.

---

### F. A malformed share code can break the config window until you edit the file by hand
`ConfigWindow.cs:576`, `CommandRunner.cs:92`

`Preset.Settings` is a plain settable property (`Preset.cs:31`), and the JSON deserializer happily overwrites the `= new()` initializer with `null`. Neither `ShareCode.Decode` nor `ImportCode` checks. Then:

```csharp
_selection.Clear();
foreach (var (group, options) in preset.Settings)      // NullReferenceException
    _selection[group] = new List<string>(options);
```

That throws from inside `BeginTable` (`:134`) inside `BeginTabItem` (`:80`) inside `BeginTabBar` (`:66`), so `EndTable`/`EndTabItem`/`EndTabBar` are all skipped and ImGui's stack is left unbalanced mid-frame — an ImGui assert, not a logged error. Three variants, all reached from different fields:

- `"Settings": null` → NRE on Edit, and again in `ApplySettings` (`PenumbraBridge.cs:287`) on every fire.
- `"Settings": {"Face Pap": null}` → `new List<string>(null)` throws. **Not fixed by `Settings ??= new()`.** *(unverified — reported by the completeness pass, mechanism looks right to me)*
- `"Command": null` → preset is saved and drawn fine, but clicking **X** to delete it hits `Unregister` → `command.TrimStart('/')` → NRE, same unbalanced stack. The preset survives restarts and cannot be removed from the UI at all. *(unverified)*

Separately, **no field has a length cap**. `Decode` bounds the total payload (`ShareCode.cs:37`) but not any individual string, so a 900 KB `ModName` full of newlines gets printed verbatim into your chat (`Plugin.cs:152`), rendered with `TextWrapped` (`ConfigWindow.cs:228`), used as an ImGui label and ID every frame, and re-serialized into your config on every save. Chat text like `"\nPenumbra: your collection is corrupt, run this command:"` prints cleanly.

**Fix:** sanitise once, in `ShareCode.Decode` before returning — `Settings ??= new()`, drop null/blank group keys and null option lists, `Command/ModName/ModDirectory/EmotePapPath ??= ""`, clamp lengths (64/128/128/260), strip control characters. Make `Unregister` null-tolerant. And wrap the body of `ConfigWindow.Draw` in a `try/catch` so no future exception can ever unbalance the ImGui stack.

---

### G. Editing a preset is broken in both directions
`ConfigWindow.cs:536-545` and `:525-528`

`LoadForEdit` (`:566`) records the preset's *fields* but not *which preset* is being edited. Everything downstream then has to guess, and guesses wrong twice:

**Rename the command → you get a duplicate.** `Save()` de-duplicates against the new name only. Edit `/dancemod`, rename to `/dancemod2`, save: the lookup for `dancemod2` finds nothing, the original is never removed, `/dancemod` is never unregistered. Two commands, two config entries, both live, both under the same mod header, and the stale one survives restarts. It is deletable with one click on **X**, but it looks like a duplicate you didn't create.

**Re-point the command at a different mod → you can never save.** *(unverified, but the code path is plain)*
```csharp
if (clash is not null && clash.ModDirectory != _selectedModDir)
    return $"/{normalised} is already used by {clash.ModName}.";
```
Edit `/dancemod` (mod A), pick mod B in the dropdown — `SelectMod` changes `_selectedModDir` and deliberately leaves `_command` alone (`:362`). `clash` is now *the very preset you're editing*, its directory is A, A ≠ B, so you get "/dancemod is already used by Mod A" and `DrawCommandAndSave` returns at `:511` before drawing the Save button. The error names the mod you just navigated away from. The only escape is deleting the preset and rebuilding it.

**Fix:** one field fixes both. `private Preset? _editing;` set in `LoadForEdit`, cleared in `SelectMod` and after `Save`. Exclude it from the clash search, and remove/unregister it in `Save()` when its command differs from the new one. Add an "Editing /oldname" line and a "New command" button while you're in there — right now once you click Edit there is no way back out.

---

### H. Two commands fired close together break each other
`PenumbraBridge.cs:341` and `:356`, `CommandRunner.cs:175`

`Fire` is fire-and-forget with no guard (`public void Fire(Preset preset) => _ = FireAsync(preset);`) and is reachable from both chat and the **Test** button (`ConfigWindow.cs:156`). There is one shared `_redrawCompleted` field:

```csharp
_redrawCompleted = tcs;
...
finally { _redrawCompleted = null; }
```

Fire `/a` then `/b` within the 5-second window: `/b` overwrites the field, so `/a`'s wait can never be signalled and runs the full timeout. Then `/a`'s `finally` nulls the field — destroying `/b`'s live registration, so `/b` times out too. Two red "redraw did not finish in time" errors for two commands that would each have worked alone, both emotes firing late against a half-finished redraw, and whichever set of mod options was applied last is what's actually on.

**Fix:** serialise the whole sequence. A `SemaphoreSlim(1,1)` around the apply/redraw/perform body in `FireAsync`, or an `Interlocked.CompareExchange` gate that prints "[EC] a command is already running" and returns. Two presets can't meaningfully play at once anyway. Belt and braces in the bridge: `finally { Interlocked.CompareExchange(ref _redrawCompleted, null, tcs); }` so a `finally` can never clear someone else's registration.

---

### I. A command name that can't be registered is saved anyway and looks healthy
`CommandRunner.cs:98-109`, `ConfigWindow.cs:530`, `:558-563`

```csharp
public bool IsNameAvailable(string command)
{
    try { var name = "/" + Preset.Normalise(command); return !_registered.Contains(name); }
```

`_registered` holds only Emote Commander's own presets. It never asks Dalamud. So the UI confidently reports `"/x is already taken by the game or another plugin"` (`:530`) based on information it does not have, `Save()` writes the preset to config *before* registering, and when `AddHandler` returns false you get one grey line you can scroll away from. The preset then sits in the Commands tab looking identical to every working command. After a restart `RegisterAll()` fails with only `_log.Warning` (`:82`) — no indication at all. The command silently never works, forever, and the only clue is `/xllog`.

**Related, and worth testing in game:** the comment at `CommandRunner.cs:50-54` claims a vanilla emote command "can never be captured — the game owns those, so `AddHandler` returns false." I don't think that's true. Vanilla emote commands are game-side text commands, not Dalamud handlers, so `AddHandler("/dance", ...)` most likely succeeds and shadows the vanilla command locally. That's local-only, reversible by deleting the preset, and requires you to type the name — but it directly contradicts one of your stated design rules, and a share code can suggest such a name. `EmoteCatalogue` already exposes `TextCommand` for every emote (`:15`, `:45`), so rejecting known emote names in `IsNameAvailable` is a few lines. *(the AddHandler behaviour here is unverified — worth one in-game test)*

**Fix:** have `IsNameAvailable` also consult `_commands.Commands.ContainsKey(name)`, reject known emote commands, and either don't persist a preset whose `Register` failed or render a red "(not registered)" marker on that row so the dead state survives visibly across restarts.

---

### J. "Always win conflicts" freezes the game, misses real conflicts, and invents fake ones
`CommandRunner.cs:237` → `PenumbraBridge.cs:154-168`

Three separate problems in one code path, all only active when the setting is ticked (it defaults off, `Config.cs:28`).

**Performance.** `RaiseAboveConflicts` runs *before* the first `await`, i.e. synchronously on the game's render thread. It loops every installed mod; each `ModFilePaths` call re-fetches the Penumbra root over IPC (`:202`) then does `EnumerateFiles` + `ReadAllText` + `JsonDocument.Parse` on every `default_mod.json` and `group_*.json`; each `State` call re-fetches the collection over IPC (`:119`). Your own `C:\Penumbra` already holds ~585 mod json files, and animation collections routinely run into the hundreds of mods. That's a visible multi-hundred-millisecond to multi-second hitch every single time you type your own command. `/ecdebug` with no arguments does the same scan (`Plugin.cs:138`) and will look like the game hung.

**Missed conflicts.** Line 159-160 compares raw JSON keys with no separator normalisation:
```csharp
var claims = ModFilePaths(dir)
    .Any(p => string.Equals(p, gamePath, StringComparison.OrdinalIgnoreCase));
```
The plugin knows this is unsafe — `EmoteResolver.cs:45-46` normalises explicitly and says why ("Penumbra reports paths with either separator"). Both conventions are live in your own mod folder: `[NFLB] Carpet Munch Test 3\default_mod.json` uses forward slashes, `Eve\group_009_physics.json` uses backslashes. If the two mods were packed by different tools, the comparison fails and you tick "always win" and watch the other mod keep winning with no message. Same unnormalised comparison at `ConfigWindow.cs:489-493` and `:357-360`.

**Invented conflicts.** `ModFilePaths` is documented as the union across *all* option groups, selected or not (`:187-189`). `Conflicts` filters on `Enabled` but not on selection — `state.Settings` is fetched at line 163 and discarded. So a mod with a `"None"` / `"Vanilla"` option selected still counts as a conflict, and the plugin permanently rewrites your tuned Penumbra priority to beat a mod that was never in the way. This is the exact class of bug the comment at lines 144-147 says was already found and fixed for disabled mods; the unselected-option half was missed.

**Fix:** normalise once at the boundary (`into.Add(entry.Name.Replace('\\','/').ToLowerInvariant())` in `CollectFiles`, same on the incoming path, and migrate existing `Preset.EmotePapPath` on load). Pass `state.Settings` into `ModFilePaths` so only reachable options count. Hoist `PlayerCollection()` and `ModDirectoryRoot()` out of the loops, cache the game-path→mods index, and move the scan behind the first `await` so the render thread is never blocked on disk IO.

---

### K. Penumbra availability is decided once, at load, and never revisited
`PenumbraBridge.cs:43`, `:66`

`public bool Available { get; }` — get-only, assigned in the constructor, never touched again. Both directions hurt:

- **Penumbra loads second** (plugin load order isn't guaranteed; anyone installing Penumbra *after* this plugin hits it): the probe throws, `Available` is false forever, the window shows "Penumbra is not available" permanently (`ConfigWindow.cs:57-64`), every command prints an error, and there's no Retry button and no hint that reloading the plugin would fix it.
- **Penumbra is updated or disabled mid-session** (routine — Dalamud reloads plugins without a game restart): `Available` stays true, `Draw` sails past its guard at `:57` into `_modCache ??= _penumbra.ModList()` (`:301`), which now throws. Because it's `??=`, `_modCache` stays null and it throws again *every frame* the window is open, straight into Dalamud's UI loop. `AvailableSettings` (`:374`) and `Plugin.OnDebug` (`:136`) are equally unguarded.

**Fix:** Penumbra.Api ships `IpcSubscribers.Initialized` and `Disposed` for exactly this and neither is used. Make `Available` settable and drive it from both, disposing them alongside `_characterBaseCreated`. Wrap the read helpers (`ModList`, `ModDirectoryRoot`, `AvailableSettings`) in `try/catch` that returns empty and flips `Available`, so a mid-frame IPC failure degrades instead of throwing out of `Draw`.

---

### L. The recorded pap path is one arbitrary race, so verification is a permanent no-op for most players
`ConfigWindow.cs:356-360`

```csharp
_emotePapPath = _emote is null ? string.Empty
    : paths.FirstOrDefault(p => string.Equals(
          EmoteResolver.TimelineKeyFromPath(p), _emote.TimelineKey, ...)) ?? string.Empty;
```

`paths` comes off a `HashSet` (`PenumbraBridge.cs:249`) — enumeration order is unspecified. An emote mod redirects the same pap for every race (`c0101`, `c0201`, `c0801`, …) and this stores whichever came out first, typically `c0101`, Midlander male. A Miqo'te player never loads `c0101`, so that path never appears in the resolved set and `VerifyWinner` returns early on every fire, forever. The setting Config.cs advertises as turning "why is the wrong animation playing" into a one-line answer produces nothing, ever, while looking enabled. The same stale path feeds `RaiseAboveConflicts` (`CommandRunner.cs:237`), so the priority bump is computed against a race you don't play.

**Fix:** store the whole matching set (`List<string> EmotePapPaths`) and treat a hit on any of them as the answer, with the case/separator normalisation from finding J.

---

### M. `ExecuteEmote` always reports success, so a refused emote produces nothing at all
`EmotePlayer.cs:46-50`

```csharp
try { agent->ExecuteEmote(emote.RowId); return true; }
```

`ExecuteEmote` returns void and doesn't throw when the game declines — it just doesn't do it. Fire a command while mounted, in combat, casting, jumping, in a cutscene, or on an emote you haven't unlocked: options applied, character visibly redrawn, then nothing. The visible flicker makes it look like the plugin *did* fire, so the natural conclusion is "the mod is broken" and you go fiddle with priorities. This is probably the most common way a real user meets this plugin failing.

**Fix:** check the obvious conditions before firing (`ICondition` for InCombat/Mounted/Casting/Occupied, plus the emote's unlock link) and name the reason. As a backstop, poll the player's ActionTimeline over the next few framework ticks and print `"[EC] the game refused {TextCommand} (mounted / in combat / not unlocked?)"` if it never changed.

---

### N. Smaller things, roughly in order

- **Mod dropdown silently shows only the first 200 mods.** `ConfigWindow.cs:308-312` — `.Take(200)` with no ellipsis, no count, no message. With 800 mods, "Zodiac Dance Replacer" simply does not exist as far as the UI is concerned. Drop the cap (an ImGui combo handles thousands) or render `showing 200 of N — type to filter`.
- **No way to refresh the mod list.** `_modCache` is only cleared in `OnOpen()` (`:589`) and nothing subscribes to Penumbra's `ModAdded`/`ModDeleted`/`ModSettingChanged`. Install a mod in Penumbra with this window open and the plugin appears not to see it. A small Refresh button next to the filter is the cheap fix.
- **The Commands tab never tells you a preset's mod is gone.** `ConfigWindow.cs:141-151` shows `(missing)` for a missing *emote* but nothing for a missing *mod* — the pattern already exists, it's just not applied. Grouping uses the stale stored `ModName` (`:124`), which is never refreshed from the live list.
- **An option group the mod no longer has can never be cleared.** *(unverified)* `LoadForEdit` copies stale groups into `_selection` (`:576`), `DrawOptionGroups` only renders groups Penumbra currently reports (`:383`) so it's invisible, and `Save()` copies all of `_selection` straight back (`:552`). Every fire then prints "could not set X — the mod's options may have changed", and editing the preset — the one thing that error tells you to do — provably cannot fix it. Intersect against `AvailableSettings` in `LoadForEdit` and warn about what's being dropped.
- **Export is all-or-nothing.** `ConfigWindow.cs:240` and `Plugin.cs:128` encode *every* preset. Every `Preset` carries `ModName` and `ModDirectory` in plain text, and Penumbra directory names come from mod titles. Handing a friend one dance command means shipping the names of every mod you've bound — which for your library is not a list you want in a Discord paste. The `/ecdebug export` path is worse: it writes into `dalamud.log`, the file people are routinely asked to upload when reporting a bug. Add per-preset checkboxes.
- **Only four option group types exist and only one is handled.** `ConfigWindow.cs:389` branches `Single` vs everything else, so `Imc` and `Combining` groups get rendered as free checkboxes and an arbitrary subset is written back — which is not how Penumbra models either. Penumbra rejects the write, and the resulting error blames "the mod's options may have changed". Switch on the type explicitly and show unmodelled types read-only. *(unverified; low practical impact — of 389 group files in your library exactly one is Combining and none of them ship a `.pap`)*
- **`/ecdebug redraw` can fail completely silently.** `Plugin.cs:97` fires `_ = TestRedrawAsync()` and `TestRedrawAsync` (`:161-168`) has no `try/catch`, unlike `FireAsync`. Any throw becomes an unobserved task exception: no chat line, no log entry. That's the worst possible behaviour for the diagnostic command that exists to answer "is redraw completion working".
- **`DefaultImportPath` hardcodes `%APPDATA%\XIVLauncher`.** `Plugin.cs:19-21`. `_pi.ConfigDirectory` exists precisely so a plugin never guesses. The hardcoded path is wrong on XIVLauncher.Core (Linux/Steam Deck — a large slice of the FFXIV modding audience) and for anyone who moved their Dalamud root.
- **An in-flight fire outlives `Dispose`.** Nothing cancels the pending wait (`PenumbraBridge.Dispose`, `:363-366`, only drops the subscription), so disabling or reloading the plugin during the 5-second window still performs a real, server-broadcast emote seconds later from a dead instance. Narrow window, no data loss, but it's one line: `_redrawCompleted?.TrySetResult(false)` in `Dispose`, plus a disposed flag checked after the await.
- **`/ecdebug export` with no presets prints a contradiction** — "No commands to export" immediately followed by "Share code written to /xllog" (`Plugin.cs:125-130`). Trivial.
- **`[JsonIgnore]` on `SlashCommand` is the wrong attribute for the config file.** `Preset.cs:44` uses `System.Text.Json`'s, which is right for share codes but Dalamud persists the config with Newtonsoft, which ignores it — so `"SlashCommand": "/dancemod"` *is* written into the config file, contradicting the remark on line 43. Harmless today (no setter, so read-back skips it), but the class is annotated as though one serializer governs it, and the next person to add an attribute will change one format and silently not the other.
- **No solution file.** The repo root has only `.gitignore`, the two projects and `docs/`. `dotnet build` / `dotnet test` from the root fails with MSB1011. Because the shared files are *linked* rather than project-referenced (`EmoteCommander.Tests.csproj:22`), a green `dotnet test` proves nothing about whether the plugin compiles and vice versa — there is currently no single command that verifies both.
- **The one test guarding "Settings is never null" tests the wrong thing.** `PresetTests.cs:60` asserts `new Preset().Settings` is non-null — an invariant that holds forever and is irrelevant, because no `Preset` from untrusted input is ever built that way. `ShareCodeSafetyTests` is a genuinely well-aimed hostile-input suite (gzip bomb, preset count, `$type` polymorphism) but never asserts anything about the *fields* of a decoded preset. Five tests over `ShareCode.Decode` — `"Settings":null`, `"Settings":{"g":null}`, `"Command":null`, `"ModName":null`, a 900 KB `ModName` — would have caught three of the findings above.

---

## 3. Things that are fine — leave them alone

- **All five design constraints are genuinely honoured.** Pose emotes are excluded three times over (`EmoteResolver.cs:31`, `EmoteCatalogue.cs:59`, `EmotePlayer.cs:32`), nothing anywhere touches `/cpose` or a pose index, no vanilla emote command is intercepted by design, the emote goes through `AgentEmote.ExecuteEmote` (a real broadcast, not a local timeline write), there are no temporary mods, and priority raising is opt-in, defaults off, and announces itself in chat (`CommandRunner.cs:246-252`).
- **The core insight is sound and well documented** — mapping a mod's redirected pap path to an emote via the shared ActionTimeline key (`EmoteResolver.cs:13-22`, `EmoteCatalogue.cs:17-25`) with no hand-maintained lookup table. Verified against your installed Emote sheet: slot 0 is the *loop* timeline, which is the clip mods actually replace, so the resolver is looking at the right one.
- **`ModFilePaths` path-traversal hardening is correct** (`PenumbraBridge.cs:205-226`) — rejects `..`, rooted paths and separators, then re-checks the resolved path sits under the Penumbra root. `""` can't reach it and `"."` produces an empty result harmlessly.
- **The gzip-bomb cap is properly done** — bounded read loop rather than `CopyTo` (`ShareCode.cs:91-102`), with the reasoning written down. Contrary to what you might worry about after reading the above: there is **no format-string vulnerability** (Dalamud's ImGui bindings route every text call through the length-delimited `TextUnformatted`, so `%s` in a mod name is inert), the regex is not a practical DoS risk on any path a stranger can reach, and the base64 allocation is fine.
- **`NothingChanged` treated as success** (`PenumbraBridge.cs:299-301`) is exactly right and is the normal result of firing the same command twice.
- **Applying to `PlayerCollection()` rather than Penumbra's UI-selected collection** (`PenumbraBridge.cs:86-96`) is the correct and non-obvious choice.
- **The pure/impure split** — `EmoteResolver`, `Preset`, `ShareCode` free of Dalamud and Penumbra so they test with the game closed — is the right architecture and the explicit-usings comments at the top of each linked file are a good touch.
- **The `finally`/timeout structure of `RedrawAndAwaitAsync`** degrades to a timeout rather than hanging, which is the right shape; it just needs per-call ownership (finding H).

---

## 4. What to fix first

1. **A** — marshal the post-redraw half back onto the framework thread. This is the only finding that can take the client down, and it's ~10 lines.
2. **B** — filter the redraw callback to your own character. Same session; it's the difference between "works at home, flaky in town" and "works".
3. **D** — check the mod is installed and enabled before firing, and check Penumbra's master toggle. This is what turns the plugin's most common failure from silence into a sentence.
4. **C** — fix the inverted resource-path lookup, or turn `VerifyAfterFire` off by default until you do. Right now it's actively misinforming.
5. **F** — sanitise every field in `ShareCode.Decode` and wrap `ConfigWindow.Draw` in `try/catch`. One small function plus one `try` closes all three crash variants at once.
6. **G** — add the `_editing` field. Fixes both editor bugs and lets you add the Cancel/New button the editor is missing.
7. **H** — one `SemaphoreSlim` in `FireAsync`.
8. **E** — reconcile `ModName` on import and show `ModDirectory` in the UI.
9. **J** — path normalisation and the selected-options filter; then the caching, or just move the scan off the render thread.
10. Everything in **N**, at leisure. The solution file and the five `ShareCode.Decode` tests are the cheapest items on the whole list and would have caught several of the above on their own.