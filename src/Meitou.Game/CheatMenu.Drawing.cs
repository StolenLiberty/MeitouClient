using System.Globalization;
using System.Numerics;
using Meitou.Data.Ui;
using Meitou.Rendering;

namespace Meitou.Game;

// The cheat menu's window, chrome and widgets, in the Meitou UI kit's look (docs/meitou-ui.md): the settings window's rust plate, a paper page header,
// plate buttons, brass sliders. Layout units are a 1120 x 720 window scaled to the screen, as the settings.
sealed partial class CheatMenu
{
    static readonly Vector4 MainInk = new(175 / 255f, 166 / 255f, 139 / 255f, 1);
    static readonly Vector4 LightInk = new(217 / 255f, 206 / 255f, 184 / 255f, 1);
    static readonly Vector4 MutedInk = new(0.52f, 0.49f, 0.43f, 1);
    static readonly Vector4 PaperInk = new(20 / 255f, 8 / 255f, 6 / 255f, 1);
    static readonly Vector4 Brass = new(196 / 255f, 160 / 255f, 98 / 255f, 1);
    static readonly Vector4 Bad = new(191 / 255f, 72 / 255f, 52 / 255f, 1);
    static readonly Vector4 Good = new(168 / 255f, 183 / 255f, 116 / 255f, 1);
    static readonly Vector4 Ally = new(92 / 255f, 180 / 255f, 115 / 255f, 1);
    static readonly Vector4 Enemy = new(230 / 255f, 81 / 255f, 57 / 255f, 1);
    static readonly Vector4 LifeFill = new(126 / 255f, 122 / 255f, 79 / 255f, 1);
    static readonly Vector4 HealedFill = new(186 / 255f, 168 / 255f, 127 / 255f, 1);
    static readonly Vector4 StunFill = new(91 / 255f, 81 / 255f, 78 / 255f, 1);
    static readonly Vector4 HurtFill = new(115 / 255f, 54 / 255f, 37 / 255f, 1);
    static readonly Vector4 Well = new(0.06f, 0.05f, 0.04f, 0.95f);
    const int DropdownRows = 8;
    const float ContentTop = 140, ContentBottom = 668;

    bool MeitouLook => player.UiMode == UiMode.Meitou;

    UiRect R(float x, float y, float w, float h) => new(MathF.Round(bounds.X + x * scale), MathF.Round(bounds.Y + y * scale), MathF.Round(w * scale), MathF.Round(h * scale));

    /// <summary>A rectangle in the scrolled page content (y from the top of the content area).</summary>
    UiRect C(float x, float y, float w, float h) => R(x, ContentTop + y - (pinned ? 0 : scroll / scale), w, h);
    bool pinned;

    bool InView(UiRect at) => at.Y + at.Height >= viewport.Y && at.Y <= viewport.Y + viewport.Height;

    void Rect(UiRect at, Vector4 colour, UiRect? clip = null) =>
        controls.Add(new("cheat-fill", "Panel", at, "", "", "", true, new Dictionary<string, string>(), colour, Clip: clip, Pickable: false));

    void Text(string caption, UiRect at, float size = 14, Vector4? colour = null, string align = "Left VCenter", UiRect? clip = null)
    {
        var properties = new Dictionary<string, string>
        {
            ["TextAlign"] = align, ["FontName"] = "Kenshi_StandardFont",
            ["FontPixels"] = ((int)MathF.Round(size * scale)).ToString(CultureInfo.InvariantCulture),
        };
        controls.Add(new("cheat-text", "TextBox", at, caption, "", "", true, properties, Vector4.Zero, colour ?? MainInk, Clip: clip, Pickable: false, TextScale: size * scale / 16));
    }

    void Surface(UiRect at, string kind, bool selected = false, bool hover = false, UiRect? clip = null)
    {
        if (MeitouLook)
        {
            string texture = kind switch { "window" => "window", "paper" => hover ? "paper-hover" : "paper", _ => selected ? "button-selected" : hover ? "button-hover" : "button" };
            bool window = kind == "window";
            float margin = window ? 16 * scale : 0;
            var properties = new Dictionary<string, string>
            {
                ["ImageTexture"] = SettingsMenuArt.SurfacePrefix + texture,
                ["ImageBorder"] = window ? SettingsMenuArt.WindowBorder.ToString(CultureInfo.InvariantCulture) : "12",
                ["ImageBorderScale"] = (window ? scale / SettingsMenuArt.WindowDensity : scale).ToString(CultureInfo.InvariantCulture),
            };
            controls.Add(new("cheat-surface", "ImageBox", new(at.X - margin, at.Y - margin, at.Width + margin * 2, at.Height + margin * 2), "", "", "", true,
                properties, Vector4.Zero, Clip: clip, Pickable: false));
        }
        else
            controls.Add(new("cheat-surface", "Panel", at, "", "", kind == "window" ? "Kenshi_GenericWindowSkin" : kind == "paper" ? "Kenshi_GenericTextBoxSkin" : "Kenshi_Button1Skin", true,
                new Dictionary<string, string> { ["State"] = selected ? "pushed" : hover ? "highlighted" : "normal" },
                kind == "window" ? new(0.12f, 0.11f, 0.09f, 1) : Vector4.Zero, Clip: clip, Pickable: false));
    }

    /// <summary>A faint card behind a group of rows (the kit's plate rim on a darker well).</summary>
    void Card(UiRect at, UiRect? clip = null)
    {
        Rect(at, new(0, 0, 0, 0.18f), clip);
        Outline(at, MainInk with { W = 0.12f }, clip);
    }

    void Outline(UiRect at, Vector4 colour, UiRect? clip = null)
    {
        float line = MathF.Max(1, MathF.Round(scale));
        Rect(at with { Height = line }, colour, clip);
        Rect(at with { Y = at.Y + at.Height - line, Height = line }, colour, clip);
        Rect(at with { Width = line }, colour, clip);
        Rect(at with { X = at.X + at.Width - line, Width = line }, colour, clip);
    }

    bool Hovered(UiRect at, UiRect? clip) => at.Contains(pointer.X, pointer.Y) && (clip is null || clip.Value.Contains(pointer.X, pointer.Y)) && openDropdown is null;

    bool Clickable(UiRect at, UiRect? clip) => clip is null || at.Intersect(clip.Value).Height >= at.Height - 1;

    void Button(string label, UiRect at, Action action, bool selected = false, bool primary = false, UiRect? clip = null, bool enabled = true, float size = 13, Vector4? ink = null)
    {
        bool hovered = enabled && Hovered(at, clip);
        var drawn = at with { Y = at.Y + (hovered && pointerDown ? scale : 0) };
        Surface(drawn, primary ? "paper" : "button", selected || hovered && pointerDown, hovered, clip);
        var colour = !enabled ? MutedInk : primary && MeitouLook ? PaperInk : ink ?? (selected || hovered ? LightInk : MainInk);
        Text(label, new(drawn.X + 8 * scale, drawn.Y, drawn.Width - 16 * scale, drawn.Height), primary ? size + 1 : size, colour, "Center", clip);
        if (enabled && Clickable(at, clip)) hits.Add(new(at, action));
    }

    /// <summary>A row of options in one frame, the chosen one filled brass (the settings' Faithful | Meitou switch).</summary>
    void Segmented(UiRect at, IReadOnlyList<string> names, int selected, Action<int> choose, UiRect? clip = null)
    {
        Rect(at, new(0.07f, 0.06f, 0.05f, 0.9f), clip);
        float part = at.Width / names.Count, line = MathF.Max(1, MathF.Round(scale));
        for (int i = 0; i < names.Count; i++)
        {
            int index = i;
            var cell = new UiRect(MathF.Round(at.X + i * part), at.Y, MathF.Round(part), at.Height);
            bool hovered = Hovered(cell, clip);
            if (selected == i) { Rect(cell, Brass with { W = 0.28f }, clip); Rect(cell with { Y = cell.Y + cell.Height - 2 * line, Height = 2 * line }, Brass with { W = 0.9f }, clip); }
            else if (hovered) Rect(cell, MainInk with { W = 0.08f }, clip);
            Text(names[i], cell, 12, selected == i ? LightInk : hovered ? MainInk : MutedInk, "Center", clip);
            if (i > 0) Rect(new(cell.X, cell.Y + 3 * scale, line, cell.Height - 6 * scale), MainInk with { W = 0.22f }, clip);
            if (Clickable(cell, clip)) hits.Add(new(cell, () => choose(index)));
        }
        Outline(at, MainInk with { W = 0.22f }, clip);
    }

    /// <summary>
    /// A brass slider over <paramref name="track"/> showing <paramref name="fraction"/>; while dragged it follows the pointer (shown through <paramref name="preview"/>) and
    /// <paramref name="commit"/> gets the released fraction.
    /// </summary>
    float Slider(string id, UiRect track, float fraction, Action<float> commit, UiRect? clip = null)
    {
        bool mine = Dragging(id);
        float f = Math.Clamp(mine ? dragFraction : fraction, 0, 1);
        var bar = track with { Y = track.Y + track.Height / 2 - 1.5f * scale, Height = MathF.Max(2, 3 * scale) };
        Rect(bar, new(0.10f, 0.08f, 0.07f, 1), clip);
        Rect(bar with { Width = bar.Width * f }, Brass with { W = 0.75f }, clip);
        Rect(new(MathF.Round(bar.X + f * bar.Width - 3 * scale), bar.Y - 4 * scale, MathF.Round(6 * scale), MathF.Round(11 * scale)), Hovered(track, clip) || mine ? Vector4.One : LightInk, clip);
        if (Clickable(track, clip))
        {
            var hit = new Hit(track, SliderKey(id), track, commit);
            hits.Add(hit);
            if (mine) dragging = hit;
        }
        return f;
    }

    /// <summary>A stable delegate per slider id, so a drag stays with its slider across frames (the hit list is rebuilt every frame).</summary>
    readonly Dictionary<string, Action> sliderKeys = [];
    Action SliderKey(string id)
    {
        if (!sliderKeys.TryGetValue(id, out var key)) sliderKeys[id] = key = new SliderTag(id).Run;
        return key;
    }

    sealed class SliderTag(string id) { public string Id { get; } = id; public void Run() { } }

    bool Dragging(string id) => dragging is { } d && d.Activate.Target is SliderTag t && t.Id == id;

    void EditField(Field field, UiRect at, string placeholder = "", UiRect? clip = null)
    {
        bool active = ReferenceEquals(focused, field);
        Rect(at, Well, clip);
        Outline(at, active ? Brass with { W = 0.8f } : MainInk with { W = 0.14f }, clip);
        var textAt = at with { X = at.X + 8 * scale, Width = at.Width - 16 * scale };
        if (field.Text.Length == 0 && !active) Text(placeholder, textAt, 13, MutedInk, "Left VCenter", clip);
        else
            controls.Add(new("cheat-edit", "EditBox", textAt, field.Text, "", "", true,
                new Dictionary<string, string>
                {
                    ["Focused"] = active ? "true" : "false", ["TextAlign"] = "Left VCenter",
                    ["FontPixels"] = ((int)MathF.Round(14 * scale)).ToString(CultureInfo.InvariantCulture),
                    ["CaretIndex"] = field.Text.Length.ToString(CultureInfo.InvariantCulture),
                    ["SelectionStart"] = "0", ["SelectionLength"] = "0",
                }, Vector4.Zero, LightInk, Clip: clip, Pickable: false));
        if (Clickable(at, clip)) hits.Add(new(at, () => { focused = field; openDropdown = null; }));
    }

    void DropdownButton(string id, UiRect at, IReadOnlyList<string> options, int current, Action<int> choose, UiRect? clip = null, Vector4? ink = null)
    {
        string label = current >= 0 && current < options.Count ? options[current] : "None";
        Button(label + "   v", at, () =>
        {
            openDropdown = new Dropdown(id, at, options, current, choose);
            dropdownScroll = Math.Clamp(current - 3, 0, Math.Max(0, options.Count - DropdownRows));
            focused = null;
        }, clip: clip, ink: ink);
    }

    void DrawDropdown(Dropdown d)
    {
        hits.Clear();
        int count = Math.Min(DropdownRows, d.Options.Count);
        float row = 26 * scale, h = count * row + 12 * scale;
        float y = d.Anchor.Y + d.Anchor.Height + 4 * scale;
        if (y + h > bounds.Y + bounds.Height - 8 * scale) y = d.Anchor.Y - 4 * scale - h;
        var at = new UiRect(d.Anchor.X, y, Math.Max(d.Anchor.Width, 220 * scale), h);
        Surface(at, "window");
        for (int i = 0; i < count; i++)
        {
            int index = dropdownScroll + i;
            Button(d.Options[index], new(at.X + 6 * scale, at.Y + 6 * scale + i * row, at.Width - 12 * scale, row - 2 * scale), () => { d.Choose(index); openDropdown = null; }, index == d.Current);
        }
        if (d.Options.Count > count)
        {
            float track = count * row, thumb = Math.Max(16 * scale, track * count / d.Options.Count);
            float top = at.Y + 6 * scale + (track - thumb) * dropdownScroll / Math.Max(1, d.Options.Count - count);
            Rect(new(at.X + at.Width - 4 * scale, top, 2 * scale, thumb), Brass with { W = 0.7f });
        }
    }

    /// <summary>A health bar: the part's health over its maximum, the bandaged share light, the stunned share dark, below zero red.</summary>
    void HealthBar(UiRect at, float flesh, float stun, float bandage, float max, UiRect? clip)
    {
        Rect(at, new(0.07f, 0.055f, 0.045f, 1), clip);
        if (max <= 0) return;
        float health = flesh - stun;
        if (health < 0)
        {
            Rect(at with { Width = at.Width * Math.Clamp(-health / max, 0, 1) }, HurtFill, clip);
            return;
        }
        float w = at.Width * Math.Clamp(health / max, 0, 1);
        Rect(at with { Width = w }, LifeFill, clip);
        float b = at.Width * Math.Clamp(bandage / max, 0, 1);
        if (b > 0) Rect(new(at.X + w, at.Y, Math.Min(b, at.Width - w), at.Height), HealedFill with { W = 0.85f }, clip);
        float s = at.Width * Math.Clamp(stun / max, 0, 1);
        if (s > 0) Rect(new(at.X + w, at.Y, Math.Min(s, at.Width - w), at.Height), StunFill, clip);
    }

    static string Number(float v) => v.ToString(MathF.Abs(v) >= 10 ? "0" : "0.#", CultureInfo.InvariantCulture).Replace("-", "−");

    // ------------------------------------------------------------------ the window

    public void Draw(int width, int height)
    {
        if (!visible || player.SettingsPainter is not { } painter) return;
        art.Ensure(painter, MeitouLook, player.UiAtlas);
        if (townsMode) { DrawTownWindow(width, height, painter); return; }
        scale = Math.Max(0.35f, Math.Min(1.25f, Math.Min(height / 900f, width / 1280f)));
        bounds = new(MathF.Round((width - 1120 * scale) / 2), MathF.Round((height - 720 * scale) / 2), MathF.Round(1120 * scale), MathF.Round(720 * scale));
        controls.Clear(); hits.Clear();

        Surface(bounds, "window");
        Text("Cheat Menu", R(20, 6, 300, 32), 22, LightInk);
        var close = R(1069, 6, 31, 32);
        controls.Add(new("cheat-close", "Button", close, "", "cheat:close", "Kenshi_CloseButtonSkin", true, new Dictionary<string, string>(), Vector4.Zero));
        hits.Add(new(close, () => Visible = false));
        Rect(R(20, 44, 1080, 1), MainInk with { W = 0.12f });

        DrawTargetBar();
        Rect(R(20, 112, 1080, 1), MainInk with { W = 0.12f });
        for (int i = 0; i < PageNames.Length; i++)
        {
            int index = i;
            var at = R(20, 124 + i * 40, 156, 34);
            Button("", at, () => { if (page != (Page)index) { page = (Page)index; scroll = scrollTarget = 0; focused = null; openDropdown = null; } }, page == (Page)i);
            if (page == (Page)i) Rect(R(20, 124 + i * 40, 3, 34), Brass);
            Text(PageNames[i], R(36, 124 + i * 40, 130, 34), 14, page == (Page)i ? LightInk : MainInk);
        }
        Rect(R(192, 124, 1, 544), MainInk with { W = 0.12f });

        viewport = R(212, ContentTop, 888, ContentBottom - ContentTop);
        scrollTarget = Math.Clamp(scrollTarget, 0, maxScroll);
        scroll += (scrollTarget - scroll) * 0.35f;
        if (MathF.Abs(scrollTarget - scroll) < 0.5f) scroll = scrollTarget;
        float contentHeight = page switch
        {
            Page.Character => DrawCharacter(),
            Page.Health => DrawHealth(),
            Page.Skills => DrawSkills(),
            Page.Items => DrawItems(),
            Page.Factions => DrawFactions(),
            _ => DrawWorld(),
        };
        maxScroll = Math.Max(0, contentHeight * scale - viewport.Height);
        if (maxScroll > 0)
        {
            float track = viewport.Height, thumb = Math.Max(24 * scale, track * track / (track + maxScroll));
            Rect(new(viewport.X + viewport.Width + 8 * scale, viewport.Y, 3 * scale, track), MainInk with { W = 0.1f });
            Rect(new(viewport.X + viewport.Width + 8 * scale, viewport.Y + (track - thumb) * scroll / maxScroll, 3 * scale, thumb), Brass with { W = 0.75f });
        }

        Rect(R(20, 676, 1080, 1), MainInk with { W = 0.12f });
        if (notice.Length > 0 && Environment.TickCount64 - noticeAt < 8000) Text(notice, R(20, 684, 700, 28), 12, Brass);
        Text("Game keeps running · Esc closes", R(700, 684, 260, 28), 11, MutedInk, "Right VCenter");
        Button("Close", R(980, 684, 120, 28), () => Visible = false);

        if (openDropdown is { } open) DrawDropdown(open);
        painter.Draw(controls, width, height, pointer, null);
    }

    void DrawTargetBar()
    {
        var ids = Targets();
        var cold = ids.Count > 0 ? Cold(ids[0]) : null;
        var portrait = R(20, 56, 44, 44);
        Rect(portrait, new(0.27f, 0.22f, 0.18f, 1));
        Outline(portrait, cold is null ? MainInk with { W = 0.2f } : Brass, null);
        Text(cold is null ? "?" : cold.Name.Length > 0 ? cold.Name[..1].ToUpperInvariant() : "?", portrait, 20, LightInk, "Center");
        if (cold is null)
        {
            Text(target switch { Target.Inspected => "Nobody inspected", Target.Squad => "No squad", _ => "Nobody selected" }, R(76, 56, 600, 26), 18, MutedInk);
            Text("Select one of your characters, or click another character to inspect them.", R(76, 80, 600, 18), 12, MutedInk);
        }
        else
        {
            string name = cold.Name + (ids.Count > 1 ? $"  +{ids.Count - 1} more" : "");
            Text(name, R(76, 56, 600, 26), 18, LightInk);
            string mine = cold.IsPlayer ? " (yours)" : "";
            Text(string.Join(" · ", new[] { cold.Race?.Name ?? "", FactionName(cold.Faction) + mine, cold.Medical is { Dead: true } ? "Dead" : cold.Medical is { Unconscious: true } ? "Unconscious" : "" }
                .Where(s => s.Length > 0)), R(76, 80, 600, 18), 12, MutedInk);
        }
        var p = world().Player;
        string[] names = [$"Selected ({p.Selection.Count})", "Inspected", "Whole squad"];
        Segmented(R(780, 64, 320, 26), names, (int)target, i => { target = (Target)i; RefreshFields(); });
    }
}
