using Penumbra.Api.Enums;
using System;
using System.Collections.Generic;

namespace MyModManager.Models;

public enum ContentRating
{
    Sfw,
    Nsfw,
    Unrated
}

[Serializable]
public class ManagedMod
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string ModName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string ShortcutName { get; set; } = string.Empty;
    public string CategoryName { get; set; } = "Default";
    public bool IsEnabled { get; set; } = false;

    /// <summary>Missing in older configs; default true so previously added entries stay starred.</summary>
    public bool IsFavorite { get; set; } = true;

    public ContentRating Rating { get; set; } = ContentRating.Sfw;
    public bool IsTemp { get; set; } = false;
    public List<string> Tags { get; set; } = new();

    // Animation specific fields
    public bool IsAnimation { get; set; } = false;
    public string AnimationCommand { get; set; } = string.Empty;

    // Optional fields for shortcutting specific options
    public string GroupName { get; set; } = string.Empty;
    public string OptionName { get; set; } = string.Empty;
    public GroupType GroupType { get; set; } = GroupType.Single;

    /// <summary>
    /// Which pose of sit / ground sit / doze this entry replaces, numbered the way mod names
    /// do ("Sit1", "Gsit2"): 0 is the default pose, 1 the first alternate. Null = any pose.
    /// Equals the game's selected-pose index.
    /// </summary>
    public int? PoseNumber { get; set; }

    /// <summary>Early V2 dev builds stored the pose 1-based (1 = default). Migrated to <see cref="PoseNumber"/>.</summary>
    public int Pose { get; set; } = 0;

    public bool ShouldSerializePose() => false;

    /// <summary>After playing, re-sync everyone's emote animation on screen (Simple Heels' /heels emotesync).</summary>
    public bool AutoEmoteSync { get; set; } = false;

    /// <summary>Single-choice groups only: the option selected to turn this entry off. Empty = auto-detect.</summary>
    public string OffOption { get; set; } = string.Empty;
}
