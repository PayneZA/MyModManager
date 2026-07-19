using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using ECommons;
using ECommons.Automation;
using ECommons.DalamudServices;
using MyModManager.Helpers;
using MyModManager.Windows;
using System;
using System.Linq;
using Penumbra.Api.Enums;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;

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
            HelpMessage = "Usage: /mmm (open favorites) | /mmm manage (open config) | /mmm [on|off|toggle] <shortcut>"
        });

        Interface.UiBuilder.Draw += WindowSystem.Draw;
        Interface.UiBuilder.OpenConfigUi += ToggleConfigUi;
        Interface.UiBuilder.OpenMainUi += ToggleMainUi;
    }

    public void Dispose()
    {
        ECommonsMain.Dispose();
        CommandManager.RemoveHandler(CommandName);
        Interface.UiBuilder.Draw -= WindowSystem.Draw;
        Interface.UiBuilder.OpenConfigUi -= ToggleConfigUi;
        Interface.UiBuilder.OpenMainUi -= ToggleMainUi;
        WindowSystem.RemoveAllWindows();
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

        if ((action == "on" || action == "off" || action == "toggle") && argList.Length >= 2)
        {
            var shortcutName = string.Join(" ", argList.Skip(1));

            // Mods sharing a shortcut name toggle together, matching the UI checkbox behavior.
            var mods = Configuration.ManagedMods
                .Where(m => m.ShortcutName.Equals(shortcutName, StringComparison.OrdinalIgnoreCase))
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
                _ => !(PenumbraOptionSetter.GetManagedModState(mods[0], Configuration.TargetCollectionId) ?? mods[0].IsEnabled)
            };

            if (!enable && mods.All(m => !string.IsNullOrEmpty(m.OptionName) && m.GroupType == GroupType.Single))
            {
                Svc.Chat.Print($"[MMM] '{shortcutName}' points at single-select option(s), which cannot be disabled - enable a different option from the group instead.");
                return;
            }

            int succeeded = 0;
            foreach (var mod in mods)
            {
                if (PenumbraOptionSetter.SetManagedModState(mod, enable, Configuration.TargetCollectionId))
                {
                    mod.IsEnabled = enable;
                    succeeded++;
                }
            }

            if (succeeded > 0)
            {
                Configuration.Save();
                PenumbraOptionSetter.RedrawPlayer();
                MainWindow.ForceStateRefresh();
                ModManagerWindow.ForceStateRefresh();

                var label = mods.Count == 1
                    ? mods[0].DisplayName
                    : $"{shortcutName} ({succeeded}/{mods.Count} entries)";
                Svc.Chat.Print($"[MMM] {label} set to {(enable ? "Enabled" : "Disabled")}");
            }
            else
            {
                Svc.Chat.Print($"[MMM] Failed to toggle shortcut '{shortcutName}'. Is Penumbra running and the mod installed?");
            }
            return;
        }

        Svc.Chat.Print("[MMM] Usage: /mmm | /mmm manage | /mmm [on|off|toggle] <shortcut>");
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
