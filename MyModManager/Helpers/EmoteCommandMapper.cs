using System;

namespace MyModManager.Helpers;

public static class EmoteCommandMapper
{
    public const string ImportedTag = "Imported";
    public const string UnassignedCategory = "Unassigned";

    public static string StripEmotePrefix(string raw)
    {
        var name = (raw ?? string.Empty).Trim();
        const string prefix = "Emote:";
        if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            name = name[prefix.Length..].Trim();
        return name;
    }
}
