using Dalamud.Interface.Colors;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Bindings.ImGui;
using Penumbra.Api.Enums;
using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Interface.Windowing;
using MyModManager.Helpers;
using MyModManager.Models;

namespace MyModManager.Windows;

public class MainWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private string favoriteModSearchText = string.Empty;
    private string cachedSearchText = "\0";
    private int cachedRevision = -1;
    private ContentRatingFilter ratingFilter = ContentRatingFilter.All;
    private string sceneTagFilter = FavoriteGrouping.SceneTagAll;
    private ContentRatingFilter cachedRatingFilter = (ContentRatingFilter)(-1);
    private string cachedSceneTag = "\0";
    private string? statusMessage;
    private List<(string Category, List<(string DisplayName, List<ManagedMod> Mods)> Names)> groupedFavorites = new();

    public MainWindow(Plugin plugin)
      : base("Favorites", ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(300, 400), MaximumSize = new Vector2(float.MaxValue, float.MaxValue) };
        this.plugin = plugin;
    }

    public void Dispose() { }

    public override void Draw()
    {
        ImGui.Spacing();
        plugin.PenumbraOptionSetter.RefreshModStatesIfDue(plugin.Configuration.TargetCollectionId);
        DrawFavoriteModsList();
    }

    private void RebuildFavoriteCacheIfNeeded()
    {
        if (cachedSearchText == favoriteModSearchText
            && cachedRevision == plugin.Configuration.Revision
            && cachedRatingFilter == ratingFilter
            && cachedSceneTag == sceneTagFilter)
            return;

        cachedSearchText = favoriteModSearchText;
        cachedRevision = plugin.Configuration.Revision;
        cachedRatingFilter = ratingFilter;
        cachedSceneTag = sceneTagFilter;
        groupedFavorites = FavoriteGrouping.Build(
            plugin.Configuration.ManagedMods,
            favoriteModSearchText,
            FavoriteFilter.Favorites,
            ratingFilter,
            sceneTagFilter);
    }

    private void DrawFavoriteModsList()
    {
        ImGui.Text("Favorites");
        ImGui.SameLine(ImGui.GetContentRegionMax().X - 90);
        if (ImGui.Button("Add/Edit"))
            plugin.ModManagerWindow.IsOpen = true;
        ManagedModListUi.Hint("Open Add/Edit to add, edit, or star mods. Also /mmm manage.");

        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##favSearch", "Search Favorites...", ref favoriteModSearchText, 100);
        ManagedModListUi.Hint("Search starred mods by name, shortcut, category, or tag.");

        ManagedModListUi.DrawRatingRadios(ref ratingFilter, "fav");
        ManagedModListUi.SameLineIfFits(200);
        ManagedModListUi.DrawSceneTagFilter(ref sceneTagFilter, plugin.Configuration.KnownTags);
        ManagedModListUi.SameLineIfFits(120);
        if (ImGui.Button("Disable temp"))
            plugin.DisableAllTempMods();
        ManagedModListUi.Hint("Turn off every Temp-tagged mod in the target Penumbra collection.");

        if (!string.IsNullOrEmpty(statusMessage))
        {
            ImGui.TextDisabled(statusMessage);
        }

        ImGui.Spacing();
        RebuildFavoriteCacheIfNeeded();

        using (var child = ImRaii.Child("FavoriteModsScroll", Vector2.Zero, true))
        {
            if (!child.Success) return;

            if (!plugin.Configuration.ManagedMods.Exists(m => m.IsFavorite))
            {
                ImGui.TextWrapped("Nothing starred yet. Click Add/Edit (or /mmm manage) to add a Penumbra mod, then star it to see it here.");
            }
            else if (groupedFavorites.Count == 0)
            {
                ImGui.TextDisabled("No favorites match this search or filter.");
            }

            foreach (var categoryGroup in groupedFavorites)
            {
                if (ImGui.CollapsingHeader(categoryGroup.Category, ImGuiTreeNodeFlags.DefaultOpen))
                {
                    ImGui.Indent();
                    foreach (var nameGroup in categoryGroup.Names)
                    {
                        if (nameGroup.Mods.Count > 1)
                        {
                            if (ImGui.TreeNode(nameGroup.DisplayName))
                            {
                                foreach (var mod in nameGroup.Mods)
                                    DrawFavoriteModItem(mod, hideDisplayName: true);
                                ImGui.TreePop();
                            }
                        }
                        else
                        {
                            DrawFavoriteModItem(nameGroup.Mods[0]);
                        }
                    }
                    ImGui.Unindent();
                }
            }
        }
    }

    private void DrawFavoriteModItem(ManagedMod mod, bool hideDisplayName = false)
    {
        ImGui.PushID($"fav_{mod.Id}");

        bool isEnabled = false;
        bool modExists = plugin.PenumbraOptionSetter.ModStates.TryGetValue(mod.ModName, out var state);

        if (modExists)
        {
            if (!string.IsNullOrEmpty(mod.OptionName))
            {
                isEnabled = state.Settings != null && state.Settings.TryGetValue(mod.GroupName, out var list) && list.Contains(mod.OptionName);
            }
            else
            {
                isEnabled = state.Enabled;
            }
        }
        else
        {
            isEnabled = mod.IsEnabled;
        }

        if (ImGui.Checkbox("##enabled", ref isEnabled))
        {
            if (!plugin.TryToggleFavorites(mod, isEnabled))
                isEnabled = !isEnabled;
        }

        if (ImGui.IsItemHovered())
        {
            if (!string.IsNullOrEmpty(mod.OptionName) && mod.GroupType == GroupType.Single)
            {
                ImGui.SetTooltip("Enable this in the target Penumbra collection.\nSingle-select option: ticking selects it. It cannot be unticked - enable a different option from the same group instead.");
            }
            else
            {
                ImGui.SetTooltip("Enable or disable this in the target Penumbra collection.");
            }
        }

        if (mod.IsAnimation)
        {
            ImGui.SameLine();
            if (ImGuiComponents.IconButton(FontAwesomeIcon.Play))
            {
                if (!isEnabled)
                    plugin.TryToggleFavorites(mod, true);

                if (!string.IsNullOrWhiteSpace(mod.AnimationCommand))
                    plugin.SendAnimationCommand(mod.AnimationCommand);
            }
            ManagedModListUi.Hint($"Play animation: {mod.AnimationCommand}");
        }

        ImGui.SameLine();
        ManagedModListUi.DrawClippedRowLabels(mod, hideDisplayName, ManagedModListUi.FavoritesGutter);
        ManagedModListUi.HandleLabelGroupInteraction(mod, ref statusMessage);

        ImGui.SameLine(ImGui.GetContentRegionMax().X - 30);
        var starColor = mod.IsFavorite ? ImGuiColors.DalamudYellow : ImGuiColors.DalamudGrey;
        ImGui.PushStyleColor(ImGuiCol.Text, starColor);
        if (ImGuiComponents.IconButton(FontAwesomeIcon.Star))
        {
            mod.IsFavorite = !mod.IsFavorite;
            plugin.Configuration.Save();
        }
        ImGui.PopStyleColor();
        ManagedModListUi.Hint(mod.IsFavorite ? "Unstar: hides this from Favorites. It stays in Add/Edit." : "Star: show this on Favorites.");

        ImGui.PopID();
    }
}
