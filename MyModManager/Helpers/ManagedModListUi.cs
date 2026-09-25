using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility.Raii;
using System;
using System.Collections.Generic;
using MyModManager.Models;
using Penumbra.Api.Enums;

namespace MyModManager.Helpers;

public static class ManagedModListUi
{
    public const float FavoritesGutter = 36f;
    public const float AddEditGutter = 100f;

    public static void Hint(string text)
    {
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(text);
    }

    /// <summary>
    /// The on/off checkbox and Play button shared by both windows. State comes from Penumbra;
    /// entries whose mod is uninstalled are shown but can't be toggled.
    /// </summary>
    public static void DrawEntryControls(Plugin plugin, ManagedMod mod)
    {
        var missing = plugin.Entries.IsMissing(mod);
        var isOn = plugin.Entries.IsOn(mod) ?? false;
        var canAct = plugin.Penumbra.Available && !missing;

        using (ImRaii.Disabled(!canAct))
        {
            if (ImGui.Checkbox("##enabled", ref isOn))
                plugin.Entries.Apply(plugin.Entries.ResolveShortcutGroup(mod), isOn);
        }

        if (missing)
            Hint($"\"{mod.ModName}\" is not installed in Penumbra.");
        else if (!plugin.Penumbra.Available)
            Hint("Penumbra isn't available.");
        else if (!string.IsNullOrEmpty(mod.OptionName) && mod.GroupType == GroupType.Single)
            Hint("Turn this option on or off. Turning it off selects the group's \"None\" option, if it has one.");
        else
            Hint("Turn this on or off in Penumbra.");

        if (!mod.IsAnimation)
            return;

        ImGui.SameLine();
        using (ImRaii.Disabled(!canAct || plugin.Player.IsBusy))
        {
            if (ImGuiComponents.IconButton(FontAwesomeIcon.Play))
                plugin.Player.Play(mod);
        }
        Hint(string.IsNullOrWhiteSpace(mod.AnimationCommand)
            ? "Turn on (this entry has no emote command)."
            : $"Turn on and play {FormatCommand(mod)}.");
    }

    public static string FormatCommand(ManagedMod mod) => mod.PoseNumber switch
    {
        null => mod.AnimationCommand,
        0 => $"{mod.AnimationCommand} · default pose",
        var n => $"{mod.AnimationCommand} · pose {n}",
    };

    public static string PoseLabel(int? pose) => pose switch
    {
        null => "Any pose",
        0 => "Default pose",
        var n => $"Pose {n}",
    };

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
        lines.Add("Right-click to copy.");
        return string.Join("\n", lines);
    }

    public static void HandleLabelGroupInteraction(ManagedMod mod, StatusLine status)
    {
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(FormatPenumbraTooltip(mod));

        if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
        {
            ImGui.SetClipboardText(FormatPenumbraPath(mod));
            status.Set("Copied Penumbra path.");
        }
    }

    /// <summary>A warning line when Penumbra is missing, or when a chosen collection was deleted.</summary>
    public static void DrawPenumbraBanner(Plugin plugin)
    {
        if (!plugin.Penumbra.Available)
        {
            ImGui.TextColored(ImGuiColors.DalamudOrange, "Penumbra isn't running. Entries can't be turned on or off until it is.");
            ImGui.Spacing();
        }
        else if (plugin.Penumbra.TargetCollectionMissing)
        {
            ImGui.TextColored(ImGuiColors.DalamudOrange, $"Your chosen collection no longer exists. Using \"{plugin.Penumbra.CollectionName}\" instead.");
            ImGui.Spacing();
        }
    }

    public static void DrawClippedRowLabels(ManagedMod mod, bool hideDisplayName, float gutter, bool missing = false)
    {
        float labelWidth = Math.Max(24f, ImGui.GetContentRegionAvail().X - gutter);
        var clipMin = ImGui.GetCursorScreenPos();
        var clipMax = new System.Numerics.Vector2(clipMin.X + labelWidth, clipMin.Y + ImGui.GetTextLineHeightWithSpacing());
        ImGui.PushClipRect(clipMin, clipMax, true);
        ImGui.BeginGroup();

        bool wrote = false;
        void Next()
        {
            if (wrote)
                ImGui.SameLine();
        }

        if (!hideDisplayName && !string.IsNullOrEmpty(mod.DisplayName))
        {
            if (missing)
                ImGui.TextColored(ImGuiColors.DalamudOrange, mod.DisplayName);
            else
                ImGui.TextUnformatted(mod.DisplayName);
            wrote = true;
        }

        if (!string.IsNullOrWhiteSpace(mod.ShortcutName))
        {
            Next();
            ImGui.TextDisabled($"<{mod.ShortcutName}>");
            wrote = true;
        }

        if (mod.IsAnimation && !string.IsNullOrWhiteSpace(mod.AnimationCommand))
        {
            Next();
            ImGui.TextDisabled(FormatCommand(mod));
            wrote = true;
        }

        var meta = FavoriteGrouping.FormatMetaLabels(mod);
        if (!string.IsNullOrEmpty(meta))
        {
            Next();
            ImGui.TextDisabled($"[{meta}]");
            wrote = true;
        }

        if (!wrote)
            ImGui.Dummy(new System.Numerics.Vector2(8, ImGui.GetTextLineHeight()));

        ImGui.EndGroup();
        ImGui.PopClipRect();
    }

    public static void SameLineIfFits(float neededWidth)
    {
        if (ImGui.GetContentRegionAvail().X >= neededWidth)
            ImGui.SameLine();
    }

    public static void DrawRatingRadios(ref ContentRatingFilter ratingFilter, string idSuffix)
    {
        if (ImGui.RadioButton($"All##rating{idSuffix}", ratingFilter == ContentRatingFilter.All))
            ratingFilter = ContentRatingFilter.All;
        Hint("Show SFW, NSFW, and unrated (Imported) entries.");
        ImGui.SameLine();
        if (ImGui.RadioButton($"SFW##rating{idSuffix}", ratingFilter == ContentRatingFilter.Sfw))
            ratingFilter = ContentRatingFilter.Sfw;
        Hint("Only entries marked SFW.");
        ImGui.SameLine();
        if (ImGui.RadioButton($"NSFW##rating{idSuffix}", ratingFilter == ContentRatingFilter.Nsfw))
            ratingFilter = ContentRatingFilter.Nsfw;
        Hint("Only entries marked NSFW.");
    }

    public static void DrawSceneTagFilter(ref string sceneTagFilter, IReadOnlyList<string> knownTags)
    {
        ImGui.SetNextItemWidth(180);
        var tagPreview = sceneTagFilter switch
        {
            FavoriteGrouping.SceneTagAll => "Tag: All",
            FavoriteGrouping.SceneTagUntagged => "Tag: Untagged",
            _ => $"Tag: {sceneTagFilter}"
        };
        if (ImGui.BeginCombo("##sceneTagFilter", tagPreview))
        {
            if (ImGui.Selectable("All", sceneTagFilter == FavoriteGrouping.SceneTagAll))
                sceneTagFilter = FavoriteGrouping.SceneTagAll;
            if (ImGui.Selectable("Untagged", sceneTagFilter == FavoriteGrouping.SceneTagUntagged))
                sceneTagFilter = FavoriteGrouping.SceneTagUntagged;
            foreach (var tag in knownTags)
            {
                if (ImGui.Selectable(tag, sceneTagFilter.Equals(tag, StringComparison.OrdinalIgnoreCase)))
                    sceneTagFilter = tag;
            }
            ImGui.EndCombo();
        }
        Hint("Filter by a scene tag assigned on Add/Edit.");
    }
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
