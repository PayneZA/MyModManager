using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using MyModManager.Services;
using Penumbra.Api.Enums;

namespace MyModManager.Helpers;

public class AnimationScanCandidate
{
    public string ModDirectory { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string EmoteName { get; set; } = string.Empty;
    public string Command { get; set; } = string.Empty;
    public bool Selected = true;
}

public class AnimationScanExclusion
{
    public string DisplayName { get; set; } = string.Empty;
    public int EmoteCount { get; set; }
}

public class AnimationScanResult
{
    public List<AnimationScanCandidate> Importable { get; } = new();
    public List<AnimationScanExclusion> ExcludedMultiEmote { get; } = new();
}

public static class AnimationModScanner
{
    public static AnimationScanResult Scan(
        IReadOnlyDictionary<string, string> modList,
        IReadOnlyList<(string ModDirectory, Dictionary<string, object?> ChangedItems)> changedItems,
        IReadOnlySet<string> alreadyManagedDirectories,
        EmoteData emotes)
    {
        var result = new AnimationScanResult();
        var byDirectory = new Dictionary<string, Dictionary<string, object?>>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in changedItems)
            byDirectory[entry.ModDirectory] = entry.ChangedItems;

        foreach (var (directory, displayName) in modList)
        {
            if (alreadyManagedDirectories.Contains(directory))
                continue;

            byDirectory.TryGetValue(directory, out var items);
            items ??= new Dictionary<string, object?>();

            Classify(directory, displayName, items, result, emotes);
        }

        result.Importable.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase));
        result.ExcludedMultiEmote.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase));
        return result;
    }

    private static void Classify(
        string directory,
        string displayName,
        Dictionary<string, object?> items,
        AnimationScanResult result,
        EmoteData emotes)
    {
        var emoteNames = new List<string>();
        foreach (var kv in items)
        {
            if (!IsEmoteChangedItem(kv.Key, kv.Value))
                continue;

            var name = EmoteCommandMapper.StripEmotePrefix(kv.Key);
            if (name.Length > 0)
                emoteNames.Add(name);
        }

        if (emoteNames.Count > 1)
        {
            result.ExcludedMultiEmote.Add(new AnimationScanExclusion
            {
                DisplayName = displayName,
                EmoteCount = emoteNames.Count
            });
            return;
        }

        if (emoteNames.Count != 1)
            return;

        var emote = emoteNames[0];
        result.Importable.Add(new AnimationScanCandidate
        {
            ModDirectory = directory,
            DisplayName = displayName,
            EmoteName = emote,
            Command = emotes.FromName(emote)?.Command ?? string.Empty,
            Selected = true
        });
    }

    private static bool IsEmoteChangedItem(string key, object? value) =>
        IsEmoteKey(key) || ResolveType(key, value) == ChangedItemType.Emote;

    private static bool IsEmoteKey(string key) =>
        !string.IsNullOrEmpty(key) && key.StartsWith("Emote:", StringComparison.OrdinalIgnoreCase);

    private static ChangedItemType ResolveType(string key, object? value)
    {
        if (IsEmoteKey(key))
            return ChangedItemType.Emote;

        if (value is ChangedItemType direct)
            return direct;

        if (value is int numeric)
            return (ChangedItemType)numeric;

        if (value is ValueTuple<ChangedItemType, object?> typedTuple)
            return typedTuple.Item1;

        if (value is ITuple tuple && tuple.Length >= 1)
        {
            var first = tuple[0];
            if (first is ChangedItemType t)
                return t;
            if (first is int n)
                return (ChangedItemType)n;
        }

        return ChangedItemType.None;
    }
}
