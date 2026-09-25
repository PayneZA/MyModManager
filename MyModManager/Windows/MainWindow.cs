using Dalamud.Interface.Colors;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Bindings.ImGui;
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
    private readonly StatusLine status = new();
    private List<(string Category, List<(string DisplayName, List<ManagedMod> Mods)> Names)> groupedFavorites = new();

    public MainWindow(Plugin plugin)
      : base("Favorites###MyModManager.Favorites", ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(300, 400), MaximumSize = new Vector2(float.MaxValue, float.MaxValue) };
        this.plugin = plugin;
    }

    public void Dispose() { }

    public override void Draw()
    {
        ImGui.Spacing();
        ManagedModListUi.DrawPenumbraBanner(plugin);
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
        if (ImGui.Button("Library"))
            plugin.LibraryWindow.IsOpen = true;
        ManagedModListUi.Hint("Open the Library to add, edit or star entries. Also /mmm.");

        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##favSearch", "Search Favorites...", ref favoriteModSearchText, 100);
        ManagedModListUi.Hint("Search starred mods by name, shortcut, category, or tag.");

        ManagedModListUi.DrawRatingRadios(ref ratingFilter, "fav");
        ManagedModListUi.SameLineIfFits(200);
        ManagedModListUi.DrawSceneTagFilter(ref sceneTagFilter, plugin.Configuration.KnownTags);
        ManagedModListUi.SameLineIfFits(160);
        var tempOn = plugin.Entries.CountTemporaryOn();
        using (ImRaii.Disabled(tempOn == 0 && plugin.Configuration.SuspendedIds.Count == 0))
        {
            if (ImGui.Button(tempOn > 0 ? $"Turn off temporary ({tempOn})###tempOff" : "Turn off temporary###tempOff"))
                plugin.Entries.TurnOffTemporary();
        }
        ManagedModListUi.Hint("Turn off every Temp entry, and turn back on kept-on entries that were paused for them.");

        if (plugin.Player.EmoteSyncAvailable)
        {
            ManagedModListUi.SameLineIfFits(110);
            if (ImGui.Button("Emote sync"))
                plugin.Player.EmoteSync();
            ManagedModListUi.Hint("Restart everyone's emote on screen so paired animations line up (Simple Heels' /heels emotesync). Only you see the effect.");
        }

        status.Draw();

        ImGui.Spacing();
        RebuildFavoriteCacheIfNeeded();

        using (var child = ImRaii.Child("FavoriteModsScroll", Vector2.Zero, true))
        {
            if (!child.Success) return;

            if (!plugin.Configuration.ManagedMods.Exists(m => m.IsFavorite))
            {
                ImGui.TextWrapped("Nothing starred yet. Open the Library (/mmm) and star entries to see them here.");
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

        ManagedModListUi.DrawEntryControls(plugin, mod);

        ImGui.SameLine();
        ManagedModListUi.DrawClippedRowLabels(mod, hideDisplayName, ManagedModListUi.FavoritesGutter, plugin.Entries.IsMissing(mod));
        ManagedModListUi.HandleLabelGroupInteraction(mod, status);

        ImGui.SameLine(ImGui.GetContentRegionMax().X - 30);
        var starColor = mod.IsFavorite ? ImGuiColors.DalamudYellow : ImGuiColors.DalamudGrey;
        ImGui.PushStyleColor(ImGuiCol.Text, starColor);
        if (ImGuiComponents.IconButton(FontAwesomeIcon.Star))
        {
            mod.IsFavorite = !mod.IsFavorite;
            plugin.Configuration.Save();
        }
        ImGui.PopStyleColor();
        ManagedModListUi.Hint(mod.IsFavorite ? "Unstar: hides this from Favorites. It stays in the Library." : "Star: show this on Favorites.");

        ImGui.PopID();
    }
}
