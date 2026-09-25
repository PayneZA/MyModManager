using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using ECommons;
using ECommons.Automation;
using ECommons.DalamudServices;
using MyModManager.Helpers;
using MyModManager.Models;
using MyModManager.Windows;
using System;
using System.Collections.Generic;
using System.Linq;
using Penumbra.Api.Enums;

namespace MyModManager;

public class Plugin : IDalamudPlugin
{
    public string Name => "My Mod Manager";
    private const string CommandName = "/mmm";

    public IDalamudPluginInterface Interface { get; init; }
    public ICommandManager CommandManager { get; init; }
    public Configuration Configuration { get; init; }
    public WindowSystem WindowSystem { get; init; } = new("MyModManager");
    public MainWindow MainWindow { get; init; }
    public ModManagerWindow ModManagerWindow { get; init; }
    public PenumbraOptionSetter PenumbraOptionSetter { get; init; }

    public Plugin(IDalamudPluginInterface pluginInterface, ICommandManager commandManager)
    {
        this.Interface = pluginInterface;
        this.CommandManager = commandManager;

        ECommonsMain.Init(pluginInterface, this);

        Configuration = pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Configuration.Initialize(pluginInterface);

        PenumbraOptionSetter = new PenumbraOptionSetter(pluginInterface);
        MainWindow = new MainWindow(this);
        ModManagerWindow = new ModManagerWindow(this);

        WindowSystem.AddWindow(MainWindow);
        WindowSystem.AddWindow(ModManagerWindow);

        commandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Usage: /mmm (favorites) | /mmm manage (add/edit) | /mmm [on|off|toggle] <shortcut> | /mmm temp off"
        });

        Interface.UiBuilder.Draw += WindowSystem.Draw;
        Interface.UiBuilder.OpenConfigUi += ToggleConfigUi;
        Interface.UiBuilder.OpenMainUi += ToggleMainUi;
    }

    public void Dispose()
    {
        Interface.UiBuilder.Draw -= WindowSystem.Draw;
        Interface.UiBuilder.OpenConfigUi -= ToggleConfigUi;
        Interface.UiBuilder.OpenMainUi -= ToggleMainUi;
        WindowSystem.RemoveAllWindows();
        MainWindow.Dispose();
        ModManagerWindow.Dispose();
        CommandManager.RemoveHandler(CommandName);
        ECommonsMain.Dispose();
    }

    private void OnCommand(string command, string args)
    {
        var argList = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (argList.Length == 0)
        {
            MainWindow.IsOpen = !MainWindow.IsOpen;
            return;
        }

        var action = argList[0].ToLowerInvariant();

        if (action == "manage")
        {
            ModManagerWindow.IsOpen = !ModManagerWindow.IsOpen;
            return;
        }

        if (action == "temp")
        {
            if (argList.Length >= 2 && argList[1].Equals("off", StringComparison.OrdinalIgnoreCase))
            {
                DisableAllTempMods();
                return;
            }

            Svc.Chat.Print("[MMM] Usage: /mmm temp off");
            return;
        }

        if ((action == "on" || action == "off" || action == "toggle") && argList.Length >= 2)
        {
            var shortcutName = string.Join(" ", argList.Skip(1));

            // Mods sharing a shortcut name toggle together, matching the UI checkbox behavior.
            var mods = Configuration.ManagedMods
                .Where(m => string.Equals(m.ShortcutName, shortcutName, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (mods.Count == 0)
            {
                Svc.Chat.Print($"[MMM] Unknown shortcut: {shortcutName}");
                return;
            }

            // "toggle" flips the live Penumbra state so it stays correct even after the
            // user changed the mod in Penumbra directly; stored state is the fallback.
            bool enable = action switch
            {
                "on" => true,
                "off" => false,
                _ => !MajorityCurrentlyEnabled(mods)
            };

            if (!enable && mods.All(IsSingleSelectOption))
            {
                Svc.Chat.Print($"[MMM] '{shortcutName}' points at single-select option(s), which cannot be disabled - enable a different option from the group instead.");
                return;
            }

            if (!ApplyFavoriteStates(mods, enable))
            {
                Svc.Chat.Print($"[MMM] Failed to toggle shortcut '{shortcutName}'. Is Penumbra running and the mod installed?");
                return;
            }

            var succeeded = mods.Count(m => m.IsEnabled == enable);
            var label = mods.Count == 1
                ? mods[0].DisplayName
                : $"{shortcutName} ({succeeded}/{mods.Count} entries)";
            Svc.Chat.Print($"[MMM] {label} set to {(enable ? "Enabled" : "Disabled")}");
            return;
        }

        Svc.Chat.Print("[MMM] Usage: /mmm (favorites) | /mmm manage (add/edit) | /mmm [on|off|toggle] <shortcut> | /mmm temp off");
    }

    /// <summary>
    /// True when more members are currently on than off. Live Penumbra state is preferred;
    /// stored IsEnabled is the fallback. Ties count as currently on so toggle turns the group off.
    /// </summary>
    private bool MajorityCurrentlyEnabled(List<ManagedMod> mods)
    {
        int enabled = 0;
        int disabled = 0;
        foreach (var mod in mods)
        {
            var live = PenumbraOptionSetter.GetManagedModState(mod, Configuration.TargetCollectionId);
            if (live == true) enabled++;
            else if (live == false) disabled++;
        }

        if (enabled + disabled == 0)
        {
            foreach (var mod in mods)
            {
                if (mod.IsEnabled) enabled++;
                else disabled++;
            }
        }

        return enabled >= disabled;
    }

    public static bool IsSingleSelectOption(ManagedMod mod) =>
        !string.IsNullOrEmpty(mod.OptionName) && mod.GroupType == GroupType.Single;

    public List<ManagedMod> ResolveShortcutGroup(ManagedMod source)
    {
        if (string.IsNullOrWhiteSpace(source.ShortcutName))
            return new List<ManagedMod> { source };

        return Configuration.ManagedMods
            .Where(m => string.Equals(m.ShortcutName, source.ShortcutName, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    /// <summary>
    /// Applies an on/off change to a favorite and every other favorite sharing its shortcut.
    /// Returns false when nothing changed (including blocked single-select disables).
    /// </summary>
    public bool TryToggleFavorites(ManagedMod source, bool enable)
    {
        var mods = ResolveShortcutGroup(source);
        if (!enable && mods.All(IsSingleSelectOption))
        {
            var label = string.IsNullOrWhiteSpace(source.ShortcutName) ? source.DisplayName : source.ShortcutName;
            Svc.Chat.Print($"[MMM] '{label}' points at single-select option(s), which cannot be disabled - enable a different option from the group instead.");
            return false;
        }

        return ApplyFavoriteStates(mods, enable);
    }

    /// <summary>Turns off every Temp-tagged managed mod in Penumbra. Does not clear the Temp tag.</summary>
    public void DisableAllTempMods()
    {
        var temps = Configuration.ManagedMods.Where(m => m.IsTemp).ToList();
        if (temps.Count == 0)
        {
            Svc.Chat.Print("[MMM] No temp-tagged mods.");
            return;
        }

        int succeeded = 0;
        foreach (var mod in temps)
        {
            if (PenumbraOptionSetter.SetManagedModState(mod, false, Configuration.TargetCollectionId))
            {
                mod.IsEnabled = false;
                succeeded++;
            }
        }

        if (succeeded == 0)
        {
            Svc.Chat.Print("[MMM] Failed to disable temp mods. Is Penumbra running?");
            return;
        }

        Configuration.Save();
        PenumbraOptionSetter.RedrawPlayer();
        PenumbraOptionSetter.ForceModStateRefresh();
        Svc.Chat.Print($"[MMM] Disabled {succeeded} temp mod{(succeeded == 1 ? "" : "s")}.");
    }

    private bool ApplyFavoriteStates(List<ManagedMod> mods, bool enable)
    {
        bool anyChanged = false;
        foreach (var mod in mods)
        {
            if (PenumbraOptionSetter.SetManagedModState(mod, enable, Configuration.TargetCollectionId))
            {
                mod.IsEnabled = enable;
                anyChanged = true;
            }
        }

        if (!anyChanged) return false;

        Configuration.Save();
        PenumbraOptionSetter.RedrawPlayer();
        PenumbraOptionSetter.ForceModStateRefresh();
        return true;
    }

    /// <summary>
    /// Sends an emote/animation chat command. A leading '/' is enforced so a typo like
    /// "dance" can never be posted to public chat as a plain message.
    /// </summary>
    public void SendAnimationCommand(string animationCommand)
    {
        var cmd = animationCommand.Trim();
        if (cmd.Length == 0) return;
        if (!cmd.StartsWith('/')) cmd = "/" + cmd;
        Chat.SendMessage(cmd);
    }

    private void ToggleConfigUi() => ModManagerWindow.IsOpen = !ModManagerWindow.IsOpen;
    private void ToggleMainUi() => MainWindow.IsOpen = !MainWindow.IsOpen;
}
