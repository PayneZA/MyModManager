using System;
using System.Collections.Generic;
using System.Linq;
using MyModManager.Models;
using Penumbra.Api.Enums;

namespace MyModManager.Services;

public enum ToggleOutcome
{
    Changed,
    Unavailable,
    Missing,
    NoOffOption,
    Failed,
}

public readonly record struct ApplyReport(int Changed, int Total, IReadOnlyList<(ManagedMod Mod, ToggleOutcome Outcome)> Failures)
{
    public bool AnyChanged => Changed > 0;
}

/// <summary>
/// What an entry means in Penumbra terms: whether it is on, and how to turn it on or off,
/// including single-choice option groups that can't simply be unticked.
/// </summary>
public sealed class EntryService
{
    private static readonly string[] OffOptionNames =
        ["none", "off", "disabled", "disable", "vanilla", "default", "nothing", "no animation", "original"];

    private readonly Configuration config;
    private readonly PenumbraService penumbra;
    private readonly EmoteData emotes;

    public EntryService(Configuration config, PenumbraService penumbra, EmoteData emotes)
    {
        this.config = config;
        this.penumbra = penumbra;
        this.emotes = emotes;
    }

    /// <summary>True/false when known; null while Penumbra hasn't reported this mod's state yet.</summary>
    public bool? IsOn(ManagedMod mod)
    {
        if (!penumbra.TryGetState(mod.ModName, out var state))
            return null;
        if (state == null)
            return false;
        if (string.IsNullOrEmpty(mod.OptionName))
            return state.Enabled;

        // An option only applies while its mod is enabled.
        return state.Enabled
            && state.Settings.TryGetValue(mod.GroupName, out var selected)
            && selected.Contains(mod.OptionName);
    }

    /// <summary>True when Penumbra is running and reports the mod as not installed.</summary>
    public bool IsMissing(ManagedMod mod) =>
        penumbra.TryGetState(mod.ModName, out var state) && state == null;

    public List<ManagedMod> ResolveShortcutGroup(ManagedMod source)
    {
        if (string.IsNullOrWhiteSpace(source.ShortcutName))
            return [source];

        return config.ManagedMods
            .Where(m => string.Equals(m.ShortcutName, source.ShortcutName, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    public ToggleOutcome Set(ManagedMod mod, bool enable)
    {
        if (!penumbra.Available)
            return ToggleOutcome.Unavailable;
        if (IsMissing(mod))
            return ToggleOutcome.Missing;

        var dir = mod.ModName;
        if (string.IsNullOrEmpty(mod.OptionName))
            return penumbra.SetModEnabled(dir, enable) ? ToggleOutcome.Changed : ToggleOutcome.Failed;

        var group = penumbra.GetOptionGroup(dir, mod.GroupName);
        var isMulti = (group?.Type ?? mod.GroupType) == GroupType.Multi;

        if (enable)
        {
            if (!penumbra.SetModEnabled(dir, true))
                return ToggleOutcome.Failed;

            if (!isMulti)
                return Result(penumbra.SetSingleOption(dir, mod.GroupName, mod.OptionName));

            var withOption = CurrentSelection(mod);
            if (!withOption.Contains(mod.OptionName))
                withOption.Add(mod.OptionName);
            return Result(penumbra.SetMultiOptions(dir, mod.GroupName, withOption));
        }

        if (isMulti)
        {
            var without = CurrentSelection(mod);
            without.Remove(mod.OptionName);
            return Result(penumbra.SetMultiOptions(dir, mod.GroupName, without));
        }

        return TurnOffSingleChoice(mod, group);
    }

    /// <summary>
    /// A single-choice option can't be unticked. Switch to the group's "None/Off" option when it has one;
    /// otherwise turn the mod off, unless that would also switch off other entries or a switch sibling.
    /// </summary>
    private ToggleOutcome TurnOffSingleChoice(ManagedMod mod, OptionGroup? group)
    {
        var offOption = FindOffOption(mod, group);
        if (offOption != null && offOption != mod.OptionName)
            return Result(penumbra.SetSingleOption(mod.ModName, mod.GroupName, offOption));

        var otherEntriesOfMod = config.ManagedMods.Where(m => m != mod && m.ModName.Equals(mod.ModName, StringComparison.OrdinalIgnoreCase));
        if (otherEntriesOfMod.Any(m => IsOn(m) == true || m.GroupName == mod.GroupName))
            return ToggleOutcome.NoOffOption;

        return Result(penumbra.SetModEnabled(mod.ModName, false));
    }

    public static string? FindOffOption(ManagedMod mod, OptionGroup? group)
    {
        if (group == null)
            return null;
        if (!string.IsNullOrEmpty(mod.OffOption) && group.Options.Contains(mod.OffOption))
            return mod.OffOption;

        return group.Options.FirstOrDefault(o =>
            OffOptionNames.Any(n => o.Trim().Equals(n, StringComparison.OrdinalIgnoreCase)));
    }

    private List<string> CurrentSelection(ManagedMod mod) =>
        penumbra.TryGetState(mod.ModName, out var state) && state != null && state.Settings.TryGetValue(mod.GroupName, out var list)
            ? [.. list]
            : [];

    private static ToggleOutcome Result(bool ok) => ok ? ToggleOutcome.Changed : ToggleOutcome.Failed;

    /// <summary>Turns a set of entries on or off, redraws once, and reports problems in chat.</summary>
    public ApplyReport Apply(IReadOnlyList<ManagedMod> mods, bool enable, bool redraw = true)
    {
        var failures = new List<(ManagedMod, ToggleOutcome)>();
        var changed = 0;
        foreach (var mod in mods)
        {
            var outcome = Set(mod, enable);
            if (outcome == ToggleOutcome.Changed)
                changed++;
            else
                failures.Add((mod, outcome));
        }

        foreach (var (mod, outcome) in failures)
            Svc.PrintError(Describe(mod, outcome, enable));

        if (changed > 0 && redraw && config.RedrawAfterChange)
            penumbra.RedrawPlayer();

        return new ApplyReport(changed, mods.Count, failures);
    }

    public string Describe(ManagedMod mod, ToggleOutcome outcome, bool enable) => outcome switch
    {
        ToggleOutcome.Unavailable => "Penumbra isn't available right now. Check that it is installed and enabled.",
        ToggleOutcome.Missing => $"{mod.DisplayName}: the mod \"{mod.ModName}\" is no longer installed in Penumbra.",
        ToggleOutcome.NoOffOption => $"{mod.DisplayName} is a single-choice option with no \"None\" option. Turn on another option from \"{mod.GroupName}\" instead.",
        _ => $"Couldn't turn {mod.DisplayName} {(enable ? "on" : "off")}. Penumbra refused the change; the mod or option may have been renamed.",
    };

    /// <summary>
    /// Turns off other entries that replace the same emote and pose, so the one being played
    /// isn't hidden behind a higher-priority mod. Kept-on entries are remembered and restored
    /// by <see cref="TurnOffTemporary"/>.
    /// </summary>
    public List<ManagedMod> PauseRivals(ManagedMod playing)
    {
        var paused = new List<ManagedMod>();
        var emote = emotes.FromCommand(playing.AnimationCommand);
        if (emote == null)
            return paused;

        var group = ResolveShortcutGroup(playing);
        foreach (var rival in config.ManagedMods)
        {
            if (group.Contains(rival) || IsOn(rival) != true)
                continue;
            if (emotes.FromCommand(rival.AnimationCommand)?.Id != emote.Id)
                continue;
            if (playing.Pose > 0 && rival.Pose > 0 && playing.Pose != rival.Pose)
                continue;
            // Penumbra already swaps options within one single-choice group.
            if (rival.ModName == playing.ModName && rival.GroupName == playing.GroupName && rival.GroupType == GroupType.Single
                && !string.IsNullOrEmpty(rival.OptionName))
                continue;

            if (Set(rival, false) != ToggleOutcome.Changed)
                continue;

            paused.Add(rival);
            if (!rival.IsTemp && !config.SuspendedIds.Contains(rival.Id))
                config.SuspendedIds.Add(rival.Id);
        }

        if (paused.Count > 0)
            config.Save();
        return paused;
    }

    /// <summary>
    /// Turns off every temporary entry and turns back on the kept-on entries that were paused for them.
    /// </summary>
    public void TurnOffTemporary()
    {
        var temps = config.ManagedMods.Where(m => m.IsTemp && IsOn(m) != false).ToList();
        var restore = config.ManagedMods.Where(m => config.SuspendedIds.Contains(m.Id) && !m.IsTemp).ToList();

        if (temps.Count == 0 && restore.Count == 0)
        {
            Svc.Print("Nothing temporary is on.");
            return;
        }

        var off = Apply(temps, false, redraw: false);
        var back = Apply(restore, true, redraw: false);
        config.SuspendedIds.Clear();
        config.Save();

        if (off.AnyChanged || back.AnyChanged)
        {
            if (config.RedrawAfterChange)
                penumbra.RedrawPlayer();

            var message = $"Turned off {Plural(off.Changed, "temporary entry", "temporary entries")}";
            if (back.Changed > 0)
                message += $" and turned {Plural(back.Changed, "kept-on entry", "kept-on entries")} back on";
            Svc.Print(message + ".");
        }
    }

    public int CountTemporaryOn() => config.ManagedMods.Count(m => m.IsTemp && IsOn(m) == true);

    private static string Plural(int count, string one, string many) => $"{count} {(count == 1 ? one : many)}";
}
