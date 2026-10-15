using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using MyModManager.Helpers;
using MyModManager.Models;
using MyModManager.Services;

namespace MyModManager.Windows;

public sealed partial class LibraryWindow
{
    private string newCategory = string.Empty;
    private string newPosition = string.Empty;
    private string newTag = string.Empty;
    private string newFolder = string.Empty;

    // Pick-one detail: label editor, and the text of whichever name field is being typed in.
    private bool editingLabels;
    private string? draftKey;
    private string draft = string.Empty;

    // Mod type editor state
    private bool editorRequested;
    private bool editorOpen;
    private ModType? editingType;
    private string editorName = string.Empty;
    private ModTypeStyle editorStyle = ModTypeStyle.Toggle;
    private bool editorDelete;
    private int editorMoveTo;

    private List<ManagedMod> SelectedEntries() =>
        visibleEntries.Where(m => selected.Contains(m.Id)).ToList();

    private void DrawDetail()
    {
        var entries = SelectedEntries();
        if (entries.Count == 0)
        {
            DrawOverview();
            return;
        }

        var type = ActiveType;
        var isPickGroup = type.Style == ModTypeStyle.Pick && entries.Count > 1 && entries.All(m => m.OptionName.Length > 0)
                          && entries.Select(PickKey).Distinct().Count() == 1;
        if (isPickGroup)
            DrawPickDetail(entries);
        else if (entries.Count > 1)
            DrawBulkDetail(entries);
        else
            DrawEntryDetail(entries[0]);
    }

    private void Heading(string text)
    {
        using (headingFont != null && headingFont.Available ? headingFont.Push() : null)
            ImGui.TextUnformatted(text);
    }

    // ------------------------------------------------------------------ nothing selected

    private void DrawOverview()
    {
        var type = ActiveType;
        Heading(type.Name);
        ImGui.TextDisabled("Click an entry to see and edit it. Ctrl-click to pick several, Shift-click for a range.");
        ImGui.Spacing();

        var unsorted = Config.SafeView ? 0 : Config.ManagedMods.Count(m => m.ModTypeId == type.Id && m.Rating == ContentRating.Unrated);
        if (unsorted > 0)
        {
            using (var box = ImRaii.Child("###unsortedBox", new Vector2(0, ImGui.GetFrameHeightWithSpacing() + Theme.Scaled(14)), true))
            {
                if (box)
                {
                    ImGui.AlignTextToFramePadding();
                    ImGui.TextColored(Theme.Unsorted, $"{unsorted} unsorted");
                    ImGui.SameLine();
                    ImGui.TextDisabled("Imported but not rated yet.");
                    ImGui.SameLine();
                    Theme.RightAlign(Theme.ButtonWidth("Show them"));
                    if (Theme.PrimaryButton("Show them"))
                        rating = RatingFilter.Unsorted;
                }
            }
        }

        ImGui.Spacing();
        var on = Config.ManagedMods.Where(m => m.ModTypeId == type.Id && IsVisible(m) && plugin.Entries.IsOn(m) == true)
            .OrderBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
        Theme.Label($"On right now · {on.Count}");
        if (on.Count == 0)
            ImGui.TextDisabled("Nothing in this tab is on.");

        foreach (var mod in on)
        {
            using var id = ImRaii.PushId(mod.Id);
            Theme.Dot(mod.IsTemp ? Theme.Unsorted : Theme.On);
            ImGui.SameLine();
            ImGui.AlignTextToFramePadding();
            if (ImGui.Selectable(mod.DisplayName, false, ImGuiSelectableFlags.None, new Vector2(ImGui.GetContentRegionAvail().X - Theme.ButtonWidth("Turn off") - Theme.Scaled(90), 0)))
            {
                selected.Clear();
                selected.Add(mod.Id);
            }
            ImGui.SameLine();
            var label = Theme.CommandLabel(mod);
            Theme.RightAlign(ImGui.CalcTextSize(label).X + Theme.ButtonWidth("Turn off") + ImGui.GetStyle().ItemSpacing.X);
            ImGui.TextColored(Theme.Gold, label);
            ImGui.SameLine();
            if (ImGui.Button("Turn off"))
                plugin.Entries.Apply(plugin.Entries.ResolveShortcutGroup(mod), false);
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextDisabled("Bar colour = rating: teal SFW, rose NSFW, amber unsorted.");
        ImGui.TextDisabled("Tick colour: green = kept on, amber = temporary.");
        ImGui.TextDisabled("Purple arrows = auto emote sync after Play. Gold star = favourite.");
    }

    // ------------------------------------------------------------------ one entry

    private void DrawEntryDetail(ManagedMod mod)
    {
        var type = Config.ModTypeOf(mod);
        var isAnimation = type.Style == ModTypeStyle.Animation;

        Heading(mod.DisplayName);
        ImGui.SameLine();
        if (Theme.IconButton(FontAwesomeIcon.Star, "fav", mod.IsFavorite ? "Remove from favourites" : "Add to favourites", mod.IsFavorite ? Theme.Gold : Theme.Faint))
        {
            mod.IsFavorite = !mod.IsFavorite;
            Config.Save();
        }
        ImGui.TextDisabled($"Penumbra: {ManagedModListUi.FormatPenumbraPath(mod)}");
        if (plugin.Entries.IsMissing(mod))
            ImGui.TextColored(Theme.Warning, "This mod is no longer installed in Penumbra. The entry is kept so your sorting isn't lost.");

        if (isAnimation)
            DrawUseBox(mod);

        ImGui.Spacing();
        DrawStateRow(mod);
        if (isAnimation)
            DrawSyncRow(mod);

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        using (var table = ImRaii.Table("###fields", 2, ImGuiTableFlags.None))
        {
            if (table)
            {
                ImGui.TableSetupColumn("label", ImGuiTableColumnFlags.WidthFixed, Theme.Scaled(96));
                ImGui.TableSetupColumn("value", ImGuiTableColumnFlags.WidthStretch);

                FieldRow("Name", () =>
                {
                    var name = mod.DisplayName;
                    ImGui.SetNextItemWidth(Theme.Scaled(360));
                    if (ImGui.InputText("###displayName", ref name, 120) && name.Trim().Length > 0)
                        SetAll([mod], m => m.DisplayName = name);
                });
                FieldRow("Rating", () => RatingField([mod]));
                FieldRow("Category", () => VocabularyCombo("category", mod.Category, type.Categories, ref newCategory, v => SetAll([mod], m => m.Category = v), v => AddCategory(type, v)));
                if (isAnimation)
                {
                    FieldRow("Position", () => VocabularyCombo("position", mod.Position, Config.Positions, ref newPosition, v => SetAll([mod], m => m.Position = v), AddPosition));
                    FieldRow("Command", () => CommandField(mod));
                }
                FieldRow("Mod type", () => ModTypeCombo([mod]));
                FieldRow("Folder", () => FolderField([mod]));
                FieldRow("Tags", () => TagsField(mod));
            }
        }

        ImGui.Spacing();
        ImGui.Separator();
        if (ImGui.Button("Open in Penumbra"))
            plugin.Penumbra.OpenInPenumbra(mod.ModName);
        ImGui.SameLine();
        if (ImGui.Button("Change mod or option…"))
            plugin.AddWindow.OpenForRebind(mod);
        Theme.Hint("Point this entry at a different Penumbra mod or option.");
        ImGui.SameLine();
        DrawRemoveButton([mod]);
    }

    private void DrawUseBox(ManagedMod mod)
    {
        ImGui.Spacing();
        var height = ImGui.GetFrameHeight() * 2.3f;
        using var box = ImRaii.Child("###use", new Vector2(0, height), true);
        if (!box)
            return;

        var emote = plugin.Emotes.FromCommand(mod.AnimationCommand);
        var playLabel = "  Play  ";
        var playWidth = Theme.ButtonWidth(playLabel);

        ImGui.BeginGroup();
        Theme.Label("Use");
        using (headingFont != null && headingFont.Available ? headingFont.Push() : null)
        using (ImRaii.PushColor(ImGuiCol.Text, Theme.Gold))
            ImGui.TextUnformatted(mod.AnimationCommand.Length > 0 ? Theme.CommandLabel(mod, true) : "No command");
        ImGui.EndGroup();

        ImGui.SameLine();
        ImGui.BeginGroup();
        ImGui.Dummy(new Vector2(0, Theme.Scaled(4)));
        ImGui.TextDisabled(emote != null
            ? emote.PoseKind != PoseKind.None && mod.PoseNumber != null ? $"{emote.Name}. Play selects the pose." : emote.Name
            : mod.AnimationCommand.Length > 0 ? "Not an emote the game knows" : "Set a command below");
        ImGui.EndGroup();

        ImGui.SameLine();
        Theme.RightAlign(playWidth);
        ImGui.SetCursorPosY((height - ImGui.GetFrameHeight()) / 2);
        using (ImRaii.Disabled(plugin.Entries.IsMissing(mod) || !plugin.Penumbra.Available || plugin.Player.IsBusy))
        {
            if (Theme.PrimaryButton(playLabel))
                plugin.Player.Play(mod);
        }
        Theme.Hint("Turn on, pause entries that replace the same emote, redraw, then play.");
    }

    private void DrawStateRow(ManagedMod mod)
    {
        var missing = plugin.Entries.IsMissing(mod);
        var on = plugin.Entries.IsOn(mod) == true ? 1 : 0;
        using (ImRaii.Disabled(missing || !plugin.Penumbra.Available))
        {
            if (Theme.Segmented("onoff", ["Off", "On"], ref on, [null, (Theme.On, Theme.OnBg)]))
                plugin.Entries.Apply(plugin.Entries.ResolveShortcutGroup(mod), on == 1);
        }

        ImGui.SameLine();
        var keep = mod.IsTemp ? 1 : 0;
        if (Theme.Segmented("keep", ["Keep on", "Temporary"], ref keep, [(Theme.On, Theme.OnBg), (Theme.Unsorted, Theme.UnsortedBg)]))
            SetAll([mod], m => m.IsTemp = keep == 1);
        ImGui.SameLine();
        ImGui.AlignTextToFramePadding();
        ImGui.TextDisabled(mod.IsTemp ? "Turned off by \"Turn off temporary\"." : "Stays on until you turn it off.");
    }

    private void DrawSyncRow(ManagedMod mod)
    {
        var sync = mod.AutoEmoteSync;
        using (ImRaii.Disabled(!plugin.Player.EmoteSyncAvailable))
        {
            if (ImGui.Checkbox("Auto emote sync after Play", ref sync))
                SetAll([mod], m => m.AutoEmoteSync = sync);
        }
        Theme.Hint(plugin.Player.EmoteSyncAvailable
            ? "Runs /heels emotesync a second after the emote starts, so your partner's animation lines up. Leave off for dances."
            : "Needs the Simple Heels plugin.");
    }

    private static void FieldRow(string label, Action draw)
    {
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.AlignTextToFramePadding();
        ImGui.TextDisabled(label);
        ImGui.TableNextColumn();
        using var id = ImRaii.PushId(label);
        draw();
    }

    // ------------------------------------------------------------------ fields (shared by single and bulk)

    private void RatingField(List<ManagedMod> mods)
    {
        var first = mods[0].Rating;
        var same = mods.All(m => m.Rating == first);
        var index = !same ? -1 : first switch { ContentRating.Sfw => 0, ContentRating.Nsfw => 1, _ => 2 };
        if (Theme.Segmented("rating", ["SFW", "NSFW", "Unsorted"], ref index, [(Theme.Sfw, Theme.SfwBg), (Theme.Nsfw, Theme.NsfwBg), (Theme.Unsorted, Theme.UnsortedBg)]))
            SetAll(mods, m => m.Rating = index switch { 0 => ContentRating.Sfw, 1 => ContentRating.Nsfw, _ => ContentRating.Unrated });
        if (!same)
        {
            ImGui.SameLine();
            ImGui.AlignTextToFramePadding();
            ImGui.TextDisabled("(mixed)");
        }
    }

    /// <summary>A combo over a user-editable list, with "(none)" and an inline "add new" box.</summary>
    private static void VocabularyCombo(string id, string? current, IReadOnlyList<string> options, ref string draft, Action<string> choose, Action<string> addOption)
    {
        ImGui.SetNextItemWidth(Theme.Scaled(260));
        var preview = current == null ? "(mixed)" : current.Length == 0 ? "(none)" : current;
        using var combo = ImRaii.Combo($"###{id}", preview);
        if (!combo)
            return;

        if (ImGui.Selectable("(none)", current?.Length == 0))
            choose(string.Empty);
        foreach (var option in options)
        {
            if (ImGui.Selectable(option, string.Equals(option, current, StringComparison.OrdinalIgnoreCase)))
                choose(option);
        }

        ImGui.Separator();
        ImGui.SetNextItemWidth(Theme.Scaled(180));
        var submitted = ImGui.InputTextWithHint("###new", "New…", ref draft, 48, ImGuiInputTextFlags.EnterReturnsTrue);
        ImGui.SameLine();
        if ((ImGui.Button("Add") || submitted) && draft.Trim().Length > 0)
        {
            var value = draft.Trim();
            addOption(value);
            choose(value);
            draft = string.Empty;
            ImGui.CloseCurrentPopup();
        }
    }

    private void AddCategory(ModType type, string value)
    {
        if (!type.Categories.Contains(value, StringComparer.OrdinalIgnoreCase))
        {
            type.Categories.Add(value);
            type.Categories.Sort(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void AddPosition(string value)
    {
        if (!Config.Positions.Contains(value, StringComparer.OrdinalIgnoreCase))
        {
            Config.Positions.Add(value);
            Config.Positions.Sort(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void CommandField(ManagedMod mod)
    {
        var command = mod.AnimationCommand;
        ImGui.SetNextItemWidth(Theme.Scaled(150));
        if (ImGui.InputTextWithHint("###command", "/groundsit", ref command, 64))
            SetAll([mod], m => m.AnimationCommand = command.Trim());

        var emote = plugin.Emotes.FromCommand(mod.AnimationCommand);
        if (emote is { PoseCount: > 1 })
        {
            ImGui.SameLine();
            ImGui.SetNextItemWidth(Theme.Scaled(140));
            using (var combo = ImRaii.Combo("###pose", ManagedModListUi.PoseLabel(mod.PoseNumber)))
            {
                if (combo)
                {
                    if (ImGui.Selectable(ManagedModListUi.PoseLabel(null), mod.PoseNumber == null))
                        SetAll([mod], m => m.PoseNumber = null);
                    for (var pose = 0; pose < emote.PoseCount; pose++)
                    {
                        if (ImGui.Selectable(ManagedModListUi.PoseLabel(pose), mod.PoseNumber == pose))
                        {
                            var p = pose;
                            SetAll([mod], m => m.PoseNumber = p);
                        }
                    }
                }
            }
            Theme.Hint("Numbered like mod names: \"[Sit1]\" or \"[Gsit1]\" is Pose 1.");
        }

        if (mod.AnimationCommand.Length == 0)
            return;
        ImGui.SameLine();
        ImGui.AlignTextToFramePadding();
        if (plugin.Commands.Check(mod.AnimationCommand, out var reason) == CommandSender.Verdict.Rejected)
        {
            ImGui.TextColored(Theme.Warning, "Not a command");
            Theme.Hint(reason);
        }
        else if (emote != null)
        {
            ImGui.TextColored(Theme.On, emote.Name);
        }
        else
        {
            ImGui.TextDisabled("Plugin command");
        }
    }

    private void ModTypeCombo(List<ManagedMod> mods)
    {
        var first = mods[0].ModTypeId;
        var same = mods.All(m => m.ModTypeId == first);
        ImGui.SetNextItemWidth(Theme.Scaled(260));
        using var combo = ImRaii.Combo("###modType", same ? Config.ModTypeOf(mods[0]).Name : "(mixed)");
        if (!combo)
            return;
        foreach (var type in Config.ModTypes)
        {
            if (ImGui.Selectable($"{type.Name}###{type.Id}", same && type.Id == first))
            {
                SetAll(mods, m => m.ModTypeId = type.Id);
                status.Set($"Moved {(mods.Count == 1 ? mods[0].DisplayName : $"{mods.Count} entries")} to {type.Name}.");
            }
        }
    }

    private void FolderField(List<ManagedMod> mods)
    {
        var first = mods[0].CategoryName;
        var same = mods.All(m => m.CategoryName == first);
        var preview = !same ? "(mixed)" : first.Length > 0 ? first : $"{FolderOf(mods[0])} (from Penumbra)";
        ImGui.SetNextItemWidth(Theme.Scaled(260));
        using var combo = ImRaii.Combo("###folder", preview);
        if (!combo)
            return;

        if (ImGui.Selectable("Use the Penumbra folder", same && first.Length == 0))
            SetAll(mods, m => m.CategoryName = string.Empty);
        ImGui.Separator();
        foreach (var folder in Config.ManagedMods.Select(m => m.CategoryName).Where(f => f.Length > 0)
                     .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            if (ImGui.Selectable(folder, same && folder == first))
                SetAll(mods, m => m.CategoryName = folder);
        }
        ImGui.Separator();
        ImGui.SetNextItemWidth(Theme.Scaled(180));
        var submitted = ImGui.InputTextWithHint("###newFolder", "New folder, e.g. NSFW/Riding", ref newFolder, 96, ImGuiInputTextFlags.EnterReturnsTrue);
        ImGui.SameLine();
        if ((ImGui.Button("Add") || submitted) && newFolder.Trim().Length > 0)
        {
            var value = newFolder.Trim().Trim('/');
            SetAll(mods, m => m.CategoryName = value);
            newFolder = string.Empty;
            ImGui.CloseCurrentPopup();
        }
    }

    private void TagsField(ManagedMod mod)
    {
        string? remove = null;
        foreach (var tag in mod.Tags)
        {
            if (Theme.Chip($"{tag}  ×###tag_{tag}", false))
                remove = tag;
            Theme.Hint("Remove tag");
            ImGui.SameLine();
        }
        if (remove != null)
            SetAll([mod], m => m.Tags.Remove(remove));

        ImGui.SetNextItemWidth(Theme.Scaled(120));
        if (ImGui.InputTextWithHint("###newTag", "+ tag", ref newTag, 32, ImGuiInputTextFlags.EnterReturnsTrue) && newTag.Trim().Length > 0)
        {
            var value = newTag.Trim();
            if (!mod.Tags.Contains(value, StringComparer.OrdinalIgnoreCase))
                SetAll([mod], m => m.Tags.Add(value));
            newTag = string.Empty;
        }
        Theme.Hint("Type a tag and press Enter.");
    }

    private void DrawRemoveButton(List<ManagedMod> mods)
    {
        var label = mods.Count == 1 ? "Remove" : $"Remove {mods.Count}";
        Theme.RightAlign(Theme.ButtonWidth(label));
        var ctrl = ImGui.GetIO().KeyCtrl;
        using (ImRaii.Disabled(!ctrl))
        {
            if (ImGui.Button(label))
            {
                foreach (var m in mods)
                    Config.ManagedMods.Remove(m);
                selected.Clear();
                Config.Save();
                status.Set(mods.Count == 1 ? $"Removed {mods[0].DisplayName}." : $"Removed {mods.Count} entries.");
            }
        }
        Theme.Hint(ctrl ? "Remove from My Mod Manager. The mod stays installed in Penumbra." : "Hold Ctrl and click to remove.");
    }

    private void SetAll(List<ManagedMod> mods, Action<ManagedMod> change)
    {
        foreach (var m in mods)
            change(m);
        Config.Save();
    }

    // ------------------------------------------------------------------ several entries

    private void DrawBulkDetail(List<ManagedMod> mods)
    {
        var type = ActiveType;
        Heading($"{mods.Count} entries selected");
        ImGui.TextDisabled("Changes apply to all of them. (mixed) means they differ.");
        ImGui.Spacing();

        using (var table = ImRaii.Table("###bulk", 2, ImGuiTableFlags.None))
        {
            if (table)
            {
                ImGui.TableSetupColumn("label", ImGuiTableColumnFlags.WidthFixed, Theme.Scaled(96));
                ImGui.TableSetupColumn("value", ImGuiTableColumnFlags.WidthStretch);

                FieldRow("Rating", () => RatingField(mods));
                FieldRow("Category", () => VocabularyCombo("category", Same(mods, m => m.Category), type.Categories, ref newCategory, v => SetAll(mods, m => m.Category = v), v => AddCategory(type, v)));
                if (type.Style == ModTypeStyle.Animation)
                    FieldRow("Position", () => VocabularyCombo("position", Same(mods, m => m.Position), Config.Positions, ref newPosition, v => SetAll(mods, m => m.Position = v), AddPosition));
                FieldRow("Keep", () =>
                {
                    var keep = mods.All(m => !m.IsTemp) ? 0 : mods.All(m => m.IsTemp) ? 1 : -1;
                    if (Theme.Segmented("keep", ["Keep on", "Temporary"], ref keep, [(Theme.On, Theme.OnBg), (Theme.Unsorted, Theme.UnsortedBg)]))
                        SetAll(mods, m => m.IsTemp = keep == 1);
                });
                FieldRow("Favourite", () =>
                {
                    var fav = mods.All(m => m.IsFavorite) ? 0 : mods.All(m => !m.IsFavorite) ? 1 : -1;
                    if (Theme.Segmented("fav", ["★ Yes", "No"], ref fav, [(Theme.Gold, Theme.FrameActive), null]))
                        SetAll(mods, m => m.IsFavorite = fav == 0);
                });
                if (type.Style == ModTypeStyle.Animation)
                {
                    FieldRow("Emote sync", () =>
                    {
                        var sync = mods.All(m => m.AutoEmoteSync) ? 0 : mods.All(m => !m.AutoEmoteSync) ? 1 : -1;
                        using (ImRaii.Disabled(!plugin.Player.EmoteSyncAvailable))
                        {
                            if (Theme.Segmented("sync", ["Auto sync", "Don't sync"], ref sync, [(Theme.Sync, Theme.FrameActive), null]))
                                SetAll(mods, m => m.AutoEmoteSync = sync == 0);
                        }
                    });
                }
                FieldRow("Mod type", () => ModTypeCombo(mods));
                FieldRow("Folder", () => FolderField(mods));
            }
        }

        ImGui.Spacing();
        ImGui.Separator();
        if (ImGui.Button("Turn all off"))
            plugin.Entries.Apply(mods, false);
        ImGui.SameLine();
        if (ImGui.Button("Clear selection"))
            selected.Clear();
        ImGui.SameLine();
        DrawRemoveButton(mods);
    }

    private static string? Same(List<ManagedMod> mods, Func<ManagedMod, string> get)
    {
        var first = get(mods[0]);
        return mods.All(m => string.Equals(get(m), first, StringComparison.OrdinalIgnoreCase)) ? first : null;
    }

    // ------------------------------------------------------------------ pick-one group

    private void DrawPickDetail(List<ManagedMod> mods)
    {
        // Members in switch order, so the editor lists options the way the row shows them.
        var configOrder = Config.ManagedMods.Select((m, i) => (m.Id, i)).ToDictionary(p => p.Id, p => p.i);
        mods = mods.OrderBy(m => m.OptionOrder).ThenBy(m => configOrder.GetValueOrDefault(m.Id)).ToList();
        var groups = PickGroupNames(mods);
        var linked = groups.Count > 1;

        Heading(PickName(mods));
        ImGui.TextDisabled(linked
            ? $"Penumbra: {mods[0].ModName} · sets {string.Join(", ", groups)}"
            : $"Penumbra: {mods[0].ModName} / {mods[0].GroupName}");
        ImGui.Spacing();

        var choices = PickChoices(mods, includeHidden: true);
        var boxRows = editingLabels ? choices.Count + 1 : 2;
        var boxHeight = ImGui.GetFrameHeightWithSpacing() * boxRows + ImGui.GetStyle().WindowPadding.Y * 2;
        using (var box = ImRaii.Child("###pickBox", new Vector2(0, boxHeight), true, ImGuiWindowFlags.NoScrollbar))
        {
            if (box)
            {
                ImGui.AlignTextToFramePadding();
                Theme.Label(editingLabels ? "Options" : "Selected option");
                ImGui.SameLine();
                Theme.RightAlign(Theme.ButtonWidth(editingLabels ? "Done" : "Edit labels"));
                if (Theme.Chip(editingLabels ? "Done###editLabels" : "Edit labels###editLabels", editingLabels))
                    editingLabels = !editingLabels;
                Theme.Hint("Rename, reorder or hide the options. Penumbra keeps its own names.");

                if (editingLabels)
                    DrawOptionEditor(mods, choices);
                else
                    DrawPickSwitch(mods);
            }
        }
        using (ImRaii.PushColor(ImGuiCol.Text, Theme.Dim))
            ImGui.TextWrapped("Picking an option selects it in Penumbra. Renamed labels are only shown here; Penumbra keeps its names.");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        var type = ActiveType;
        using (var table = ImRaii.Table("###pickFields", 2, ImGuiTableFlags.None))
        {
            if (table)
            {
                ImGui.TableSetupColumn("label", ImGuiTableColumnFlags.WidthFixed, Theme.Scaled(96));
                ImGui.TableSetupColumn("value", ImGuiTableColumnFlags.WidthStretch);
                FieldRow("Name", () => PickNameField(mods, linked));
                var candidates = LinkCandidates(mods, groups);
                if (linked || candidates.Count > 0)
                    FieldRow("Linked", () => LinkField(mods, groups, candidates));
                FieldRow("Rating", () => RatingField(mods));
                FieldRow("Category", () => VocabularyCombo("category", Same(mods, m => m.Category), type.Categories, ref newCategory, v => SetAll(mods, m => m.Category = v), v => AddCategory(type, v)));
                FieldRow("Mod type", () => ModTypeCombo(mods));
                FieldRow("Folder", () => FolderField(mods));
            }
        }

        ImGui.Spacing();
        ImGui.Separator();
        if (ImGui.Button("Open in Penumbra"))
            plugin.Penumbra.OpenInPenumbra(mods[0].ModName);
        ImGui.SameLine();
        DrawRemoveButton(mods);
    }

    /// <summary>
    /// A text field that commits on Enter or when it loses focus, so names can be typed freely
    /// (the shown value is trimmed and re-derived from the entries every frame).
    /// </summary>
    private bool DraftInput(string key, string current, float width, out string committed)
    {
        var value = draftKey == key ? draft : current;
        ImGui.SetNextItemWidth(width);
        ImGui.InputText($"###{key}", ref value, 80);
        if (ImGui.IsItemActive())
        {
            draftKey = key;
            draft = value;
        }
        committed = value.Trim();
        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            draftKey = null;
            return committed.Length > 0 && committed != current;
        }
        if (draftKey == key && !ImGui.IsItemActive())
            draftKey = null;
        return false;
    }

    /// <summary>" - " separates the group name from the option label in DisplayName.</summary>
    private static string CleanPickText(string text) => text.Replace(" - ", " – ");

    private void PickNameField(List<ManagedMod> mods, bool linked)
    {
        if (!DraftInput("pickName", PickName(mods), -1, out var name))
            return;
        name = CleanPickText(name);
        if (linked)
            SetAll(mods, m => m.PickLink = name);
        else
            SetAll(mods, m => m.DisplayName = $"{name} - {PickOptionLabel(m)}");
    }

    private static void SetOptionLabel(ManagedMod target, List<ManagedMod> mods, string label)
    {
        var prefix = GroupTitle(mods.Where(m => m.GroupName == target.GroupName).ToList());
        target.DisplayName = $"{prefix} - {CleanPickText(label)}";
    }

    /// <summary>One row per option: label, Penumbra's name, move up/down, hide.</summary>
    private void DrawOptionEditor(List<ManagedMod> mods, List<(string Label, List<ManagedMod> Targets)> choices)
    {
        var buttons = Theme.IconButtonSize(FontAwesomeIcon.Eye).X * 4 + ImGui.GetStyle().ItemSpacing.X * 3;
        for (var i = 0; i < choices.Count; i++)
        {
            var (label, targets) = choices[i];
            var original = targets[0].OptionName;
            var hidden = targets.All(t => t.OptionHidden);
            using var id = ImRaii.PushId(i);

            if (DraftInput($"label{targets[0].Id}", label, Theme.Scaled(150), out var newLabel))
            {
                foreach (var t in targets)
                    SetOptionLabel(t, mods, newLabel);
                Config.Save();
            }
            ImGui.SameLine();
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(hidden ? Theme.Faint : Theme.Dim, hidden ? $"← {original} · hidden" : $"← {original}");

            ImGui.SameLine();
            Theme.RightAlign(buttons);
            using (ImRaii.Disabled(label == original))
            {
                if (Theme.IconButton(FontAwesomeIcon.Undo, "reset", $"Go back to Penumbra's name, \"{original}\".", subtle: true))
                {
                    foreach (var t in targets)
                        SetOptionLabel(t, mods, t.OptionName);
                    Config.Save();
                }
            }
            ImGui.SameLine();
            using (ImRaii.Disabled(i == 0))
            {
                if (Theme.IconButton(FontAwesomeIcon.ArrowUp, "up", "Move left in the switch.", subtle: true))
                    MoveChoice(choices, i, i - 1);
            }
            ImGui.SameLine();
            using (ImRaii.Disabled(i == choices.Count - 1))
            {
                if (Theme.IconButton(FontAwesomeIcon.ArrowDown, "down", "Move right in the switch.", subtle: true))
                    MoveChoice(choices, i, i + 1);
            }
            ImGui.SameLine();
            if (Theme.IconButton(hidden ? FontAwesomeIcon.EyeSlash : FontAwesomeIcon.Eye, "hide",
                    hidden ? "Hidden from the switch. Click to show it." : "Hide from the switch. Shortcuts still work.",
                    hidden ? Theme.Faint : null, subtle: !hidden))
            {
                foreach (var t in targets)
                    t.OptionHidden = !hidden;
                Config.Save();
            }
        }
    }

    private void MoveChoice(List<(string Label, List<ManagedMod> Targets)> choices, int from, int to)
    {
        (choices[from], choices[to]) = (choices[to], choices[from]);
        for (var k = 0; k < choices.Count; k++)
        {
            foreach (var t in choices[k].Targets)
                t.OptionOrder = k + 1;
        }
        Config.Save();
    }

    /// <summary>
    /// Other option groups of the same mod in this tab with exactly the same options, e.g. a second
    /// garment that also has Erect / Flaccid. Those can share one switch.
    /// </summary>
    private List<List<ManagedMod>> LinkCandidates(List<ManagedMod> mods, List<string> groups)
    {
        var options = mods.Select(m => m.OptionName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Config.ManagedMods
            .Where(m => m.ModTypeId == mods[0].ModTypeId && m.OptionName.Length > 0
                        && m.ModName.Equals(mods[0].ModName, StringComparison.OrdinalIgnoreCase) && !groups.Contains(m.GroupName))
            .GroupBy(m => m.GroupName)
            .Select(g => g.ToList())
            .Where(g => g.Select(m => m.OptionName).ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(options))
            .ToList();
    }

    /// <summary>Lit chips are the linked groups (click to unlink one); dim "+" chips can join.</summary>
    private void LinkField(List<ManagedMod> mods, List<string> groups, List<List<ManagedMod>> candidates)
    {
        var linked = groups.Count > 1;
        for (var i = 0; linked && i < groups.Count; i++)
        {
            if (i > 0)
                ImGui.SameLine();
            if (Theme.Chip($"{groups[i]}  ×###linked{i}", true, Theme.Text, Theme.FrameActive))
            {
                var leaving = mods.Where(m => m.GroupName == groups[i]).ToList();
                var staying = mods.Except(leaving).ToList();
                // A link of one group is no link: unlinking the second-last group unlinks both.
                if (groups.Count == 2)
                    leaving = mods;
                SetAll(leaving, m => m.PickLink = string.Empty);
                selected.Clear();
                foreach (var m in staying)
                    selected.Add(m.Id);
                status.Set($"Unlinked {groups[i]}.");
                return;
            }
            Theme.Hint($"Unlink {groups[i]} so it gets its own switch again.");
        }

        for (var i = 0; i < candidates.Count; i++)
        {
            if (linked || i > 0)
                ImGui.SameLine();
            var group = candidates[i];
            if (Theme.Chip($"+ {group[0].GroupName}###link{i}", false))
            {
                var name = linked ? mods[0].PickLink : UniqueLinkName(mods[0].ModName);
                SetAll(mods.Concat(group).ToList(), m => m.PickLink = name);
                foreach (var m in group)
                    selected.Add(m.Id);
                status.Set($"Linked {group[0].GroupName}: one switch now sets {groups.Count + 1} groups.");
                return;
            }
            Theme.Hint($"Link with {group[0].GroupName}: it has the same options, so one switch can set both.");
        }
    }

    private string UniqueLinkName(string modName)
    {
        var taken = Config.ManagedMods.Where(m => m.ModName.Equals(modName, StringComparison.OrdinalIgnoreCase)).Select(m => m.PickLink)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var name = "State";
        for (var n = 2; taken.Contains(name); n++)
            name = $"State {n}";
        return name;
    }

    // ------------------------------------------------------------------ mod type editor

    private void OpenModTypeEditor(ModType? type, bool delete = false)
    {
        editingType = type;
        editorName = type?.Name ?? string.Empty;
        editorStyle = type?.Style ?? ModTypeStyle.Toggle;
        editorDelete = delete;
        editorMoveTo = 0;
        editorRequested = true;
    }

    private void DrawModTypeEditor()
    {
        const string popupId = "###modTypeEditor";
        if (editorRequested)
        {
            editorRequested = false;
            editorOpen = true;
            ImGui.OpenPopup(popupId);
        }

        using var modal = ImRaii.PopupModal($"{(editingType == null ? "New mod type" : $"Edit “{editingType.Name}”")}{popupId}", ref editorOpen, ImGuiWindowFlags.AlwaysAutoResize);
        if (!modal)
            return;

        if (editorDelete && editingType != null)
        {
            DrawDeleteSection(editingType);
            return;
        }

        ImGui.TextDisabled("Name");
        ImGui.SetNextItemWidth(Theme.Scaled(340));
        if (ImGui.IsWindowAppearing())
            ImGui.SetKeyboardFocusHere();
        ImGui.InputTextWithHint("###name", "e.g. VFX, Outfits, Expressions", ref editorName, 48);

        ImGui.Spacing();
        ImGui.TextDisabled("Entries in this tab behave as");
        StyleOption(ModTypeStyle.Animation, "Animation", "Emote command, pose and a Play button. Rating, category, position.");
        StyleOption(ModTypeStyle.Toggle, "On / off", "A plain on/off toggle. Rating and category.");
        StyleOption(ModTypeStyle.Pick, "Pick one", "Entries for options of the same Penumbra group show as one switch.");

        ImGui.Spacing();
        ImGui.Separator();
        if (editingType != null && Config.ModTypes.Count > 1)
        {
            if (ImGui.Button("Delete tab…"))
                editorDelete = true;
            ImGui.SameLine();
        }

        var name = editorName.Trim();
        Theme.RightAlign(Theme.ButtonWidth("Cancel") + Theme.ButtonWidth(editingType == null ? "Create tab" : "Save") + ImGui.GetStyle().ItemSpacing.X);
        if (ImGui.Button("Cancel"))
            ImGui.CloseCurrentPopup();
        ImGui.SameLine();
        using (ImRaii.Disabled(name.Length == 0))
        {
            if (Theme.PrimaryButton(editingType == null ? "Create tab" : "Save"))
            {
                if (editingType == null)
                {
                    var created = new ModType { Name = name, Style = editorStyle };
                    Config.ModTypes.Add(created);
                    requestTypeId = created.Id;
                    status.Set($"Created {name}. Move entries here with their Mod type field.");
                }
                else
                {
                    editingType.Name = name;
                    editingType.Style = editorStyle;
                }
                Config.Save();
                ImGui.CloseCurrentPopup();
            }
        }
    }

    private void StyleOption(ModTypeStyle style, string title, string description)
    {
        if (ImGui.RadioButton($"{title}###style_{style}", editorStyle == style))
            editorStyle = style;
        using (ImRaii.PushIndent(Theme.Scaled(28), false))
            ImGui.TextDisabled(description);
    }

    private void DrawDeleteSection(ModType type)
    {
        var count = Config.ManagedMods.Count(m => m.ModTypeId == type.Id);
        var others = Config.ModTypes.Where(t => t.Id != type.Id).ToList();
        ImGui.TextUnformatted($"Delete the \"{type.Name}\" tab?");
        if (count > 0)
        {
            ImGui.TextDisabled($"Its {count} {(count == 1 ? "entry moves" : "entries move")} to:");
            ImGui.SetNextItemWidth(Theme.Scaled(260));
            editorMoveTo = Math.Clamp(editorMoveTo, 0, others.Count - 1);
            using (var combo = ImRaii.Combo("###moveTo", others[editorMoveTo].Name))
            {
                if (combo)
                {
                    for (var i = 0; i < others.Count; i++)
                    {
                        if (ImGui.Selectable($"{others[i].Name}###{others[i].Id}", i == editorMoveTo))
                            editorMoveTo = i;
                    }
                }
            }
        }
        else
        {
            ImGui.TextDisabled("It has no entries.");
        }

        ImGui.Spacing();
        if (ImGui.Button("Cancel"))
            ImGui.CloseCurrentPopup();
        ImGui.SameLine();
        if (Theme.PrimaryButton("Delete tab"))
        {
            var target = others[editorMoveTo];
            foreach (var m in Config.ManagedMods.Where(m => m.ModTypeId == type.Id))
                m.ModTypeId = target.Id;
            Config.ModTypes.Remove(type);
            requestTypeId = target.Id;
            Config.Save();
            status.Set($"Deleted {type.Name}; its entries are in {target.Name}.");
            ImGui.CloseCurrentPopup();
        }
    }
}
