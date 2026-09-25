using System;
using System.Collections.Generic;
using System.Linq;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using MyModManager.Models;

namespace MyModManager.Services;

/// <summary>
/// Plays an entry: turns it on, pauses entries that replace the same emote, waits for Penumbra's
/// redraw to finish (a redraw mid-emote cancels it), selects the entry's pose, then runs the emote.
/// </summary>
public sealed class PlayService : IDisposable
{
    private static readonly TimeSpan RedrawTimeout = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan SettleAfterRedraw = TimeSpan.FromMilliseconds(350);
    private static readonly TimeSpan CommandSpacing = TimeSpan.FromMilliseconds(300);

    private readonly Configuration config;
    private readonly PenumbraService penumbra;
    private readonly EntryService entries;
    private readonly EmoteData emotes;
    private readonly CommandSender commands;

    private readonly Queue<string> queuedCommands = new();
    private DateTime nextCommandAt = DateTime.MinValue;
    private PendingPlay? pending;

    private sealed class PendingPlay(ManagedMod mod, DateTime deadline)
    {
        public ManagedMod Mod { get; } = mod;
        public DateTime Deadline { get; } = deadline;
        public DateTime? ReadyAt { get; set; }
    }

    public PlayService(Configuration config, PenumbraService penumbra, EntryService entries, EmoteData emotes, CommandSender commands)
    {
        this.config = config;
        this.penumbra = penumbra;
        this.entries = entries;
        this.emotes = emotes;
        this.commands = commands;
        penumbra.PlayerRedrawn += OnPlayerRedrawn;
    }

    public void Dispose() => penumbra.PlayerRedrawn -= OnPlayerRedrawn;

    /// <summary>True while waiting for a redraw before running an entry's emote.</summary>
    public bool IsBusy => pending != null;

    public void Play(ManagedMod mod)
    {
        var changed = false;
        if (entries.IsOn(mod) != true)
        {
            var report = entries.Apply(entries.ResolveShortcutGroup(mod), true, redraw: false);
            if (!report.AnyChanged)
                return;
            changed = true;
        }

        if (config.OneAnimationPerEmote)
        {
            var paused = entries.PauseRivals(mod);
            if (paused.Count > 0)
            {
                changed = true;
                Svc.Print($"Paused {string.Join(", ", paused.Select(p => p.DisplayName))} so {mod.DisplayName} plays. "
                          + "Turn off temporary brings kept-on entries back.");
            }
        }

        if (changed && config.RedrawAfterChange)
        {
            pending = new PendingPlay(mod, DateTime.UtcNow + RedrawTimeout);
            penumbra.RedrawPlayer();
            return;
        }

        Perform(mod);
    }

    private void OnPlayerRedrawn()
    {
        if (pending != null)
            pending.ReadyAt = DateTime.UtcNow + SettleAfterRedraw;
    }

    /// <summary>Advances a pending play and sends queued commands. Call once per framework tick.</summary>
    public void Update()
    {
        var now = DateTime.UtcNow;
        if (pending != null && ((pending.ReadyAt is { } ready && now >= ready) || now >= pending.Deadline))
        {
            var mod = pending.Mod;
            pending = null;
            Perform(mod);
        }

        if (queuedCommands.Count > 0 && now >= nextCommandAt)
        {
            nextCommandAt = now + CommandSpacing;
            Send(queuedCommands.Dequeue(), null);
        }
    }

    private void Perform(ManagedMod mod)
    {
        var command = mod.AnimationCommand.Trim();
        if (command.Length == 0)
            return;

        var emote = emotes.FromCommand(command);
        if (emote is { PoseKind: not PoseKind.None } && mod.Pose > 0 && config.AutoPose)
        {
            // Re-sending /groundsit while already on the ground stands the character up,
            // so cycle with /cpose instead when already in that pose family.
            if (TryCyclePoseInPlace(emote, mod.Pose))
                return;
            SelectPose(emote, mod.Pose);
        }

        if (emote != null && config.SilentEmotes && !command.Contains(" motion", StringComparison.OrdinalIgnoreCase))
            command += " motion";

        Send(command, mod);
    }

    private void Send(string command, ManagedMod? mod)
    {
        if (!commands.TrySend(command, out var reason))
            Svc.PrintError(mod == null ? reason : $"Couldn't play {mod.DisplayName}: {reason} Fix the command in the entry.");
    }

    private static unsafe Character* LocalCharacter()
    {
        var player = Svc.Objects.LocalPlayer;
        return player == null ? null : (Character*)player.Address;
    }

    private unsafe bool TryCyclePoseInPlace(EmoteInfo emote, int pose)
    {
        var chara = LocalCharacter();
        if (chara == null)
            return false;

        var type = (EmoteController.PoseType)emote.PoseKind;
        ref var controller = ref chara->EmoteController;
        if (controller.CurrentPoseType != type)
            return false;

        var available = EmoteController.GetAvailablePoses(type);
        var target = pose - 1;
        if (available == 0 || target >= available)
        {
            Svc.PrintError($"{emote.Command} has {available} poses on this character; pose {pose} isn't available.");
            return true;
        }

        var steps = (target - controller.CPoseState + available) % available;
        for (var i = 0; i < steps; i++)
            queuedCommands.Enqueue("/cpose");
        return true;
    }

    private static unsafe void SelectPose(EmoteInfo emote, int pose)
    {
        var chara = LocalCharacter();
        var state = PlayerState.Instance();
        if (chara == null || state == null)
            return;

        var type = (EmoteController.PoseType)emote.PoseKind;
        var available = EmoteController.GetAvailablePoses(type);
        if (available == 0 || pose - 1 >= available)
        {
            Svc.PrintError($"{emote.Command} has {available} poses on this character; pose {pose} isn't available.");
            return;
        }

        state->SelectedPoses[(int)type] = (byte)(pose - 1);
    }
}
