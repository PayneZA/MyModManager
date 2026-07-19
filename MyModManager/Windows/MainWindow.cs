using Dalamud.Interface.Colors;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility.Raii;
using ECommons.DalamudServices;
using Dalamud.Bindings.ImGui;
using Penumbra.Api.Enums;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Interface.Windowing;
using MyModManager.Models;
using ECommons.Automation;

namespace MyModManager.Windows;

public class MainWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private string favoriteModSearchText = string.Empty;
    private Dictionary<string, (bool Enabled, Dictionary<string, List<string>> Settings)> penumbraModStates = new();
    private DateTime lastModStateRefresh = DateTime.MinValue;

    public MainWindow(Plugin plugin)
      : base("My Mod Manager", ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(300, 400), MaximumSize = new Vector2(float.MaxValue, float.MaxValue) };
        this.plugin = plugin;
    }

    public void Dispose() { }

    public override void Draw()
    {
        ImGui.Spacing();
        RefreshPenumbraModStates();
        DrawFavoriteModsList();
    }

    private void RefreshPenumbraModStates()
    {
        if ((DateTime.Now - lastModStateRefresh).TotalSeconds < 2) return;
        // Arm the throttle before attempting, so failures also wait out the interval
        // instead of retrying (and logging) every frame while Penumbra is unavailable.
        lastModStateRefresh = DateTime.Now;

        try
        {
            Guid collectionId = plugin.Configuration.TargetCollectionId;
            if (collectionId == Guid.Empty)
            {
                var current = new Penumbra.Api.IpcSubscribers.GetCollection(plugin.Interface).Invoke(ApiCollectionType.Current);
                if (current != null) collectionId = current.Value.Id;
            }

            if (collectionId != Guid.Empty)
            {
                var (ec, settings) = new Penumbra.Api.IpcSubscribers.GetAllModSettings(plugin.Interface).Invoke(collectionId, false, false, 0);
                if (ec == PenumbraApiEc.Success && settings != null)
                {
                    penumbraModStates = settings.ToDictionary(k => k.Key, v => (v.Value.Item1, v.Value.Item3));
                }
            }
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, "Failed to refresh Penumbra mod states.");
        }
    }

    private void DrawFavoriteModsList()
    {
        ImGui.Text("Favorites");
        ImGui.SameLine(ImGui.GetContentRegionMax().X - 80);
        if (ImGui.Button("Manage"))
        {
            plugin.ModManagerWindow.IsOpen = true;
        }

        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##favSearch", "Search Favorites...", ref favoriteModSearchText, 100);

        ImGui.Spacing();

        var query = plugin.Configuration.ManagedMods.AsEnumerable();
        if (!string.IsNullOrWhiteSpace(favoriteModSearchText))
        {
            query = query.Where(m => 
                m.DisplayName.Contains(favoriteModSearchText, StringComparison.OrdinalIgnoreCase) ||
                m.ModName.Contains(favoriteModSearchText, StringComparison.OrdinalIgnoreCase) ||
                m.ShortcutName.Contains(favoriteModSearchText, StringComparison.OrdinalIgnoreCase) ||
                m.CategoryName.Contains(favoriteModSearchText, StringComparison.OrdinalIgnoreCase));
        }

        var groupedByCategory = query.GroupBy(m => m.CategoryName).OrderBy(g => g.Key);

        using (var child = ImRaii.Child("FavoriteModsScroll", Vector2.Zero, true))
        {
            if (!child.Success) return;

            foreach (var categoryGroup in groupedByCategory)
            {
                if (ImGui.CollapsingHeader(categoryGroup.Key, ImGuiTreeNodeFlags.DefaultOpen))
                {
                    ImGui.Indent();
                    var groupedByName = categoryGroup.GroupBy(m => m.DisplayName).OrderBy(g => g.Key);
                    
                    foreach (var nameGroup in groupedByName)
                    {
                        var modsInNameGroup = nameGroup.ToList();
                        
                        if (modsInNameGroup.Count > 1)
                        {
                            if (ImGui.TreeNode(nameGroup.Key))
                            {
                                foreach (var mod in modsInNameGroup)
                                {
                                    DrawFavoriteModItem(mod, hideDisplayName: true);
                                }
                                ImGui.TreePop();
                            }
                        }
                        else
                        {
                            DrawFavoriteModItem(modsInNameGroup[0]);
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
        bool modExists = penumbraModStates.TryGetValue(mod.ModName, out var state);

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
            var targetState = isEnabled;
            var modsToToggle = new List<ManagedMod> { mod };

            if (!string.IsNullOrWhiteSpace(mod.ShortcutName))
            {
                modsToToggle = plugin.Configuration.ManagedMods
                    .Where(m => m.ShortcutName.Equals(mod.ShortcutName, StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }

            bool redrawNeeded = false;
            foreach (var m in modsToToggle)
            {
                m.IsEnabled = targetState;
                if (plugin.PenumbraOptionSetter.SetManagedModState(m, targetState, plugin.Configuration.TargetCollectionId))
                {
                    redrawNeeded = true;
                }
            }

            plugin.Configuration.Save();

            if (redrawNeeded)
            {
                new Penumbra.Api.IpcSubscribers.RedrawObject(plugin.Interface).Invoke(0, RedrawType.Redraw);
                lastModStateRefresh = DateTime.MinValue; // Force refresh
            }
        }

        if (mod.IsAnimation)
        {
            ImGui.SameLine();
            if (ImGuiComponents.IconButton(FontAwesomeIcon.Play))
            {
                if (!isEnabled)
                {
                    var modsToToggle = new List<ManagedMod> { mod };

                    if (!string.IsNullOrWhiteSpace(mod.ShortcutName))
                    {
                        modsToToggle = plugin.Configuration.ManagedMods
                            .Where(m => m.ShortcutName.Equals(mod.ShortcutName, StringComparison.OrdinalIgnoreCase))
                            .ToList();
                    }

                    bool redrawNeeded = false;
                    foreach (var m in modsToToggle)
                    {
                        m.IsEnabled = true;
                        if (plugin.PenumbraOptionSetter.SetManagedModState(m, true, plugin.Configuration.TargetCollectionId))
                        {
                            redrawNeeded = true;
                        }
                    }

                    plugin.Configuration.Save();
                    if (redrawNeeded)
                    {
                        new Penumbra.Api.IpcSubscribers.RedrawObject(plugin.Interface).Invoke(0, RedrawType.Redraw);
                        lastModStateRefresh = DateTime.MinValue;
                    }
                }
                
                if (!string.IsNullOrWhiteSpace(mod.AnimationCommand))
                {
                    Chat.SendMessage(mod.AnimationCommand);
                }
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip($"Play Animation: {mod.AnimationCommand}");
            }
        }

        ImGui.SameLine();
        ImGui.BeginGroup();
        
        if (!hideDisplayName)
        {
            ImGui.Text(mod.DisplayName);
            ImGui.SameLine();
        }

        if (!string.IsNullOrWhiteSpace(mod.ShortcutName))
        {
            ImGui.TextDisabled($"<{mod.ShortcutName}>");
            ImGui.SameLine();
        }

        if (!string.IsNullOrEmpty(mod.OptionName))
        {
            ImGui.TextDisabled($"[{mod.GroupName}: {mod.OptionName}]");
            ImGui.SameLine();
        }
        else if (!string.IsNullOrEmpty(mod.GroupName))
        {
            ImGui.TextColored(ImGuiColors.DalamudYellow, $"[{mod.GroupName}: no option - toggles whole mod!]");
            ImGui.SameLine();
        }
        
        if (mod.IsAnimation && !string.IsNullOrWhiteSpace(mod.AnimationCommand))
        {
            ImGui.TextDisabled(mod.AnimationCommand);
        }
        ImGui.EndGroup();

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip($"Directory: {mod.ModName}");
        }

        ImGui.PopID();
    }
}