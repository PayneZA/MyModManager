using System;
using System.Linq;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using MyModManager.Services;
using MyModManager.Windows;

namespace MyModManager;

public sealed class Plugin : IDalamudPlugin
{
    private const string CommandName = "/mmm";
    private const string Usage =
        "/mmm opens the Library · /mmm add · /mmm play <name or shortcut> · /mmm on|off|toggle <shortcut> · /mmm temp off · /mmm help · /mmm guide";

    private static readonly (string Command, string What)[] ChatHelp =
    [
        ("/mmm", "open the Library"),
        ("/mmm add", "add mods from Penumbra"),
        ("/mmm play <name or shortcut>", "play an entry (works in macros)"),
        ("/mmm on|off|toggle <shortcut>", "switch the entries sharing a shortcut"),
        ("/mmm temp off", "turn off everything temporary"),
        ("/mmm guide", "open the full guide (also the ? in the Library)"),
    ];

    public Configuration Configuration { get; }
    public EmoteData Emotes { get; }
    public PenumbraService Penumbra { get; }
    public EntryService Entries { get; }
    public CommandSender Commands { get; }
    public PlayService Player { get; }

    public WindowSystem WindowSystem { get; } = new("MyModManager");
    public LibraryWindow LibraryWindow { get; }
    public AddWindow AddWindow { get; }
    public HelpWindow HelpWindow { get; }

    public Plugin(IDalamudPluginInterface pluginInterface)
    {
        pluginInterface.Create<Svc>();

        Configuration = pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Configuration.Initialize(pluginInterface);

        Emotes = new EmoteData(Svc.Data);
        RepairGuessedCommands();
        Penumbra = new PenumbraService(pluginInterface, Configuration);
        Entries = new EntryService(Configuration, Penumbra, Emotes);
        Commands = new CommandSender(Emotes);
        Player = new PlayService(Configuration, Penumbra, Entries, Emotes, Commands);

        LibraryWindow = new LibraryWindow(this);
        AddWindow = new AddWindow(this);
        HelpWindow = new HelpWindow(this);
        WindowSystem.AddWindow(LibraryWindow);
        WindowSystem.AddWindow(AddWindow);
        WindowSystem.AddWindow(HelpWindow);

        Svc.Commands.AddHandler(CommandName, new CommandInfo(OnCommand) { HelpMessage = Usage });

        pluginInterface.UiBuilder.Draw += WindowSystem.Draw;
        pluginInterface.UiBuilder.OpenConfigUi += ToggleManageUi;
        pluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;
        Svc.Framework.Update += OnFrameworkUpdate;
    }

    public void Dispose()
    {
        Svc.Framework.Update -= OnFrameworkUpdate;
        Svc.PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
        Svc.PluginInterface.UiBuilder.OpenConfigUi -= ToggleManageUi;
        Svc.PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;
        Svc.Commands.RemoveHandler(CommandName);

        WindowSystem.RemoveAllWindows();
        LibraryWindow.Dispose();
        AddWindow.Dispose();
        HelpWindow.Dispose();

        Player.Dispose();
        Penumbra.Dispose();
        Configuration.Flush();
    }

    /// <summary>One-time fix for commands V1 guessed from emote names that the game doesn't accept.</summary>
    private void RepairGuessedCommands()
    {
        var repaired = 0;
        foreach (var mod in Configuration.ManagedMods)
        {
            if (Emotes.FromV1GuessedCommand(mod.AnimationCommand) is not { } emote)
                continue;
            Svc.Log.Information($"Repaired command for {mod.DisplayName}: {mod.AnimationCommand} -> {emote.Command}");
            mod.AnimationCommand = emote.Command;
            repaired++;
        }

        if (repaired > 0)
            Configuration.Save();
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        Penumbra.Update();
        Player.Update();
        Configuration.FlushIfDue();
    }

    private void OnCommand(string command, string args)
    {
        var argList = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (argList.Length == 0)
        {
            LibraryWindow.Toggle();
            return;
        }

        var action = argList[0].ToLowerInvariant();
        var rest = string.Join(" ", argList.Skip(1));

        switch (action)
        {
            case "manage" or "library" or "v2":
                LibraryWindow.Toggle();
                return;

            case "add":
                if (AddWindow.IsOpen)
                    AddWindow.IsOpen = false;
                else
                    AddWindow.Open();
                return;

            case "help":
                Svc.Print("My Mod Manager commands:");
                foreach (var (cmd, what) in ChatHelp)
                    Svc.Print($"{cmd}  -  {what}");
                return;

            case "guide":
                HelpWindow.Open(HelpTopic.GettingStarted);
                return;

            case "temp" when rest.Equals("off", StringComparison.OrdinalIgnoreCase):
                Entries.TurnOffTemporary();
                return;

            case "play" when rest.Length > 0:
                PlayByName(rest);
                return;

            case "on" or "off" or "toggle" when rest.Length > 0:
                ToggleShortcut(action, rest);
                return;
        }

        Svc.Print(Usage);
    }

    private void PlayByName(string name)
    {
        var match = Configuration.ManagedMods.FirstOrDefault(m => m.ShortcutName.Equals(name, StringComparison.OrdinalIgnoreCase))
                    ?? Configuration.ManagedMods.FirstOrDefault(m => m.DisplayName.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (match == null)
        {
            Svc.PrintError($"No entry has the shortcut or name \"{name}\".");
            return;
        }

        Player.Play(match);
    }

    private void ToggleShortcut(string action, string shortcut)
    {
        // Entries sharing a shortcut name switch together, matching the UI checkbox.
        var mods = Configuration.ManagedMods
            .Where(m => string.Equals(m.ShortcutName, shortcut, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (mods.Count == 0)
        {
            Svc.PrintError($"No entry has the shortcut \"{shortcut}\".");
            return;
        }

        // "toggle" follows live Penumbra state, so it stays right after changes made in Penumbra itself.
        var enable = action switch
        {
            "on" => true,
            "off" => false,
            _ => mods.Count(m => Entries.IsOn(m) == true) * 2 < mods.Count,
        };

        var report = Entries.Apply(mods, enable);
        if (!report.AnyChanged)
            return;

        var label = mods.Count == 1 ? mods[0].DisplayName : $"{shortcut} ({report.Changed}/{mods.Count})";
        Svc.Print($"{label} turned {(enable ? "on" : "off")}.");
    }

    private void ToggleManageUi() => LibraryWindow.Toggle();
    private void ToggleMainUi() => LibraryWindow.Toggle();
}
