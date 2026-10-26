using System.Globalization;
using System.Numerics;
using Meitou.Data.Ui;
using Meitou.Rendering;
using Meitou.Rendering.Characters;
using Meitou.Rendering.Gpu;
using Meitou.Simulation;

namespace Meitou.Game;

/// <summary>
/// The squad bar's portraits (both UI modes; docs/game/ui-screens.md): a cell per member as the original's <c>Kenshi_PortraitCharacter</c> lays it out (the
/// background, the portrait the renderer draws into its atlas, the name along the bottom, the frame that is the button), left to right on the squad tab's page.
/// </summary>
sealed partial class PlayerInterface
{
    /// <summary>The ImageBox texture name of the renderer's portrait atlas (<see cref="CharacterRenderer.PortraitAtlas"/>).</summary>
    public const string PortraitTexture = "meitou:portraits";
    const int PortraitCells = CharacterRenderer.PortraitColumns * CharacterRenderer.PortraitRows;
    /// <summary>The cell layout's size (Kenshi_PortraitCharacter: 136 x 136); the parts below are placed in its pixels.</summary>
    const float PortraitLayoutSize = 136;

    /// <summary>The atlas cells after the squad's: the conversation window's speaker and player character.</summary>
    public const int DialogueNpcCell = PortraitCells - 2, DialoguePlayerCell = PortraitCells - 1;

    /// <summary>How much of its atlas cell a portrait fills (<see cref="CharacterRenderer.PortraitSizeFor"/>); the host sets it with the renderer's look.</summary>
    public int PortraitSize { get; set; } = CharacterRenderer.FaithfulPortraitSize;

    /// <summary>An ImageBox's <c>ImageRect</c> for atlas cell <paramref name="cell"/>: the part of the cell its portrait fills.</summary>
    string PortraitRect(int cell) => FormattableString.Invariant(
        $"{cell % CharacterRenderer.PortraitColumns * CharacterRenderer.PortraitCell} {cell / CharacterRenderer.PortraitColumns * CharacterRenderer.PortraitCell} {PortraitSize} {PortraitSize}");

    /// <summary>How many of the player's characters are posed every frame however far from the eye (<see cref="FullRateCharacters"/>).</summary>
    const int FullRateCount = 10;

    /// <summary>
    /// The player's characters posed every frame however far (<see cref="CharacterInstance.FullRateAnimation"/>) into <paramref name="into"/>: the first
    /// <see cref="FullRateCount"/> of the squad bar and then the other squads in order, and the first selected one.
    /// </summary>
    public void FullRateCharacters(HashSet<CharacterId> into)
    {
        into.Clear();
        foreach (var c in SquadBarMembers())
            if (into.Count < FullRateCount) into.Add(c.Id);
        foreach (var squad in session.CurrentSnapshot.PlayerSquads)
            foreach (var id in squad.Members)
                if (into.Count < FullRateCount) into.Add(id);
        if (Selected().FirstOrDefault() is { } first) into.Add(first.Id);
    }

    /// <summary>The characters the squad bar shows, the active squad's in order: the first atlas cells (<see cref="PortraitMembers"/>).</summary>
    public IReadOnlyList<CharacterSnapshot> SquadBarMembers()
    {
        var byId = session.CurrentSnapshot.Characters.Where(c => c.IsPlayer).ToDictionary(c => c.Id);
        return [.. ActiveSquadMembers().Select(byId.GetValueOrDefault).OfType<CharacterSnapshot>().Take(DialogueNpcCell)];
    }

    /// <summary>
    /// The characters whose portraits the host draws, each into the atlas cell of its index: the squad bar's first, then, while the squads screen is open, the other
    /// squads' members in order. As many as there are cells before the conversation's (30, CONSTANTS <c>max faction size</c>); a modded faction past that shows nameless cells.
    /// </summary>
    public IReadOnlyList<CharacterSnapshot> PortraitMembers()
    {
        var bar = SquadBarMembers();
        if (!SquadsShown) return bar;   // the others are drawn (and posed out of range) only while the squads screen shows them
        var byId = session.CurrentSnapshot.Characters.Where(c => c.IsPlayer).ToDictionary(c => c.Id);
        var shown = bar.Select(c => c.Id).ToHashSet();
        var rest = session.CurrentSnapshot.PlayerSquads.SelectMany(s => s.Members).Where(id => shown.Add(id)).Select(byId.GetValueOrDefault).OfType<CharacterSnapshot>();
        return [.. bar.Concat(rest).Take(DialogueNpcCell)];
    }

    /// <summary>The speaker and the player character of the open conversation, whose portraits the host draws into <see cref="DialogueNpcCell"/> and <see cref="DialoguePlayerCell"/>.</summary>
    public (CharacterId Npc, CharacterId Player)? DialoguePortraitCharacters() =>
        session.CurrentSnapshot.Conversation is { } talk ? (talk.Speaker.IsNone ? talk.Npc : talk.Speaker, talk.Player) : null;

    /// <summary>The number that turns a character's portrait camera (<see cref="CharacterSnapshot.PortraitSerial"/>; 0 for one not in the snapshot).</summary>
    public uint PortraitSerial(CharacterId id) => session.CurrentSnapshot.Characters.FirstOrDefault(c => c.Id == id)?.PortraitSerial ?? 0;

    /// <summary>The conversation window's two portraits over the layout's <c>NpcPortrait</c> and <c>PlayerPortrait</c> frames.</summary>
    void AddDialoguePortraits(List<UiControl> controls)
    {
        foreach (var (name, cell) in new[] { ("NpcPortrait", DialogueNpcCell), ("PlayerPortrait", DialoguePlayerCell) })
        {
            if (controls.FirstOrDefault(c => c.Name == name) is not { } frame) continue;
            controls.Add(new($"{name}-image", "ImageBox", frame.Bounds, "", "", "", true,
                new Dictionary<string, string> { ["ImageTexture"] = PortraitTexture, ["ImageFlipV"] = "true", ["ImageRect"] = PortraitRect(cell) },
                Vector4.Zero, Pickable: false, Clip: frame.Clip));
        }
    }

    /// <summary>Gives the interface a texture drawn at run time (the portrait atlas) under <paramref name="name"/>.</summary>
    public void SetUiTexture(string name, GpuTexture texture, SampledTexture sampled) => uiPainter?.SetTexture(name, texture, sampled);

    /// <summary>The squad bar's page, its row height and how far its rows are scrolled (the ItemBox scrolls a row per wheel notch).</summary>
    UiRect squadBarPage;
    float squadBarRow, squadBarScroll;
    /// <summary>Where a press on the squad bar's page began (a box over the portraits once dragged); null: none.</summary>
    Vector2? barBoxFrom;

    /// <summary>The button let go after a box over the squad bar: every member whose portrait the box touches is selected (Shift: added). Meitou.</summary>
    void SquadBarBoxUp(Vector2 p, bool shift)
    {
        if (barBoxFrom is not { } from) return;
        barBoxFrom = null;
        if (Vector2.Distance(from, p) < DragThreshold || squadBarRow <= 0) return;
        float x0 = MathF.Min(from.X, p.X), y0 = MathF.Min(from.Y, p.Y), x1 = MathF.Max(from.X, p.X), y1 = MathF.Max(from.Y, p.Y);
        var members = SquadBarMembers();
        float size = squadBarRow;
        int columns = Math.Max(1, (int)(squadBarPage.Width / size));
        var picked = new List<CharacterId>();
        for (int i = 0; i < members.Count; i++)
        {
            float left = squadBarPage.X + i % columns * size, top = squadBarPage.Y + i / columns * size - squadBarScroll;
            if (left < x1 && left + size > x0 && top < y1 && top + size > y0) picked.Add(members[i].Id);
        }
        if (picked.Count > 0) Select(picked, shift);
    }

    /// <summary>The wheel over the squad bar scrolls its rows; clamped when the bar is laid out.</summary>
    bool SquadBarWheel(Vector2 p, float steps)
    {
        if (squadBarRow <= 0 || !squadBarPage.Contains(p.X, p.Y)) return false;
        squadBarScroll -= MathF.Sign(steps) * squadBarRow;
        return true;
    }

    /// <summary>The squad bar member whose portrait is at <paramref name="p"/>, if any.</summary>
    CharacterId? SquadBarMemberAt(Vector2 p)
    {
        if (!UiVisible || MyGuiPresentation.HitTest(ArrangeUi(), p.X, p.Y) is not { Action: "meitou:member" } frame
            || !int.TryParse(frame.Argument, NumberStyles.None, CultureInfo.InvariantCulture, out int i)) return null;
        var members = SquadBarMembers();
        return i < members.Count ? members[i].Id : null;
    }

    void AddSquadPortraits(List<UiControl> controls, UiRect panel)
    {
        var members = SquadBarMembers();
        if (members.Count == 0) return;
        // The tab's page (the layout's TabItem rectangle inside SquadPanel).
        var page = new UiRect(panel.X + panel.Width * 0.0183727f, panel.Y + panel.Height * 0.206897f, panel.Width * 0.965879f, panel.Height * 0.737069f);
        // The page's ItemBox (docs/game/ui-screens.md "Squad bar"): square cells 0.9 of its height, or of its width over the members (counted up to 10), 60 to
        // 128 px at 1080p and even, filled left to right in rows; rows past the page are cut off.
        float scale = InventoryScale;
        float fit = Math.Min(page.Height * 0.9f, page.Width * 0.9f / Math.Min(members.Count, 10)) / scale;
        float size = MathF.Floor(Math.Clamp(fit, 60, 128) / 2) * 2 * scale, k = size / PortraitLayoutSize;
        int columns = Math.Max(1, (int)(page.Width / size)), rows = (members.Count + columns - 1) / columns;
        squadBarPage = page;
        squadBarRow = size;
        squadBarScroll = Math.Clamp(squadBarScroll, 0, Math.Max(0, rows * size - page.Height));
        var camera = session.Camera.Current;
        PrunePortraitFlashes(members);
        for (int i = 0; i < members.Count; i++)
        {
            var m = members[i];
            float left = page.X + i % columns * size, top = page.Y + i / columns * size - squadBarScroll;
            if (top + size <= page.Y || top >= page.Y + page.Height) continue;
            UiRect At(float x, float y, float w, float h) => new(left + x * k, top + y * k, w * k, h * k);
            // The layout's depths, back to front: background, back overlay, portrait, front overlay, name, frame.
            var state = PortraitStateOf(m, camera.Eye, camera.Target);
            controls.Add(PortraitImage($"portrait-back-{i}", At(0, 0, 135, 135), "Background", (state & PortraitState.Hurt) != 0 ? "Hurt" : "Normal", PortraitFlash(m.Id, state)) with { Clip = page });
            if (BackOverlayName(state) is { Length: > 0 } back) controls.Add(PortraitImage($"portrait-back-overlay-{i}", At(6, 5, 124, 124), "BackOverlay", back) with { Clip = page });
            var portrait = new Dictionary<string, string> { ["ImageTexture"] = PortraitTexture, ["ImageFlipV"] = "true", ["ImageRect"] = PortraitRect(i) };
            if ((state & PortraitState.Faded) != 0) portrait["Alpha"] = "0.25";
            controls.Add(new($"portrait-{i}", "ImageBox", At(6, 6, 124, 123), "", "", "", true, portrait, Vector4.Zero, Clip: page));
            if (FrontOverlayName(state) is { Length: > 0 } front) controls.Add(PortraitImage($"portrait-front-overlay-{i}", At(6, 6, 124, 123), "FrontOverlay", front) with { Clip = page });
            controls.Add(new($"portrait-name-{i}", "TextBox", At(4, 4, 128, 126), m.Name, "", "Kenshi_TextboxStandardText", true,
                new Dictionary<string, string> { ["TextAlign"] = "HCenter Bottom", ["TextColour"] = "1 1 1", ["TextShadow"] = "true" }, Vector4.Zero, Pickable: false, Clip: page));
            controls.Add(new($"member-{i}", "Button", At(0, 0, 136, 136), "", "meitou:member", "Kenshi_PortraitFrameSkin", true,
                new Dictionary<string, string> { ["Checked"] = m.Selected ? "true" : "false" }, Vector4.Zero, Argument: i.ToString(CultureInfo.InvariantCulture), Clip: page));
        }
    }
}
