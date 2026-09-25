using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace MyModManager;

/// <summary>Dalamud services, injected once at plugin load via <c>pluginInterface.Create&lt;Svc&gt;()</c>.</summary>
internal sealed class Svc
{
    [PluginService] public static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] public static ICommandManager Commands { get; private set; } = null!;
    [PluginService] public static IChatGui Chat { get; private set; } = null!;
    [PluginService] public static IPluginLog Log { get; private set; } = null!;
    [PluginService] public static IDataManager Data { get; private set; } = null!;
    [PluginService] public static IFramework Framework { get; private set; } = null!;
    [PluginService] public static IObjectTable Objects { get; private set; } = null!;
    [PluginService] public static IClientState ClientState { get; private set; } = null!;

    /// <summary>Prints a plugin-tagged line to the local chat log (never sent to the server).</summary>
    public static void Print(string message) => Chat.Print(message, "MMM");

    public static void PrintError(string message) => Chat.PrintError(message, "MMM");
}
