using System;
using System.Collections.Generic;
using Dalamud.Bindings.ImGui;
using MyModManager.Models;

namespace MyModManager.Helpers;

/// <summary>Small text helpers shared by the Library and Add windows.</summary>
public static class ManagedModListUi
{
    /// <summary>"Mod / Group / Option" for an entry.</summary>
    public static string FormatPenumbraPath(ManagedMod mod)
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(mod.ModName))
            parts.Add(mod.ModName);
        if (!string.IsNullOrEmpty(mod.GroupName))
            parts.Add(mod.GroupName);
        if (!string.IsNullOrEmpty(mod.OptionName))
            parts.Add(mod.OptionName);
        return string.Join(" / ", parts);
    }

    public static string FormatPenumbraTooltip(ManagedMod mod)
    {
        var option = string.IsNullOrEmpty(mod.OptionName) ? "whole mod" : mod.OptionName;
        var lines = new List<string> { $"Mod: {mod.ModName}" };
        if (!string.IsNullOrEmpty(mod.GroupName))
            lines.Add($"Group: {mod.GroupName}");
        lines.Add($"Option: {option}");
        return string.Join("\n", lines);
    }

    public static string PoseLabel(int? pose) => pose switch
    {
        null => "Any pose",
        0 => "Default pose",
        var n => $"Pose {n}",
    };
}

/// <summary>A one-line status message that clears itself after a few seconds.</summary>
public sealed class StatusLine
{
    private string? text;
    private DateTime until;

    public void Set(string message, double seconds = 4)
    {
        text = message;
        until = DateTime.UtcNow.AddSeconds(seconds);
    }

    public void Draw()
    {
        if (text != null && DateTime.UtcNow < until)
            ImGui.TextDisabled(text);
    }
}
