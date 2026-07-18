using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using ECommons;
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

        var action = argList[0].ToLower();

        if (action == "manage")
        {
            ModManagerWindow.IsOpen = !ModManagerWindow.IsOpen;
            return;
        }

        if (argList.Length >= 2)
        {
            var shortcutName = string.Join(" ", argList.Skip(1));

            var mod = Configuration.ManagedMods.FirstOrDefault(m => m.ShortcutName.Equals(shortcutName, StringComparison.OrdinalIgnoreCase));
            if (mod != null)
            {
                bool enable = action switch
                {
                    "on" => true,
                    "off" => false,
                    "toggle" => !mod.IsEnabled,
                    _ => true
                };

                if (PenumbraOptionSetter.SetManagedModState(mod, enable, Configuration.TargetCollectionId))
                {
                    mod.IsEnabled = enable;
                    Configuration.Save();
                    new Penumbra.Api.IpcSubscribers.RedrawObject(Interface).Invoke(0, RedrawType.Redraw);
                    Svc.Chat.Print($"[PMM] {mod.DisplayName} set to {(enable ? "Enabled" : "Disabled")}");
                }
                else
                {
                    Svc.Chat.Print($"[PMM] Failed to toggle mod via shortcut: {shortcutName}");
                }
                return;
            }
            else
            {
                Svc.Chat.Print($"[PMM] Unknown shortcut: {shortcutName}");
                return;
            }
        }
        
        Svc.Chat.Print("[PMM] Usage: /pmm | /pmm manage | /pmm [on|off|toggle] <shortcut>");
    }

    private void ToggleConfigUi() => ModManagerWindow.IsOpen = !ModManagerWindow.IsOpen;
    private void ToggleMainUi() => MainWindow.IsOpen = !MainWindow.IsOpen;
}
