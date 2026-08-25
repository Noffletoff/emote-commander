# Emote Commander

Bind your own slash command to a Penumbra preset and a vanilla emote.

Typing the command applies the mod's options, redraws your character, waits for
the redraw to finish, then performs the emote — so a custom animation plays
without opening Penumbra mid-scene. Because the emote is a real one the game
broadcasts, other players see the animation through whichever sync service they
already use. They need nothing new installed.

## Install

Dalamud → `/xlsettings` → **Experimental** → *Custom Plugin Repositories*, add:

```
https://raw.githubusercontent.com/Noffletoff/emote-commander/main/repo.json
```

Then install **Emote Commander** from the plugin list. You will also need
[Penumbra](https://github.com/xivdev/Penumbra).

## Use

`/ec` opens the window.

1. **New / Edit** — pick one of your mods
2. Choose the options you want the command to apply
3. The emote is worked out from the mod itself; correct it if it guessed wrong
4. Give it a command name, and **Save**

Then type your command. The **Commands** tab lists what you have, grouped by
mod, with **Test**, **Edit** and delete on each.

### Sharing

The **Share** tab turns your commands into a code you can paste to someone, or
put in a mod's Penumbra description so it travels with the mod. Importing
proposes the commands; nothing is registered without you accepting it.

### Conflicts

If another enabled mod also replaces the same emote, only one can win — that is
how Penumbra works. After firing, the plugin checks which file actually won and
tells you if it was not yours. **Always win conflicts** (Commands tab) raises
your mod one priority above the highest conflicting mod. It is off by default
because it edits your Penumbra priorities, and it says so in chat when it does.

## What it deliberately does not do

- **No `/cpose`, no pose switching.** Pose-family emotes (sit, ground sit and
  their variants) are excluded throughout. Reaching a specific pose index means
  driving `/cpose`, which is not how the game intends it to be used. Mods built
  on a sit pose therefore cannot be driven by a command, and the editor says so
  rather than silently binding one that plays the wrong pose.
- **No vanilla commands are intercepted.** Only names you choose are registered.
- **Nothing is faked locally.** The emote is performed through the game's own
  execution so it broadcasts. Writing the animation state directly would look
  right on your screen and be invisible to everyone else.
- **No macros are written for you.** Macro slots are finite and already full of
  your own macros; anything that picks a slot eventually overwrites one. Write a
  two-line macro yourself if you want a hotbar button:

  ```
  /micon Wring Hands emote
  /yourcommand
  ```

## Building

Requires the .NET 10 SDK and Dalamud installed (for its dev assemblies).

```
dotnet build          # both projects
dotnet test           # 58 tests, no game required
```

The pure logic — path resolution, presets, share codes — is free of Dalamud and
Penumbra references and linked into the test project rather than referenced, so
the tests build and run with the game closed.

## Credits

Built by Noffletoff with Claude. Uses [Penumbra](https://github.com/xivdev/Penumbra)'s IPC.
