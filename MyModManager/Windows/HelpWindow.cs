using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.GameFonts;
using Dalamud.Interface.ManagedFontAtlas;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using MyModManager.Helpers;

namespace MyModManager.Windows;

/// <summary>Topics a "?" button can open the help on.</summary>
public enum HelpTopic
{
    GettingStarted,
    Library,
    ModTypes,
    Adding,
    Playing,
    KeepAndTemporary,
    Sorting,
    EmoteSync,
    Commands,
    Settings,
    Troubleshooting,
}

/// <summary>
/// The user guide: topics on the left, a searchable page on the right. Content is data
/// (blocks), so search can match any sentence and every page is drawn the same way.
/// </summary>
public sealed class HelpWindow : Window, IDisposable
{
    private enum Kind { Heading, Text, Bullets, Table, Colours, Note }

    private sealed record Block(Kind Kind, string Text = "", string[]? Items = null, (string Left, string Right)[]? Rows = null);

    private sealed record Page(HelpTopic Topic, string Title, Block[] Blocks)
    {
        public string SearchText { get; } = string.Join(' ',
            Blocks.SelectMany(b => new[] { b.Text }.Concat(b.Items ?? []).Concat((b.Rows ?? []).SelectMany(r => new[] { r.Left, r.Right }))));
    }

    private readonly Plugin plugin;
    private readonly List<Page> pages;
    private HelpTopic current = HelpTopic.GettingStarted;
    private string search = string.Empty;
    private bool scrollToTop;
    private IFontHandle? headingFont;

    public HelpWindow(Plugin plugin)
        : base("My Mod Manager · Help###MyModManager.Help", ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        this.plugin = plugin;
        Size = new Vector2(900, 620);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(640, 400), MaximumSize = new Vector2(float.MaxValue, float.MaxValue) };
        pages = BuildPages();
    }

    public void Dispose() => headingFont?.Dispose();

    public override void PreDraw() => Theme.Push(plugin.Configuration.WindowOpacity);

    public override void PostDraw() => Theme.Pop();

    public void Open(HelpTopic topic)
    {
        current = topic;
        search = string.Empty;
        scrollToTop = true;
        IsOpen = true;
    }

    public override void Draw()
    {
        headingFont ??= Svc.PluginInterface.UiBuilder.FontAtlas.NewGameFontHandle(new GameFontStyle(GameFontFamily.Axis, 22f));

        var leftWidth = Theme.Scaled(220);
        using (var left = ImRaii.Child("###topics", new Vector2(leftWidth, 0), true))
        {
            if (left)
                DrawTopics();
        }

        ImGui.SameLine();
        using var right = ImRaii.Child("###page", Vector2.Zero, true);
        if (!right)
            return;
        if (scrollToTop)
        {
            ImGui.SetScrollY(0);
            scrollToTop = false;
        }
        DrawPage(pages.First(p => p.Topic == current));
    }

    private void DrawTopics()
    {
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("###helpSearch", "Search help…", ref search, 64);
        ImGui.Spacing();

        var words = search.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var shown = 0;
        foreach (var page in pages)
        {
            if (words.Length > 0 && !words.All(w => $"{page.Title} {page.SearchText}".Contains(w, StringComparison.OrdinalIgnoreCase)))
                continue;
            shown++;
            if (ImGui.Selectable(page.Title, page.Topic == current))
            {
                current = page.Topic;
                scrollToTop = true;
            }
        }
        if (shown == 0)
            ImGui.TextDisabled("No topic mentions that.");
    }

    private void DrawPage(Page page)
    {
        using (headingFont != null && headingFont.Available ? headingFont.Push() : null)
            ImGui.TextUnformatted(page.Title);
        ImGui.Spacing();

        using var wrap = ImRaii.TextWrapPos(ImGui.GetCursorPosX() + Math.Min(ImGui.GetContentRegionAvail().X, Theme.Scaled(680)));
        foreach (var block in page.Blocks)
        {
            switch (block.Kind)
            {
                case Kind.Heading:
                    ImGui.Spacing();
                    ImGui.Spacing();
                    Theme.Label(block.Text);
                    break;
                case Kind.Text:
                    ImGui.TextWrapped(block.Text);
                    break;
                case Kind.Note:
                    ImGui.TextColored(Theme.Gold, block.Text);
                    break;
                case Kind.Bullets:
                    foreach (var item in block.Items ?? [])
                    {
                        ImGui.Bullet();
                        ImGui.SameLine();
                        ImGui.TextWrapped(item);
                    }
                    break;
                case Kind.Table:
                    DrawTable(block.Rows ?? []);
                    break;
                case Kind.Colours:
                    DrawColourKey();
                    break;
            }
            ImGui.Spacing();
        }

        ImGui.Spacing();
        ImGui.Separator();
        if (ImGui.Button("Open the Library"))
            plugin.LibraryWindow.IsOpen = true;
        ImGui.SameLine();
        if (ImGui.Button("Add from Penumbra"))
            plugin.AddWindow.Open();
    }

    private static void DrawTable((string Left, string Right)[] rows)
    {
        using var table = ImRaii.Table("###helpTable", 2, ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.PadOuterX);
        if (!table)
            return;
        ImGui.TableSetupColumn("left", ImGuiTableColumnFlags.WidthFixed, Theme.Scaled(230));
        ImGui.TableSetupColumn("right", ImGuiTableColumnFlags.WidthStretch);
        foreach (var (left, right) in rows)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextColored(Theme.Gold, left);
            ImGui.TableNextColumn();
            ImGui.TextWrapped(right);
        }
    }

    private static void DrawColourKey()
    {
        void Swatch(Vector4 colour, string text, bool bar)
        {
            var pos = ImGui.GetCursorScreenPos();
            var h = ImGui.GetFrameHeight();
            var dl = ImGui.GetWindowDrawList();
            if (bar)
                dl.AddRectFilled(pos + new Vector2(Theme.Scaled(4), Theme.Scaled(4)), pos + new Vector2(Theme.Scaled(7), h - Theme.Scaled(4)), Theme.U32(colour), Theme.Scaled(2));
            else
                dl.AddCircleFilled(pos + new Vector2(Theme.Scaled(6), h / 2), Theme.Scaled(4.5f), Theme.U32(colour));
            ImGui.Dummy(new Vector2(Theme.Scaled(16), h));
            ImGui.SameLine();
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(text);
        }

        Swatch(Theme.Sfw, "Teal bar: rated SFW", true);
        Swatch(Theme.Nsfw, "Rose bar: rated NSFW", true);
        Swatch(Theme.Unsorted, "Amber bar: not sorted yet", true);
        Swatch(Theme.On, "Green tick: on, and kept on", false);
        Swatch(Theme.Unsorted, "Amber tick: on, temporary", false);
        Swatch(Theme.Gold, "Gold text: the command you type (and its pose); gold star: favourite", false);
        Swatch(Theme.Sync, "Purple arrows: auto emote sync after Play", false);
        Swatch(Theme.Warning, "Orange name: the mod is no longer installed in Penumbra", false);
    }

    // ------------------------------------------------------------------ content

    private static Block H(string text) => new(Kind.Heading, text);
    private static Block T(string text) => new(Kind.Text, text);
    private static Block N(string text) => new(Kind.Note, text);
    private static Block B(params string[] items) => new(Kind.Bullets, Items: items);
    private static Block Rows(params (string, string)[] rows) => new(Kind.Table, Rows: rows);

    private static List<Page> BuildPages() =>
    [
        new(HelpTopic.GettingStarted, "Getting started",
        [
            T("My Mod Manager is an animation mode for Penumbra. Penumbra still does the work in the background: My Mod Manager keeps a library of the mods and options you care about, tells you what to type to use them, and turns them on and off for you."),
            N("It is an unofficial project made for friends, not affiliated with Penumbra, Dalamud or Square Enix."),
            H("First steps"),
            B("Open the Library with /mmm. The header shows which Penumbra collection it changes. By default that's the collection your character uses; change it under the cog button if you need to.",
              "Press + Add to add mods from Penumbra. Tick what you want, press Try to test it, then Add.",
              "Rate new entries SFW or NSFW, and give them a category and position, so they're easy to find later.",
              "Press Play on an entry (or double-click it). It turns the mod on, redraws you, picks the right pose and plays the emote."),
            H("The windows"),
            Rows(("Library  (/mmm)", "Everything in your library, by tab. Search, filter, edit, play."),
                 ("Add from Penumbra  (/mmm add)", "Browse your Penumbra mods and add them, or change which mod an entry uses."),
                 ("Guide  (/mmm guide)", "This guide. The ? buttons open it on the matching page. /mmm help lists the commands in chat.")),
        ]),

        new(HelpTopic.Library, "The Library",
        [
            T("The Library works like Penumbra's Mods tab: a list on the left, the selected entry on the right."),
            H("Finding things"),
            B("Search matches every word you type against the name, mod, command, category, position, folder and tags. \"ground riding\" finds ground-sit entries in the Riding position.",
              "All / SFW / NSFW / Unsorted filter by rating. ★ shows favourites only, On shows what is on right now.",
              "The emote, category and position drop-downs narrow it further.",
              "Group changes how the list is sorted into headers: by Folder (your Penumbra folders unless you set one), by Emote (e.g. every /groundsit pose 2 together), by Category, by Position, or not at all. Click a header to fold it."),
            H("Colours"),
            new(Kind.Colours),
            H("Selecting and editing"),
            B("Click a row to open it on the right. Click it again to close it.",
              "Ctrl-click to pick several; Shift-click to pick a range. With several selected, the right side edits them all at once (rating, category, position, keep, favourite, emote sync, tab, folder).",
              "The tick box on a row turns the entry on or off in Penumbra. Double-click an animation row to play it.",
              "Remove needs Ctrl held, like in Penumbra. Removing only takes the entry out of My Mod Manager; the mod stays installed."),
            H("With nothing selected"),
            T("The right side lists what is on right now in the current tab, each with Turn off, and how many entries still need sorting."),
        ]),

        new(HelpTopic.ModTypes, "Tabs and mod types",
        [
            T("Each tab is a mod type you define: Animations, Body, VFX, Outfits, whatever suits you. Right-click a tab to rename it, change how it behaves, or delete it. The + at the end adds one."),
            H("How entries in a tab behave"),
            Rows(("Animation", "An emote command and pose, with a Play button. Rating, category and position."),
                 ("On / off", "A plain toggle. Rating and category."),
                 ("Pick one", "Entries for options of the same Penumbra group (e.g. Physics: Clothed / Nude) show as one switch. Picking a side selects that option in Penumbra.")),
            H("Categories and positions"),
            B("Every tab has its own category list, e.g. Sex, Dance, Oral for animations, or Physics and Scale for body mods.",
              "Positions (Riding, Laying, Kneeling…) belong to animation tabs.",
              "Add new values straight from any category or position drop-down: type in the New… box at the bottom."),
            H("Deleting a tab"),
            T("You choose which tab its entries move to, so nothing is lost."),
        ]),

        new(HelpTopic.Adding, "Adding mods",
        [
            T("Press + Add in the Library (or /mmm add). Pick a mod on the left; the right side shows it the way Penumbra does."),
            H("The mod itself"),
            B("Just the mod (on / off): an entry that turns the whole mod on or off, using the options you've set in Penumbra.",
              "For mods with a few animations you'll also see one row per animation, in plain words: \"As Sit on Ground, pose 1\". A [Gsit1_2] couple mod shows pose 1 and pose 2, one entry each.",
              "A mod with a single animation shows one row, The whole mod, with its command filled in."),
            H("Options"),
            B("Below, each Penumbra option group is a bar you can fold, labelled pick one or any number, like in Penumbra.",
              "A green dot marks the option currently selected in Penumbra.",
              "Animations only hides options without an emote. Turn it off to see every option.",
              "Packs (lots of animated options, like Nightlife) start folded. Type in Filter options to open every group with a match.",
              "Option names like \"(Dom-Gsit2/Sub-Gsit3)\" become one entry per role: Dom on pose 2, Sub on pose 3."),
            H("The columns"),
            Rows(("Add", "Tick what you want."),
                 ("In Penumbra", "The mod or option, as Penumbra names it."),
                 ("Plays", "The command and pose it was found to use. Click it to change either."),
                 ("Try", "Turns it on and plays it, without adding it. An amber bar offers Undo try, which puts the mod's Penumbra settings back exactly. Closing the window undoes it too."),
                 ("Name in your library", "What the entry will be called. Click to edit.")),
            H("How detection works"),
            T("My Mod Manager reads the mod's own files to see which animation files it replaces, and maps them to the exact emote and pose using the game's data. When a mod has no readable files it falls back to the name ([Gsit1_2], Csit2, (Gdance)). Everything it finds can be changed before you add."),
            H("New animations"),
            T("The New animations view lists mods that replace emotes but aren't in your library. Mods with a single emote can all be added in one go as unsorted; open the others to choose."),
            H("Changing which mod an entry uses"),
            T("In the Library, select the entry and press Change mod or option… The Add window opens on its mod; press Use on the row you want."),
        ]),

        new(HelpTopic.Playing, "Playing and poses",
        [
            T("Play does the whole job in order:"),
            B("Turns the entry on in Penumbra (and anything sharing its shortcut).",
              "Pauses other entries that replace the same emote and pose, so a higher-priority mod can't hide the one you picked.",
              "Redraws your character and waits for the redraw to finish, so the new animation loads and the emote isn't cancelled.",
              "Selects the entry's pose, then plays the emote. If you're already sitting in that pose type, it switches pose with /cpose instead of standing you up."),
            H("Pose numbers"),
            T("Poses are numbered the way mod names do: [Sit1] or [Gsit1] is pose 1, the first alternate pose. The ordinary sit is the default pose (pose 0). Set Any pose if an entry works with whichever pose you're in."),
            H("Commands"),
            T("Commands come from the game's own emote list, so they're exact (/sit, /groundsit, /thavdance…). Only emotes and commands of installed plugins can run, so a typo can never be posted to chat. A command the game doesn't know shows \"Not a command\" in orange."),
        ]),

        new(HelpTopic.KeepAndTemporary, "Keep on and Temporary",
        [
            Rows(("Keep on", "Stays on until you turn it off. For favourite idles, walks and dances you always want."),
                 ("Temporary", "For scenes. The header's Turn off temporary button turns every temporary entry off at once.")),
            H("Paused entries come back"),
            T("When playing a temporary animation pauses a kept-on one (because both replace the same emote), Turn off temporary turns the kept-on one back on."),
            N("Both use your real Penumbra settings; nothing is hidden from Penumbra."),
        ]),

        new(HelpTopic.Sorting, "Rating, sorting and Safe view",
        [
            B("Every entry is SFW, NSFW or Unsorted. New imports start Unsorted and show an amber bar.",
              "The Unsorted button in the header (and the Unsorted filter) lists everything still to rate. Select several with Ctrl or Shift and rate them together.",
              "Category and position answer \"what is this\" at a glance; group the list by them to browse."),
            H("Safe view"),
            T("Safe view hides everything that isn't rated SFW, everywhere, including counts. Useful when streaming or sharing your screen. Unsorted entries are hidden too, because they might not be safe."),
        ]),

        new(HelpTopic.EmoteSync, "Emote sync (Simple Heels)",
        [
            T("With the Simple Heels plugin installed, the header shows an Emote sync button. It runs /heels emotesync, which restarts every character's emote on your screen at the same moment, so a couple's animations line up. Only you see the effect."),
            H("Auto emote sync"),
            T("Tick Auto emote sync after Play on an entry (purple arrows in the list) and Play runs the sync about a second after your emote starts. Good for couple animations; leave it off for dances you don't want in step."),
            N("Without Simple Heels the button is hidden and the option is greyed out."),
        ]),

        new(HelpTopic.Commands, "Chat commands",
        [
            Rows(("/mmm", "Open or close the Library."),
                 ("/mmm add", "Open Add from Penumbra."),
                 ("/mmm help", "List the commands in chat."),
                 ("/mmm guide", "Open this guide."),
                 ("/mmm play <name or shortcut>", "Play an entry, e.g. from a macro."),
                 ("/mmm on <shortcut>", "Turn every entry with that shortcut on."),
                 ("/mmm off <shortcut>", "Turn them off."),
                 ("/mmm toggle <shortcut>", "Flip them, based on what's on in Penumbra right now."),
                 ("/mmm temp off", "Same as Turn off temporary.")),
        ]),

        new(HelpTopic.Settings, "Settings",
        [
            T("The cog button in the Library header:"),
            Rows(("Collection", "Which Penumbra collection is changed. \"Your character's collection\" follows whatever Penumbra applies to you, and is the safest choice."),
                 ("Background", "How see-through My Mod Manager's windows are. Solid by default for readability."),
                 ("Select the entry's pose", "Play selects the pose set on the entry."),
                 ("Pause other entries…", "Play turns off entries that replace the same emote and pose."),
                 ("Redraw my character", "Redraw after changes so new files load. Leave on unless you know you don't need it."),
                 ("Silent emotes", "Adds \"motion\" to emotes so no emote text appears in chat.")),
        ]),

        new(HelpTopic.Troubleshooting, "Troubleshooting",
        [
            Rows(("The animation doesn't change", "Another mod may override the same emote at a higher priority in Penumbra. Keep \"Pause other entries\" on, and check Penumbra's Changed Items for that emote."),
                 ("Wrong pose", "Check the entry's pose. Numbers follow mod names: [Gsit2] is pose 2, the ordinary sit is the default pose."),
                 ("Play stood me up", "Emotes like /groundsit toggle. My Mod Manager switches pose with /cpose when you're already in that pose type; if it still happens, stand, then Play."),
                 ("A name is orange", "The mod was removed from Penumbra. The entry is kept so your sorting isn't lost; point it at another mod with Change mod or option…, or remove it."),
                 ("\"Not a command\"", "The command isn't an emote the game knows. Fix it in the entry's Command field."),
                 ("Penumbra isn't running", "Entries can't be turned on or off until Penumbra loads. The header dot is amber while it's missing."),
                 ("Changes affect the wrong character", "Set the collection to \"Your character's collection\" under the cog."),
                 ("An option switched something else off", "Pick-one groups in Penumbra can only have one option on; picking one switches the others off. That's Penumbra's rule.")),
        ]),
    ];
}
