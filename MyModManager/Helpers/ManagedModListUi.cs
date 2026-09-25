using Dalamud.Bindings.ImGui;
using System;
using System.Collections.Generic;
using MyModManager.Models;

namespace MyModManager.Helpers;

public static class ManagedModListUi
{
    public const float FavoritesGutter = 36f;
    public const float AddEditGutter = 100f;

    public static void Hint(string text)
    {
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(text);
    }

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

    public static void HandleLabelGroupInteraction(ManagedMod mod, ref string? status)
    {
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(FormatPenumbraTooltip(mod));

        if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
        {
            ImGui.SetClipboardText(FormatPenumbraPath(mod));
            status = "Copied Penumbra path.";
        }
    }

    public static void DrawClippedRowLabels(ManagedMod mod, bool hideDisplayName, float gutter)
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
            ImGui.TextDisabled(mod.AnimationCommand);
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
