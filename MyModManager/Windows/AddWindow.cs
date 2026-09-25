using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using MyModManager.Helpers;
using MyModManager.Models;
using MyModManager.Services;
using Penumbra.Api.Enums;

namespace MyModManager.Windows;

/// <summary>
/// Adds Penumbra mods to the Library. Reads each mod's files to find which emotes and poses it
/// replaces, proposes one entry per animation (or per option for packs), and applies shared
/// settings. Also used to point an existing entry at a different mod or option.
/// </summary>
public sealed class AddWindow : Window, IDisposable
{
    private enum View { Browse, NewAnimations }

    private sealed class Candidate
    {
        public bool Include;
        public string Name = string.Empty;
        public string Group = string.Empty;
        public string Option = string.Empty;
        public GroupType Type = GroupType.Single;
        public string Command = string.Empty;
        public int? Pose;
        public string Found = string.Empty;
        public bool InLibrary;
        public bool WholeMod;
    }

    private static readonly string[] OffOptionNames = ["none", "off", "disabled", "disable", "vanilla", "default", "nothing", "original"];
    private static readonly Regex Brackets = new(@"\[[^\]]*\]", RegexOptions.Compiled);
    private static readonly Regex TrailingParens = new(@"\s*\([^)]*\)\s*$", RegexOptions.Compiled);
    private static readonly Regex LeadingNumber = new(@"^\s*\d{2,4}\s+", RegexOptions.Compiled);

    private readonly Plugin plugin;
    private readonly StatusLine status = new();

    private View view = View.Browse;
    private string search = string.Empty;
    private bool hideAdded;
    private string? selectedDir;
    private ManagedMod? rebindTarget;

    // Mod list cache
    private List<KeyValuePair<string, string>> modRows = new();
    private string listKey = string.Empty;

    // Selected mod
    private string? loadedDir;
    private Task<ModFiles?>? filesTask;
    private List<Candidate> candidates = new();
    private bool candidatesBuilt;
    private bool isPack;
    private string optionFilter = string.Empty;

    // Settings for new entries
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

    // "New animations" view
    private Dictionary<string, List<EmoteInfo>>? unlisted;

    private Configuration Config => plugin.Configuration;

    public AddWindow(Plugin plugin)
        : base("Add from Penumbra###MyModManager.Add", ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        this.plugin = plugin;
        Size = new Vector2(1000, 640);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(760, 420), MaximumSize = new Vector2(float.MaxValue, float.MaxValue) };
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
        Select(mod.ModName);
        IsOpen = true;
    }

    public override void OnClose() => rebindTarget = null;

    public override void Draw()
    {
        if (!plugin.Penumbra.Available)
        {
            ImGui.TextColored(Theme.Unsorted, "Penumbra isn't running, so there's nothing to add from yet.");
            return;
        }

        if (rebindTarget != null)
        {
            ImGui.TextColored(Theme.Unsorted, $"Choose the mod or option for “{rebindTarget.DisplayName}”, then press Use on a row.");
            ImGui.SameLine();
            if (ImGui.SmallButton("Cancel"))
                rebindTarget = null;
        }

        var leftWidth = Math.Clamp(ImGui.GetContentRegionAvail().X * 0.36f, Theme.Scaled(300), Theme.Scaled(420));
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
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("###modSearch", $"Search {plugin.Penumbra.ModList.Count} Penumbra mods…", ref search, 128);

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

        if (view == View.Browse)
        {
            ImGui.SameLine();
            Theme.RightAlign(Theme.ButtonWidth("Hide added"));
            if (Theme.Chip("Hide added", hideAdded))
                hideAdded = !hideAdded;
        }
        else
        {
            ImGui.SameLine();
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
                    ImGui.SetTooltip(dir == name ? name : $"{name}\nFolder: {dir}");

                var dl = ImGui.GetWindowDrawList();
                var textY = start.Y + (h - ImGui.GetTextLineHeight()) / 2;
                var right = start.X + width - Theme.Scaled(6);
                if (managed.TryGetValue(dir, out var count))
                {
                    var label = count == 1 ? "in library" : $"{count} in library";
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

    private void Select(string dir)
    {
        selectedDir = dir;
        if (loadedDir == dir)
            return;

        loadedDir = dir;
        candidatesBuilt = false;
        candidates = new List<Candidate>();
        optionFilter = string.Empty;
        var modFolder = plugin.Penumbra.GetModFolder(dir);
        filesTask = modFolder == null ? Task.FromResult<ModFiles?>(null) : Task.Run(() => ModFileReader.Read(modFolder));
    }

    // ------------------------------------------------------------------ overview (nothing selected)

    private void DrawOverview()
    {
        if (view == View.Browse)
        {
            ImGui.TextUnformatted("Pick a mod on the left.");
            ImGui.Spacing();
            ImGui.TextDisabled("My Mod Manager reads the mod's files to see which emotes and poses it replaces,");
            ImGui.TextDisabled("then suggests one entry for each. A mod tagged [Gsit1_2] becomes two entries, pose 1 and 2.");
            ImGui.TextDisabled("Packs with many options become one entry per option you tick.");
            return;
        }

        if (unlisted == null)
            return;
        var single = unlisted.Where(kv => kv.Value.Count == 1 && !Config.ManagedMods.Any(m => m.ModName.Equals(kv.Key, StringComparison.OrdinalIgnoreCase))).ToList();
        ImGui.TextUnformatted($"{unlisted.Count} mods replace emotes and aren't in your library.");
        ImGui.Spacing();
        if (single.Count > 0)
        {
            ImGui.TextDisabled($"{single.Count} of them replace a single emote, so they can be added in one go.");
            ImGui.TextDisabled("They go into Unsorted, ready to rate.");
            ImGui.Spacing();
            if (Theme.PrimaryButton($"Add {single.Count} as unsorted"))
                AddAllSingleEmote(single);
        }
        ImGui.Spacing();
        ImGui.TextDisabled("Mods that touch several emotes or poses: open them to choose what to add.");
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
                    DisplayName = poses.Count > 1 ? $"{CleanName(name)} · pose {pose}" : CleanName(name),
                    AnimationCommand = emote.Command,
                    PoseNumber = pose,
                    IsAnimation = true,
                    ModTypeId = animationType.Id,
                    Rating = ContentRating.Unrated,
                    IsTemp = true,
                    IsFavorite = false,
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
        using (ImRaii.PushColor(ImGuiCol.Text, Theme.Text))
            ImGui.TextUnformatted(name);
        var selector = plugin.Penumbra.GetSelectorPath(dir);
        ImGui.TextDisabled($"Penumbra: {selector ?? dir}");

        if (filesTask is not { IsCompleted: true })
        {
            ImGui.Spacing();
            ImGui.TextDisabled("Reading the mod's files…");
            return;
        }

        if (!candidatesBuilt)
            BuildCandidates(dir, name, filesTask.Result);

        var existing = Config.ManagedMods.Where(m => m.ModName.Equals(dir, StringComparison.OrdinalIgnoreCase)).ToList();
        if (existing.Count > 0)
            ImGui.TextColored(Theme.Gold, $"Already in your library: {string.Join(", ", existing.Select(m => m.DisplayName).Take(4))}{(existing.Count > 4 ? $" and {existing.Count - 4} more" : string.Empty)}");

        ImGui.Spacing();
        DrawSummary();
        ImGui.Separator();

        var settingsHeight = rebindTarget == null ? ImGui.GetFrameHeightWithSpacing() * 7.6f : ImGui.GetFrameHeightWithSpacing();
        using (var table = ImRaii.Child("###candidates", new Vector2(0, -settingsHeight), false))
        {
            if (table)
                DrawCandidates();
        }

        ImGui.Separator();
        if (rebindTarget == null)
            DrawNewEntrySettings(dir);
        status.Draw();
    }

    private void DrawSummary()
    {
        var detected = candidates.Where(c => c.Command.Length > 0).ToList();
        if (detected.Count == 0)
            ImGui.TextDisabled("No animations found. You can still add the mod or its options, e.g. as a toggle or a switch.");
        else if (isPack)
            ImGui.TextDisabled($"A pack: {detected.Count(c => !c.WholeMod)} options with animations. Tick the ones you want; each becomes its own entry.");
        else
            ImGui.TextDisabled($"Found {string.Join(", ", detected.Where(c => c.WholeMod).Select(c => c.Found).Distinct())}. Options are listed below if you want a specific variant instead.");

        if (candidates.Count > 12)
        {
            ImGui.SetNextItemWidth(Theme.Scaled(220));
            ImGui.InputTextWithHint("###optionFilter", "Filter options…", ref optionFilter, 64);
            ImGui.SameLine();
            if (ImGui.Button("Tick detected"))
                foreach (var c in VisibleCandidates().Where(c => c.Command.Length > 0 && !c.InLibrary && !c.WholeMod)) c.Include = true;
            ImGui.SameLine();
            if (ImGui.Button("Untick all"))
                foreach (var c in candidates) c.Include = false;
        }
    }

    private IEnumerable<Candidate> VisibleCandidates()
    {
        var words = optionFilter.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return candidates.Where(c => words.All(w => $"{c.Name} {c.Option} {c.Group} {c.Command}".Contains(w, StringComparison.OrdinalIgnoreCase)));
    }

    private void DrawCandidates()
    {
        var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.PadOuterX;
        using var table = ImRaii.Table("###cand", rebindTarget == null ? 4 : 5, flags);
        if (!table)
            return;

        ImGui.TableSetupColumn("###inc", ImGuiTableColumnFlags.WidthFixed, ImGui.GetFrameHeight());
        ImGui.TableSetupColumn("Entry name", ImGuiTableColumnFlags.WidthStretch, 3f);
        ImGui.TableSetupColumn("Use", ImGuiTableColumnFlags.WidthStretch, 2f);
        ImGui.TableSetupColumn("Found in", ImGuiTableColumnFlags.WidthFixed, Theme.Scaled(70));
        if (rebindTarget != null)
            ImGui.TableSetupColumn("###use", ImGuiTableColumnFlags.WidthFixed, Theme.ButtonWidth("Use"));
        ImGui.TableHeadersRow();

        string? lastGroup = null;
        var index = 0;
        foreach (var c in VisibleCandidates())
        {
            using var id = ImRaii.PushId(index++);
            var group = c.WholeMod ? "Whole mod" : $"{c.Group} · {(c.Type == GroupType.Multi ? "multi choice" : "single choice")}";
            if (group != lastGroup)
            {
                lastGroup = group;
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TableNextColumn();
                ImGui.TextColored(Theme.Dim, group);
            }

            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            using (ImRaii.Disabled(c.InLibrary))
                ImGui.Checkbox("###include", ref c.Include);
            if (c.InLibrary)
                Theme.Hint("Already in your library.");

            ImGui.TableNextColumn();
            ImGui.SetNextItemWidth(-1);
            ImGui.InputText("###name", ref c.Name, 96);
            if (!c.WholeMod && ImGui.IsItemHovered())
                ImGui.SetTooltip($"Penumbra option: {c.Option}");

            ImGui.TableNextColumn();
            DrawCommandCell(c);

            ImGui.TableNextColumn();
            ImGui.AlignTextToFramePadding();
            if (c.InLibrary)
                ImGui.TextColored(Theme.Gold, "in library");
            else
                ImGui.TextDisabled(c.Command.Length == 0 ? "—" : c.Found.StartsWith("name") ? "name" : "files");

            if (rebindTarget != null)
            {
                ImGui.TableNextColumn();
                if (Theme.PrimaryButton("Use"))
                    Rebind(c);
            }
        }
    }

    private void DrawCommandCell(Candidate c)
    {
        var emote = plugin.Emotes.FromCommand(c.Command);
        var hasPoses = emote is { PoseCount: > 1 };
        var commandWidth = hasPoses ? ImGui.GetContentRegionAvail().X * 0.55f : ImGui.GetContentRegionAvail().X;
        ImGui.SetNextItemWidth(commandWidth);
        using (ImRaii.PushColor(ImGuiCol.Text, Theme.Gold, c.Command.Length > 0))
            ImGui.InputTextWithHint("###cmd", "no command", ref c.Command, 48);
        if (!hasPoses)
            return;

        ImGui.SameLine();
        ImGui.SetNextItemWidth(-1);
        using var combo = ImRaii.Combo("###pose", ManagedModListUi.PoseLabel(c.Pose));
        if (!combo)
            return;
        if (ImGui.Selectable(ManagedModListUi.PoseLabel(null), c.Pose == null))
            c.Pose = null;
        for (var pose = 0; pose < emote!.PoseCount; pose++)
        {
            if (ImGui.Selectable(ManagedModListUi.PoseLabel(pose), c.Pose == pose))
                c.Pose = pose;
        }
    }

    // ------------------------------------------------------------------ candidates

    private void BuildCandidates(string dir, string modName, ModFiles? files)
    {
        candidatesBuilt = true;
        candidates = new List<Candidate>();
        var emotes = plugin.Emotes;

        var whole = AnimationDetector.Detect(emotes, modName, files?.AllPaths ?? Enumerable.Empty<string>());
        if (whole.Count == 0 && files == null)
        {
            // No readable files: fall back to Penumbra's changed items.
            var changed = plugin.Penumbra.GetChangedItemNames(dir)
                .Select(emotes.FromName).OfType<EmoteInfo>().DistinctBy(e => e.Id)
                .Select(e => new Detection(e, null, string.Empty, DetectionSource.Name));
            whole = changed.ToList();
        }

        var cleanMod = CleanName(modName);
        if (whole.Count == 0)
            candidates.Add(new Candidate { Name = cleanMod, WholeMod = true });
        foreach (var d in whole)
            candidates.Add(FromDetection(d, whole.Count == 1 ? cleanMod : $"{cleanMod} · {Label(d)}", string.Empty, string.Empty, GroupType.Single, wholeMod: true));

        IEnumerable<(string Group, GroupType Type, string Option, IReadOnlyList<string> Paths)> options = files != null
            ? files.Groups.SelectMany(g => g.Options.Select(o => (g.Name, g.Type, o.Name, o.GamePaths)))
            : (plugin.Penumbra.GetOptionGroups(dir) ?? new List<OptionGroup>())
                .SelectMany(g => g.Options.Select(o => (g.Name, g.Type, o, (IReadOnlyList<string>)Array.Empty<string>())));

        foreach (var (group, type, option, paths) in options)
        {
            if (OffOptionNames.Contains(option.Trim(), StringComparer.OrdinalIgnoreCase))
                continue;
            var detected = AnimationDetector.Detect(emotes, option, paths);
            var clean = CleanName(option);
            if (detected.Count == 0)
                candidates.Add(new Candidate { Name = clean, Group = group, Option = option, Type = type });
            foreach (var d in detected)
                candidates.Add(FromDetection(d, detected.Count == 1 ? clean : $"{clean} · {Label(d)}", group, option, type, wholeMod: false));
        }

        foreach (var c in candidates)
            c.InLibrary = IsInLibrary(dir, c);

        isPack = candidates.Count(c => !c.WholeMod && c.Command.Length > 0) > 6;
        var anyAnimation = candidates.Any(c => c.Command.Length > 0);
        var hasOptions = candidates.Any(c => !c.WholeMod);
        foreach (var c in candidates)
        {
            c.Include = !c.InLibrary && c.WholeMod && !isPack && (anyAnimation ? c.Command.Length > 0 : !hasOptions);
        }

        // Sensible defaults for the new entries.
        var animationType = Config.ModTypes.FirstOrDefault(t => t.Style == ModTypeStyle.Animation);
        typeId = rebindTarget?.ModTypeId ?? (anyAnimation ? animationType?.Id : Config.ModTypes.FirstOrDefault(t => t.Style != ModTypeStyle.Animation)?.Id) ?? Config.ModTypes[0].Id;
        temporary = anyAnimation;
        autoSync = candidates.Any(c => c.Found.Contains('(') && !c.Found.StartsWith("name")) || modName.Contains("Dom&Sub", StringComparison.OrdinalIgnoreCase)
                   || whole.Count(d => d.Pose != null) > 1;
        var path = plugin.Penumbra.GetSelectorPath(dir) ?? string.Empty;
        ratingChoice = path.Contains("nsfw", StringComparison.OrdinalIgnoreCase) || modName.Contains("nsfw", StringComparison.OrdinalIgnoreCase) ? 1 : 2;
    }

    private static string Label(Detection d) =>
        d.Role.Length > 0 ? d.Role : d.Pose switch
        {
            null => d.Emote.Command,
            0 => "default pose",
            var p => $"pose {p}",
        };

    private static Candidate FromDetection(Detection d, string name, string group, string option, GroupType type, bool wholeMod) => new()
    {
        Name = name,
        Group = group,
        Option = option,
        Type = type,
        Command = d.Emote.Command,
        Pose = d.Pose,
        Found = $"{(d.Source == DetectionSource.Name ? "name" : "files")} {AnimationDetector.Describe(d)}",
        WholeMod = wholeMod,
    };

    private bool IsInLibrary(string dir, Candidate c) =>
        Config.ManagedMods.Any(m =>
            m.ModName.Equals(dir, StringComparison.OrdinalIgnoreCase)
            && m.GroupName == c.Group && m.OptionName == c.Option
            && (c.Command.Length == 0 || plugin.Emotes.FromCommand(m.AnimationCommand)?.Id == plugin.Emotes.FromCommand(c.Command)?.Id)
            && (m.PoseNumber == null || c.Pose == null || m.PoseNumber == c.Pose));

    /// <summary>"154 [Dom&Sub] [Noff] Standing Doggy (Dom-Gsit2/Sub-Gsit3) -- note" becomes "Standing Doggy".</summary>
    private static string CleanName(string raw)
    {
        var name = raw;
        var dashes = name.IndexOf(" -- ", StringComparison.Ordinal);
        if (dashes > 0)
            name = name[..dashes];
        name = Brackets.Replace(name, " ");
        name = TrailingParens.Replace(name, string.Empty);
        name = LeadingNumber.Replace(name, string.Empty);
        name = Regex.Replace(name, @"\s{2,}", " ").Trim(' ', '$', '-');
        return name.Length > 0 ? name : raw.Trim();
    }

    // ------------------------------------------------------------------ settings + add

    private void DrawNewEntrySettings(string dir)
    {
        var type = Config.ModTypes.FirstOrDefault(t => t.Id == typeId) ?? Config.ModTypes[0];
        using (var table = ImRaii.Table("###newSettings", 4, ImGuiTableFlags.SizingStretchProp))
        {
            if (table)
            {
                ImGui.TableSetupColumn("k1", ImGuiTableColumnFlags.WidthFixed, Theme.Scaled(70));
                ImGui.TableSetupColumn("v1", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("k2", ImGuiTableColumnFlags.WidthFixed, Theme.Scaled(70));
                ImGui.TableSetupColumn("v2", ImGuiTableColumnFlags.WidthStretch);

                ImGui.TableNextRow();
                Cell("Tab", () =>
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
                Cell(string.Empty, () =>
                {
                    ImGui.Checkbox("Favourite", ref favourite);
                }, "favourite");
                Cell(string.Empty, () =>
                {
                    using (ImRaii.Disabled(!plugin.Player.EmoteSyncAvailable || type.Style != ModTypeStyle.Animation))
                        ImGui.Checkbox("Auto emote sync after Play", ref autoSync);
                    Theme.Hint("Runs /heels emotesync after Play. Useful for couples; leave off for dances.");
                }, "sync");
            }
        }

        var count = candidates.Count(c => c.Include);
        Theme.RightAlign(Theme.ButtonWidth($"Add {count} entries"));
        using (ImRaii.Disabled(count == 0))
        {
            if (Theme.PrimaryButton(count == 1 ? "Add 1 entry" : $"Add {count} entries"))
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
            DisplayName = c.Name.Trim().Length > 0 ? c.Name.Trim() : dir,
            GroupName = c.Group,
            OptionName = c.Option,
            GroupType = c.Type,
            AnimationCommand = c.Command.Trim(),
            PoseNumber = c.Pose,
            IsAnimation = c.Command.Trim().Length > 0 || type.Style == ModTypeStyle.Animation,
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
