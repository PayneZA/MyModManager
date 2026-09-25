using System;
using System.Collections.Generic;

namespace MyModManager.Models;

/// <summary>How the entries of a mod type behave and are drawn.</summary>
public enum ModTypeStyle
{
    /// <summary>Emote command, pose and a Play button.</summary>
    Animation,

    /// <summary>A plain on/off toggle.</summary>
    Toggle,

    /// <summary>Entries bound to options of the same Penumbra group, shown as one switch.</summary>
    Pick,
}

/// <summary>A user-defined tab in the Library, e.g. "Animations", "Body", "VFX".</summary>
[Serializable]
public class ModType
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public ModTypeStyle Style { get; set; } = ModTypeStyle.Toggle;

    /// <summary>The category vocabulary offered for entries of this type.</summary>
    public List<string> Categories { get; set; } = new();
}
