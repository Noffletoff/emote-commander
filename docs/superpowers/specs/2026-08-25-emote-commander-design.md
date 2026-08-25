# Emote Commander — design

A Dalamud plugin that binds a user-defined slash command to a Penumbra preset
plus a vanilla emote. Type the command, the plugin applies the mod options,
redraws, and performs the emote — which the game broadcasts, so other players
(including sync-service partners) see the modded animation.

Working name only; rename before release.

## Why it exists

FFXIV resolves one game path to exactly one file. Many animation mods on the
same emote cannot coexist — Penumbra picks the highest priority and shadows the
rest, and changing the winner requires a character redraw because the game
caches a loaded `.pap` by game path (confirmed in game 2026-08-24: option
switched, emote replayed, no change until redraw).

So an animation swap costs a redraw no matter who performs it. Today that means
opening Penumbra, finding the mod, changing options, redrawing, then typing the
emote. This collapses that to one command. The redraw is not avoided — it is
already in the workflow.

## Hard constraints

These are not negotiable and shape the design:

1. **No `/cpose`, no pose-index manipulation, ever.** Noff's rule: driving pose
   changes programmatically is not how SE intends it and carries report/ban
   risk. Pose index must be unreachable from the command path *by construction*,
   not merely unused. Consequence: sit-pose mods (`[Sit 1]`, `[Sit 3]`) cannot
   be driven by a command. Accepted.
2. **Never intercept or override a vanilla emote command.** Only user-defined
   commands are registered. Typing `/wringhands` is handled by the game as
   normal.
3. **The emote must genuinely broadcast.** Faking it locally
   (`Timeline.BaseOverride` / `PlayTimeline`) is invisible to other players —
   that is how posing tools work and is the wrong mechanism here.
4. **No temporary Penumbra mods.** Sync services replicate persistent collection
   state; runtime temporary mods are not reliably shared.

## Behaviour

- A **preset** is: command name, target mod, option selections across *all* that
  mod's groups, the resolved emote, and an override flag.
- On fire: apply the preset's option selections → redraw → wait for the redraw
  to complete → perform the emote.
- Settings **stay applied** until the next command. Repeating the same command
  costs no redraw. There is no automatic revert (it would double the blips).
- The emote is **auto-detected** from the mod's file redirects and shown
  pre-filled in a dropdown the user can correct.

## Relationship to BypassEmote — none

BypassEmote (by a friend of Noff's, already installed) solves a different
problem: playing emotes you do NOT own, by matching them onto an owned emote and
swapping the animation through a generated Penumbra mod.

**We are not using or depending on that mechanism.** Decided 2026-08-25. This
plugin only ever fires an emote the character already owns, and the animation
comes from the user's own existing mods. No matching layer, no substitution, no
generated mods, no IPC to BypassEmote.

It is referenced here only as evidence that the underlying approach — perform an
owned emote, let Penumbra supply the animation, let sync carry it — works in
production and is visible to sync partners.

## Conflicts

One game path resolves to one file, so another enabled mod can out-prioritise a
preset and the wrong animation plays with no indication why. Handled in three
places:

1. **At save time** — when binding a command, check whether another enabled mod
   claims the same emote path and warn in the UI, while the user is looking at
   it.
2. **After firing** — read `GetPlayerResourcePaths` (ground truth after priority
   resolution) and confirm the emote path resolved to this preset's mod. If not,
   print which mod won. One IPC call; makes silent wrong-animation impossible.
3. **Optional +1 priority** — a per-preset opt-in using `TrySetModPriority` that
   raises this mod one above the highest conflicting mod. **Never automatic and
   never silent** — auto-reordering a user's tuned mod priorities behind their
   back is unacceptable. The UI states what it would override before enabling.

## Distribution — how presets reach other people

Decided 2026-08-25. Without this the plugin is a personal tool: every user would
hand-build every preset, which nobody will do.

A preset serialises to a compact **share code** (a tagged, encoded string).
The same code travels two ways:

1. **Embedded in the mod's Penumbra description.** The author pastes the code
   into the mod description between markers. The plugin scans installed mods,
   finds them, and offers: *"this mod offers 3 commands - add them?"* Command
   names are suggestions the user can edit before accepting, and nothing is
   registered without confirmation.
2. **Pasted by hand.** The same string works in an import box, for sharing over
   Discord or applying to a mod whose description cannot be edited.

The description field is used rather than a sidecar file because Penumbra owns
it, so it survives `.pmp` packing and installation with certainty. A sidecar
`emotecommander.json` would be nicer to author but depends on Penumbra
extracting files that no mod json references — unverified, and not worth the
risk. It also means presets can be added to mods **already shipped**, by editing
a description rather than repacking.

Rules:
- Import never auto-registers a command. It proposes; the user accepts.
- A code carrying a pose-family emote is rejected on import, same as everywhere
  else.
- Import must survive a mod being renamed or its options changed — match on
  Penumbra's directory name, and report clearly when a named option group no
  longer exists rather than silently applying a partial preset.

## Components

Each is separately testable.

**1. Preset store** — JSON in the Dalamud plugin config. Presets keyed by
command name. No game or Penumbra knowledge.

**2. Penumbra bridge** — the only component that talks to Penumbra IPC: list
mods, read a mod's option groups, read its file redirects, apply settings,
redraw, and signal redraw completion. Everything else depends on this interface
rather than on Penumbra directly, so IPC changes are contained.

**3. Emote resolver** — given a mod's redirected game paths, find
`chara/human/<race>/animation/a0001/bt_common/emote/<name>.pap` and map `<name>`
to an emote. Pure function over path strings plus a static table; testable with
no game running. The ActionTimeline sheet is already extracted to
`pap-export/work/exd/` and read by `pap-export/work/face_flags2.py`.

**4. Command runner** — registers and deregisters commands; executes the fire
sequence. Owns the guard rails: refuses when a redraw is pending, and has no
code path that can touch pose index.

**5. UI** — mod dropdown, that mod's option groups underneath, command name
field, emote override dropdown, list of saved presets.

## Risks, spiked before anything else is built

**A. Performing an emote so that it broadcasts.** Two candidates: send the
command through the chat pipeline, or call the game's own emote execution (the
path used when clicking an emote in the UI). The second is preferred — same
outcome, nothing injected into chat. If neither can be made to broadcast, the
design changes fundamentally, so this is spiked first.

**B. Detecting redraw completion.** Firing the emote before the redraw finishes
plays the *old* cached pap and is indistinguishable from a broken plugin.
Event-driven off Penumbra's redraw signal, with a timeout fallback so a missed
event degrades to a short delay rather than a hang.

## Sync — resolved

**Sync partners do see an option swap + redraw, and it is usually fast.**
Confirmed 2026-08-25 from Noff's routine experience: he swaps options and
redraws with paired partners regularly and the change propagates quickly.

This is the operation the plugin performs — nothing novel. Snowcloak listens for
it explicitly (`Manager_PenumbraModSettingChanged` → *"Penumbra Mod Settings
changed, verifying SemiTransientResources"*) and treats `.pap` as a first-class
transient resource. Viewers therefore need **no new software**: they already
have Penumbra and a sync plugin, and the plugin never touches their client.

Residual risk, not blocking: Snowcloak's `VerifyPlayerAnimationBones` silently
strips animation files whose bone data it rejects — *"those animation files have
been removed from your sent data"*. Since Noff's animations already reach
partners today, this is not currently triggering, but it is the first thing to
suspect if an animation ever fails to appear for others while looking fine
locally.

## Out of scope

- Pose-index / `/cpose` emotes (constraint 1)
- Presets spanning multiple mods — one mod per preset for now
- Playing animations on anyone but the local player
- Authoring or editing paps; that stays in the existing `pap-export` pipeline

## Prerequisite

No .NET SDK is installed on this machine. Required before anything compiles.
Dalamud's dev `Dalamud.dll` is present; Penumbra is 1.6.1.12 (has IPC).
