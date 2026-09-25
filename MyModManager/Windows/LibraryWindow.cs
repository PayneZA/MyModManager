using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.GameFonts;
using Dalamud.Interface.ManagedFontAtlas;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using MyModManager.Helpers;
using MyModManager.Models;

namespace MyModManager.Windows;

/// <summary>
/// The V2 Library: a Penumbra-style two-pane window. Tabs are the user's mod types; the left
/// pane is a filtered, grouped, virtualised list; the right pane edits the selection.
/// </summary>
public sealed partial class LibraryWindow : Window, IDisposable
{
    private enum RatingFilter { All, Sfw, Nsfw, Unsorted }

    private enum RowKind { Header, Entry, PickGroup }

    private sealed class Row
    {
        public RowKind Kind;
        public string Key = string.Empty;
        public string Label = string.Empty;
        public ManagedMod? Mod;
        public List<ManagedMod> Members = new();
        public int Count;
        public int OnCount;
    }

    private static readonly string[] GroupIds = ["folder", "emote", "category", "position", "none"];

    private readonly Plugin plugin;
    private readonly StatusLine status = new();
    private IFontHandle? headingFont;

    // Filters
    private string search = string.Empty;
    private RatingFilter rating = RatingFilter.All;
    private bool favouritesOnly;
    private bool onOnly;
    private string emoteFilter = string.Empty;
    private string categoryFilter = string.Empty;
    private string positionFilter = string.Empty;

    // Tabs and selection
    private string activeTypeId = string.Empty;
    private string? requestTypeId;
    private readonly HashSet<string> collapsed = new(StringComparer.Ordinal);
    private readonly HashSet<string> selected = new(StringComparer.Ordinal);
    private string? anchorId;

    // Row cache: rebuilt only when config, Penumbra state or a filter changes.
    private List<Row> rows = new();
    private List<ManagedMod> visibleEntries = new();
    private string cacheKey = string.Empty;
    private int typeTotal;
    private readonly Dictionary<string, string> penumbraFolders = new(StringComparer.OrdinalIgnoreCase);
    private object? penumbraFoldersSource;

    private Configuration Config => plugin.Configuration;

    public LibraryWindow(Plugin plugin)
        : base("My Mod Manager###MyModManager.Library", ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        this.plugin = plugin;
        Size = new Vector2(1040, 680);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(760, 440), MaximumSize = new Vector2(float.MaxValue, float.MaxValue) };
    }

    public void Dispose() => headingFont?.Dispose();

    public override void PreDraw() => Theme.Push(Config.WindowOpacity);

    public override void PostDraw() => Theme.Pop();

    /// <summary>Opens the Library on a tab with a rating filter, e.g. from "Unsorted".</summary>
    public void ShowUnsorted()
    {
        IsOpen = true;
        requestTypeId = Config.ModTypes.FirstOrDefault(t => t.Style == ModTypeStyle.Animation)?.Id ?? Config.ModTypes[0].Id;
        rating = RatingFilter.Unsorted;
    }

    /// <summary>Shows the given entries selected in their tab, e.g. right after adding them.</summary>
    public void Reveal(IReadOnlyList<ManagedMod> mods)
    {
        if (mods.Count == 0)
            return;
        IsOpen = true;
        requestTypeId = mods[0].ModTypeId;
        ClearFilters();
        selected.Clear();
        foreach (var m in mods.Where(m => m.ModTypeId == mods[0].ModTypeId))
            selected.Add(m.Id);
        collapsed.Clear();
        cacheKey = string.Empty;
    }

    public override void Draw()
    {
        headingFont ??= Svc.PluginInterface.UiBuilder.FontAtlas.NewGameFontHandle(new GameFontStyle(GameFontFamily.Axis, 22f));
        if (Config.ModTypes.All(t => t.Id != activeTypeId))
            activeTypeId = Config.ModTypes[0].Id;

        DrawHeader();
        DrawTabs();
        DrawPanes();
        DrawModTypeEditor();
    }

    private ModType ActiveType => Config.ModTypes.FirstOrDefault(t => t.Id == activeTypeId) ?? Config.ModTypes[0];

    private bool IsVisible(ManagedMod mod) => !Config.SafeView || mod.Rating == ContentRating.Sfw;

    // ------------------------------------------------------------------ header

    private void DrawHeader()
    {
        var penumbra = plugin.Penumbra;
        Theme.Dot(penumbra.Available ? Theme.On : Theme.Unsorted);
        ImGui.SameLine();
        if (!penumbra.Available)
        {
            ImGui.TextColored(Theme.Unsorted, "Penumbra isn't running");
        }
        else
        {
            ImGui.TextDisabled("Applies to");
            ImGui.SameLine();
            ImGui.TextUnformatted(penumbra.CollectionName);
            ImGui.SameLine();
            ImGui.TextDisabled(penumbra.FollowsPlayer ? "· your character" : "· chosen collection");
        }

        var tempOn = plugin.Entries.CountTemporaryOn();
        var unsorted = Config.SafeView ? 0 : Config.ManagedMods.Count(m => m.Rating == ContentRating.Unrated);
        var syncAvailable = plugin.Player.EmoteSyncAvailable;
        var tempLabel = tempOn > 0 ? $"{tempOn} temporary on · Turn off###tempOff" : "Nothing temporary on###tempOff";
        var unsortedLabel = $"Unsorted {unsorted}###unsorted";
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var width = (syncAvailable ? Theme.ButtonWidth("Emote sync") + spacing : 0)
                    + Theme.ButtonWidth("Safe view") + spacing
                    + Theme.ButtonWidth(tempLabel) + spacing
                    + (unsorted > 0 ? Theme.ButtonWidth(unsortedLabel) + spacing : 0)
                    + Theme.ButtonWidth("+ Add") + spacing
                    + Theme.IconButtonSize(FontAwesomeIcon.QuestionCircle).X + spacing
                    + Theme.IconButtonSize(FontAwesomeIcon.Cog).X;
        ImGui.SameLine();
        Theme.RightAlign(width);

        if (syncAvailable)
        {
            if (ImGui.Button("Emote sync"))
                plugin.Player.EmoteSync();
            Theme.Hint("Restart everyone's emote on screen together so paired animations line up (Simple Heels: /heels emotesync). Only you see the effect.");
            ImGui.SameLine();
        }

        if (Theme.Chip("Safe view", Config.SafeView, Theme.Sfw, Theme.SfwBg))
        {
            Config.SafeView = !Config.SafeView;
            selected.Clear();
            Config.Save();
        }
        Theme.Hint(Config.SafeView ? "Safe view is on: only SFW entries are shown anywhere." : "Hide everything not rated SFW, everywhere (for streaming or screen sharing).");

        ImGui.SameLine();
        using (ImRaii.Disabled(tempOn == 0 && Config.SuspendedIds.Count == 0))
        using (ImRaii.PushColor(ImGuiCol.Button, Theme.UnsortedBg).Push(ImGuiCol.Text, Theme.Unsorted, tempOn > 0))
        {
            if (ImGui.Button(tempLabel))
                plugin.Entries.TurnOffTemporary();
        }
        Theme.Hint("Turn off every temporary entry, and turn back on kept-on entries that were paused for them.");

        if (unsorted > 0)
        {
            ImGui.SameLine();
            using (ImRaii.PushColor(ImGuiCol.Text, Theme.Unsorted))
            {
                if (ImGui.Button(unsortedLabel))
                    ShowUnsorted();
            }
            Theme.Hint("Entries that haven't been rated yet. Click to list them.");
        }

        ImGui.SameLine();
        if (ImGui.Button("+ Add"))
            plugin.AddWindow.Open();
        Theme.Hint("Add mods from Penumbra. Animations and poses are detected from the mod's files.");

        ImGui.SameLine();
        if (Theme.IconButton(FontAwesomeIcon.QuestionCircle, "help", "Help"))
            plugin.HelpWindow.Open(selected.Count > 0 ? HelpTopic.Library : HelpTopic.GettingStarted);

        ImGui.SameLine();
        if (Theme.IconButton(FontAwesomeIcon.Cog, "settings", "Settings"))
            ImGui.OpenPopup("###mmmSettings");
        DrawSettingsPopup();

        if (Config.SafeView)
            ImGui.TextColored(Theme.Sfw, "Safe view is on · NSFW and unsorted entries are hidden");
        status.Draw();
    }

    private void DrawSettingsPopup()
    {
        using var popup = ImRaii.Popup("###mmmSettings");
        if (!popup)
            return;

        Theme.Label("Collection");
        var penumbra = plugin.Penumbra;
        var followLabel = $"Your character's collection{(penumbra.FollowsPlayer && penumbra.CollectionName.Length > 0 ? $" ({penumbra.CollectionName})" : string.Empty)}";
        ImGui.SetNextItemWidth(Theme.Scaled(320));
        using (var combo = ImRaii.Combo("##collection", Config.TargetCollectionId == Guid.Empty || penumbra.TargetCollectionMissing ? followLabel : penumbra.CollectionName))
        {
            if (combo)
            {
                if (ImGui.Selectable(followLabel, Config.TargetCollectionId == Guid.Empty))
                    SetCollection(Guid.Empty);
                foreach (var (id, name) in penumbra.Collections.OrderBy(c => c.Value, StringComparer.OrdinalIgnoreCase))
                {
                    if (ImGui.Selectable($"{name}##{id}", Config.TargetCollectionId == id))
                        SetCollection(id);
                }
            }
        }

        ImGui.Spacing();
        Theme.Label("Window");
        var opacity = Config.WindowOpacity;
        ImGui.SetNextItemWidth(Theme.Scaled(320));
        if (ImGui.SliderFloat("##opacity", ref opacity, 0.2f, 1f, "Background %.2f"))
        {
            Config.WindowOpacity = opacity;
            Config.Save();
        }

        ImGui.Spacing();
        Theme.Label("Play");
        Toggle("Select the entry's pose (sit, ground sit, doze)", () => Config.AutoPose, v => Config.AutoPose = v);
        Toggle("Pause other entries that replace the same emote", () => Config.OneAnimationPerEmote, v => Config.OneAnimationPerEmote = v);
        Toggle("Redraw my character after changes", () => Config.RedrawAfterChange, v => Config.RedrawAfterChange = v);
        Toggle("Silent emotes (no emote text in chat)", () => Config.SilentEmotes, v => Config.SilentEmotes = v);
    }

    private void Toggle(string label, Func<bool> get, Action<bool> set)
    {
        var value = get();
        if (ImGui.Checkbox(label, ref value))
        {
            set(value);
            Config.Save();
        }
    }

    private void SetCollection(Guid id)
    {
        Config.TargetCollectionId = id;
        Config.Save();
        plugin.Penumbra.Invalidate();
    }

    // ------------------------------------------------------------------ tabs

    private void DrawTabs()
    {
        using var bar = ImRaii.TabBar("###modTypes");
        if (!bar)
            return;

        foreach (var type in Config.ModTypes.ToList())
        {
            var count = Config.ManagedMods.Count(m => m.ModTypeId == type.Id && IsVisible(m));
            var flags = requestTypeId == type.Id ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None;
            using (var tab = ImRaii.TabItem($"{type.Name}  {count}###type_{type.Id}", flags))
            {
                if (tab.Success && activeTypeId != type.Id)
                {
                    activeTypeId = type.Id;
                    if (requestTypeId != type.Id)
                        selected.Clear();
                    categoryFilter = emoteFilter = positionFilter = string.Empty;
                    if (rating == RatingFilter.Unsorted && type.Style != ModTypeStyle.Animation && requestTypeId == null)
                        rating = RatingFilter.All;
                }
            }

            Theme.Hint($"{StyleName(type.Style)} · right-click to rename, change or delete");
            using (var context = ImRaii.ContextPopupItem($"###typeMenu_{type.Id}"))
            {
                if (context)
                {
                    if (ImGui.Selectable("Edit tab…"))
                        OpenModTypeEditor(type);
                    using (ImRaii.Disabled(Config.ModTypes.Count <= 1))
                    {
                        if (ImGui.Selectable("Delete tab…"))
                            OpenModTypeEditor(type, delete: true);
                    }
                }
            }
        }

        requestTypeId = null;
        if (ImGui.TabItemButton("+###newType", ImGuiTabItemFlags.Trailing | ImGuiTabItemFlags.NoTooltip))
            OpenModTypeEditor(null);
        Theme.Hint("Add a mod type (a new tab), e.g. VFX or Outfits");
    }

    private static string StyleName(ModTypeStyle style) => style switch
    {
        ModTypeStyle.Animation => "Animation: command, pose and Play",
        ModTypeStyle.Pick => "Pick one: switch between options",
        _ => "On / off",
    };

    // ------------------------------------------------------------------ panes

    private void DrawPanes()
    {
        var leftWidth = Math.Clamp(ImGui.GetContentRegionAvail().X * 0.42f, Theme.Scaled(330), Theme.Scaled(460));
        using (var left = ImRaii.Child("###left", new Vector2(leftWidth, 0), true))
        {
            if (left)
                DrawLeftPane();
        }

        ImGui.SameLine();
        using (var right = ImRaii.Child("###right", Vector2.Zero, true))
        {
            if (right)
                DrawDetail();
        }
    }

    private void DrawLeftPane()
    {
        var type = ActiveType;
        var isAnimation = type.Style == ModTypeStyle.Animation;

        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("###search", $"Search {type.Name.ToLowerInvariant()}…", ref search, 128);
        Theme.Hint("Every word must match: name, mod, command, category, position, folder or tag.");

        if (!Config.SafeView)
        {
            var ratingIndex = (int)rating;
            var labels = isAnimation ? new[] { "All", "SFW", "NSFW", "Unsorted" } : new[] { "All", "SFW", "NSFW" };
            if (!isAnimation && ratingIndex == 3) ratingIndex = 0;
            if (Theme.Segmented("rating", labels, ref ratingIndex, [null, (Theme.Sfw, Theme.SfwBg), (Theme.Nsfw, Theme.NsfwBg), (Theme.Unsorted, Theme.UnsortedBg)]))
                rating = (RatingFilter)ratingIndex;
            ImGui.SameLine();
        }

        var chipsWidth = Theme.ButtonWidth("★") + Theme.ButtonWidth("On") + ImGui.GetStyle().ItemSpacing.X;
        Theme.RightAlign(chipsWidth);
        if (Theme.Chip("★", favouritesOnly, Theme.Gold))
            favouritesOnly = !favouritesOnly;
        Theme.Hint("Favourites only");
        ImGui.SameLine();
        if (Theme.Chip("On", onOnly, Theme.On, Theme.OnBg))
            onOnly = !onOnly;
        Theme.Hint("Only entries that are on right now");

        DrawFilterCombos(type, isAnimation);

        var groupIds = isAnimation ? GroupIds : new[] { "folder", "category", "none" };
        var groupLabels = isAnimation ? new[] { "Folder", "Emote", "Category", "Position", "None" } : new[] { "Folder", "Category", "None" };
        var groupIndex = Math.Max(0, Array.IndexOf(groupIds, Config.LibraryGroupBy));
        ImGui.AlignTextToFramePadding();
        ImGui.TextDisabled("Group");
        ImGui.SameLine();
        if (Theme.Segmented("group", groupLabels, ref groupIndex))
        {
            Config.LibraryGroupBy = groupIds[groupIndex];
            collapsed.Clear();
            Config.Save();
        }

        ImGui.Separator();
        RebuildRowsIfNeeded();

        var footerHeight = ImGui.GetFrameHeightWithSpacing();
        using (var list = ImRaii.Child("###list", new Vector2(0, -footerHeight), false))
        {
            if (list)
                DrawRows();
        }

        ImGui.Separator();
        var onCount = visibleEntries.Count(m => plugin.Entries.IsOn(m) == true);
        ImGui.TextDisabled($"{visibleEntries.Count} of {typeTotal} shown · {onCount} on");
        if (selected.Count > 0)
        {
            ImGui.SameLine();
            Theme.RightAlign(ImGui.CalcTextSize($"{selected.Count} selected").X);
            ImGui.TextUnformatted($"{selected.Count} selected");
        }
    }

    private void DrawFilterCombos(ModType type, bool isAnimation)
    {
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var count = isAnimation ? 3 : 1;
        var width = (ImGui.GetContentRegionAvail().X - spacing * (count - 1)) / count;

        if (isAnimation)
        {
            ImGui.SetNextItemWidth(width);
            FilterCombo("###emote", "Any emote", ref emoteFilter,
                Config.ManagedMods.Where(m => m.ModTypeId == type.Id).Select(m => plugin.Emotes.FromCommand(m.AnimationCommand)?.Command ?? string.Empty));
            ImGui.SameLine();
        }

        ImGui.SetNextItemWidth(width);
        FilterCombo("###category", "Any category", ref categoryFilter, type.Categories);

        if (isAnimation)
        {
            ImGui.SameLine();
            ImGui.SetNextItemWidth(width);
            FilterCombo("###position", "Any position", ref positionFilter, Config.Positions);
        }
    }

    private static void FilterCombo(string id, string anyLabel, ref string value, IEnumerable<string> options)
    {
        using var combo = ImRaii.Combo(id, value.Length == 0 ? anyLabel : value);
        if (!combo)
            return;
        if (ImGui.Selectable(anyLabel, value.Length == 0))
            value = string.Empty;
        foreach (var option in options.Where(o => o.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(o => o, StringComparer.OrdinalIgnoreCase))
        {
            if (ImGui.Selectable(option, value.Equals(option, StringComparison.OrdinalIgnoreCase)))
                value = option;
        }
    }

    // ------------------------------------------------------------------ rows

    private void RebuildRowsIfNeeded()
    {
        var key = string.Join('\u0001', Config.Revision, plugin.Penumbra.Version, activeTypeId, search, rating, favouritesOnly, onOnly,
            emoteFilter, categoryFilter, positionFilter, Config.LibraryGroupBy, Config.SafeView, string.Join(',', collapsed));
        if (key == cacheKey)
            return;
        cacheKey = key;

        if (!ReferenceEquals(penumbraFoldersSource, plugin.Penumbra.ModList))
        {
            penumbraFolders.Clear();
            penumbraFoldersSource = plugin.Penumbra.ModList;
        }

        var type = ActiveType;
        var inType = Config.ManagedMods.Where(m => m.ModTypeId == type.Id && IsVisible(m)).ToList();
        typeTotal = inType.Count;
        var words = search.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var emoteId = emoteFilter.Length > 0 ? plugin.Emotes.FromCommand(emoteFilter)?.Id : null;

        visibleEntries = inType.Where(m =>
        {
            switch (rating)
            {
                case RatingFilter.Sfw when m.Rating != ContentRating.Sfw:
                case RatingFilter.Nsfw when m.Rating != ContentRating.Nsfw:
                case RatingFilter.Unsorted when m.Rating != ContentRating.Unrated:
                    return false;
            }
            if (favouritesOnly && !m.IsFavorite) return false;
            if (onOnly && plugin.Entries.IsOn(m) != true) return false;
            if (emoteFilter.Length > 0 && plugin.Emotes.FromCommand(m.AnimationCommand)?.Id != emoteId) return false;
            if (categoryFilter.Length > 0 && !m.Category.Equals(categoryFilter, StringComparison.OrdinalIgnoreCase)) return false;
            if (positionFilter.Length > 0 && !m.Position.Equals(positionFilter, StringComparison.OrdinalIgnoreCase)) return false;
            if (words.Length == 0) return true;
            var haystack = $"{m.DisplayName} {m.ModName} {m.GroupName} {m.OptionName} {m.AnimationCommand} {m.Category} {m.Position} {FolderOf(m)} {string.Join(' ', m.Tags)}";
            return words.All(w => haystack.Contains(w, StringComparison.OrdinalIgnoreCase));
        }).ToList();

        // Keep the selection to entries that are still visible.
        var visibleIds = visibleEntries.Select(m => m.Id).ToHashSet();
        selected.RemoveWhere(id => !visibleIds.Contains(id));

        var groupBy = type.Style == ModTypeStyle.Animation || Config.LibraryGroupBy is "folder" or "category" or "none"
            ? Config.LibraryGroupBy
            : "folder";
        rows = new List<Row>();
        var groups = visibleEntries
            .GroupBy(m => GroupKey(m, groupBy, type))
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase);

        foreach (var group in groups)
        {
            var members = group.OrderBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
            if (groupBy != "none")
            {
                rows.Add(new Row
                {
                    Kind = RowKind.Header, Key = group.Key, Label = group.Key, Count = members.Count,
                    OnCount = members.Count(m => plugin.Entries.IsOn(m) == true),
                });
                if (collapsed.Contains(group.Key))
                    continue;
            }

            if (type.Style == ModTypeStyle.Pick)
                AddPickRows(members);
            else
                rows.AddRange(members.Select(m => new Row { Kind = RowKind.Entry, Key = m.Id, Mod = m }));
        }
    }

    /// <summary>Pick-one tabs show entries that share a mod and option group as one switch row.</summary>
    private void AddPickRows(List<ManagedMod> members)
    {
        foreach (var bundle in members.GroupBy(m => string.IsNullOrEmpty(m.OptionName) ? m.Id : $"{m.ModName}\u0001{m.GroupName}"))
        {
            var list = bundle.ToList();
            if (list.Count == 1 && string.IsNullOrEmpty(list[0].OptionName))
            {
                rows.Add(new Row { Kind = RowKind.Entry, Key = list[0].Id, Mod = list[0] });
                continue;
            }

            rows.Add(new Row { Kind = RowKind.PickGroup, Key = "pick:" + bundle.Key, Label = PickName(list), Members = list, Mod = list[0] });
        }
    }

    /// <summary>"Physics - Clothed" + "Physics - Nude" are shown as "Physics".</summary>
    private static string PickName(List<ManagedMod> members)
    {
        var prefixes = members.Select(m => m.DisplayName.Split(" - ", 2)[0].Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return prefixes.Count == 1 && members.All(m => m.DisplayName.Contains(" - ")) ? prefixes[0] : members[0].GroupName;
    }

    private static string PickOptionLabel(ManagedMod mod)
    {
        var parts = mod.DisplayName.Split(" - ", 2);
        return parts.Length == 2 ? parts[1].Trim() : mod.OptionName;
    }

    private string GroupKey(ManagedMod m, string groupBy, ModType type) => groupBy switch
    {
        "folder" => FolderOf(m),
        "emote" => EmoteKey(m),
        "category" => type.Style == ModTypeStyle.Animation
            ? m.Rating == ContentRating.Unrated ? "Unsorted" : $"{(m.Rating == ContentRating.Nsfw ? "NSFW" : "SFW")} · {(m.Category.Length > 0 ? m.Category : "No category")}"
            : m.Category.Length > 0 ? m.Category : "No category",
        "position" => m.Position.Length > 0 ? m.Position : "No position",
        _ => string.Empty,
    };

    private string EmoteKey(ManagedMod m)
    {
        var emote = plugin.Emotes.FromCommand(m.AnimationCommand);
        var command = emote?.Command ?? (m.AnimationCommand.Length > 0 ? m.AnimationCommand : "No command");
        if (emote is not { PoseCount: > 1 })
            return command;
        return m.PoseNumber switch
        {
            null => $"{command} · any pose",
            0 => $"{command} · default pose",
            var n => $"{command} · pose {n}",
        };
    }

    /// <summary>The entry's own folder, or the folder the mod sits in inside Penumbra's selector.</summary>
    private string FolderOf(ManagedMod m)
    {
        if (!string.IsNullOrWhiteSpace(m.CategoryName))
            return m.CategoryName;
        if (!penumbraFolders.TryGetValue(m.ModName, out var folder))
        {
            var path = plugin.Penumbra.Available ? plugin.Penumbra.GetSelectorPath(m.ModName) : null;
            var slash = path?.LastIndexOf('/') ?? -1;
            folder = slash > 0 ? path![..slash] : "(Penumbra root)";
            penumbraFolders[m.ModName] = folder;
        }
        return folder;
    }

    private void DrawRows()
    {
        using var rowSpacing = ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, new Vector2(ImGui.GetStyle().ItemSpacing.X, Theme.Scaled(2)));
        var rowHeight = ImGui.GetFrameHeightWithSpacing();
        if (rows.Count == 0)
        {
            ImGui.TextDisabled(typeTotal == 0 ? $"Nothing in {ActiveType.Name} yet." : "No entries match.");
            if (typeTotal == 0)
                ImGui.TextDisabled("Add mods with + Add, or move entries here with their Mod type field.");
            else if (ImGui.Button("Clear filters"))
                ClearFilters();
            return;
        }

        var clipper = ImGui.ImGuiListClipper();
        clipper.Begin(rows.Count, rowHeight);
        while (clipper.Step())
        {
            for (var i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
            {
                if (i < 0 || i >= rows.Count)
                    continue;
                var row = rows[i];
                using var id = ImRaii.PushId(row.Key);
                switch (row.Kind)
                {
                    case RowKind.Header: DrawHeaderRow(row); break;
                    case RowKind.PickGroup: DrawPickRow(row); break;
                    default: DrawEntryRow(row.Mod!); break;
                }
            }
        }
        clipper.End();
        clipper.Destroy();
    }

    private void ClearFilters()
    {
        search = emoteFilter = categoryFilter = positionFilter = string.Empty;
        rating = RatingFilter.All;
        favouritesOnly = onOnly = false;
    }

    private void DrawHeaderRow(Row row)
    {
        var h = ImGui.GetFrameHeight();
        var start = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        var isCollapsed = collapsed.Contains(row.Key);
        if (ImGui.Selectable("###header", false, ImGuiSelectableFlags.None, new Vector2(width, h)))
        {
            if (!collapsed.Add(row.Key))
                collapsed.Remove(row.Key);
        }

        var dl = ImGui.GetWindowDrawList();
        var textY = start.Y + (h - ImGui.GetTextLineHeight()) / 2;
        var icon = (isCollapsed ? FontAwesomeIcon.CaretRight : FontAwesomeIcon.CaretDown).ToIconString();
        dl.AddText(UiBuilder.IconFont, ImGui.GetFontSize(), new Vector2(start.X + Theme.Scaled(4), textY), Theme.U32(Theme.Dim), icon);
        dl.AddText(new Vector2(start.X + Theme.Scaled(22), textY), Theme.U32(Theme.Dim), row.Label);

        var countText = row.Count.ToString();
        var countWidth = ImGui.CalcTextSize(countText).X;
        dl.AddText(new Vector2(start.X + width - countWidth - Theme.Scaled(6), textY), Theme.U32(Theme.Faint), countText);
        if (row.OnCount > 0)
            dl.AddCircleFilled(new Vector2(start.X + width - countWidth - Theme.Scaled(16), start.Y + h / 2), Theme.Scaled(3.5f), Theme.U32(Theme.On));
    }

    private void DrawRatingBar(Vector2 start, ContentRating rating)
    {
        var h = ImGui.GetFrameHeight();
        ImGui.GetWindowDrawList().AddRectFilled(
            start + new Vector2(Theme.Scaled(2), Theme.Scaled(4)),
            start + new Vector2(Theme.Scaled(5), h - Theme.Scaled(4)),
            Theme.U32(Theme.RatingColor(rating)), Theme.Scaled(2));
    }

    private void DrawEntryRow(ManagedMod mod)
    {
        var type = Config.ModTypeOf(mod);
        var h = ImGui.GetFrameHeight();
        var start = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        var isSelected = selected.Contains(mod.Id);
        var rowHovered = ImGui.IsWindowHovered() && ImGui.IsMouseHoveringRect(start, start + new Vector2(width, h));
        var dl = ImGui.GetWindowDrawList();
        if (isSelected)
            dl.AddRectFilled(start, start + new Vector2(width, h), Theme.U32(Theme.Selected), Theme.Scaled(4));
        DrawRatingBar(start, mod.Rating);

        ImGui.SetCursorScreenPos(start + new Vector2(Theme.Scaled(10), 0));
        DrawOnToggle(mod, h);
        ImGui.SetCursorScreenPos(new Vector2(ImGui.GetItemRectMax().X + ImGui.GetStyle().ItemSpacing.X, start.Y));

        var isAnimation = type.Style == ModTypeStyle.Animation && mod.IsAnimation;
        var playWidth = isAnimation ? Theme.IconButtonSize(FontAwesomeIcon.Play).X + ImGui.GetStyle().ItemSpacing.X : 0;
        var selectWidth = Math.Max(Theme.Scaled(40), start.X + width - ImGui.GetCursorScreenPos().X - playWidth);
        var clicked = ImGui.Selectable("###row", false, ImGuiSelectableFlags.AllowDoubleClick, new Vector2(selectWidth, h));
        var hovered = ImGui.IsItemHovered();
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        if (clicked)
        {
            if (isAnimation && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                plugin.Player.Play(mod);
            else
                HandleRowClick(mod.Id);
        }
        if (hovered)
        {
            var tip = ManagedModListUi.FormatPenumbraTooltip(mod).Replace("Right-click to copy.", string.Empty).TrimEnd();
            if (mod.AutoEmoteSync && isAnimation)
                tip += "\nPurple arrows: auto emote sync (runs /heels emotesync after Play).";
            if (isAnimation)
                tip += "\nDouble-click to play.";
            ImGui.SetTooltip(tip);
        }

        // Row text is drawn over the selectable so the whole middle of the row is clickable.
        var textY = min.Y + (h - ImGui.GetTextLineHeight()) / 2;
        var x = min.X + Theme.Scaled(4);
        var right = max.X - Theme.Scaled(4);
        var rightText = isAnimation ? Theme.CommandLabel(mod) : mod.Category;
        var rightColor = isAnimation ? Theme.Gold : Theme.Dim;
        if (rightText.Length > 0)
        {
            var w = ImGui.CalcTextSize(rightText).X;
            dl.AddText(new Vector2(right - w, textY), Theme.U32(rightColor), rightText);
            right -= w + Theme.Scaled(8);
        }
        if (mod.AutoEmoteSync && isAnimation)
        {
            var icon = FontAwesomeIcon.Sync.ToIconString();
            var w = Theme.Scaled(14);
            dl.AddText(UiBuilder.IconFont, ImGui.GetFontSize() * 0.8f, new Vector2(right - w, textY + Theme.Scaled(1)), Theme.U32(Theme.Sync), icon);
            right -= w + Theme.Scaled(6);
        }
        if (mod.IsFavorite)
        {
            dl.AddText(UiBuilder.IconFont, ImGui.GetFontSize() * 0.8f, new Vector2(x, textY + Theme.Scaled(1)), Theme.U32(Theme.Gold), FontAwesomeIcon.Star.ToIconString());
            x += Theme.Scaled(18);
        }

        var missing = plugin.Entries.IsMissing(mod);
        dl.PushClipRect(new Vector2(x, min.Y), new Vector2(Math.Max(x, right), max.Y), true);
        dl.AddText(new Vector2(x, textY), Theme.U32(missing ? Theme.Warning : Theme.Text), mod.DisplayName);
        dl.PopClipRect();

        if (isAnimation)
        {
            ImGui.SameLine();
            using (ImRaii.Disabled(missing || !plugin.Penumbra.Available || plugin.Player.IsBusy))
            {
                if (Theme.IconButton(FontAwesomeIcon.Play, "play", $"Turn on and play {Theme.CommandLabel(mod, true)}", subtle: !rowHovered && !isSelected))
                    plugin.Player.Play(mod);
            }
        }
    }

    /// <summary>A checkbox a little smaller than the row, centred in it, so rows read as text first.</summary>
    private void DrawOnToggle(ManagedMod mod, float rowHeight)
    {
        var missing = plugin.Entries.IsMissing(mod);
        var isOn = plugin.Entries.IsOn(mod) ?? false;
        var checkColor = mod.IsTemp ? Theme.Unsorted : Theme.On;
        var padding = ImGui.GetStyle().FramePadding;
        var shrink = Theme.Scaled(3);
        ImGui.SetCursorScreenPos(ImGui.GetCursorScreenPos() + new Vector2(0, shrink));
        using (ImRaii.PushStyle(ImGuiStyleVar.FramePadding, new Vector2(padding.X, Math.Max(0, padding.Y - shrink))))
        using (ImRaii.Disabled(missing || !plugin.Penumbra.Available))
        using (ImRaii.PushColor(ImGuiCol.CheckMark, checkColor).Push(ImGuiCol.FrameBg, Theme.FrameHover))
        {
            if (ImGui.Checkbox("###on", ref isOn))
                plugin.Entries.Apply(plugin.Entries.ResolveShortcutGroup(mod), isOn);
        }
        Theme.Hint(missing ? $"\"{mod.ModName}\" is not installed in Penumbra."
            : mod.IsTemp ? "Temporary: turned off by \"Turn off temporary\"." : "Kept on until you turn it off.");
    }

    private void DrawPickRow(Row row)
    {
        var h = ImGui.GetFrameHeight();
        var start = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        var isSelected = row.Members.All(m => selected.Contains(m.Id));
        var dl = ImGui.GetWindowDrawList();
        if (isSelected)
            dl.AddRectFilled(start, start + new Vector2(width, h), Theme.U32(Theme.Selected), Theme.Scaled(4));
        DrawRatingBar(start, row.Members.Max(m => m.Rating == ContentRating.Unrated ? ContentRating.Unrated : m.Rating));

        var labels = row.Members.Select(PickOptionLabel).ToList();
        var spacing = Theme.Scaled(2);
        var segWidth = labels.Sum(l => Theme.ButtonWidth(l)) + spacing * (labels.Count - 1);
        var nameWidth = Math.Max(Theme.Scaled(40), width - Theme.Scaled(12) - segWidth - ImGui.GetStyle().ItemSpacing.X);

        ImGui.SetCursorScreenPos(start + new Vector2(Theme.Scaled(10), 0));
        if (ImGui.Selectable("###pick", false, ImGuiSelectableFlags.None, new Vector2(nameWidth, h)))
        {
            if (!ImGui.GetIO().KeyCtrl)
                selected.Clear();
            foreach (var m in row.Members)
                selected.Add(m.Id);
            anchorId = row.Members[0].Id;
        }
        var min = ImGui.GetItemRectMin();
        dl.AddText(new Vector2(min.X + Theme.Scaled(4), min.Y + (h - ImGui.GetTextLineHeight()) / 2), Theme.U32(Theme.Text), row.Label);

        ImGui.SameLine();
        DrawPickSwitch(row.Members, labels);
    }

    /// <summary>One button per option; the lit one is the option currently selected in Penumbra.</summary>
    private void DrawPickSwitch(List<ManagedMod> members, List<string> labels)
    {
        var current = members.FindIndex(m => plugin.Entries.IsOn(m) == true);
        var index = current;
        using (ImRaii.Disabled(!plugin.Penumbra.Available))
        {
            if (Theme.Segmented("switch", labels, ref index) && index >= 0)
            {
                var target = members[index];
                plugin.Entries.Apply([target], true);
                status.Set($"{PickName(members)}: {labels[index]}.");
            }
        }
    }

    private void HandleRowClick(string id)
    {
        var io = ImGui.GetIO();
        if (io.KeyShift && anchorId != null)
        {
            var order = rows.Where(r => r.Kind != RowKind.Header).SelectMany(r => r.Kind == RowKind.PickGroup ? r.Members.Select(m => m.Id) : new[] { r.Key }).ToList();
            var a = order.IndexOf(anchorId);
            var b = order.IndexOf(id);
            if (a >= 0 && b >= 0)
            {
                if (!io.KeyCtrl)
                    selected.Clear();
                for (var i = Math.Min(a, b); i <= Math.Max(a, b); i++)
                    selected.Add(order[i]);
                return;
            }
        }

        if (io.KeyCtrl)
        {
            if (!selected.Add(id))
                selected.Remove(id);
        }
        else
        {
            var wasOnly = selected.Count == 1 && selected.Contains(id);
            selected.Clear();
            if (!wasOnly)
                selected.Add(id);
        }
        anchorId = id;
    }
}
