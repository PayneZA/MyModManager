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

    /// <summary>1-based pose in the /cpose cycle (1 = the emote's default); 0 when not set.</summary>
    public int Pose { get; set; } = 0;

    /// <summary>Single-choice groups only: the option selected to turn this entry off. Empty = auto-detect.</summary>
    public string OffOption { get; set; } = string.Empty;
}
