using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using MyModManager.Models;

namespace MyModManager.Helpers;

/// <summary>
/// My Mod Manager's look: pushed around our own windows only (Window.PreDraw/PostDraw),
/// so other plugins keep the user's Dalamud style.
/// </summary>
public static class Theme
{
    private static Vector4 Rgb(uint hex, float a = 1f) =>
        new(((hex >> 16) & 0xFF) / 255f, ((hex >> 8) & 0xFF) / 255f, (hex & 0xFF) / 255f, a);

    public static readonly Vector4 WindowBg = Rgb(0x17161C);
    public static readonly Vector4 Bar = Rgb(0x1C1A22);
    public static readonly Vector4 Title = Rgb(0x2C2839);
    public static readonly Vector4 Frame = Rgb(0x25232F);
    public static readonly Vector4 FrameHover = Rgb(0x302D3D);
    public static readonly Vector4 FrameActive = Rgb(0x3B3350);
    public static readonly Vector4 Border = Rgb(0x393546);
    public static readonly Vector4 Text = Rgb(0xE7E4EF);
    public static readonly Vector4 Dim = Rgb(0x8E899D);
    public static readonly Vector4 Faint = Rgb(0x5C5869);
    public static readonly Vector4 Selected = Rgb(0x3B3350);
    public static readonly Vector4 Hover = Rgb(0x24212D);

    public static readonly Vector4 Sfw = Rgb(0x46BCAE);
    public static readonly Vector4 SfwBg = Rgb(0x17332F);
    public static readonly Vector4 Nsfw = Rgb(0xE4719A);
    public static readonly Vector4 NsfwBg = Rgb(0x3A2130);
    public static readonly Vector4 Unsorted = Rgb(0xF2A43E);
    public static readonly Vector4 UnsortedBg = Rgb(0x3A2A14);
    public static readonly Vector4 On = Rgb(0x5CD27C);
    public static readonly Vector4 OnBg = Rgb(0x1D3324);
    public static readonly Vector4 Gold = Rgb(0xF1C24F);
    public static readonly Vector4 Sync = Rgb(0xB58CFF);
    public static readonly Vector4 Primary = Rgb(0x8B3A5C);
    public static readonly Vector4 PrimaryHover = Rgb(0xA2466C);
    public static readonly Vector4 Warning = Rgb(0xE8935A);

    private const int ColorCount = 27;
    private const int StyleCount = 11;

    /// <summary>Pushes the theme. Pair every call with <see cref="Pop"/>.</summary>
    public static void Push(float bgAlpha)
    {
        ImGui.PushStyleColor(ImGuiCol.WindowBg, WindowBg with { W = bgAlpha });
        ImGui.PushStyleColor(ImGuiCol.ChildBg, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.PopupBg, Rgb(0x211F2A));
        ImGui.PushStyleColor(ImGuiCol.Border, Border);
        ImGui.PushStyleColor(ImGuiCol.FrameBg, Frame);
        ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, FrameHover);
        ImGui.PushStyleColor(ImGuiCol.FrameBgActive, FrameActive);
        ImGui.PushStyleColor(ImGuiCol.TitleBg, Title);
        ImGui.PushStyleColor(ImGuiCol.TitleBgActive, Title);
        ImGui.PushStyleColor(ImGuiCol.TitleBgCollapsed, Title);
        ImGui.PushStyleColor(ImGuiCol.Button, FrameHover);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Rgb(0x3B3749));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, Rgb(0x4A4560));
        ImGui.PushStyleColor(ImGuiCol.Header, Selected);
        ImGui.PushStyleColor(ImGuiCol.HeaderHovered, Hover);
        ImGui.PushStyleColor(ImGuiCol.HeaderActive, Rgb(0x463D60));
        ImGui.PushStyleColor(ImGuiCol.Tab, Rgb(0x211F29));
        ImGui.PushStyleColor(ImGuiCol.TabHovered, Selected);
        ImGui.PushStyleColor(ImGuiCol.TabActive, FrameHover);
        ImGui.PushStyleColor(ImGuiCol.TabUnfocusedActive, FrameHover);
        ImGui.PushStyleColor(ImGuiCol.CheckMark, On);
        ImGui.PushStyleColor(ImGuiCol.Separator, Border);
        ImGui.PushStyleColor(ImGuiCol.Text, Text);
        ImGui.PushStyleColor(ImGuiCol.TextDisabled, Dim);
        ImGui.PushStyleColor(ImGuiCol.ScrollbarBg, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.ScrollbarGrab, Rgb(0x34303F));
        ImGui.PushStyleColor(ImGuiCol.ModalWindowDimBg, Rgb(0x08070C, 0.55f));

        var scale = ImGuiHelpers.GlobalScale;
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 8 * scale);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 6 * scale);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 4 * scale);
        ImGui.PushStyleVar(ImGuiStyleVar.PopupRounding, 6 * scale);
        ImGui.PushStyleVar(ImGuiStyleVar.GrabRounding, 4 * scale);
        ImGui.PushStyleVar(ImGuiStyleVar.TabRounding, 4 * scale);
        ImGui.PushStyleVar(ImGuiStyleVar.ScrollbarRounding, 6 * scale);
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(8, 4) * scale);
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(6, 5) * scale);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 1f);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildBorderSize, 1f);
    }

    public static void Pop()
    {
        ImGui.PopStyleVar(StyleCount);
        ImGui.PopStyleColor(ColorCount);
    }

    public static Vector4 RatingColor(ContentRating rating) => rating switch
    {
        ContentRating.Sfw => Sfw,
        ContentRating.Nsfw => Nsfw,
        _ => Unsorted,
    };

    /// <summary>
    /// A row of joined buttons where one is active, like a segmented control.
    /// Returns true when the selection changed.
    /// </summary>
    public static bool Segmented(string id, IReadOnlyList<string> labels, ref int selected, IReadOnlyList<(Vector4 Fg, Vector4 Bg)?>? activeColors = null)
    {
        var changed = false;
        using var pushId = ImRaii.PushId(id);
        using var spacing = ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, new Vector2(2, ImGui.GetStyle().ItemSpacing.Y));
        for (var i = 0; i < labels.Count; i++)
        {
            if (i > 0)
                ImGui.SameLine();

            var active = i == selected;
            (Vector4 Fg, Vector4 Bg) colors = active ? activeColors?[i] ?? (Text, FrameActive) : (Dim, Frame);
            using var color = ImRaii.PushColor(ImGuiCol.Button, colors.Bg)
                .Push(ImGuiCol.ButtonHovered, active ? colors.Bg : FrameHover)
                .Push(ImGuiCol.ButtonActive, colors.Bg)
                .Push(ImGuiCol.Text, colors.Fg);
            if (ImGui.Button($"{labels[i]}##{i}") && !active)
            {
                selected = i;
                changed = true;
            }
        }

        return changed;
    }

    /// <summary>A small toggle styled like a chip; returns true when clicked.</summary>
    public static bool Chip(string label, bool active, Vector4? activeFg = null, Vector4? activeBg = null)
    {
        using var color = ImRaii.PushColor(ImGuiCol.Button, active ? activeBg ?? Selected : Frame)
            .Push(ImGuiCol.ButtonHovered, active ? activeBg ?? Selected : FrameHover)
            .Push(ImGuiCol.Text, active ? activeFg ?? Text : Dim);
        using var round = ImRaii.PushStyle(ImGuiStyleVar.FrameRounding, 20 * ImGuiHelpers.GlobalScale);
        return ImGui.Button(label);
    }

    public static bool PrimaryButton(string label, Vector2 size = default)
    {
        using var color = ImRaii.PushColor(ImGuiCol.Button, Primary)
            .Push(ImGuiCol.ButtonHovered, PrimaryHover)
            .Push(ImGuiCol.ButtonActive, PrimaryHover)
            .Push(ImGuiCol.Text, new Vector4(1, 1, 1, 1));
        return ImGui.Button(label, size);
    }

    /// <summary>Upper-case dim label used above a group of fields.</summary>
    public static void Label(string text)
    {
        using var color = ImRaii.PushColor(ImGuiCol.Text, Dim);
        ImGui.TextUnformatted(text.ToUpperInvariant());
    }

    public static void Hint(string text)
    {
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(text);
    }

    /// <summary>Command plus pose, e.g. "/groundsit · 2" or "/groundsit · default".</summary>
    public static string CommandLabel(ManagedMod mod, bool longForm = false)
    {
        if (string.IsNullOrWhiteSpace(mod.AnimationCommand))
            return string.Empty;
        return mod.PoseNumber switch
        {
            null => mod.AnimationCommand,
            0 => longForm ? $"{mod.AnimationCommand} · default pose" : $"{mod.AnimationCommand} · def",
            var n => longForm ? $"{mod.AnimationCommand} · pose {n}" : $"{mod.AnimationCommand} · {n}",
        };
    }

    public static float Scaled(float value) => value * ImGuiHelpers.GlobalScale;

    public static uint U32(Vector4 color) => ImGui.GetColorU32(color);

    public static void RightAlign(float width)
    {
        var avail = ImGui.GetContentRegionAvail().X;
        if (avail > width)
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + avail - width);
    }

    public static float ButtonWidth(string label) =>
        ImGui.CalcTextSize(label.Split("##")[0]).X + ImGui.GetStyle().FramePadding.X * 2;

    /// <summary>
    /// A square icon button exactly one frame tall, so it never makes a row taller than text
    /// widgets (the icon font is taller than the game font). Subtle buttons have no background
    /// until hovered.
    /// </summary>
    public static bool IconButton(Dalamud.Interface.FontAwesomeIcon icon, string id, string tooltip, Vector4? color = null, bool subtle = false)
    {
        var size = IconButtonSize(icon);
        bool clicked;
        using (ImRaii.PushColor(ImGuiCol.Button, Vector4.Zero, subtle).Push(ImGuiCol.ButtonHovered, Primary, subtle).Push(ImGuiCol.ButtonActive, PrimaryHover, subtle))
        using (ImRaii.PushFont(Dalamud.Interface.UiBuilder.IconFont))
        using (ImRaii.PushColor(ImGuiCol.Text, color ?? (subtle ? Faint : Text), color.HasValue || subtle))
        using (ImRaii.PushStyle(ImGuiStyleVar.FramePadding, Vector2.Zero))
            clicked = ImGui.Button($"{icon.ToIconString()}##{id}", size);

        // Tooltip text uses the normal font.
        Hint(tooltip);
        return clicked;
    }

    public static Vector2 IconButtonSize(Dalamud.Interface.FontAwesomeIcon icon)
    {
        var h = ImGui.GetFrameHeight();
        return new Vector2(h, h);
    }

    public static void Dot(Vector4 color, float radius = 4)
    {
        var pos = ImGui.GetCursorScreenPos();
        var h = ImGui.GetFrameHeight();
        ImGui.GetWindowDrawList().AddCircleFilled(pos + new Vector2(Scaled(radius), h / 2), Scaled(radius), U32(color));
        ImGui.Dummy(new Vector2(Scaled(radius * 2), h));
    }
}
