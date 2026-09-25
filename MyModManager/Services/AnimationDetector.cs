using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace MyModManager.Services;

public enum DetectionSource
{
    Files,
    Name,
}

/// <summary>One emote (and pose) a mod or option replaces, with the role it plays if the name says.</summary>
public sealed record Detection(EmoteInfo Emote, int? Pose, string Role, DetectionSource Source);

/// <summary>
/// Works out which emote and pose an animation replaces. Files first: a replaced path such as
/// ".../bt_common/emote/j_pose02_loop.pap" maps to ground sit pose 2 through the game's timeline
/// data. Names second: "[Gsit1_2]", "(Gdance)". Role labels come from names such as
/// "(Dom-Gsit2/Sub-Gsit3)" or "(Dom-Tdance/Sub-Beesknees)".
/// </summary>
public static class AnimationDetector
{
    private static readonly Regex PapPath = new(@"/animation/a\d{4}/bt_common/(.+)\.pap$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Gsit1_2, Gsit0,1,2,3, GroundSit2, Sit1 Sit2, Chairsit1, Csit2, Lpose1
    private static readonly Regex PoseTag = new(@"(?<kind>g(?:round)?\s?sit|c(?:hair)?\s?sit|sit|l\s?pose)\s?(?<nums>\d(?:\s?[_,&]\s?\d)*)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Parenthesised or bracketed single words, e.g. "(Gdance)", "[beesknees]".
    private static readonly Regex Wrapped = new(@"[\(\[]\s*([A-Za-z]+)\s*[\)\]]", RegexOptions.Compiled);

    // Role-Emote pairs separated by '/', e.g. "Dom-Gsit2/Sub-Gsit3".
    private static readonly Regex RolePair = new(@"(?<role>[A-Za-z]+)\s?-\s?(?<what>[A-Za-z]+(?:\s?\d(?:\s?[_,&]\s?\d)*)?)", RegexOptions.Compiled);

    public static List<Detection> Detect(EmoteData emotes, string name, IEnumerable<string> gamePaths)
    {
        var found = FromGamePaths(emotes, gamePaths).Select(d => d with { Source = DetectionSource.Files }).ToList();
        if (found.Count == 0)
            found = FromName(emotes, name);

        var roles = RolesFromName(emotes, name);
        return found.Select(d => d with { Role = RoleFor(roles, d) }).ToList();
    }

    public static List<Detection> FromGamePaths(EmoteData emotes, IEnumerable<string> gamePaths)
    {
        var result = new List<Detection>();
        foreach (var path in gamePaths)
        {
            var match = PapPath.Match(path);
            if (!match.Success)
                continue;
            var key = match.Groups[1].Value;
            // Sit/stand transitions are shared by every pose; they don't identify one.
            if (key.StartsWith("event_base/", StringComparison.OrdinalIgnoreCase))
                continue;
            if (emotes.FromTimelineKey(key) is not { } hit)
                continue;

            int? pose = hit.Emote.PoseKind == PoseKind.None ? null : hit.Pose;
            if (!result.Any(r => r.Emote.Id == hit.Emote.Id && r.Pose == pose))
                result.Add(new Detection(hit.Emote, pose, string.Empty, DetectionSource.Files));
        }

        return Order(result);
    }

    public static List<Detection> FromName(EmoteData emotes, string name)
    {
        var result = new List<Detection>();
        foreach (Match tag in PoseTag.Matches(name))
        {
            if (PoseEmote(emotes, tag.Groups["kind"].Value) is not { } emote)
                continue;
            foreach (var pose in Numbers(tag.Groups["nums"].Value))
                Add(result, emote, pose);
        }

        foreach (Match word in Wrapped.Matches(name))
        {
            if (emotes.FromCommand("/" + word.Groups[1].Value) is { } emote)
                Add(result, emote, null);
        }

        return Order(result.Select(d => d with { Source = DetectionSource.Name }).ToList());
    }

    /// <summary>Maps each (emote, pose) mentioned as "Role-Emote" to its role name.</summary>
    private static Dictionary<(uint Emote, int? Pose), string> RolesFromName(EmoteData emotes, string name)
    {
        var roles = new Dictionary<(uint, int?), string>();
        foreach (Match pair in RolePair.Matches(name))
        {
            var role = pair.Groups["role"].Value;
            var what = pair.Groups["what"].Value;
            var tag = PoseTag.Match(what);
            if (tag.Success && tag.Index == 0 && PoseEmote(emotes, tag.Groups["kind"].Value) is { } poseEmote)
            {
                foreach (var pose in Numbers(tag.Groups["nums"].Value))
                    roles.TryAdd((poseEmote.Id, pose), role);
            }
            else if (emotes.FromCommand("/" + what.Trim()) is { } emote)
            {
                roles.TryAdd((emote.Id, null), role);
            }
        }
        return roles;
    }

    private static string RoleFor(Dictionary<(uint Emote, int? Pose), string> roles, Detection d) =>
        roles.TryGetValue((d.Emote.Id, d.Pose), out var exact) ? exact
        : roles.TryGetValue((d.Emote.Id, null), out var any) ? any
        : string.Empty;

    private static EmoteInfo? PoseEmote(EmoteData emotes, string kind)
    {
        var k = kind.Replace(" ", string.Empty).ToLowerInvariant();
        var command = k switch
        {
            "gsit" or "groundsit" => "/groundsit",
            "sit" or "chairsit" or "csit" => "/sit",
            "lpose" => "/doze",
            _ => null,
        };
        return command == null ? null : emotes.FromCommand(command);
    }

    private static IEnumerable<int> Numbers(string text) =>
        text.Where(char.IsAsciiDigit).Select(c => c - '0').Distinct();

    private static void Add(List<Detection> list, EmoteInfo emote, int? pose)
    {
        if (emote.PoseKind != PoseKind.None && pose is { } p && emote.PoseCount > 0 && p >= emote.PoseCount)
            return;
        if (!list.Any(d => d.Emote.Id == emote.Id && d.Pose == pose))
            list.Add(new Detection(emote, pose, string.Empty, DetectionSource.Name));
    }

    private static List<Detection> Order(List<Detection> list) =>
        list.OrderBy(d => d.Emote.Command, StringComparer.OrdinalIgnoreCase).ThenBy(d => d.Pose ?? -1).ToList();

    /// <summary>A short label for one detection, e.g. "/groundsit · pose 2" or "Dom".</summary>
    public static string Describe(Detection d)
    {
        var command = d.Pose switch
        {
            null => d.Emote.Command,
            0 => $"{d.Emote.Command} · default pose",
            var p => $"{d.Emote.Command} · pose {p}",
        };
        return d.Role.Length > 0 ? $"{d.Role} ({command})" : command;
    }
}
