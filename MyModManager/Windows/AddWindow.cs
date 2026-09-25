using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using MyModManager.Helpers;
using MyModManager.Models;
using MyModManager.Services;
using Penumbra.Api.Enums;

namespace MyModManager.Windows;

/// <summary>
/// Adds Penumbra mods to the Library. Laid out like Penumbra's own mod panel: the mod itself
/// first (just on/off, or once per animation it plays), then its option groups with the real
/// option names. Animations and poses are read from the mod's files.
/// Also used to point an existing entry at a different mod or option.
/// </summary>
public sealed class AddWindow : Window, IDisposable
{
    private enum View { Browse, NewAnimations }

    private sealed class Candidate
    {
        public bool Include;
        public bool WholeMod;
        public bool Plain;                  // "just the mod", no emote
        public string Label = string.Empty; // what it is, in Penumbra's words
        public string Suffix = string.Empty; // role or pose when one option plays several
        public string Name = string.Empty;  // editable entry name
        public string Group = string.Empty;
        public string Option = string.Empty;
        public GroupType Type = GroupType.Single;
        public string Command = string.Empty;
        public int? Pose;
        public DetectionSource? Source;
        public bool InLibrary;
        public bool SelectedInPenumbra;
    }

    private static readonly string[] OffOptionNames = ["none", "off", "disabled", "disable", "vanilla", "nothing", "original"];
    private static readonly Regex TagBrackets = new(@"\s*\[[^\]]*\]", RegexOptions.Compiled);
    private static readonly Regex LeadingNumber = new(@"^\s*\d{2,4}\s+", RegexOptions.Compiled);
    private static readonly Regex RoleParens = new(@"\s*\([^)]*[A-Za-z]+\s?-\s?[A-Za-z]+[^)]*\)", RegexOptions.Compiled);

    private readonly Plugin plugin;
    private readonly StatusLine status = new();

    private View view = View.Browse;
    private string search = string.Empty;
    private bool hideAdded;
    private string? selectedDir;
    private ManagedMod? rebindTarget;

    private List<KeyValuePair<string, string>> modRows = new();
    private string listKey = string.Empty;

    private string? loadedDir;
    private Task<ModFiles?>? filesTask;
    private List<Candidate> candidates = new();
    private bool candidatesBuilt;
    private bool isPack;
    private bool modOnInPenumbra;
    private string optionFilter = string.Empty;
    private bool animationsOnly;
    private readonly Dictionary<string, bool> openSections = new(StringComparer.Ordinal);
    private Candidate? editingCommand;
    private bool openCommandPopup;
    private (float Check, float Label, float Plays, float Try, float Name) columns;

    /// <summary>What "Try" turned on, and the mod's settings before, so Undo can restore them exactly.</summary>
    private sealed record TrySession(string Dir, string Label, string Group, GroupType Type, ModState? Before);
    private TrySession? trying;

    private const string ModSection = "\u0001mod";

    private string typeId = string.Empty;
    private int ratingChoice = 2; // 0 SFW, 1 NSFW, 2 sort later
    private string category = string.Empty;
    private string position = string.Empty;
    private string folder = string.Empty;
    private bool temporary = true;
    private bool autoSync;
    private bool favourite;
    private string newCategory = string.Empty;
    private string newPosition = string.Empty;

    private Dictionary<string, List<EmoteInfo>>? unlisted;

    private Configuration Config => plugin.Configuration;

    public AddWindow(Plugin plugin)
        : base("Add from Penumbra###MyModManager.Add", ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        this.plugin = plugin;
        Size = new Vector2(1100, 680);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(820, 460), MaximumSize = new Vector2(float.MaxValue, float.MaxValue) };
    }

    public void Dispose() { }

    public override void PreDraw() => Theme.Push(Config.WindowOpacity);

    public override void PostDraw() => Theme.Pop();

    public void Open()
    {
        rebindTarget = null;
        IsOpen = true;
    }

    /// <summary>Opens on the entry's current mod so it can be pointed at another mod or option.</summary>
    public void OpenForRebind(ManagedMod mod)
    {
        rebindTarget = mod;
        view = View.Browse;
        search = string.Empty;
        Select(mod.ModName, force: true);
        IsOpen = true;
    }

    public override void OnClose()
    {
        rebindTarget = null;
        // "Try" promises nothing is added, so closing puts a tried mod back.
        UndoTry();
    }

    public override void Draw()
    {
        if (!plugin.Penumbra.Available)
        {
            ImGui.TextColored(Theme.Unsorted, "Penumbra isn't running, so there's nothing to add from yet.");
            return;
        }

        if (rebindTarget != null)
        {
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(Theme.Unsorted, $"Choosing what “{rebindTarget.DisplayName}” turns on: pick a mod, then press Use on a row.");
            ImGui.SameLine();
            if (ImGui.Button("Cancel"))
                rebindTarget = null;
        }

        var leftWidth = Math.Clamp(ImGui.GetContentRegionAvail().X * 0.32f, Theme.Scaled(280), Theme.Scaled(400));
        using (var left = ImRaii.Child("###addLeft", new Vector2(leftWidth, 0), true))
        {
            if (left)
                DrawModList();
        }

        ImGui.SameLine();
        using (var right = ImRaii.Child("###addRight", Vector2.Zero, true))
        {
            if (right)
            {
                if (selectedDir == null)
                    DrawOverview();
                else
                    DrawModDetail(selectedDir);
            }
        }
    }

    // ------------------------------------------------------------------ mod list

    private void DrawModList()
    {
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - Theme.IconButtonSize(FontAwesomeIcon.QuestionCircle).X - ImGui.GetStyle().ItemSpacing.X);
        ImGui.InputTextWithHint("###modSearch", $"Search {plugin.Penumbra.ModList.Count} Penumbra mods…", ref search, 128);
        ImGui.SameLine();
        if (Theme.IconButton(FontAwesomeIcon.QuestionCircle, "help", "How adding works"))
            plugin.HelpWindow.Open(HelpTopic.Adding);

        var viewIndex = (int)view;
        var newCount = unlisted?.Count;
        if (Theme.Segmented("view", ["All mods", newCount is { } n ? $"New animations ({n})" : "New animations"], ref viewIndex))
        {
            view = (View)viewIndex;
            if (view == View.NewAnimations)
                ScanUnlisted();
            selectedDir = null;
        }
        Theme.Hint(viewIndex == 1 ? "Mods that replace emotes and aren't in your library yet." : "Every mod installed in Penumbra.");

        ImGui.SameLine();
        if (view == View.Browse)
        {
            Theme.RightAlign(Theme.ButtonWidth("Hide added"));
            if (Theme.Chip("Hide added", hideAdded))
                hideAdded = !hideAdded;
        }
        else
        {
            Theme.RightAlign(Theme.ButtonWidth("Rescan"));
            if (ImGui.Button("Rescan"))
                ScanUnlisted();
        }

        ImGui.Separator();
        RebuildModRows();

        using var spacing = ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, new Vector2(ImGui.GetStyle().ItemSpacing.X, Theme.Scaled(2)));
        using var list = ImRaii.Child("###mods", Vector2.Zero, false);
        if (!list)
            return;
        if (modRows.Count == 0)
        {
            ImGui.TextDisabled(view == View.NewAnimations ? "Every animation mod is already in your library." : "No Penumbra mod matches.");
            return;
        }

        var managed = Config.ManagedMods.GroupBy(m => m.ModName, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        var clipper = ImGui.ImGuiListClipper();
        clipper.Begin(modRows.Count, ImGui.GetFrameHeightWithSpacing());
        while (clipper.Step())
        {
            for (var i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
            {
                if (i < 0 || i >= modRows.Count)
                    continue;
                var (dir, name) = modRows[i];
                using var id = ImRaii.PushId(dir);
                var h = ImGui.GetFrameHeight();
                var width = ImGui.GetContentRegionAvail().X;
                var start = ImGui.GetCursorScreenPos();
                if (ImGui.Selectable("###mod", selectedDir == dir, ImGuiSelectableFlags.None, new Vector2(width, h)))
                    Select(dir);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(name);

                var dl = ImGui.GetWindowDrawList();
                var textY = start.Y + (h - ImGui.GetTextLineHeight()) / 2;
                var right = start.X + width - Theme.Scaled(6);
                if (managed.TryGetValue(dir, out var count))
                {
                    var label = count == 1 ? "added" : $"{count} added";
                    var w = ImGui.CalcTextSize(label).X;
                    dl.AddText(new Vector2(right - w, textY), Theme.U32(Theme.Gold), label);
                    right -= w + Theme.Scaled(8);
                }
                dl.PushClipRect(start, new Vector2(Math.Max(start.X, right), start.Y + h), true);
                dl.AddText(new Vector2(start.X + Theme.Scaled(6), textY), Theme.U32(Theme.Text), name);
                dl.PopClipRect();
            }
        }
        clipper.End();
        clipper.Destroy();
    }

    private void RebuildModRows()
    {
        var key = string.Join('\u0001', search, view, hideAdded, plugin.Penumbra.Version, Config.Revision, unlisted?.Count ?? -1);
        if (key == listKey)
            return;
        listKey = key;

        var managed = Config.ManagedMods.Select(m => m.ModName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var words = search.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        IEnumerable<KeyValuePair<string, string>> mods = plugin.Penumbra.ModList;
        if (view == View.NewAnimations)
            mods = mods.Where(m => unlisted?.ContainsKey(m.Key) == true && !managed.Contains(m.Key));
        else if (hideAdded)
            mods = mods.Where(m => !managed.Contains(m.Key));
        if (words.Length > 0)
            mods = mods.Where(m => words.All(w => m.Value.Contains(w, StringComparison.OrdinalIgnoreCase) || m.Key.Contains(w, StringComparison.OrdinalIgnoreCase)));
        modRows = mods.OrderBy(m => m.Value, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private void Select(string dir, bool force = false)
    {
        selectedDir = dir;
        if (loadedDir == dir && !force)
            return;

        loadedDir = dir;
        candidatesBuilt = false;
        candidates = new List<Candidate>();
        optionFilter = string.Empty;
        openSections.Clear();
        editingCommand = null;
        var modFolder = plugin.Penumbra.GetModFolder(dir);
        filesTask = modFolder == null ? Task.FromResult<ModFiles?>(null) : Task.Run(() => ModFileReader.Read(modFolder));
    }

    // ------------------------------------------------------------------ overview

    private void DrawOverview()
    {
        if (view == View.Browse)
        {
            ImGui.TextUnformatted("Pick a mod on the left.");
            ImGui.Spacing();
            ImGui.TextDisabled("You'll see it the way Penumbra shows it: the mod itself, then its options.");
            ImGui.TextDisabled("Tick what you want in your library. Animations and poses are read from the mod's files,");
            ImGui.TextDisabled("so a [Gsit1_2] mod shows as Sit on Ground pose 1 and pose 2.");
            return;
        }

        if (unlisted == null)
            return;
        var single = unlisted.Where(kv => kv.Value.Count == 1 && !Config.ManagedMods.Any(m => m.ModName.Equals(kv.Key, StringComparison.OrdinalIgnoreCase))).ToList();
        ImGui.TextUnformatted($"{unlisted.Count} mods replace emotes and aren't in your library.");
        ImGui.Spacing();
        if (single.Count > 0)
        {
            ImGui.TextDisabled($"{single.Count} of them replace a single emote, so they can be added in one go, ready to sort.");
            ImGui.Spacing();
            if (Theme.PrimaryButton($"Add {single.Count} as unsorted"))
                AddAllSingleEmote(single);
        }
        ImGui.Spacing();
        ImGui.TextDisabled("Mods that play several emotes or poses: open them to choose.");
    }

    private void ScanUnlisted()
    {
        var snapshot = plugin.Penumbra.GetChangedItemsSnapshot();
        if (snapshot == null)
        {
            status.Set("Couldn't read Penumbra's changed items.");
            return;
        }

        var managed = Config.ManagedMods.Select(m => m.ModName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        unlisted = new Dictionary<string, List<EmoteInfo>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (dir, items) in snapshot)
        {
            if (managed.Contains(dir))
                continue;
            var found = items.Keys
                .Where(k => k.StartsWith("Emote:", StringComparison.OrdinalIgnoreCase))
                .Select(k => plugin.Emotes.FromName(k))
                .OfType<EmoteInfo>()
                .DistinctBy(e => e.Id)
                .ToList();
            if (found.Count > 0)
                unlisted[dir] = found;
        }
        listKey = string.Empty;
    }

    private void AddAllSingleEmote(List<KeyValuePair<string, List<EmoteInfo>>> mods)
    {
        var animationType = Config.ModTypes.FirstOrDefault(t => t.Style == ModTypeStyle.Animation) ?? Config.ModTypes[0];
        var created = new List<ManagedMod>();
        foreach (var (dir, emotesFound) in mods)
        {
            var name = plugin.Penumbra.ModList.GetValueOrDefault(dir, dir);
            var emote = emotesFound[0];
            var poses = AnimationDetector.FromName(plugin.Emotes, name).Where(d => d.Emote.Id == emote.Id && d.Pose != null).Select(d => d.Pose).ToList();
            if (poses.Count == 0)
                poses.Add(null);
            foreach (var pose in poses)
            {
                created.Add(new ManagedMod
                {
                    ModName = dir,
                    DisplayName = poses.Count > 1 ? $"{name} · pose {pose}" : name,
                    AnimationCommand = emote.Command,
                    PoseNumber = pose,
                    IsAnimation = true,
                    ModTypeId = animationType.Id,
                    Rating = ContentRating.Unrated,
                    IsTemp = true,
                });
            }
        }

        Config.ManagedMods.AddRange(created);
        Config.Save();
        status.Set($"Added {created.Count} entries to {animationType.Name} as unsorted.");
        Svc.Print($"Added {created.Count} animation entries as unsorted. Rate them from the Library's Unsorted button.");
        ScanUnlisted();
    }

    // ------------------------------------------------------------------ one mod

    private void DrawModDetail(string dir)
    {
        var name = plugin.Penumbra.ModList.GetValueOrDefault(dir, dir);
        ImGui.TextUnformatted(name);
        ImGui.TextDisabled($"Penumbra: {plugin.Penumbra.GetSelectorPath(dir) ?? dir}");

        if (filesTask is not { IsCompleted: true })
        {
            ImGui.Spacing();
            ImGui.TextDisabled("Reading the mod's files…");
            return;
        }

        if (!candidatesBuilt)
            BuildCandidates(dir, name, filesTask.Result);

        ImGui.SameLine();
        var stateText = modOnInPenumbra ? "On in your collection" : "Off in your collection";
        Theme.RightAlign(ImGui.CalcTextSize(stateText).X + Theme.Scaled(14));
        Theme.Dot(modOnInPenumbra ? Theme.On : Theme.Faint);
        ImGui.SameLine();
        if (modOnInPenumbra)
            ImGui.TextColored(Theme.On, stateText);
        else
            ImGui.TextDisabled(stateText);

        ImGui.Spacing();
        DrawSummary();
        DrawTryBar();

        var listWidth = ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ScrollbarSize;
        var check = rebindTarget != null ? Theme.ButtonWidth("Use") : ImGui.GetFrameHeight() * 1.6f;
        var playsWidth = Math.Max(Theme.Scaled(180), listWidth * 0.2f);
        var tryWidth = ImGui.GetFrameHeight() * 1.6f;
        var rest = Math.Max(Theme.Scaled(200), listWidth - check - playsWidth - tryWidth - ImGui.GetStyle().CellPadding.X * 10);
        columns = (check, rest * 0.55f, playsWidth, tryWidth, rest * 0.45f);
        DrawColumnHeaders();

        var settingsHeight = rebindTarget == null ? ImGui.GetFrameHeightWithSpacing() * 5 + Theme.Scaled(16) : 0;
        using (var table = ImRaii.Child("###candidates", new Vector2(0, -settingsHeight), false))
        {
            if (table)
                DrawCandidates();
        }

        if (rebindTarget == null)
            DrawNewEntrySettings(dir);
    }

    private void DrawSummary()
    {
        var animated = candidates.Where(c => c.Command.Length > 0 && !c.WholeMod).ToList();
        if (isPack)
            ImGui.TextDisabled($"A pack with {animated.Count} animations in its options. Tick the ones you want; each becomes its own entry.");
        else if (candidates.Any(c => c.WholeMod && !c.Plain))
            ImGui.TextDisabled("Tick what to add. Adding the mod turns it on with the options you've set in Penumbra.");
        else
            ImGui.TextDisabled("No animations found in this mod. Add it as an on/off entry, or pick options to switch between.");

        var hasOptions = candidates.Any(c => !c.WholeMod);
        if (hasOptions)
        {
            if (candidates.Count(c => !c.WholeMod) > 10)
            {
                ImGui.SetNextItemWidth(Theme.Scaled(240));
                ImGui.InputTextWithHint("###optionFilter", "Filter options…", ref optionFilter, 64);
                ImGui.SameLine();
            }
            if (candidates.Any(c => c.Command.Length > 0))
            {
                if (Theme.Chip("Animations only", animationsOnly, Theme.Gold))
                    animationsOnly = !animationsOnly;
                Theme.Hint(animationsOnly ? "Options without an emote are hidden. Click to show every option." : "Showing every option. Click to hide options without an emote.");
                ImGui.SameLine();
            }
            if (isPack)
            {
                if (ImGui.Button("Tick all shown"))
                    foreach (var c in VisibleCandidates().Where(c => !c.WholeMod && !c.InLibrary && IsSectionOpen(SectionKey(c)))) c.Include = true;
                ImGui.SameLine();
            }
            if (ImGui.Button("Untick all"))
                foreach (var c in candidates) c.Include = false;
        }
        status.Draw();
    }

    private IEnumerable<Candidate> VisibleCandidates()
    {
        var words = optionFilter.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return candidates.Where(c => c.WholeMod
            || ((!animationsOnly || c.Command.Length > 0)
                && words.All(w => $"{c.Label} {c.Suffix} {c.Group} {c.Command}".Contains(w, StringComparison.OrdinalIgnoreCase))));
    }

    private static string SectionKey(Candidate c) => c.WholeMod ? ModSection : c.Group;

    /// <summary>
    /// Sections look like Penumbra's option groups: a full-width collapsible bar, then identical
    /// rows. Packs start collapsed; a filter opens every section with a match.
    /// </summary>
    private void DrawCandidates()
    {
        var visible = VisibleCandidates().ToList();
        if (visible.Count == 0)
        {
            ImGui.TextDisabled("No options match.");
            return;
        }


        var sectionIndex = 0;
        foreach (var section in visible.GroupBy(SectionKey))
        {
            using var sectionId = ImRaii.PushId(sectionIndex++);
            var all = candidates.Where(c => SectionKey(c) == section.Key).ToList();
            if (!DrawSectionHeader(section.Key, section.First(), all))
                continue;

            using var table = ImRaii.Table("###rows", 5, ImGuiTableFlags.PadOuterX);
            if (!table)
                continue;
            SetupColumns();

            var rowIndex = 0;
            foreach (var c in section)
            {
                using var rowId = ImRaii.PushId(rowIndex++);
                DrawCandidateRow(c);
            }
        }

        DrawCommandPopup();
    }

    private bool IsSectionOpen(string key)
    {
        if (optionFilter.Length > 0)
            return true;
        if (openSections.TryGetValue(key, out var open))
            return open;
        if (key == ModSection)
            return true;

        var rows = candidates.Where(c => SectionKey(c) == key).ToList();
        if (isPack)
            return false;
        var modHasAnimations = candidates.Any(c => c.Command.Length > 0);
        return rows.Count <= 30 && (!modHasAnimations || rows.Any(c => c.Command.Length > 0));
    }

    private bool DrawSectionHeader(string key, Candidate first, List<Candidate> all)
    {
        var open = IsSectionOpen(key);
        var h = ImGui.GetFrameHeight();
        var start = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        var dl = ImGui.GetWindowDrawList();
        dl.AddRectFilled(start, start + new Vector2(width, h), Theme.U32(Theme.FrameHover), Theme.Scaled(4));

        using (ImRaii.PushColor(ImGuiCol.HeaderHovered, Theme.FrameActive).Push(ImGuiCol.HeaderActive, Theme.FrameActive))
        {
            if (ImGui.Selectable("###section", false, ImGuiSelectableFlags.None, new Vector2(width, h)))
                openSections[key] = !open;
        }

        var textY = start.Y + (h - ImGui.GetTextLineHeight()) / 2;
        var x = start.X + Theme.Scaled(8);
        var caret = (open ? FontAwesomeIcon.CaretDown : FontAwesomeIcon.CaretRight).ToIconString();
        dl.AddText(UiBuilder.IconFont, ImGui.GetFontSize(), new Vector2(x, textY), Theme.U32(Theme.Text), caret);
        x += Theme.Scaled(20);

        var title = key == ModSection ? "The mod itself" : first.Group;
        var animated = all.Count(c => c.Command.Length > 0);
        var detail = key == ModSection
            ? "on / off with the options you've set in Penumbra"
            : $"{(first.Type == GroupType.Multi ? "any number" : "pick one")} · {all.Select(c => c.Option).Distinct().Count()} options{(animated > 0 ? $", {animated} animations" : string.Empty)}";
        var ticked = all.Count(c => c.Include);
        var tickedText = ticked > 0 ? $"{ticked} ticked" : string.Empty;
        var right = start.X + width - Theme.Scaled(10);
        if (tickedText.Length > 0)
        {
            var tw = ImGui.CalcTextSize(tickedText).X;
            dl.AddText(new Vector2(right - tw, textY), Theme.U32(Theme.Gold), tickedText);
            right -= tw + Theme.Scaled(12);
        }

        dl.PushClipRect(new Vector2(x, start.Y), new Vector2(Math.Max(x, right), start.Y + h), true);
        dl.AddText(new Vector2(x, textY), Theme.U32(Theme.Text), title);
        var titleWidth = ImGui.CalcTextSize(title).X;
        dl.AddText(new Vector2(x + titleWidth + Theme.Scaled(12), textY), Theme.U32(Theme.Dim), detail);
        dl.PopClipRect();
        return open;
    }

    private void DrawCandidateRow(Candidate c)
    {
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        if (rebindTarget != null)
        {
            if (Theme.PrimaryButton("Use"))
                Rebind(c);
        }
        else if (c.InLibrary)
        {
            ImGui.AlignTextToFramePadding();
            using (ImRaii.PushFont(UiBuilder.IconFont))
                ImGui.TextColored(Theme.Gold, FontAwesomeIcon.Check.ToIconString());
            Theme.Hint("Already in your library.");
        }
        else
        {
            ImGui.Checkbox("###include", ref c.Include);
        }

        ImGui.TableNextColumn();
        ImGui.AlignTextToFramePadding();
        if (c.SelectedInPenumbra)
        {
            Theme.Dot(Theme.On);
            Theme.Hint("Currently selected in Penumbra.");
            ImGui.SameLine();
        }
        var fullText = c.Suffix.Length > 0 ? $"{c.Label}    {c.Suffix}" : c.Label;
        if (ImGui.CalcTextSize(fullText).X <= ImGui.GetContentRegionAvail().X)
        {
            ImGui.TextUnformatted(c.Label);
            if (c.Suffix.Length > 0)
            {
                ImGui.SameLine();
                ImGui.TextDisabled(c.Suffix);
            }
        }
        else
        {
            ImGui.TextWrapped(c.Label);
            if (c.Suffix.Length > 0)
                ImGui.TextDisabled(c.Suffix);
        }
        if (c.InLibrary)
        {
            ImGui.SameLine();
            ImGui.TextColored(Theme.Gold, "added");
        }

        ImGui.TableNextColumn();
        DrawPlaysButton(c);

        ImGui.TableNextColumn();
        using (ImRaii.Disabled(!plugin.Penumbra.Available || plugin.Player.IsBusy))
        {
            if (Theme.IconButton(FontAwesomeIcon.Play, "try", c.Command.Length > 0
                    ? $"Try it: turns this on in Penumbra and plays {CommandText(c.Command, c.Pose)} on your character. Nothing is added."
                    : "Try it: turns this on in Penumbra. Nothing is added."))
                Try(c);
        }

        ImGui.TableNextColumn();
        ImGui.SetNextItemWidth(-1);
        using (ImRaii.Disabled(c.InLibrary || rebindTarget != null))
        using (ImRaii.PushStyle(ImGuiStyleVar.FrameBorderSize, 1f).Push(ImGuiStyleVar.FrameRounding, Theme.Scaled(4)))
        using (ImRaii.PushColor(ImGuiCol.Border, Theme.Border).Push(ImGuiCol.FrameBg, Theme.WindowBg))
            ImGui.InputText("###name", ref c.Name, 120);
        Theme.Hint("The entry's name in your library. Click to change it.");
    }

    private void SetupColumns()
    {
        ImGui.TableSetupColumn(rebindTarget != null ? "Use" : "Add", ImGuiTableColumnFlags.WidthFixed, columns.Check);
        ImGui.TableSetupColumn("In Penumbra", ImGuiTableColumnFlags.WidthFixed, columns.Label);
        ImGui.TableSetupColumn("Plays  (click to change)", ImGuiTableColumnFlags.WidthFixed, columns.Plays);
        ImGui.TableSetupColumn("Try", ImGuiTableColumnFlags.WidthFixed, columns.Try);
        ImGui.TableSetupColumn("Name in your library  (click to edit)", ImGuiTableColumnFlags.WidthFixed, columns.Name);
    }

    /// <summary>Column titles, drawn once above the scrolling list so they stay visible.</summary>
    private void DrawColumnHeaders()
    {
        using var table = ImRaii.Table("###headers", 5, ImGuiTableFlags.PadOuterX);
        if (!table)
            return;
        SetupColumns();
        using (ImRaii.PushColor(ImGuiCol.TableHeaderBg, Vector4.Zero).Push(ImGuiCol.Text, Theme.Dim))
            ImGui.TableHeadersRow();
    }

    // ------------------------------------------------------------------ try before adding

    private void DrawTryBar()
    {
        if (trying == null)
            return;
        using (ImRaii.PushColor(ImGuiCol.ChildBg, Theme.UnsortedBg))
        using (var bar = ImRaii.Child("###tryBar", new Vector2(0, ImGui.GetFrameHeightWithSpacing() + Theme.Scaled(8)), true))
        {
            if (!bar)
                return;
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(Theme.Unsorted, $"Trying “{trying.Label}”. It's on in Penumbra but not added.");
            ImGui.SameLine();
            Theme.RightAlign(Theme.ButtonWidth("Undo try"));
            if (ImGui.Button("Undo try"))
                UndoTry();
            Theme.Hint("Put the mod's Penumbra settings back to how they were before you tried it.");
        }
    }

    private void Try(Candidate c)
    {
        if (selectedDir == null)
            return;

        // Keep the original "before" when trying several rows of the same mod in a row.
        var before = trying?.Dir == selectedDir ? trying.Before : plugin.Penumbra.ReadSettings(selectedDir);
        if (trying != null && trying.Dir != selectedDir)
            UndoTry();

        var temp = new ManagedMod
        {
            ModName = selectedDir,
            DisplayName = c.Name.Trim().Length > 0 ? c.Name.Trim() : c.Label,
            GroupName = c.Group,
            OptionName = c.Option,
            GroupType = c.Type,
            AnimationCommand = c.Command.Trim(),
            PoseNumber = c.Pose,
            IsAnimation = c.Command.Trim().Length > 0,
            IsTemp = true,
            AutoEmoteSync = autoSync && c.Command.Trim().Length > 0,
        };
        trying = new TrySession(selectedDir, temp.DisplayName, c.Group, c.Type, before);

        if (temp.IsAnimation)
            plugin.Player.Play(temp);
        else
            plugin.Entries.Apply([temp], true);
    }

    private void UndoTry()
    {
        var session = trying;
        trying = null;
        if (session == null)
            return;

        var penumbra = plugin.Penumbra;
        var before = session.Before;
        if (before == null)
        {
            penumbra.SetModEnabled(session.Dir, false);
        }
        else
        {
            if (session.Group.Length > 0)
            {
                var previous = before.Settings.TryGetValue(session.Group, out var list) ? list : new List<string>();
                if (session.Type == GroupType.Multi)
                    penumbra.SetMultiOptions(session.Dir, session.Group, previous);
                else if (previous.Count > 0)
                    penumbra.SetSingleOption(session.Dir, session.Group, previous[0]);
            }
            penumbra.SetModEnabled(session.Dir, before.Enabled);
        }

        if (Config.RedrawAfterChange)
            penumbra.RedrawPlayer();
        status.Set($"Put {plugin.Penumbra.ModList.GetValueOrDefault(session.Dir, session.Dir)} back the way it was.");
    }

    /// <summary>The command a row plays, as a gold button; clicking it edits command and pose.</summary>
    private void DrawPlaysButton(Candidate c)
    {
        var label = c.Command.Length == 0 ? (c.Plain ? "just on / off" : "no emote") : CommandText(c.Command, c.Pose);
        using (ImRaii.PushColor(ImGuiCol.Text, c.Command.Length == 0 ? Theme.Dim : Theme.Gold))
        using (ImRaii.PushColor(ImGuiCol.Button, Theme.Frame))
        {
            if (ImGui.Button($"{label}###plays", new Vector2(-1, 0)) && rebindTarget == null && !c.InLibrary)
            {
                editingCommand = c;
                openCommandPopup = true;
            }
        }

        if (rebindTarget == null && !c.InLibrary)
        {
            var max = ImGui.GetItemRectMax();
            var iconSize = ImGui.GetFontSize() * 0.75f;
            ImGui.GetWindowDrawList().AddText(UiBuilder.IconFont, iconSize,
                new Vector2(max.X - iconSize - Theme.Scaled(6), max.Y - (ImGui.GetFrameHeight() + iconSize) / 2),
                Theme.U32(Theme.Dim), FontAwesomeIcon.Pen.ToIconString());
        }
        if (c.Source == DetectionSource.Name)
            Theme.Hint("Guessed from the name, not the files. Click to change.");
        else if (c.Command.Length > 0)
            Theme.Hint("Found in the mod's files. Click to change.");
        else
            Theme.Hint("Click to set an emote command, e.g. /groundsit.");
    }

    private void DrawCommandPopup()
    {
        // Opened here rather than in the row: OpenPopup and BeginPopup must share an ID stack.
        if (openCommandPopup)
        {
            openCommandPopup = false;
            ImGui.OpenPopup("###commandPopup");
        }

        using var popup = ImRaii.Popup("###commandPopup");
        if (!popup || editingCommand == null)
            return;

        var c = editingCommand;
        Theme.Label("Emote command");
        ImGui.SetNextItemWidth(Theme.Scaled(200));
        if (ImGui.IsWindowAppearing())
            ImGui.SetKeyboardFocusHere();
        ImGui.InputTextWithHint("###cmd", "/groundsit", ref c.Command, 48);
        var emote = plugin.Emotes.FromCommand(c.Command);
        if (c.Command.Length > 0)
        {
            if (emote != null)
                ImGui.TextColored(Theme.On, emote.Name);
            else
                ImGui.TextColored(Theme.Warning, "Not an emote the game knows.");
        }

        if (emote is { PoseCount: > 1 })
        {
            Theme.Label("Pose");
            var options = new List<string> { "Any" };
            options.AddRange(Enumerable.Range(0, emote.PoseCount).Select(p => p == 0 ? "Default" : p.ToString()));
            var index = c.Pose is { } p ? p + 1 : 0;
            if (Theme.Segmented("pose", options, ref index))
                c.Pose = index == 0 ? null : index - 1;
            ImGui.TextDisabled("Numbered like mod names: [Gsit1] is pose 1.");
        }
        else
        {
            c.Pose = null;
        }

        if (ImGui.Button("Done"))
            ImGui.CloseCurrentPopup();
    }

    private static string CommandText(string command, int? pose) => pose switch
    {
        null => command,
        0 => $"{command} · default",
        var p => $"{command} · pose {p}",
    };

    // ------------------------------------------------------------------ candidates

    private void BuildCandidates(string dir, string modName, ModFiles? files)
    {
        candidatesBuilt = true;
        candidates = new List<Candidate>();
        var emotes = plugin.Emotes;
        var settings = plugin.Penumbra.ReadSettings(dir);
        modOnInPenumbra = settings?.Enabled == true;

        // The mod itself: once as plain on/off, then once per animation it plays.
        var whole = AnimationDetector.Detect(emotes, modName, files?.AllPaths ?? Enumerable.Empty<string>());
        if (whole.Count == 0 && files == null)
        {
            whole = plugin.Penumbra.GetChangedItemNames(dir)
                .Select(emotes.FromName).OfType<EmoteInfo>().DistinctBy(e => e.Id)
                .Select(e => new Detection(e, null, string.Empty, DetectionSource.Name)).ToList();
        }

        candidates.Add(new Candidate { WholeMod = true, Plain = true, Label = "Just the mod (on / off)", Name = modName });
        var manyEmotes = whole.Select(d => d.Emote.Id).Distinct().Count() > 1;
        foreach (var d in whole)
        {
            candidates.Add(new Candidate
            {
                WholeMod = true,
                Label = $"As {Describe(d)}",
                Name = $"{modName} · {ShortSuffix(d, manyEmotes)}",
                Command = d.Emote.Command,
                Pose = d.Pose,
                Source = d.Source,
            });
        }

        // Options, grouped as in Penumbra, with their real names.
        IEnumerable<(string Group, GroupType Type, string Option, IReadOnlyList<string> Paths)> options = files != null
            ? files.Groups.SelectMany(g => g.Options.Select(o => (g.Name, g.Type, o.Name, o.GamePaths)))
            : (plugin.Penumbra.GetOptionGroups(dir) ?? new List<OptionGroup>())
                .SelectMany(g => g.Options.Select(o => (g.Name, g.Type, o, (IReadOnlyList<string>)Array.Empty<string>())));

        foreach (var (group, type, option, paths) in options)
        {
            var selected = settings?.Settings.TryGetValue(group, out var list) == true && list.Contains(option);
            if (OffOptionNames.Contains(option.Trim(), StringComparer.OrdinalIgnoreCase))
                continue;

            var detected = AnimationDetector.Detect(emotes, option, paths);
            var baseName = EntryNameFromOption(option, detected.Any(d => d.Role.Length > 0));
            var optionManyEmotes = detected.Select(d => d.Emote.Id).Distinct().Count() > 1;
            if (detected.Count == 0)
            {
                candidates.Add(new Candidate { Label = option, Name = baseName, Group = group, Option = option, Type = type, SelectedInPenumbra = selected });
                continue;
            }

            foreach (var d in detected)
            {
                candidates.Add(new Candidate
                {
                    Label = option,
                    Suffix = detected.Count == 1 ? d.Role : ShortSuffix(d, optionManyEmotes),
                    Name = detected.Count == 1 ? baseName : $"{baseName} · {ShortSuffix(d, optionManyEmotes)}",
                    Group = group,
                    Option = option,
                    Type = type,
                    Command = d.Emote.Command,
                    Pose = d.Pose,
                    Source = d.Source,
                    SelectedInPenumbra = selected,
                });
            }
        }

        foreach (var c in candidates)
            c.InLibrary = IsInLibrary(dir, c);

        var optionRows = candidates.Where(c => !c.WholeMod).ToList();
        var animatedOptions = optionRows.Count(c => c.Command.Length > 0);
        var multiAnimated = optionRows.Where(c => c.Type == GroupType.Multi && c.Command.Length > 0).GroupBy(c => c.Group).Any(g => g.Count() >= 5);
        isPack = animatedOptions > 12 || multiAnimated;

        // "The mod itself" lists what it plays only when that's short and meaningful. A pack's
        // dozens of emotes are noise there; its options carry them. One animation folds into
        // the single "whole mod" row.
        var wholeRows = candidates.Where(c => c.WholeMod && !c.Plain).ToList();
        if (isPack || wholeRows.Count > 4)
        {
            candidates.RemoveAll(c => c.WholeMod && !c.Plain);
        }
        else if (wholeRows.Count == 1)
        {
            var plain = candidates[0];
            plain.Label = "The whole mod";
            plain.Command = wholeRows[0].Command;
            plain.Pose = wholeRows[0].Pose;
            plain.Source = wholeRows[0].Source;
            candidates.Remove(wholeRows[0]);
            plain.InLibrary = IsInLibrary(dir, plain);
        }

        // Pre-tick only the obvious: a couple mod's one or two poses, or a mod with no animations.
        var wholeAnimated = candidates.Where(c => c.WholeMod && !c.Plain).ToList();
        foreach (var c in candidates)
            c.Include = false;
        if (!isPack)
        {
            if (wholeAnimated.Count is > 0 and <= 2)
                wholeAnimated.ForEach(c => c.Include = !c.InLibrary);
            else if (wholeAnimated.Count == 0 && (candidates[0].Command.Length > 0 || optionRows.Count == 0))
                candidates[0].Include = !candidates[0].InLibrary;
        }

        var anyAnimation = candidates.Any(c => c.Command.Length > 0);
        animationsOnly = anyAnimation;
        var animationType = Config.ModTypes.FirstOrDefault(t => t.Style == ModTypeStyle.Animation);
        typeId = rebindTarget?.ModTypeId ?? (anyAnimation ? animationType?.Id : Config.ModTypes.FirstOrDefault(t => t.Style != ModTypeStyle.Animation)?.Id) ?? Config.ModTypes[0].Id;
        temporary = anyAnimation;
        autoSync = whole.Count(d => d.Pose != null) > 1 || whole.Any(d => d.Role.Length > 0)
                   || modName.Contains("Dom&Sub", StringComparison.OrdinalIgnoreCase);
        var path = plugin.Penumbra.GetSelectorPath(dir) ?? string.Empty;
        ratingChoice = path.Contains("nsfw", StringComparison.OrdinalIgnoreCase) || modName.Contains("nsfw", StringComparison.OrdinalIgnoreCase) ? 1 : 2;
    }

    /// <summary>"Dom · Sit on Ground, pose 2" or "Doze, pose 1" or "Gold Dance".</summary>
    private static string Describe(Detection d)
    {
        var what = d.Pose switch
        {
            null => d.Emote.Name,
            0 => $"{d.Emote.Name}, default pose",
            var p => $"{d.Emote.Name}, pose {p}",
        };
        return d.Role.Length > 0 ? $"{d.Role} · {what}" : what;
    }

    /// <summary>Name suffix: the role if known, else the pose (with the emote when a mod plays several).</summary>
    private static string ShortSuffix(Detection d, bool includeEmote)
    {
        if (d.Role.Length > 0)
            return d.Role;
        var pose = d.Pose switch { null => string.Empty, 0 => "default pose", var p => $"pose {p}" };
        if (!includeEmote)
            return pose.Length > 0 ? pose : d.Emote.Name;
        return pose.Length > 0 ? $"{d.Emote.Name} {pose}" : d.Emote.Name;
    }

    /// <summary>
    /// Keeps the Penumbra option name recognisable: drops a leading "057 " index and a trailing
    /// "-- author note", and the "(Dom-X/Sub-Y)" part when roles become the suffix.
    /// </summary>
    private static string EntryNameFromOption(string option, bool hasRoles)
    {
        var name = option;
        var note = name.IndexOf(" -- ", StringComparison.Ordinal);
        if (note > 0)
            name = name[..note];
        name = LeadingNumber.Replace(name, string.Empty);
        if (hasRoles)
            name = RoleParens.Replace(name, string.Empty);
        name = Regex.Replace(name, @"\s{2,}", " ").Trim(' ', '$', '-');
        return name.Length > 0 ? name : option.Trim();
    }

    private bool IsInLibrary(string dir, Candidate c) =>
        Config.ManagedMods.Any(m =>
            m.ModName.Equals(dir, StringComparison.OrdinalIgnoreCase)
            && m.GroupName == c.Group && m.OptionName == c.Option
            && (c.Plain ? m.AnimationCommand.Length == 0 || !c.WholeMod
                : c.Command.Length == 0 || plugin.Emotes.FromCommand(m.AnimationCommand)?.Id == plugin.Emotes.FromCommand(c.Command)?.Id)
            && (m.PoseNumber == null || c.Pose == null || m.PoseNumber == c.Pose));

    // ------------------------------------------------------------------ settings + add

    private void DrawNewEntrySettings(string dir)
    {
        ImGui.Separator();
        var type = Config.ModTypes.FirstOrDefault(t => t.Id == typeId) ?? Config.ModTypes[0];
        var labelWidth = Theme.Scaled(76);
        using (var table = ImRaii.Table("###newSettings", 4, ImGuiTableFlags.None))
        {
            if (table)
            {
                ImGui.TableSetupColumn("k1", ImGuiTableColumnFlags.WidthFixed, labelWidth);
                ImGui.TableSetupColumn("v1", ImGuiTableColumnFlags.WidthStretch, 1f);
                ImGui.TableSetupColumn("k2", ImGuiTableColumnFlags.WidthFixed, labelWidth);
                ImGui.TableSetupColumn("v2", ImGuiTableColumnFlags.WidthStretch, 1f);

                ImGui.TableNextRow();
                Cell("Add to", () =>
                {
                    ImGui.SetNextItemWidth(-1);
                    using var combo = ImRaii.Combo("###type", type.Name);
                    if (!combo) return;
                    foreach (var t in Config.ModTypes)
                        if (ImGui.Selectable($"{t.Name}###{t.Id}", t.Id == typeId)) typeId = t.Id;
                });
                Cell("Rating", () =>
                    Theme.Segmented("rating", ["SFW", "NSFW", "Sort later"], ref ratingChoice,
                        [(Theme.Sfw, Theme.SfwBg), (Theme.Nsfw, Theme.NsfwBg), (Theme.Unsorted, Theme.UnsortedBg)]));

                ImGui.TableNextRow();
                Cell("Category", () => Vocabulary("category", ref category, type.Categories, ref newCategory, v =>
                {
                    if (!type.Categories.Contains(v, StringComparer.OrdinalIgnoreCase)) type.Categories.Add(v);
                }));
                Cell("Position", () =>
                {
                    using (ImRaii.Disabled(type.Style != ModTypeStyle.Animation))
                        Vocabulary("position", ref position, Config.Positions, ref newPosition, v =>
                        {
                            if (!Config.Positions.Contains(v, StringComparer.OrdinalIgnoreCase)) Config.Positions.Add(v);
                        });
                });

                ImGui.TableNextRow();
                Cell("Folder", () =>
                {
                    ImGui.SetNextItemWidth(-1);
                    ImGui.InputTextWithHint("###folder", "Same as in Penumbra", ref folder, 96);
                });
                Cell("Keep", () =>
                {
                    var keep = temporary ? 1 : 0;
                    if (Theme.Segmented("keep", ["Keep on", "Temporary"], ref keep, [(Theme.On, Theme.OnBg), (Theme.Unsorted, Theme.UnsortedBg)]))
                        temporary = keep == 1;
                });

                ImGui.TableNextRow();
                Cell(string.Empty, () => ImGui.Checkbox("Favourite", ref favourite), "favourite");
                Cell(string.Empty, () =>
                {
                    using (ImRaii.Disabled(!plugin.Player.EmoteSyncAvailable || type.Style != ModTypeStyle.Animation))
                        ImGui.Checkbox("Auto emote sync after Play", ref autoSync);
                    Theme.Hint("Runs /heels emotesync after Play. Useful for couples; leave off for dances.");
                }, "sync");
            }
        }

        var count = candidates.Count(c => c.Include);
        var label = count == 1 ? "Add 1 entry" : $"Add {count} entries";
        Theme.RightAlign(Theme.ButtonWidth(label));
        using (ImRaii.Disabled(count == 0))
        {
            if (Theme.PrimaryButton(label))
                AddIncluded(dir, type);
        }
    }

    private static void Cell(string label, Action draw, string? id = null)
    {
        ImGui.TableNextColumn();
        ImGui.AlignTextToFramePadding();
        ImGui.TextDisabled(label);
        ImGui.TableNextColumn();
        using var pushId = ImRaii.PushId(id ?? label);
        draw();
    }

    private static void Vocabulary(string id, ref string value, IReadOnlyList<string> options, ref string draft, Action<string> add)
    {
        ImGui.SetNextItemWidth(-1);
        using var combo = ImRaii.Combo($"###{id}", value.Length == 0 ? "(none)" : value);
        if (!combo)
            return;
        if (ImGui.Selectable("(none)", value.Length == 0))
            value = string.Empty;
        foreach (var option in options)
        {
            if (ImGui.Selectable(option, option.Equals(value, StringComparison.OrdinalIgnoreCase)))
                value = option;
        }
        ImGui.Separator();
        ImGui.SetNextItemWidth(Theme.Scaled(160));
        var submitted = ImGui.InputTextWithHint("###new", "New…", ref draft, 48, ImGuiInputTextFlags.EnterReturnsTrue);
        ImGui.SameLine();
        if ((ImGui.Button("Add") || submitted) && draft.Trim().Length > 0)
        {
            value = draft.Trim();
            add(value);
            draft = string.Empty;
            ImGui.CloseCurrentPopup();
        }
    }

    private void AddIncluded(string dir, ModType type)
    {
        var created = candidates.Where(c => c.Include).Select(c => new ManagedMod
        {
            ModName = dir,
            DisplayName = c.Name.Trim().Length > 0 ? c.Name.Trim() : c.Label,
            GroupName = c.Group,
            OptionName = c.Option,
            GroupType = c.Type,
            AnimationCommand = c.Command.Trim(),
            PoseNumber = c.Pose,
            IsAnimation = c.Command.Trim().Length > 0,
            ModTypeId = type.Id,
            Rating = ratingChoice switch { 0 => ContentRating.Sfw, 1 => ContentRating.Nsfw, _ => ContentRating.Unrated },
            Category = category,
            Position = type.Style == ModTypeStyle.Animation ? position : string.Empty,
            CategoryName = folder.Trim().Trim('/'),
            IsTemp = temporary,
            AutoEmoteSync = autoSync && c.Command.Trim().Length > 0,
            IsFavorite = favourite,
        }).ToList();

        Config.ManagedMods.AddRange(created);
        Config.Save();
        if (trying?.Dir == dir)
            trying = null; // it's in the library now; keep it as it is
        foreach (var c in candidates.Where(c => c.Include))
        {
            c.Include = false;
            c.InLibrary = true;
        }

        plugin.LibraryWindow.Reveal(created);
        status.Set(created.Count == 1 ? $"Added {created[0].DisplayName} to {type.Name}." : $"Added {created.Count} entries to {type.Name}.");
    }

    private void Rebind(Candidate c)
    {
        var target = rebindTarget;
        if (target == null || selectedDir == null)
            return;

        target.ModName = selectedDir;
        target.GroupName = c.Group;
        target.OptionName = c.Option;
        target.GroupType = c.Type;
        if (c.Command.Length > 0)
        {
            target.AnimationCommand = c.Command;
            target.PoseNumber = c.Pose;
            target.IsAnimation = true;
        }
        Config.Save();
        plugin.Penumbra.Invalidate();
        plugin.LibraryWindow.Reveal([target]);
        Svc.Print($"{target.DisplayName} now uses {plugin.Penumbra.ModList.GetValueOrDefault(selectedDir, selectedDir)}{(c.Option.Length > 0 ? $" / {c.Option}" : string.Empty)}.");
        rebindTarget = null;
        IsOpen = false;
    }
}
