# My Mod Manager

An **animation mode for [Penumbra](https://github.com/xivdev/Penumbra)**, built as a Dalamud plugin for FFXIV.

Penumbra keeps doing the work in the background. My Mod Manager keeps a library of the mods and options you care about, tells you exactly what to type to use them, and turns them on and off for you. It is made for roleplay animation mods (idles, walks, dances, sits, couples), and handles other mods too.

> [!WARNING]
> **Please read before installing.**
>
> - **Co-authored with AI.** This plugin was co-authored with **Claude Opus 5.5 (high effort)**. The developer does have Dalamud plugin knowledge and reviewed and tested the work in-game, but much of the code and text was written with AI assistance.
> - **No official support.** It is made for the author and close friends. Issues and requests may not be answered.
> - **May be discontinued at any time**, without notice.
> - **Unofficial.** Not affiliated with the Penumbra team, Dalamud, or Square Enix.

## What it does

- **Library** (`/mmm`): your entries in tabs you define, laid out like Penumbra's Mods tab. Search across names, commands, categories and positions; filter by rating, emote, category or position; group by Penumbra folder, emote and pose, category or position. Rows are colour-coded (teal SFW, rose NSFW, amber unsorted) and show the command to type, e.g. `/groundsit · 2`.
- **Play**: one button turns the mod on, pauses other entries that replace the same emote, redraws you, selects the right pose, and plays the emote. If you're already sitting, it switches pose with `/cpose` instead of standing you up.
- **Add from Penumbra** (`/mmm add`): shows a mod the way Penumbra does (the mod itself, then its option groups) and reads the mod's files to find exactly which emote and pose each part replaces. A `[Gsit1_2]` couple mod becomes pose 1 and pose 2; a pack's `(Dom-Gsit2/Sub-Gsit3)` option becomes a Dom entry and a Sub entry. **Try** any row before adding it; **Undo try** restores the mod's previous settings exactly.
- **Tabs you define**: Animations, Body, VFX… each with a behaviour (*Animation*, *On / off*, or *Pick one* for switching between options like Physics: Clothed / Nude) and its own category list.
- **Keep on or Temporary**: favourite idles and walks stay on; scene animations are temporary and one button turns them all off, restoring any kept-on entries they paused.
- **Sorting**: rate entries SFW / NSFW, give them a category and position, individually or many at once. Unsorted imports are one click away.
- **Safe view**: hides everything not rated SFW, everywhere, for streaming or screen sharing.
- **Emote sync**: with [Simple Heels](https://github.com/Caraxi/SimpleHeels) installed, one button (or an automatic option per entry) runs `/heels emotesync` so a couple's animations line up.
- **Guide** (`/mmm guide`): a searchable in-game guide; the **?** buttons open it on the right page.

It follows the Penumbra collection your character actually uses (or one you choose), reacts to Penumbra's change events instead of polling, and only ever runs real emote commands, so a typo can't be posted to chat.

## Commands

| Command | Effect |
| --- | --- |
| `/mmm` | Open or close the Library |
| `/mmm add` | Add mods from Penumbra |
| `/mmm play <name or shortcut>` | Play an entry (works in macros) |
| `/mmm on\|off\|toggle <shortcut>` | Switch every entry sharing that shortcut |
| `/mmm temp off` | Turn off everything temporary |
| `/mmm help` | List these commands in chat |
| `/mmm guide` | Open the full guide |

## Installing in-game

1. In FFXIV, open Dalamud settings (`/xlsettings`), **Experimental** tab.
2. Under **Custom Plugin Repositories**, paste:

   ```
   https://raw.githubusercontent.com/PayneZA/MyModManager/main/pluginmaster.json
   ```

3. Click the **+** button, then **Save and Close**.
4. Open the plugin installer (`/xlplugins`), search for **My Mod Manager**, and install.

Requires Penumbra. Simple Heels is optional (for emote sync).

### Upgrading from 1.x

Your library is upgraded automatically the first time V2 loads, and the old settings file is kept next to it as `MyModManager.v1-backup.json`. Tags written as `Type - Position` (e.g. `Sex - Riding`) become the new Category and Position fields; entries in "Unassigned" or "To Assign" show their Penumbra folder instead.

## Building

```
dotnet build MyModManager.sln
```

Requires a local XIVLauncher/Dalamud dev install (the SDK resolves Dalamud assemblies from `%AppData%\XIVLauncher\addon\Hooks\dev`, or `DALAMUD_HOME`). Load `MyModManager/bin/x64/Debug/MyModManager.dll` as a Dalamud dev plugin to test in-game.

## Releasing (maintainer)

Bump `<Version>` in `MyModManager/MyModManager.csproj` and push to `main`. The release workflow builds the plugin, publishes a GitHub release with `latest.zip`, and updates `pluginmaster.json`, so Dalamud offers the update. Pushing without a version bump builds but publishes nothing.

## License

AGPL-3.0-or-later.
