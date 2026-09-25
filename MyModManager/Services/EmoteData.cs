using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace MyModManager.Services;

/// <summary>Pose families the game tracks separately. Values match <c>EmoteController.PoseType</c>.</summary>
public enum PoseKind : byte
{
    None = 0,
    Sit = 2,
    GroundSit = 3,
    Doze = 4,
}

public sealed class EmoteInfo
{
    public required uint Id { get; init; }
    public required string Name { get; init; }

    /// <summary>The command people actually type, e.g. "/sit" rather than the canonical "/lounge".</summary>
    public required string Command { get; init; }

    /// <summary>Every spelling the game accepts: command, short command, alias, short alias.</summary>
    public required IReadOnlyList<string> Commands { get; init; }

    public PoseKind PoseKind { get; init; }

    /// <summary>Number of poses including the default one; 0 when the emote has no pose variants.</summary>
    public int PoseCount { get; internal set; }
}

/// <summary>
/// Emote lookups built from the game's Emote sheet, so commands are exact (and localised)
/// instead of guessed from display names.
/// </summary>
public sealed class EmoteData
{
    // Emote row ids are stable across patches and client languages.
    private const uint DozeId = 13;
    private const uint SitId = 50;
    private const uint GroundSitId = 52;

    private readonly Dictionary<string, EmoteInfo> byCommand = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, EmoteInfo> byName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (EmoteInfo Emote, int Pose)> byTimelineKey = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, EmoteInfo> byV1Slug = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<EmoteInfo> All { get; }

    public EmoteData(IDataManager data)
    {
        var list = new List<EmoteInfo>();
        var sheet = data.GetExcelSheet<Emote>();

        foreach (var row in sheet)
        {
            var name = row.Name.ExtractText();
            var textCommand = row.TextCommand.ValueNullable;
            if (name.Length == 0 || textCommand == null)
                continue;

            var tc = textCommand.Value;
            var spellings = new[] { tc.Command, tc.ShortCommand, tc.Alias, tc.ShortAlias }
                .Select(s => s.ExtractText().Trim())
                .Where(s => s.StartsWith('/'))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (spellings.Count == 0)
                continue;

            var alias = tc.Alias.ExtractText().Trim();
            var kind = row.RowId switch
            {
                SitId => PoseKind.Sit,
                GroundSitId => PoseKind.GroundSit,
                DozeId => PoseKind.Doze,
                _ => PoseKind.None,
            };

            var info = new EmoteInfo
            {
                Id = row.RowId,
                Name = name,
                Command = alias.StartsWith('/') ? alias : spellings[0],
                Commands = spellings,
                PoseKind = kind,
            };

            list.Add(info);
            byName.TryAdd(name, info);
            byV1Slug.TryAdd(V1Slug(name), info);
            foreach (var spelling in spellings)
                byCommand.TryAdd(spelling, info);

            foreach (var timeline in row.ActionTimeline)
            {
                var key = timeline.ValueNullable?.Key.ExtractText();
                if (!string.IsNullOrEmpty(key))
                    byTimelineKey.TryAdd(key, (info, kind == PoseKind.None ? 0 : 1));
            }
        }

        All = list.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
        IndexPoseTimelines(data);
    }

    /// <summary>
    /// Maps pose timelines (e.g. "emote/j_pose02_loop") to their emote and 1-based pose number,
    /// where pose 1 is the emote's default. Pose counts come from the same data.
    /// </summary>
    private void IndexPoseTimelines(IDataManager data)
    {
        var prefixes = new (string Prefix, uint EmoteId)[]
        {
            ("emote/s_pose", SitId),
            ("emote/j_pose", GroundSitId),
            ("emote/l_pose", DozeId),
        };

        var maxPose = new Dictionary<uint, int>();
        foreach (var timeline in data.GetExcelSheet<ActionTimeline>())
        {
            var key = timeline.Key.ExtractText();
            foreach (var (prefix, emoteId) in prefixes)
            {
                if (!key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    continue;

                var digits = key.AsSpan(prefix.Length);
                var end = 0;
                while (end < digits.Length && char.IsAsciiDigit(digits[end]))
                    end++;
                if (end == 0 || !int.TryParse(digits[..end], out var fileIndex))
                    continue;

                var emote = All.FirstOrDefault(e => e.Id == emoteId);
                if (emote == null)
                    continue;

                // s_pose01 is the first alternate pose, so it is pose 2 in the /cpose cycle.
                var pose = fileIndex + 1;
                byTimelineKey.TryAdd(key, (emote, pose));
                maxPose[emoteId] = Math.Max(maxPose.GetValueOrDefault(emoteId, 1), pose);
            }
        }

        foreach (var emote in All)
        {
            if (maxPose.TryGetValue(emote.Id, out var count))
                emote.PoseCount = count;
        }
    }

    /// <summary>Resolves a command such as "/groundsit" or "/sit motion" to its emote.</summary>
    public EmoteInfo? FromCommand(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return null;

        var first = command.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries)[0];
        if (!first.StartsWith('/'))
            first = "/" + first;
        return byCommand.GetValueOrDefault(first);
    }

    /// <summary>Resolves an emote display name, e.g. from Penumbra's "Emote: Sit on Ground" changed item.</summary>
    public EmoteInfo? FromName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;

        var trimmed = name.Trim();
        const string prefix = "Emote:";
        if (trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed[prefix.Length..].Trim();
        return byName.GetValueOrDefault(trimmed);
    }

    /// <summary>
    /// V1 built commands by lower-casing an emote's name and dropping everything but letters and
    /// digits ("Thavnairian Dance" became "/thavnairiandance", which isn't a real command).
    /// Returns the emote such a guessed command was derived from, if any.
    /// </summary>
    public EmoteInfo? FromV1GuessedCommand(string? command)
    {
        if (string.IsNullOrWhiteSpace(command) || FromCommand(command) != null)
            return null;
        return byV1Slug.GetValueOrDefault(command.Trim());
    }

    private static string V1Slug(string name)
    {
        var slug = new System.Text.StringBuilder("/", name.Length + 1);
        foreach (var c in name)
        {
            if (char.IsAsciiLetterOrDigit(c))
                slug.Append(char.ToLowerInvariant(c));
        }
        return slug.ToString();
    }

    /// <summary>Resolves an animation timeline key such as "emote/j_pose01_loop".</summary>
    public (EmoteInfo Emote, int Pose)? FromTimelineKey(string key) =>
        byTimelineKey.TryGetValue(key, out var hit) ? hit : null;
}
