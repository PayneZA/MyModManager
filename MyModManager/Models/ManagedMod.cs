using Penumbra.Api.Enums;
using System;

namespace MyModManager.Models;

[Serializable]
public class ManagedMod
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string ModName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string ShortcutName { get; set; } = string.Empty;
    public string CategoryName { get; set; } = "Default";
    public bool IsEnabled { get; set; } = false;

    // Animation specific fields
    public bool IsAnimation { get; set; } = false;
    public string AnimationCommand { get; set; } = string.Empty;

    // Optional fields for shortcutting specific options
    public string GroupName { get; set; } = string.Empty;
    public string OptionName { get; set; } = string.Empty;
    public GroupType GroupType { get; set; } = GroupType.Single;
}
