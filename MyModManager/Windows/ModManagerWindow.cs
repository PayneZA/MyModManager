using Dalamud.Interface.Colors;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Bindings.ImGui;
using Penumbra.Api.Enums;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Interface.Windowing;
using MyModManager.Helpers;
using MyModManager.Models;
using MyModManager.Services;

namespace MyModManager.Windows;

public class ModManagerWindow : Window, IDisposable
{
    private readonly Plugin plugin;

    private string modSearchText = string.Empty;
    private string newModDisplayName = string.Empty;
    private string newModShortcutName = string.Empty;
    private string newModCategoryName = "Default";
    private bool newModIsAnimation = false;
    private bool newModIsFavorite = false;
    private bool newModIsTemp = false;
    private ContentRating newModRating = ContentRating.Sfw;
    private List<string> newModTags = new();
    private string newTagDraft = string.Empty;
    private string newModAnimationCommand = string.Empty;
    private string favoriteModSearchText = string.Empty;
    private FavoriteFilter favoriteFilter = FavoriteFilter.All;
    private ContentRatingFilter ratingFilter = ContentRatingFilter.All;
    private string sceneTagFilter = FavoriteGrouping.SceneTagAll;
    private int? newModPose;
    private bool newModAutoEmoteSync;
    private string cachedModSearch = "\0";
    private int cachedModSearchVersion = -1;
    private List<KeyValuePair<string, string>> filteredPenumbraMods = new();
    private string cachedSearchText = "\0";
    private int cachedRevision = -1;
    private FavoriteFilter cachedFilter = (FavoriteFilter)(-1);
    private ContentRatingFilter cachedRatingFilter = (ContentRatingFilter)(-1);
    private string cachedSceneTag = "\0";
    private readonly StatusLine status = new();
    private List<(string Category, List<(string DisplayName, List<ManagedMod> Mods)> Names)> groupedFavorites = new();

    private string selectedGroupName = string.Empty;
    private string selectedOptionName = string.Empty;
    private GroupType selectedGroupType = GroupType.Single;
    private Dictionary<string, (string[] Options, GroupType Type)> currentModOptions = new();

    private string? editingModId = null;
    private AnimationScanResult? animationScan;

    public ModManagerWindow(Plugin plugin)
      : base("Add/Edit###MyModManager.Manage", ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(500, 400), MaximumSize = new Vector2(float.MaxValue, float.MaxValue) };
        this.plugin = plugin;
    }

    public void Dispose() { }

    public override void Draw()
    {
        ImGui.Spacing();
        ManagedModListUi.DrawPenumbraBanner(plugin);
        DrawCollectionPicker();
        ImGui.Spacing();
        if (editingModId != null)
            ImGui.SetNextItemOpen(true, ImGuiCond.Always);
        bool formOpen = ImGui.CollapsingHeader("Add or edit a mod");
        ManagedModListUi.Hint("Add a Penumbra mod or option, or edit the entry selected below.");
        if (formOpen)
        {
            ImGui.TextWrapped("Search Penumbra, optionally pick a group/option, then Add. SFW/NSFW drive filters. Temp mods can be turned off from Favorites with Disable temp.");
            DrawAddEditForm();
        }
        ImGui.Spacing();
        bool scanOpen = ImGui.CollapsingHeader("Scan animations");
        ManagedModListUi.Hint("Find Penumbra mods whose Changed Items are a single emote and add them as Unassigned / Imported.");
        if (scanOpen)
            DrawAnimationScan();
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        DrawFavoriteModsList();
    }

    private Dictionary<string, (string[] Options, GroupType Type)> LoadOptions(string modDirectory) =>
        plugin.Penumbra.GetOptionGroups(modDirectory)?.ToDictionary(g => g.Name, g => (g.Options, g.Type))
        ?? new Dictionary<string, (string[] Options, GroupType Type)>();

    /// <summary>Filters and sorts the Penumbra mod list only when the search text or mod list changes.</summary>
    private List<KeyValuePair<string, string>> FilteredPenumbraMods()
    {
        if (cachedModSearch == modSearchText && cachedModSearchVersion == plugin.Penumbra.Version)
            return filteredPenumbraMods;

        cachedModSearch = modSearchText;
        cachedModSearchVersion = plugin.Penumbra.Version;
        filteredPenumbraMods = plugin.Penumbra.ModList
            .Where(m => m.Value.Contains(modSearchText, StringComparison.OrdinalIgnoreCase) || m.Key.Contains(modSearchText, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(m => m.Value.StartsWith(modSearchText, StringComparison.OrdinalIgnoreCase))
            .ThenBy(m => m.Value, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return filteredPenumbraMods;
    }

    private void DrawAnimationScan()
    {
        ImGui.TextWrapped("Scans Penumbra Changed Items. One Emote: line is enough, even with Other / Files Manipulating Animations or Sounds. Multiple Emote: lines are excluded.");

        if (ImGui.Button("Scan Penumbra for animations"))
            RunAnimationScan();
        ManagedModListUi.Hint("Read Changed Items from Penumbra. Already managed directories are skipped.");

        if (animationScan == null)
            return;

        var selectedCount = animationScan.Importable.Count(c => c.Selected);
        ImGui.SameLine();
        if (ImGui.Button("Select all") && animationScan.Importable.Count > 0)
        {
            foreach (var c in animationScan.Importable)
                c.Selected = true;
        }
        ImGui.SameLine();
        if (ImGui.Button("Select none"))
        {
            foreach (var c in animationScan.Importable)
                c.Selected = false;
        }

        using (ImRaii.Disabled(selectedCount == 0))
        {
            if (ImGui.Button($"Add {selectedCount} to Unassigned"))
                ImportSelectedAnimations();
        }
        ManagedModListUi.Hint("Create catalog rows. They stay off Favorites until you star them.");

        var listHeight = ImGui.GetTextLineHeightWithSpacing() * 10;
        using (var child = ImRaii.Child("AnimationScanImportable", new Vector2(-1, listHeight), true))
        {
            if (child.Success)
            {
                if (animationScan.Importable.Count == 0)
                    ImGui.TextDisabled("No single-emote mods to import.");

                foreach (var candidate in animationScan.Importable)
                {
                    ImGui.PushID(candidate.ModDirectory);
                    ImGui.Checkbox("##sel", ref candidate.Selected);
                    ImGui.SameLine();
                    var command = string.IsNullOrEmpty(candidate.Command) ? "(no command)" : candidate.Command;
                    var emote = string.IsNullOrEmpty(candidate.EmoteName) ? "name match" : candidate.EmoteName;
                    ImGui.TextUnformatted($"{candidate.DisplayName}  {emote}  {command}");
                    ImGui.PopID();
                }
            }
        }

        if (animationScan.ExcludedMultiEmote.Count > 0)
        {
            ImGui.Spacing();
            ImGui.Text("Excluded from import due to multiple emotes");
            using (var child = ImRaii.Child("AnimationScanExcluded", new Vector2(-1, listHeight * 0.6f), true))
            {
                if (child.Success)
                {
                    foreach (var ex in animationScan.ExcludedMultiEmote)
                        ImGui.TextUnformatted($"{ex.DisplayName}  ({ex.EmoteCount} emotes)");
                }
            }
        }
    }

    private void RunAnimationScan()
    {
        var changed = plugin.Penumbra.GetChangedItemsSnapshot();
        if (changed == null)
        {
            status.Set("Couldn't read Penumbra's Changed Items. Is Penumbra running?");
            animationScan = null;
            return;
        }

        var managed = plugin.Configuration.ManagedMods
            .Select(m => m.ModName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        animationScan = AnimationModScanner.Scan(
            plugin.Penumbra.ModList,
            changed,
            managed,
            plugin.Emotes);

        status.Set($"Found {animationScan.Importable.Count} to import, {animationScan.ExcludedMultiEmote.Count} excluded.", 8);
    }

    private void ImportSelectedAnimations()
    {
        if (animationScan == null)
            return;

        int added = 0;
        foreach (var candidate in animationScan.Importable.Where(c => c.Selected).ToList())
        {
            var entry = new ManagedMod
            {
                ModName = candidate.ModDirectory,
                DisplayName = string.IsNullOrWhiteSpace(candidate.DisplayName) ? candidate.ModDirectory : candidate.DisplayName,
                CategoryName = EmoteCommandMapper.UnassignedCategory,
                IsAnimation = true,
                IsFavorite = false,
                Rating = ContentRating.Unrated,
                Tags = new List<string> { EmoteCommandMapper.ImportedTag },
                AnimationCommand = candidate.Command ?? string.Empty
            };
            plugin.Configuration.ManagedMods.Add(entry);
            plugin.Configuration.RememberTags(entry.Tags);
            added++;
        }

        plugin.Configuration.Save();
        status.Set($"Added {added} animation mods.");
        animationScan = null;
    }

    private void DrawCollectionPicker()
    {
        var config = plugin.Configuration;
        var penumbra = plugin.Penumbra;
        var followLabel = penumbra.FollowsPlayer && penumbra.CollectionName.Length > 0
            ? $"Your character's collection ({penumbra.CollectionName})"
            : "Your character's collection";
        var preview = config.TargetCollectionId == Guid.Empty || penumbra.TargetCollectionMissing
            ? followLabel
            : penumbra.CollectionName;

        ImGui.SetNextItemWidth(320);
        using (var combo = ImRaii.Combo("Collection", preview))
        {
            if (combo.Success)
            {
                if (ImGui.Selectable(followLabel, config.TargetCollectionId == Guid.Empty))
                    SetTargetCollection(Guid.Empty);

                foreach (var (id, name) in penumbra.Collections.OrderBy(c => c.Value, StringComparer.OrdinalIgnoreCase))
                {
                    if (ImGui.Selectable($"{name}##{id}", config.TargetCollectionId == id))
                        SetTargetCollection(id);
                }
            }
        }
        ManagedModListUi.Hint("The Penumbra collection entries are turned on and off in.\nRecommended: your character's collection, so changes always affect you.");
    }

    private void SetTargetCollection(Guid id)
    {
        plugin.Configuration.TargetCollectionId = id;
        plugin.Configuration.Save();
        plugin.Penumbra.Invalidate();
    }

    private void DrawAddEditForm()
    {
        bool isEditing = editingModId != null;
        if (isEditing)
            ImGui.TextColored(ImGuiColors.DalamudYellow, $"Editing: {newModDisplayName}");

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
        ManagedModListUi.Hint("Type to find a Penumbra mod by display name or directory.");

        if (!string.IsNullOrWhiteSpace(modSearchText))
        {
            var filteredMods = FilteredPenumbraMods();

            if (filteredMods.Count > 0)
            {
                var listHeight = ImGui.GetTextLineHeightWithSpacing() * 8;
                using (var child = ImRaii.Child("AddModSearchResults", new Vector2(-1, listHeight), true))
                {
                    if (child.Success)
                    {
                        var managedDirectories = plugin.Configuration.ManagedMods
                            .Select(m => m.ModName)
                            .ToHashSet(StringComparer.OrdinalIgnoreCase);

                        foreach (var mod in filteredMods)
                        {
                            var label = mod.Key.Equals(mod.Value, StringComparison.Ordinal)
                                ? mod.Value
                                : $"{mod.Value} ({mod.Key})";
                            bool alreadyManaged = managedDirectories.Contains(mod.Key);
                            if (alreadyManaged)
                                ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudYellow);
                            if (ImGui.Selectable($"{label}##{mod.Key}", alreadyManaged))
                            {
                                modSearchText = mod.Key;
                                newModDisplayName = mod.Value;
                                selectedGroupName = string.Empty;
                                selectedOptionName = string.Empty;
                                selectedGroupType = GroupType.Single;
                                currentModOptions = LoadOptions(modSearchText);
                            }
                            if (alreadyManaged)
                            {
                                if (ImGui.IsItemHovered())
                                    ImGui.SetTooltip("Already in the managed list. Click to add another option from this mod.");
                                ImGui.PopStyleColor();
                            }
                            else
                            {
                                ManagedModListUi.Hint($"Penumbra directory: {mod.Key}");
                            }
                        }
                    }
                }
            }
            else if (!plugin.Penumbra.ModList.ContainsKey(modSearchText))
            {
                ImGui.TextDisabled("No mods match.");
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
            ManagedModListUi.Hint("Leave none to enable/disable the whole mod. Pick a group to bind one option.");

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
                                newModShortcutName = opt;
                        }
                    }
                    ImGui.EndCombo();
                }
                ManagedModListUi.Hint("Penumbra option this entry will toggle.");
            }
        }

        ImGui.Spacing();

        ImGui.Columns(2, "addModColumns", false);
        ImGui.SetColumnWidth(0, 120);

        ImGui.Text("Display Name:");
        ImGui.NextColumn();
        ImGui.SetNextItemWidth(-1);
        ImGui.InputText("##displayName", ref newModDisplayName, 64);
        ManagedModListUi.Hint("Name shown on Favorites and in this list.");
        ImGui.NextColumn();

        ImGui.Text("Shortcut Name:");
        ImGui.NextColumn();
        ImGui.SetNextItemWidth(-1);
        ImGui.InputText("##shortcutName", ref newModShortcutName, 64);
        ManagedModListUi.Hint("Used with /mmm on|off|toggle <shortcut>.");
        ImGui.NextColumn();

        ImGui.Text("Category:");
        ImGui.NextColumn();
        ImGui.SetNextItemWidth(-1);
        ImGui.InputText("##categoryName", ref newModCategoryName, 64);
        ManagedModListUi.Hint("Folder header in the list. Kept between adds.");
        ImGui.NextColumn();

        ImGui.Text("Is Animation:");
        ImGui.NextColumn();
        ImGui.Checkbox("##isAnimation", ref newModIsAnimation);
        ManagedModListUi.Hint("Adds a Play button that sends the chat command.");
        ImGui.NextColumn();

        ImGui.Text("Favorite:");
        ImGui.NextColumn();
        ImGui.Checkbox("##isFavorite", ref newModIsFavorite);
        ManagedModListUi.Hint("Star this so it appears on the Favorites window.");
        ImGui.NextColumn();

        ImGui.Text("Rating:");
        ImGui.NextColumn();
        if (ImGui.RadioButton("SFW", newModRating == ContentRating.Sfw))
            newModRating = ContentRating.Sfw;
        ManagedModListUi.Hint("Mark as SFW for Favorites and Add/Edit filters.");
        ImGui.SameLine();
        if (ImGui.RadioButton("NSFW", newModRating == ContentRating.Nsfw))
            newModRating = ContentRating.Nsfw;
        ManagedModListUi.Hint("Mark as NSFW for Favorites and Add/Edit filters.");
        ImGui.SameLine();
        if (ImGui.RadioButton("Unrated", newModRating == ContentRating.Unrated))
            newModRating = ContentRating.Unrated;
        ManagedModListUi.Hint("Neither SFW nor NSFW. Only listed when the rating filter is All.");
        ImGui.NextColumn();

        ImGui.Text("Temp:");
        ImGui.NextColumn();
        ImGui.Checkbox("##isTemp", ref newModIsTemp);
        ManagedModListUi.Hint("Favorites Disable temp (or /mmm temp off) turns this off in Penumbra.");
        ImGui.NextColumn();

        if (newModIsAnimation)
        {
            ImGui.Text("Chat Command:");
            ImGui.NextColumn();
            ImGui.SetNextItemWidth(-1);
            ImGui.InputTextWithHint("##animationCommand", "e.g. /groundsit", ref newModAnimationCommand, 64);
            ManagedModListUi.Hint("Emote command run when you press Play.");
            ImGui.NextColumn();

            DrawCommandCheckAndPose();

            ImGui.Text("Emote sync:");
            ImGui.NextColumn();
            using (ImRaii.Disabled(!plugin.Player.EmoteSyncAvailable))
                ImGui.Checkbox("Sync after playing##autoEmoteSync", ref newModAutoEmoteSync);
            ManagedModListUi.Hint(plugin.Player.EmoteSyncAvailable
                ? "After Play, restart everyone's emote on screen so a couple's animation lines up.\nLeave off for dances you don't want synced."
                : "Needs the Simple Heels plugin (/heels emotesync).");
            ImGui.NextColumn();
        }

        ImGui.Columns(1);

        DrawTagEditor();

        ImGui.Spacing();
        bool groupWithoutOption = !string.IsNullOrEmpty(selectedGroupName) && string.IsNullOrEmpty(selectedOptionName);
        if (groupWithoutOption)
        {
            ImGui.TextColored(ImGuiColors.DalamudYellow, "Select a specific option, or set the group back to '(None - Toggle whole mod)'.");
        }

        bool noModSelected = string.IsNullOrWhiteSpace(modSearchText);

        bool unknownMod = !isEditing && !noModSelected && plugin.Penumbra.ModList.Count > 0 && !plugin.Penumbra.ModList.ContainsKey(modSearchText);
        if (unknownMod)
        {
            ImGui.TextColored(ImGuiColors.DalamudYellow, "Pick a mod from the search results above - this text is not a Penumbra mod directory.");
        }

        using (ImRaii.Disabled(groupWithoutOption || noModSelected || unknownMod))
        {
            if (ImGui.Button(isEditing ? "Save Changes" : "Add") && !string.IsNullOrWhiteSpace(modSearchText))
            {
                var target = isEditing
                    ? plugin.Configuration.ManagedMods.FirstOrDefault(m => m.Id == editingModId)
                    : null;

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
                    target.IsFavorite = newModIsFavorite;
                    target.IsTemp = newModIsTemp;
                    target.Rating = newModRating;
                    target.Tags = newModTags.ToList();
                    plugin.Configuration.RememberTags(target.Tags);
                    target.AnimationCommand = newModAnimationCommand.Trim();
                    target.PoseNumber = newModIsAnimation ? newModPose : null;
                    target.AutoEmoteSync = newModIsAnimation && newModAutoEmoteSync;
                    target.GroupName = selectedGroupName;
                    target.OptionName = selectedOptionName;
                    target.GroupType = selectedGroupType;
                    plugin.Configuration.Save();
                    status.Set(isEditing ? $"Saved {target.DisplayName}." : $"Added {target.DisplayName}.");
                }

                var categoryToKeep = target?.CategoryName ?? "Default";
                ResetModForm();
                if (!isEditing)
                    newModCategoryName = categoryToKeep;
            }
        }
        ManagedModListUi.Hint(isEditing ? "Save this entry." : "Add this Penumbra mod or option to the catalog.");

        if (isEditing)
        {
            ImGui.SameLine();
            if (ImGui.Button("Cancel"))
                ResetModForm();
            ManagedModListUi.Hint("Discard edits and leave the form empty.");
        }
    }

    private void ResetModForm()
    {
        modSearchText = string.Empty;
        newModDisplayName = string.Empty;
        newModShortcutName = string.Empty;
        newModCategoryName = "Default";
        newModIsAnimation = false;
        newModIsFavorite = false;
        newModIsTemp = false;
        newModRating = ContentRating.Sfw;
        newModTags = new List<string>();
        newTagDraft = string.Empty;
        newModAnimationCommand = string.Empty;
        newModPose = null;
        newModAutoEmoteSync = false;
        selectedGroupName = string.Empty;
        selectedOptionName = string.Empty;
        selectedGroupType = GroupType.Single;
        currentModOptions.Clear();
        editingModId = null;
        plugin.Configuration.RebuildAssignedTags();
    }

    private void DrawTagEditor()
    {
        ImGui.Text("Tags");
        int removeIndex = -1;
        for (int i = 0; i < newModTags.Count; i++)
        {
            if (i > 0) ImGui.SameLine();
            ImGui.PushID($"appliedTag{i}");
            ImGui.TextDisabled($"[{newModTags[i]}]");
            ImGui.SameLine();
            if (ImGui.SmallButton("x"))
                removeIndex = i;
            ManagedModListUi.Hint("Remove this tag from the entry.");
            ImGui.PopID();
        }
        if (removeIndex >= 0)
            newModTags.RemoveAt(removeIndex);

        var unusedKnown = plugin.Configuration.KnownTags
            .Where(k => !newModTags.Any(t => t.Equals(k, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        ImGui.SetNextItemWidth(200);
        if (ImGui.BeginCombo("##existingTag", "Add existing tag..."))
        {
            if (unusedKnown.Count == 0)
                ImGui.TextDisabled("No other tags yet. Type a new one below.");
            foreach (var tag in unusedKnown)
            {
                if (ImGui.Selectable(tag))
                    newModTags.Add(tag);
            }
            ImGui.EndCombo();
        }
        ManagedModListUi.Hint("Reuse a tag already assigned to another mod.");

        ImGui.SameLine();
        ImGui.SetNextItemWidth(160);
        ImGui.InputTextWithHint("##newTag", "New tag...", ref newTagDraft, 64);
        ManagedModListUi.Hint("Type a new scene tag, then Add tag.");
        ImGui.SameLine();
        if (ImGui.Button("Add tag") && !string.IsNullOrWhiteSpace(newTagDraft))
        {
            var tag = newTagDraft.Trim();
            plugin.Configuration.RememberTags(new[] { tag });
            if (!newModTags.Any(t => t.Equals(tag, StringComparison.OrdinalIgnoreCase)))
                newModTags.Add(tag);
            newTagDraft = string.Empty;
        }
        ManagedModListUi.Hint("Attach the new tag to this entry.");
    }

    /// <summary>Opens this window editing the given entry (used by the Library).</summary>
    public void BeginEdit(ManagedMod mod)
    {
        IsOpen = true;
        BeginEditMod(mod);
    }

    private void BeginEditMod(ManagedMod mod)
    {
        editingModId = mod.Id;
        modSearchText = mod.ModName;
        newModDisplayName = mod.DisplayName;
        newModShortcutName = mod.ShortcutName;
        newModCategoryName = mod.CategoryName;
        newModIsAnimation = mod.IsAnimation;
        newModIsFavorite = mod.IsFavorite;
        newModIsTemp = mod.IsTemp;
        newModRating = mod.Rating;
        newModTags = (mod.Tags ?? new List<string>()).ToList();
        newTagDraft = string.Empty;
        newModAnimationCommand = mod.AnimationCommand;
        newModPose = mod.PoseNumber;
        newModAutoEmoteSync = mod.AutoEmoteSync;
        selectedGroupName = mod.GroupName;
        selectedOptionName = mod.OptionName;
        selectedGroupType = mod.GroupType;

        currentModOptions = LoadOptions(mod.ModName);
    }

    /// <summary>
    /// Shows whether the command is a real emote, and for sit / ground sit / doze lets the
    /// user pick which pose of the /cpose cycle this entry replaces.
    /// </summary>
    private void DrawCommandCheckAndPose()
    {
        var command = newModAnimationCommand.Trim();
        if (command.Length > 0)
        {
            ImGui.NextColumn();
            if (plugin.Commands.Check(command, out var reason) == CommandSender.Verdict.Rejected)
                ImGui.TextColored(ImGuiColors.DalamudOrange, reason);
            else if (plugin.Emotes.FromCommand(command) is { } emote)
                ImGui.TextColored(ImGuiColors.HealerGreen, $"Emote: {emote.Name}");
            else
                ImGui.TextDisabled("Plugin command");
            ImGui.NextColumn();
        }

        var poseEmote = plugin.Emotes.FromCommand(command);
        if (poseEmote is not { PoseKind: not PoseKind.None, PoseCount: > 1 })
            return;

        ImGui.Text("Pose:");
        ImGui.NextColumn();
        ImGui.SetNextItemWidth(200);
        using (var combo = ImRaii.Combo("##pose", ManagedModListUi.PoseLabel(newModPose)))
        {
            if (combo.Success)
            {
                if (ImGui.Selectable(ManagedModListUi.PoseLabel(null), newModPose == null))
                    newModPose = null;
                for (var pose = 0; pose < poseEmote.PoseCount; pose++)
                {
                    if (ImGui.Selectable(ManagedModListUi.PoseLabel(pose), newModPose == pose))
                        newModPose = pose;
                }
            }
        }
        ManagedModListUi.Hint($"Which {poseEmote.Command} pose this animation replaces, numbered like mod names:\n\"[Sit1]\" or \"[Gsit1]\" is Pose 1. Play selects it for you.");
        ImGui.NextColumn();
    }

    private void RebuildFavoriteCacheIfNeeded()
    {
        if (cachedSearchText == favoriteModSearchText
            && cachedRevision == plugin.Configuration.Revision
            && cachedFilter == favoriteFilter
            && cachedRatingFilter == ratingFilter
            && cachedSceneTag == sceneTagFilter)
            return;

        cachedSearchText = favoriteModSearchText;
        cachedRevision = plugin.Configuration.Revision;
        cachedFilter = favoriteFilter;
        cachedRatingFilter = ratingFilter;
        cachedSceneTag = sceneTagFilter;
        groupedFavorites = FavoriteGrouping.Build(
            plugin.Configuration.ManagedMods,
            favoriteModSearchText,
            favoriteFilter,
            ratingFilter,
            sceneTagFilter);
    }

    private void DrawFavoriteModsList()
    {
        ImGui.Text("Managed Mods");
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##favSearch", "Search...", ref favoriteModSearchText, 100);
        ManagedModListUi.Hint("Search the catalog by name, shortcut, category, or tag.");

        DrawFavoriteFilter();
        ManagedModListUi.DrawRatingRadios(ref ratingFilter, "edit");
        ManagedModListUi.SameLineIfFits(200);
        ManagedModListUi.DrawSceneTagFilter(ref sceneTagFilter, plugin.Configuration.KnownTags);

        status.Draw();

        ImGui.Spacing();
        RebuildFavoriteCacheIfNeeded();

        using (var child = ImRaii.Child("FavoriteModsScroll", Vector2.Zero, true))
        {
            if (!child.Success) return;

            if (plugin.Configuration.ManagedMods.Count == 0)
            {
                ImGui.TextWrapped("Search Penumbra above, fill the name, click Add. Star an entry to show it on Favorites.");
            }
            else if (groupedFavorites.Count == 0)
            {
                ImGui.TextDisabled("No mods match this search or filter.");
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

    private void DrawFavoriteFilter()
    {
        if (ImGui.RadioButton("All##favFilter", favoriteFilter == FavoriteFilter.All))
            favoriteFilter = FavoriteFilter.All;
        ManagedModListUi.Hint("Show starred and unstarred catalog entries.");
        ImGui.SameLine();
        if (ImGui.RadioButton("Favorites##favFilter", favoriteFilter == FavoriteFilter.Favorites))
            favoriteFilter = FavoriteFilter.Favorites;
        ManagedModListUi.Hint("Only starred entries (same set as the Favorites window).");
        ImGui.SameLine();
        if (ImGui.RadioButton("Non-favorites##favFilter", favoriteFilter == FavoriteFilter.NonFavorites))
            favoriteFilter = FavoriteFilter.NonFavorites;
        ManagedModListUi.Hint("Only unstarred entries hidden from Favorites.");
    }

    private void DrawFavoriteModItem(ManagedMod mod, bool hideDisplayName = false)
    {
        ImGui.PushID($"fav_{mod.Id}");

        if (mod.Id == editingModId)
        {
            ImGui.TextColored(ImGuiColors.DalamudYellow, ">");
            ImGui.SameLine();
        }

        ManagedModListUi.DrawEntryControls(plugin, mod);

        ImGui.SameLine();
        ManagedModListUi.DrawClippedRowLabels(mod, hideDisplayName, ManagedModListUi.AddEditGutter, plugin.Entries.IsMissing(mod));
        ManagedModListUi.HandleLabelGroupInteraction(mod, status);

        ImGui.SameLine(ImGui.GetContentRegionMax().X - 90);
        var starColor = mod.IsFavorite ? ImGuiColors.DalamudYellow : ImGuiColors.DalamudGrey;
        ImGui.PushStyleColor(ImGuiCol.Text, starColor);
        if (ImGuiComponents.IconButton(FontAwesomeIcon.Star))
        {
            mod.IsFavorite = !mod.IsFavorite;
            plugin.Configuration.Save();
        }
        ImGui.PopStyleColor();
        ManagedModListUi.Hint(mod.IsFavorite ? "Unstar: hides this from Favorites." : "Star: show this on Favorites.");

        ImGui.SameLine(ImGui.GetContentRegionMax().X - 60);
        if (ImGuiComponents.IconButton(FontAwesomeIcon.Pen))
            BeginEditMod(mod);
        ManagedModListUi.Hint("Edit this entry in the form above.");

        // Same convention as Penumbra: deleting needs Ctrl held, so a stray click can't lose an entry.
        ImGui.SameLine(ImGui.GetContentRegionMax().X - 30);
        var ctrlHeld = ImGui.GetIO().KeyCtrl;
        using (ImRaii.Disabled(!ctrlHeld))
        {
            if (ImGuiComponents.IconButton(FontAwesomeIcon.Trash))
            {
                plugin.Configuration.ManagedMods.Remove(mod);
                if (mod.Id == editingModId) ResetModForm();
                plugin.Configuration.Save();
                status.Set($"Removed {mod.DisplayName}.");
            }
        }
        ManagedModListUi.Hint(ctrlHeld ? "Remove this entry from the catalog." : "Hold Ctrl and click to remove this entry.");

        ImGui.PopID();
    }
}
