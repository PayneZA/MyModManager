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

public class ModManagerWindow : Window, IDisposable
{
    private readonly Plugin plugin;

    // Mod Manager state
    private string modSearchText = string.Empty;
    private string newModDisplayName = string.Empty;
    private string newModShortcutName = string.Empty;
    private string newModCategoryName = "Default";
    private bool newModIsAnimation = false;
    private string newModAnimationCommand = string.Empty;
    private string favoriteModSearchText = string.Empty;
    private Dictionary<string, string> penumbraMods = new();
    private Dictionary<string, (bool Enabled, Dictionary<string, List<string>> Settings)> penumbraModStates = new();
    private Dictionary<Guid, string> penumbraCollections = new();
    private DateTime lastModRefresh = DateTime.MinValue;
    private DateTime lastModStateRefresh = DateTime.MinValue;
    private DateTime lastCollectionRefresh = DateTime.MinValue;

    // Selection state for adding mod options
    private string selectedGroupName = string.Empty;
    private string selectedOptionName = string.Empty;
    private GroupType selectedGroupType = GroupType.Single;
    private Dictionary<string, (string[] Options, GroupType Type)> currentModOptions = new();

    // When non-null, the top form is editing the entry with this Id rather than adding a new one.
    private string? editingModId = null;

    public ModManagerWindow(Plugin plugin)
      : base("My Mod Manager - Manage", ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(500, 400), MaximumSize = new Vector2(float.MaxValue, float.MaxValue) };
        this.plugin = plugin;
    }

    public void Dispose() { }

    public override void Draw()
    {
        ImGui.Spacing();
        RefreshPenumbraModStates();
        RefreshPenumbraCollections();
        DrawModManagerHeader();
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
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

    private void RefreshPenumbraCollections()
    {
        if ((DateTime.Now - lastCollectionRefresh).TotalSeconds < 10) return;
        lastCollectionRefresh = DateTime.Now;

        try
        {
            var collections = new Penumbra.Api.IpcSubscribers.GetCollections(plugin.Interface).Invoke();
            if (collections != null)
            {
                penumbraCollections = collections;
            }
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, "Failed to refresh Penumbra collections.");
        }
    }

    private void RefreshPenumbraMods()
    {
        if ((DateTime.Now - lastModRefresh).TotalSeconds < 5) return;
        lastModRefresh = DateTime.Now;

        try
        {
            var mods = new Penumbra.Api.IpcSubscribers.GetModList(plugin.Interface).Invoke();
            if (mods != null)
            {
                penumbraMods = mods.ToDictionary(k => k.Key, v => v.Value);
            }
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, "Failed to refresh Penumbra mod list.");
        }
    }

    private void DrawModManagerHeader()
    {
        ImGui.Text("Target Collection Settings");
        
        string currentName = "Current Collection";
        if (plugin.Configuration.TargetCollectionId != Guid.Empty && penumbraCollections.TryGetValue(plugin.Configuration.TargetCollectionId, out var name))
        {
            currentName = name;
        }

        ImGui.SetNextItemWidth(300);
        if (ImGui.BeginCombo("Target Collection", currentName))
        {
            if (ImGui.Selectable("Current Collection", plugin.Configuration.TargetCollectionId == Guid.Empty))
            {
                plugin.Configuration.TargetCollectionId = Guid.Empty;
                plugin.Configuration.Save();
                lastModStateRefresh = DateTime.MinValue; // Force refresh
            }

            foreach (var col in penumbraCollections)
            {
                if (ImGui.Selectable(col.Value, plugin.Configuration.TargetCollectionId == col.Key))
                {
                    plugin.Configuration.TargetCollectionId = col.Key;
                    plugin.Configuration.Save();
                    lastModStateRefresh = DateTime.MinValue; // Force refresh
                }
            }
            ImGui.EndCombo();
        }
        
        ImGui.TextDisabled("Toggles will apply to this collection.");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        bool isEditing = editingModId != null;
        if (isEditing)
            ImGui.TextColored(ImGuiColors.DalamudYellow, $"Editing: {newModDisplayName}");
        else
            ImGui.Text("Add New Managed Mod / Option Shortcut");

        RefreshPenumbraMods();

        if (isEditing && !currentModOptions.Any() && !string.IsNullOrEmpty(selectedGroupName))
            ImGui.TextDisabled("Mod not currently installed - binding preserved.");

        string oldSearch = modSearchText;
        ImGui.SetNextItemWidth(300);
        if (ImGui.InputTextWithHint("##modSearch", "Search Penumbra Mods...", ref modSearchText, 100))
        {
            if (modSearchText != oldSearch)
            {
                selectedGroupName = string.Empty;
                selectedOptionName = string.Empty;
                currentModOptions.Clear();
            }
        }

        if (!string.IsNullOrWhiteSpace(modSearchText))
        {
            var filteredMods = penumbraMods
                .Where(m => m.Value.Contains(modSearchText, StringComparison.OrdinalIgnoreCase) || m.Key.Contains(modSearchText, StringComparison.OrdinalIgnoreCase))
                .Take(10);

            if (filteredMods.Any())
            {
                ImGui.Indent();
                foreach (var mod in filteredMods)
                {
                    if (ImGui.Selectable($"{mod.Value} (##{mod.Key})"))
                    {
                        modSearchText = mod.Key;
                        newModDisplayName = mod.Value;
                        selectedGroupName = string.Empty;
                        selectedOptionName = string.Empty;
                        selectedGroupType = GroupType.Single;
                        currentModOptions.Clear();

                        var available = new Penumbra.Api.IpcSubscribers.GetAvailableModSettings(plugin.Interface).Invoke(modSearchText, modSearchText);
                        if (available != null)
                        {
                            currentModOptions = available.ToDictionary(k => k.Key, v => (v.Value.Item1, v.Value.Item2));
                        }
                    }
                }
                ImGui.Unindent();
            }
        }

        if (currentModOptions.Any())
        {
            ImGui.Spacing();
            ImGui.Text("Shortcut to a specific option (Optional):");
            
            if (ImGui.BeginCombo("Option Group", string.IsNullOrEmpty(selectedGroupName) ? "(None - Toggle whole mod)" : selectedGroupName))
            {
                if (ImGui.Selectable("(None - Toggle whole mod)", string.IsNullOrEmpty(selectedGroupName)))
                {
                    selectedGroupName = string.Empty;
                    selectedOptionName = string.Empty;
                }
                foreach (var group in currentModOptions)
                {
                    if (ImGui.Selectable(group.Key, selectedGroupName == group.Key))
                    {
                        selectedGroupName = group.Key;
                        selectedOptionName = string.Empty;
                        selectedGroupType = group.Value.Type;
                    }
                }
                ImGui.EndCombo();
            }

            if (!string.IsNullOrEmpty(selectedGroupName) && currentModOptions.TryGetValue(selectedGroupName, out var selectedGroupOptions))
            {
                if (ImGui.BeginCombo("Specific Option", string.IsNullOrEmpty(selectedOptionName) ? "Select an option..." : selectedOptionName))
                {
                    foreach (var opt in selectedGroupOptions.Options)
                    {
                        if (ImGui.Selectable(opt, selectedOptionName == opt))
                        {
                            selectedOptionName = opt;
                            if (string.IsNullOrEmpty(newModShortcutName))
                            {
                                newModShortcutName = opt;
                            }
                        }
                    }
                    ImGui.EndCombo();
                }
            }
        }

        ImGui.Spacing();

        ImGui.Columns(2, "addModColumns", false);
        ImGui.SetColumnWidth(0, 120);

        ImGui.Text("Display Name:");
        ImGui.NextColumn();
        ImGui.SetNextItemWidth(-1);
        ImGui.InputText("##displayName", ref newModDisplayName, 64);
        ImGui.NextColumn();

        ImGui.Text("Shortcut Name:");
        ImGui.NextColumn();
        ImGui.SetNextItemWidth(-1);
        ImGui.InputText("##shortcutName", ref newModShortcutName, 64);
        ImGui.NextColumn();

        ImGui.Text("Category:");
        ImGui.NextColumn();
        ImGui.SetNextItemWidth(-1);
        ImGui.InputText("##categoryName", ref newModCategoryName, 64);
        ImGui.NextColumn();

        ImGui.Text("Is Animation:");
        ImGui.NextColumn();
        ImGui.Checkbox("##isAnimation", ref newModIsAnimation);
        ImGui.NextColumn();

        if (newModIsAnimation)
        {
            ImGui.Text("Chat Command:");
            ImGui.NextColumn();
            ImGui.SetNextItemWidth(-1);
            ImGui.InputTextWithHint("##animationCommand", "e.g. /dance", ref newModAnimationCommand, 64);
            ImGui.NextColumn();
        }

        ImGui.Columns(1);

        // A group without a chosen option would silently degrade to toggling the
        // whole mod, so block the add until an option is picked (or group cleared).
        bool groupWithoutOption = !string.IsNullOrEmpty(selectedGroupName) && string.IsNullOrEmpty(selectedOptionName);
        if (groupWithoutOption)
        {
            ImGui.TextColored(ImGuiColors.DalamudYellow, "Select a specific option, or set the group back to '(None - Toggle whole mod)'.");
        }

        using (ImRaii.Disabled(groupWithoutOption))
        {
            if (ImGui.Button(isEditing ? "Save Changes" : "Add to Favorites") && !string.IsNullOrWhiteSpace(modSearchText))
            {
                var target = isEditing
                    ? plugin.Configuration.ManagedMods.FirstOrDefault(m => m.Id == editingModId)
                    : null;

                // When editing, update the existing entry in place (preserving Id and IsEnabled);
                // otherwise append a new one. A deleted-while-editing entry (target == null) is a no-op.
                if (!isEditing)
                {
                    target = new ManagedMod();
                    plugin.Configuration.ManagedMods.Add(target);
                }

                if (target != null)
                {
                    target.ModName = modSearchText;
                    target.DisplayName = string.IsNullOrWhiteSpace(newModDisplayName) ? modSearchText : newModDisplayName;
                    target.ShortcutName = newModShortcutName;
                    target.CategoryName = string.IsNullOrWhiteSpace(newModCategoryName) ? "Default" : newModCategoryName;
                    target.IsAnimation = newModIsAnimation;
                    target.AnimationCommand = newModAnimationCommand;
                    target.GroupName = selectedGroupName;
                    target.OptionName = selectedOptionName;
                    target.GroupType = selectedGroupType;
                    plugin.Configuration.Save();
                }

                ResetModForm();
            }
        }

        if (isEditing)
        {
            ImGui.SameLine();
            if (ImGui.Button("Cancel"))
                ResetModForm();
        }
    }

    private void ResetModForm()
    {
        modSearchText = string.Empty;
        newModDisplayName = string.Empty;
        newModShortcutName = string.Empty;
        newModCategoryName = "Default";
        newModIsAnimation = false;
        newModAnimationCommand = string.Empty;
        selectedGroupName = string.Empty;
        selectedOptionName = string.Empty;
        selectedGroupType = GroupType.Single;
        currentModOptions.Clear();
        editingModId = null;
    }

    private void BeginEditMod(ManagedMod mod)
    {
        editingModId = mod.Id;
        modSearchText = mod.ModName;
        newModDisplayName = mod.DisplayName;
        newModShortcutName = mod.ShortcutName;
        newModCategoryName = mod.CategoryName;
        newModIsAnimation = mod.IsAnimation;
        newModAnimationCommand = mod.AnimationCommand;
        selectedGroupName = mod.GroupName;
        selectedOptionName = mod.OptionName;
        selectedGroupType = mod.GroupType;

        currentModOptions.Clear();
        var available = new Penumbra.Api.IpcSubscribers.GetAvailableModSettings(plugin.Interface).Invoke(mod.ModName, mod.ModName);
        if (available != null)
            currentModOptions = available.ToDictionary(k => k.Key, v => (v.Value.Item1, v.Value.Item2));
    }

    private void DrawFavoriteModsList()
    {
        ImGui.Text("Managed Mods Favorites");
        ImGui.SetNextItemWidth(200);
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

        if (mod.Id == editingModId)
        {
            ImGui.TextColored(ImGuiColors.DalamudYellow, ">");
            ImGui.SameLine();
        }

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

        ImGui.SameLine(ImGui.GetContentRegionMax().X - 60);
        if (ImGuiComponents.IconButton(FontAwesomeIcon.Pen))
        {
            BeginEditMod(mod);
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Edit favorite");

        ImGui.SameLine(ImGui.GetContentRegionMax().X - 30);
        if (ImGuiComponents.IconButton(FontAwesomeIcon.Trash))
        {
            plugin.Configuration.ManagedMods.Remove(mod);
            if (mod.Id == editingModId) ResetModForm();
            plugin.Configuration.Save();
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Remove from favorites");

        ImGui.PopID();
    }
}
