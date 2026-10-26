using System.Numerics;
using Meitou.Data;
using Meitou.Data.Gameplay;
using Meitou.Data.Fcs;
using Meitou.Data.Ui;
using Meitou.Engine.Input;
using Meitou.Rendering.Ui;
using Meitou.Simulation;

namespace Meitou.Game;

// Zeus (the cheat menu's button under World, or its own key; Meitou only, after Arma 3's Zeus "Create" panel): every character and animal of the game and its mods by
// faction in a panel down the right of the screen. Pick one and the ground under the pointer shows see-through figures of it where the units will stand (drawn by the
// character renderer, ZeusGhosts); a left click spawns them there, joining the squad the earlier ones of the same unit and side are in; a right click or Esc puts the pick down.
sealed partial class CheatMenu
{
    /// <summary>The window is the Zeus panel.</summary>
    bool zeusMode;
    readonly Field zeusSearch = new("zeus-search");
    readonly HashSet<int> zeusOpen = [];
    List<ZeusFaction>? zeusFactions;
    ZeusUnit? zeusPick;
    int zeusPlaced;
    /// <summary>Whose side the placed units are on: Own (the faction they are listed under), Friend (yours: your own characters, in a squad of yours), Neutral or Foe to you.</summary>
    int zeusSide;
    /// <summary>How many each click places, in a block (the formation drag's rows).</summary>
    int zeusCount = 1;
    static readonly string[] ZeusSides = ["Own", "Friend", "Neutral", "Foe"];
    /// <summary>The characters picked with Zeus (a click or a box over the world, any faction): Delete removes them.</summary>
    readonly HashSet<CharacterId> zeusSelected = [];
    /// <summary>Where a press on the world began (Zeus with no unit picked): a box once dragged, else a click on one character.</summary>
    Vector2? zeusBoxFrom;
    const int ZeusMost = 100;

    /// <summary>The amount slider: its position (0..1) for a count and back, squared so the small counts get most of its length.</summary>
    static float CountToSlider(int count) => MathF.Sqrt((Math.Clamp(count, 1, ZeusMost) - 1) / (float)(ZeusMost - 1));
    static int SliderToCount(float t) => Math.Clamp(1 + (int)MathF.Round(Math.Clamp(t, 0, 1) * Math.Clamp(t, 0, 1) * (ZeusMost - 1)), 1, ZeusMost);

    /// <summary>A unit: its record, name, whether an animal, the faction it is listed under, and the mod that adds it ("" for the base game).</summary>
    sealed record ZeusUnit(string Record, string Name, bool Animal, int Faction, string Mod);
    /// <summary>A faction's units; <paramref name="Mod"/> the mod that adds the faction ("" for the base game).</summary>
    sealed record ZeusFaction(int Index, string Name, IReadOnlyList<ZeusUnit> Units, string Mod);

    /// <summary>The mod a record comes from, "" for the base game's files.</summary>
    static string ModOf(GameRecord r) => Source(r) is "base game" ? "" : Source(r);

    const float ZeusWidth = 360;

    /// <summary>Zeus's key: opens the panel (with the cheat menu behind it), or closes the menu when the panel is open.</summary>
    public void ToggleZeus()
    {
        if (visible && zeusMode) { Visible = false; return; }
        Visible = false;
        townsMode = false;
        Visible = true;
        OpenZeus();
    }

    void OpenZeus()
    {
        zeusMode = true;
        zeusPick = null;
        scroll = scrollTarget = 0;
        focused = null;
        openDropdown = null;
    }

    /// <summary>
    /// The characters and animals of the game and its mods (the load order's records) by faction: the factions of the squads (SQUAD_TEMPLATE) they are in (most characters
    /// name no faction themselves), else the faction their own record names; one in no squad and with no faction is under "Other" and spawns hostile to you. A character in
    /// squads of several factions is in each. A mod's units and factions are tagged with its name.
    /// </summary>
    List<ZeusFaction> ZeusFactions()
    {
        if (zeusFactions is not null) return zeusFactions;
        if (database() is not { } db || population() is not { } data) return [];
        var factionsOf = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
        foreach (var r in db.OfType(FcsRecordType.SQUAD_TEMPLATE))
        {
            var template = SquadTemplate.From(r);
            int faction = template.Faction is { } f ? data.Relations.IndexOf(f) : -1;
            if (faction < 0) continue;
            foreach (var link in new[] { template.Leader }.OfType<RecordLink>().Concat(template.Squad).Concat(template.Squad2).Concat(template.ChooseFrom).Concat(template.Animals).Concat(template.Animals2))
            {
                if (!factionsOf.TryGetValue(link.Id, out var set)) factionsOf[link.Id] = set = [];
                set.Add(faction);
            }
        }
        var byFaction = new Dictionary<int, List<ZeusUnit>>();
        foreach (var r in db.OfType(FcsRecordType.CHARACTER).Concat(db.OfType(FcsRecordType.ANIMAL_CHARACTER)))
        {
            if (string.IsNullOrWhiteSpace(r.Name)) continue;
            IEnumerable<int> factions = factionsOf.TryGetValue(r.StringId, out var set) ? set : [];
            int own = r.GetReferences("faction").Select(x => data.Relations.IndexOf(x.TargetStringId)).FirstOrDefault(i => i >= 0, -1);
            if (own >= 0) factions = factions.Append(own);
            var list = factions.Distinct().ToList();
            if (list.Count == 0) list.Add(-1);
            foreach (int faction in list)
            {
                if (!byFaction.TryGetValue(faction, out var units)) byFaction[faction] = units = [];
                units.Add(new ZeusUnit(r.StringId, r.Name.Trim(), r.Type == FcsRecordType.ANIMAL_CHARACTER, faction, ModOf(r)));
            }
        }
        return zeusFactions = [.. byFaction
            .Select(p => new ZeusFaction(p.Key, p.Key >= 0 ? FactionName(p.Key) : "Other", [.. p.Value.OrderBy(u => u.Animal).ThenBy(u => u.Name, StringComparer.OrdinalIgnoreCase)],
                p.Key >= 0 && db.Find(data.Factions[p.Key].Id) is { } record ? ModOf(record) : ""))
            .Where(f => f.Name.Length > 0)
            .OrderBy(f => f.Index < 0).ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>The ground under the pointer, when the pointer is over the world.</summary>
    Vector3? ZeusGround(Vector2 at) =>
        bounds.Contains(at.X, at.Y) || player.Ray(at) is not { } ray ? null : player.GroundHit(ray.Origin, ray.Direction);

    /// <summary>A click outside the panel while Zeus is open: left places the pick, right puts it down. False: the click is the game's.</summary>
    bool ZeusClick(Vector2 at, MouseButton button)
    {
        // No unit picked: a left press picks characters (a click on one, or a box), of any faction; the right button stays the game's.
        if (zeusPick is not { } unit)
        {
            if (button != MouseButton.Left) return false;
            zeusBoxFrom = at;
            return true;
        }
        if (button == MouseButton.Right) { zeusPick = null; return true; }
        if (button != MouseButton.Left) return false;
        if (ZeusGround(at) is not { } ground || ZeusSpots(at, ground) is not { } placed) return true;
        bool mine = zeusSide == 1;
        int? faction = mine ? population()?.PlayerFaction : ZeusFactionOf(unit);
        var offsets = placed.Spots.Select(p => p - placed.Spots[0]).ToArray();
        Send(new DebugSpawnSquad(unit.Record, placed.Spots[0], offsets, MathF.Atan2(placed.Facing.X, placed.Facing.Y), faction, mine) { Tick = Now });
        zeusPlaced += offsets.Length;
        string side = mine ? "yours" : faction is { } f ? FactionName(f) : "hostile";
        Notice($"Placed {(offsets.Length > 1 ? $"{offsets.Length} x " : "")}{unit.Name} ({side}).");
        return true;
    }

    /// <summary>The press let go: a box picks everyone inside it, a click the character under it (Ctrl adds to the picked ones; a click on nobody lets them go).</summary>
    void ZeusPickUp(Vector2 from, Vector2 to)
    {
        bool add = player.CtrlHeld;
        if (!add) zeusSelected.Clear();
        if (Vector2.Distance(from, to) >= 6) foreach (var id in player.AnyInBox(from, to)) zeusSelected.Add(id);
        else if (player.AnyCharacterAt(to) is { } one && !(add && zeusSelected.Remove(one))) zeusSelected.Add(one);
    }

    /// <summary>Zeus closes: nothing stays picked, the HUD's alerts go back to the edge.</summary>
    void ZeusLetGo()
    {
        zeusSelected.Clear();
        zeusBoxFrom = null;
        player.ZeusMarked = null;
        player.RightPanel = 0;
    }

    /// <summary>
    /// Where a click at <paramref name="pixel"/> puts the units: a block centred on <paramref name="ground"/>, facing away from the camera, as the formation drag's rows
    /// (<see cref="Formation.Line"/>: a front about twice as wide as it is deep, 9 units apart).
    /// </summary>
    (Vector2[] Spots, Vector2 Facing)? ZeusSpots(Vector2 pixel, Vector3 ground)
    {
        if (player.Ray(pixel) is not { } ray) return null;
        var away = new Vector2(ray.Direction.X, ray.Direction.Z);
        away = away.LengthSquared() > 1e-6f ? Vector2.Normalize(away) : Vector2.UnitY;
        int n = Math.Clamp(zeusCount, 1, ZeusMost);
        var centre = new Vector2(ground.X, ground.Z);
        if (n == 1) return ([centre], away);
        int perRow = Math.Clamp((int)MathF.Ceiling(MathF.Sqrt(2f * n)), 1, n), rows = (n + perRow - 1) / perRow;
        float half = (perRow - 1) * Formation.Spacing / 2;
        var across = new Vector2(away.Y, -away.X);
        // The front line half the block's depth ahead of the click, so the block sits on it.
        var front = centre + away * ((rows - 1) * Formation.Spacing / 2);
        var (spots, facing) = Formation.Line(new Vector2[n], front - across * half, front + across * half, away);
        return (spots, facing);
    }

    /// <summary>The faction a pick spawns as (Own, Neutral, Foe; Friend is yours): its own (Own, and when that is already on the chosen side), else the first faction on that side of you;
    /// null: hostile (the sandbox's pick).</summary>
    int? ZeusFactionOf(ZeusUnit unit)
    {
        if (population() is not { } data || data.PlayerFaction < 0) return unit.Faction >= 0 ? unit.Faction : null;
        int me = data.PlayerFaction;
        bool Fits(int f) => f >= 0 && f < data.Factions.Count && zeusSide switch
        {
            1 => f == me || data.Relations.Get(f, me) >= FactionRelations.AllyThreshold,
            2 => f != me && data.Relations.Get(f, me) > FactionRelations.HostileThreshold && data.Relations.Get(f, me) < FactionRelations.AllyThreshold,
            3 => f != me && data.Relations.Get(f, me) <= FactionRelations.HostileThreshold,
            _ => true,
        };
        if (zeusSide == 0) return unit.Faction >= 0 ? unit.Faction : null;
        if (Fits(unit.Faction) && unit.Faction != me) return unit.Faction;
        for (int f = 0; f < data.Factions.Count; f++)
            if (f != me && Fits(f)) return f;
        return zeusSide switch { 1 => me, 3 => null, _ => unit.Faction >= 0 ? unit.Faction : null };
    }

    void DrawZeus(int width, int height, UiPainter painter)
    {
        scale = Math.Max(0.35f, Math.Min(1.25f, Math.Min(height / 900f, width / 1280f))) * player.HudScale;
        controls.Clear(); hits.Clear();
        // Below the HUD's top-right line (the outpost) and clear of its bottom panels; the alerts move to its left.
        float pw = MathF.Round(ZeusWidth * scale), top = MathF.Round(Math.Max(48 * scale, player.HudRightBelow + 8 * scale)), bottom = MathF.Round(280 * scale);
        bounds = new(width - pw - MathF.Round(12 * scale), top, pw, Math.Max(240 * scale, height - top - bottom));
        float H = bounds.Height / scale, W = ZeusWidth;

        // The pick's name over its figures (the figures are the character renderer's, ZeusGhosts).
        if (zeusPick is { } pick && ZeusGround(pointer) is { } ground && player.Project(ground + new Vector3(0, pick.Animal ? 14 : 21, 0)) is { } over)
            Text(zeusCount > 1 ? $"{zeusCount} x {pick.Name}" : pick.Name, new(over.X - 160, over.Y - 24, 320, 22), 14, LightInk, "Center");

        player.RightPanel = bounds.Width + 12 * scale;
        // The characters picked: those still in the world, ringed red by the HUD.
        var present = player.Snapshot.Characters.Select(c => c.Id).ToHashSet();
        zeusSelected.RemoveWhere(id => !present.Contains(id));
        player.ZeusMarked = zeusSelected;
        if (zeusBoxFrom is { } boxFrom && Vector2.Distance(boxFrom, pointer) >= 6)
        {
            var box = new UiRect(MathF.Min(boxFrom.X, pointer.X), MathF.Min(boxFrom.Y, pointer.Y), MathF.Abs(pointer.X - boxFrom.X), MathF.Abs(pointer.Y - boxFrom.Y));
            Rect(box, Enemy with { W = 0.1f });
            Outline(box, Enemy with { W = 0.85f });
        }

        // The Modern style's see-through plate; the others' solid one, so nothing of the game's HUD shows through.
        if (!Modern) Rect(bounds, new Vector4(0.075f, 0.065f, 0.055f, 1));
        Surface(bounds, "window");
        Text("Zeus", R(16, 6, 200, 32), 22, LightInk);
        Text("CREATE", R(110, 10, 160, 26), 12, MutedInk);
        CloseButton(R(W - 47, 6, 31, 32), () => Visible = false);
        Rect(R(16, 44, W - 32, 1), MainInk with { W = 0.12f });
        EditField(zeusSearch, R(16, 54, W - 32, 30), "Search units or mods");

        // The tree: factions, open ones showing their units.
        viewport = R(16, 94, W - 32, H - 94 - 182);
        var clip = viewport;
        scrollTarget = Math.Clamp(scrollTarget, 0, maxScroll);
        scroll += (scrollTarget - scroll) * 0.35f;
        if (MathF.Abs(scrollTarget - scroll) < 0.5f) scroll = scrollTarget;
        string search = zeusSearch.Text.Trim();
        float y = 0, row = 26;
        UiRect Row(float at) => new(viewport.X, MathF.Round(viewport.Y + at * scale - scroll), viewport.Width, MathF.Round((row - 1) * scale));
        bool InList(UiRect at) => at.Y + at.Height >= viewport.Y && at.Y <= viewport.Y + viewport.Height;
        foreach (var faction in ZeusFactions())
        {
            var units = search.Length == 0 ? faction.Units : faction.Name.Contains(search, StringComparison.OrdinalIgnoreCase) || faction.Mod.Contains(search, StringComparison.OrdinalIgnoreCase) ? faction.Units
                : faction.Units.Where(u => u.Name.Contains(search, StringComparison.OrdinalIgnoreCase) || u.Mod.Contains(search, StringComparison.OrdinalIgnoreCase)).ToList();
            if (units.Count == 0) continue;
            bool open = search.Length > 0 || zeusOpen.Contains(faction.Index);
            var header = Row(y);
            if (InList(header))
            {
                bool hovered = Hovered(header, clip);
                Rect(header, hovered ? MainInk with { W = 0.1f } : new Vector4(0, 0, 0, 0.22f), clip);
                Text($"{(open ? "v" : ">")}  {faction.Name}", header with { X = header.X + 6 * scale, Width = header.Width - 60 * scale }, 14, faction.Index == population()?.PlayerFaction ? Ally : LightInk, clip: clip);
                int modded = units.Count(u => u.Mod.Length > 0);
                string count = units.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
                Text(faction.Mod.Length > 0 ? $"{faction.Mod} · {count}" : modded > 0 ? $"{modded} modded · {count}" : count, header with { Width = header.Width - 8 * scale }, 11,
                    faction.Mod.Length > 0 || modded > 0 ? Brass : MutedInk, "Right VCenter", clip);
                int index = faction.Index;
                if (Clickable(header, clip)) hits.Add(new(header, () => { if (!zeusOpen.Remove(index)) zeusOpen.Add(index); }));
            }
            y += row;
            if (!open) continue;
            foreach (var unit in units)
            {
                var at = Row(y);
                y += row;
                if (!InList(at)) continue;
                bool picked = zeusPick is { } p && p.Record == unit.Record && p.Faction == unit.Faction;
                if (picked) { Rect(at, Brass with { W = 0.22f }, clip); Rect(at with { Width = 3 * scale }, Brass, clip); }
                else if (Hovered(at, clip)) Rect(at, MainInk with { W = 0.06f }, clip);
                Text(unit.Name, at with { X = at.X + 26 * scale, Width = at.Width - 130 * scale }, 13, picked ? LightInk : MainInk, clip: clip);
                string tag = string.Join(" · ", new[] { unit.Mod, unit.Animal ? "animal" : "" }.Where(t => t.Length > 0));
                if (tag.Length > 0) Text(tag, at with { X = at.X + at.Width - 128 * scale, Width = 120 * scale }, 11, unit.Mod.Length > 0 ? Brass : MutedInk, "Right VCenter", clip);
                var chosen = unit;
                if (Clickable(at, clip)) hits.Add(new(at, () => zeusPick = zeusPick == chosen ? null : chosen));
            }
        }
        if (y == 0) Text(search.Length > 0 ? "Nothing matches." : "No game data.", Row(0), 13, MutedInk, clip: clip);
        maxScroll = Math.Max(0, y * scale - viewport.Height);
        if (maxScroll > 0)
        {
            float track = viewport.Height, thumb = Math.Max(24 * scale, track * track / (track + maxScroll));
            Rect(new(viewport.X + viewport.Width + 5 * scale, viewport.Y, 3 * scale, track), MainInk with { W = 0.1f });
            Rect(new(viewport.X + viewport.Width + 5 * scale, viewport.Y + (track - thumb) * scroll / maxScroll, 3 * scale, thumb), Brass with { W = 0.75f });
        }

        // Whose side, and how many a click places.
        Rect(R(16, H - 176, W - 32, 1), MainInk with { W = 0.12f });
        Text("SIDE", R(16, H - 170, 80, 18), 11, MutedInk);
        Segmented(R(16, H - 150, W - 32, 26), ZeusSides, zeusSide, i => zeusSide = i);
        Text("AMOUNT", R(16, H - 118, 80, 18), 11, MutedInk);
        float slid = Slider("zeus-count", R(16, H - 96, W - 92, 22), CountToSlider(zeusCount), t => zeusCount = SliderToCount(t));
        int shown = Dragging("zeus-count") ? SliderToCount(slid) : zeusCount;
        Text(shown.ToString(System.Globalization.CultureInfo.InvariantCulture), R(W - 70, H - 100, 54, 28), 16, LightInk, "Right VCenter");

        Rect(R(16, H - 76, W - 32, 1), MainInk with { W = 0.12f });
        string hint = zeusPick is { } held ? $"{held.Name}: left-click the ground to place, right-click to stop."
            : zeusSelected.Count > 0 ? $"{zeusSelected.Count} picked: Delete removes them, Esc lets go, Ctrl adds."
            : "Click or drag over anyone to pick them; Delete removes them.";
        Text(hint, R(16, H - 72, W - 32, 22), 11, zeusPick is null && zeusSelected.Count == 0 ? MutedInk : zeusSelected.Count > 0 ? Enemy : Brass);
        if (notice.Length > 0 && Environment.TickCount64 - noticeAt < 6000) Text(notice, R(16, H - 52, W - 140, 22), 11, Good);
        Button("Cheat menu", R(W - 124, H - 46, 108, 30), () => { zeusMode = false; zeusPick = null; scroll = scrollTarget = 0; }, size: 12);
        painter.Draw(controls, width, height, pointer, null);
    }

    /// <summary>Zeus's Delete: removes the characters picked with Zeus, else the one under the mouse, else your selected ones (<see cref="DebugRemove"/>).</summary>
    void ZeusDelete()
    {
        IReadOnlyList<CharacterId> doomed = zeusSelected.Count > 0 ? [.. zeusSelected]
            : !bounds.Contains(pointer.X, pointer.Y) && player.AnyCharacterAt(pointer) is { } under ? [under] : [.. world().Player.Selection];
        zeusSelected.Clear();
        if (doomed.Count == 0) { Notice("Nobody picked, under the mouse or selected."); return; }
        Send(new DebugRemove(doomed) { Tick = Now });
        Notice(doomed.Count == 1 ? "Removed 1 character." : $"Removed {doomed.Count} characters.");
    }

    /// <summary>One figure to draw where a pick will stand: its unit, faction (for its look), feet, facing, and outline colour (alpha above 1: a figure drawn see-through).</summary>
    public readonly record struct ZeusGhost(string Record, string? FactionId, Vector3 Feet, float Yaw, Vector4 Colour);

    /// <summary>The figures of the pick where it would be placed, under the pointer now (none without a pick or over the panel). Once a frame, by the host's draw list.</summary>
    public IReadOnlyList<ZeusGhost> ZeusGhosts()
    {
        if (!visible || !zeusMode || zeusPick is not { } pick || ZeusGround(pointer) is not { } ground || ZeusSpots(pointer, ground) is not { } placing) return [];
        var data = population();
        int? faction = zeusSide == 1 ? data?.PlayerFaction : ZeusFactionOf(pick);
        string? factionId = data is not null && faction is int f && f >= 0 && f < data.Factions.Count ? data.Factions[f].Id : null;
        var colour = zeusSide switch { 1 => Ally, 2 => LightInk, 3 => Enemy, _ => Brass } with { W = 2 };
        float yaw = MathF.Atan2(placing.Facing.X, placing.Facing.Y);
        var walk = world().Walkability;
        var list = new ZeusGhost[placing.Spots.Length];
        for (int i = 0; i < list.Length; i++)
        {
            var s = placing.Spots[i];
            list[i] = new ZeusGhost(pick.Record, factionId, new Vector3(s.X, walk.GroundHeight(s.X, s.Y, ground.Y), s.Y), yaw, colour);
        }
        return list;
    }
}
