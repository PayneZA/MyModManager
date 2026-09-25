using System;
using System.Collections.Generic;
using System.Text;

namespace MyModManager.Helpers;

public static class EmoteCommandMapper
{
    public const string ImportedTag = "Imported";
    public const string UnassignedCategory = "Unassigned";

    private static readonly Dictionary<string, string> Overrides = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Sit on Ground"] = "/groundsit",
        ["Female Dance"] = "/fdance",
        ["Male Dance"] = "/mdance",
    };

    public static string StripEmotePrefix(string raw)
    {
        var name = (raw ?? string.Empty).Trim();
        const string prefix = "Emote:";
        if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            name = name[prefix.Length..].Trim();
        return name;
    }

    public static string? FromChangedItemName(string? raw)
    {
        var name = StripEmotePrefix(raw ?? string.Empty);
        if (name.Length == 0)
            return null;

        if (Overrides.TryGetValue(name, out var mapped))
            return mapped;

        var slug = new StringBuilder(name.Length + 1);
        slug.Append('/');
        foreach (var c in name)
        {
            if (c is >= 'a' and <= 'z' or >= '0' and <= '9')
                slug.Append(c);
            else if (c is >= 'A' and <= 'Z')
                slug.Append(char.ToLowerInvariant(c));
        }

        return slug.Length > 1 ? slug.ToString() : null;
    }
}
