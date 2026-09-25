using System;
using System.Linq;
using System.Text;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;

namespace MyModManager.Services;

/// <summary>
/// Runs slash commands on the player's behalf. Only emote commands (optionally with
/// "motion" or a target placeholder) and commands registered by Dalamud plugins are allowed,
/// so a stored typo can never be posted to a chat channel as a message.
/// </summary>
public sealed class CommandSender
{
    private static readonly string[] AllowedEmoteArguments = ["motion", "<t>", "<me>", "<tt>"];

    private readonly EmoteData emotes;

    public CommandSender(EmoteData emotes) => this.emotes = emotes;

    public enum Verdict
    {
        Emote,
        PluginCommand,
        Rejected,
    }

    public Verdict Check(string command, out string reason)
    {
        reason = string.Empty;
        var trimmed = command.Trim();
        if (trimmed.Length == 0 || !trimmed.StartsWith('/') || trimmed.Contains('\n') || trimmed.Contains('\r'))
        {
            reason = "Commands must be a single line starting with '/'.";
            return Verdict.Rejected;
        }

        var parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (emotes.FromCommand(parts[0]) != null)
        {
            var extra = parts.Skip(1).FirstOrDefault(p => !AllowedEmoteArguments.Contains(p, StringComparer.OrdinalIgnoreCase));
            if (extra == null)
                return Verdict.Emote;

            reason = $"'{extra}' is not an emote option. Only 'motion' and target placeholders are allowed.";
            return Verdict.Rejected;
        }

        if (Svc.Commands.Commands.ContainsKey(parts[0].ToLowerInvariant()))
            return Verdict.PluginCommand;

        reason = $"'{parts[0]}' is not an emote command.";
        return Verdict.Rejected;
    }

    public bool TrySend(string command, out string reason)
    {
        var trimmed = command.Trim();
        switch (Check(trimmed, out reason))
        {
            case Verdict.Emote:
                SendToGame(trimmed);
                return true;
            case Verdict.PluginCommand:
                return Svc.Commands.ProcessCommand(trimmed);
            default:
                return false;
        }
    }

    private static unsafe void SendToGame(string text)
    {
        if (Encoding.UTF8.GetByteCount(text) > 500)
            return;

        var uiModule = UIModule.Instance();
        if (uiModule == null)
            return;

        var message = Utf8String.FromString(text);
        try
        {
            uiModule->ProcessChatBoxEntry(message, nint.Zero, false);
        }
        finally
        {
            message->Dtor(true);
        }
    }
}
