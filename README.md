# MyModManager

An **unofficial** favorites/shortcut manager for [Penumbra](https://github.com/xivdev/Penumbra) mods in FFXIV, built as a Dalamud plugin.

This is a small project made for friends. It is **not** an official mod manager and is not affiliated with the Penumbra team, Dalamud, or Square Enix.

## What it does

- Bookmark Penumbra mods (or a specific option within a mod) as favorites.
- Organize favorites into categories and toggle them from an in-game window.
- Toggle favorites from chat: `/mmm on|off|toggle <shortcut>`.
- Optionally fire an emote/animation command when a favorite is played.

## Commands

| Command | Effect |
| --- | --- |
| `/mmm` | Open/close the favorites window |
| `/mmm manage` | Open/close the management window |
| `/mmm on\|off\|toggle <shortcut>` | Switch every favorite sharing that shortcut name |

## Building

```
dotnet build MyModManager.sln
```

Requires a local XIVLauncher/Dalamud dev install (the SDK resolves Dalamud assemblies from `%AppData%\XIVLauncher\addon\Hooks\dev`, or `DALAMUD_HOME`). Load `bin/x64/Debug/MyModManager.dll` as a Dalamud dev plugin to test in-game.
