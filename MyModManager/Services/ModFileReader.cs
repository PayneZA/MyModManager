using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Penumbra.Api.Enums;

namespace MyModManager.Services;

public sealed record OptionFiles(string Name, IReadOnlyList<string> GamePaths);

public sealed record GroupFiles(string Name, GroupType Type, IReadOnlyList<OptionFiles> Options);

/// <summary>The game paths a mod replaces, per option group and option.</summary>
public sealed record ModFiles(IReadOnlyList<string> DefaultPaths, IReadOnlyList<GroupFiles> Groups)
{
    public IEnumerable<string> AllPaths => DefaultPaths.Concat(Groups.SelectMany(g => g.Options).SelectMany(o => o.GamePaths));
}

/// <summary>
/// Reads a Penumbra mod folder's JSON to learn which game files each option replaces.
/// Supports the current format (groups inside meta.json, FileVersion 4) and the older one
/// (group_*.json plus default_mod.json). Read-only; never writes to the mod.
/// </summary>
public static class ModFileReader
{
    private static readonly JsonDocumentOptions Options = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };

    public static ModFiles? Read(string modFolder)
    {
        try
        {
            var meta = Path.Combine(modFolder, "meta.json");
            if (!File.Exists(meta))
                return null;

            using var metaDoc = JsonDocument.Parse(File.ReadAllText(meta), Options);
            var root = metaDoc.RootElement;
            if (root.TryGetProperty("Groups", out var groups) && groups.ValueKind == JsonValueKind.Array)
            {
                var defaults = root.TryGetProperty("DefaultData", out var data) ? PathsOf(data) : new List<string>();
                return new ModFiles(defaults, groups.EnumerateArray().Select(ReadGroup).ToList());
            }

            return ReadLegacy(modFolder);
        }
        catch (Exception ex)
        {
            Svc.Log.Warning(ex, $"Couldn't read mod files in {modFolder}.");
            return null;
        }
    }

    private static ModFiles ReadLegacy(string modFolder)
    {
        var defaults = new List<string>();
        var defaultFile = Path.Combine(modFolder, "default_mod.json");
        if (File.Exists(defaultFile))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(defaultFile), Options);
            defaults = PathsOf(doc.RootElement);
        }

        var groups = new List<GroupFiles>();
        foreach (var file in Directory.EnumerateFiles(modFolder, "group_*.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file), Options);
            groups.Add(ReadGroup(doc.RootElement));
        }

        return new ModFiles(defaults, groups);
    }

    private static GroupFiles ReadGroup(JsonElement group)
    {
        var name = group.TryGetProperty("Name", out var n) ? n.GetString() ?? string.Empty : string.Empty;
        var typeText = group.TryGetProperty("Type", out var t) ? t.GetString() : null;
        var type = Enum.TryParse<GroupType>(typeText, true, out var parsed) ? parsed : GroupType.Single;

        var options = new List<OptionFiles>();
        if (group.TryGetProperty("Options", out var opts) && opts.ValueKind == JsonValueKind.Array)
        {
            foreach (var option in opts.EnumerateArray())
            {
                var optionName = option.TryGetProperty("Name", out var on) ? on.GetString() ?? string.Empty : string.Empty;
                options.Add(new OptionFiles(optionName, PathsOf(option)));
            }
        }

        return new GroupFiles(name, type, options);
    }

    /// <summary>Replaced game paths: keys of "Files" and "FileSwaps".</summary>
    private static List<string> PathsOf(JsonElement container)
    {
        var paths = new List<string>();
        foreach (var property in new[] { "Files", "FileSwaps" })
        {
            if (container.ValueKind == JsonValueKind.Object && container.TryGetProperty(property, out var map) && map.ValueKind == JsonValueKind.Object)
                paths.AddRange(map.EnumerateObject().Select(p => p.Name.Replace('\\', '/')));
        }
        return paths;
    }
}
