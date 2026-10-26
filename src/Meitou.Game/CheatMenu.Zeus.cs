using System.Numerics;
using Meitou.Data;
using Meitou.Data.Gameplay;
using Meitou.Data.Fcs;
using Meitou.Data.Ui;
using Meitou.Engine.Input;
using Meitou.Rendering.Ui;
using Meitou.Simulation;

namespace Meitou.Game;

// Zeus (the cheat menu's button under World; Meitou only, after Arma 3's Zeus "Create" panel): every character and animal of the game by faction in a panel down the
// right of the screen. Pick one and the ground under the pointer shows a figure where it will stand; a left click spawns one there (a new look each time, of the faction
// it is listed under), as many as wanted; a right click or Esc puts the pick down. The game keeps running and the camera keeps moving.
sealed partial class CheatMenu
{
    /// <summary>The window is the Zeus panel.</summary>
    bool zeusMode;
    readonly Field zeusSearch = new("zeus-search");
    readonly HashSet<int> zeusOpen = [];
    List<ZeusFaction>? zeusFactions;
    ZeusUnit? zeusPick;
    int zeusPlaced;
    /// <summary>Whose side the placed units are on: as listed (their own faction), friend, neutral or foe to you.</summary>
    int zeusSide;
    /// <summary>How many each click places, in a block (the formation drag's rows).</summary>
    int zeusCount = 1;
    static readonly string[] ZeusSides = ["Own", "Friend", "Neutral", "Foe"];
    static readonly int[] ZeusCounts = [1, 5, 10, 25, 50];
    const int ZeusMost = 100;

    sealed record ZeusUnit(string Record, string Name, bool Animal, int Faction);
    sealed record ZeusFaction(int Index, string Name, IReadOnlyList<ZeusUnit> Units);

    const float ZeusWidth = 360;

    void OpenZeus()
    {
        zeusMode = true;
        zeusPick = null;
        scroll = scrollTarget = 0;
        focused = null;
        openDropdown = null;
    }

    /// <summary>
    /// The game's characters and animals by faction: the factions of the squads (SQUAD_TEMPLATE) they are in (most characters name no faction themselves), else the
    /// faction their own record names; one in no squad and with no faction is under "Other" and spawns hostile to you. A character in squads of several factions is in each.
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
                units.Add(new ZeusUnit(r.StringId, r.Name.Trim(), r.Type == FcsRecordType.ANIMAL_CHARACTER, faction));
            }
        }
        return zeusFactions = [.. byFaction
            .Select(p => new ZeusFaction(p.Key, p.Key >= 0 ? FactionName(p.Key) : "Other", [.. p.Value.OrderBy(u => u.Animal).ThenBy(u => u.Name, StringComparer.OrdinalIgnoreCase)]))
            .Where(f => f.Name.Length > 0)
            .OrderBy(f => f.Index < 0).ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>The ground under the pointer, when the pointer is over the world.</summary>
    Vector3? ZeusGround(Vector2 at) =>
        bounds.Contains(at.X, at.Y) || player.Ray(at) is not { } ray ? null : player.GroundHit(ray.Origin, ray.Direction);

    /// <summary>A click outside the panel while Zeus is open: left places the pick, right puts it down. False: the click is the game's.</summary>
    bool ZeusClick(Vector2 at, MouseButton button)
    {
        if (zeusPick is not { } unit) return false;
        if (button == MouseButton.Right) { zeusPick = null; return true; }
        if (button != MouseButton.Left) return false;
        if (ZeusGround(at) is not { } ground || ZeusSpots(at, ground) is not { } placed) return true;
        int? faction = ZeusFactionOf(unit);
        var offsets = placed.Spots.Select(p => p - placed.Spots[0]).ToArray();
        Send(new DebugSpawnSquad(unit.Record, placed.Spots[0], offsets, MathF.Atan2(placed.Facing.X, placed.Facing.Y), faction) { Tick = Now });
        zeusPlaced += offsets.Length;
        string side = faction is { } f ? FactionName(f) : "hostile";
        Notice($"Placed {(offsets.Length > 1 ? $"{offsets.Length} x " : "")}{unit.Name} ({side}).");
        return true;
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

    /// <summary>The faction a pick spawns as: its own (Own, and when that is already on the chosen side), else the first faction on that side of you; null: hostile (the sandbox's pick).</summary>
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
        scale = Math.Max(0.35f, Math.Min(1.25f, Math.Min(height / 900f, width / 1280f)));
        controls.Clear(); hits.Clear();
        float pw = MathF.Round(ZeusWidth * scale), top = MathF.Round(12 * scale), bottom = MathF.Round(200 * scale);
        bounds = new(width - pw - MathF.Round(12 * scale), top, pw, Math.Max(240 * scale, height - top - bottom));
        float H = bounds.Height / scale, W = ZeusWidth;

        // The figures where the picks will stand, under the pointer.
        if (zeusPick is { } pick && ZeusGround(pointer) is { } ground && ZeusSpots(pointer, ground) is { } placing)
        {
            var walk = world().Walkability;
            for (int i = 0; i < placing.Spots.Length; i++)
            {
                var s = placing.Spots[i];
                Ghost(new Vector3(s.X, walk.GroundHeight(s.X, s.Y, ground.Y), s.Y), pick, named: i == 0);
            }
        }

        Surface(bounds, "window", alpha: WindowAlpha);
        Text("Zeus", R(16, 6, 200, 32), 22, LightInk);
        Text("CREATE", R(110, 10, 160, 26), 12, MutedInk);
        var close = R(W - 47, 6, 31, 32);
        controls.Add(new("zeus-close", "Button", close, "", "zeus:close", "Kenshi_CloseButtonSkin", true, new Dictionary<string, string>(), Vector4.Zero));
        hits.Add(new(close, () => { zeusMode = false; zeusPick = null; }));
        Rect(R(16, 44, W - 32, 1), MainInk with { W = 0.12f });
        EditField(zeusSearch, R(16, 54, W - 32, 30), "Search units");

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
            var units = search.Length == 0 ? faction.Units : faction.Units.Where(u => u.Name.Contains(search, StringComparison.OrdinalIgnoreCase)).ToList();
            if (units.Count == 0) continue;
            bool open = search.Length > 0 || zeusOpen.Contains(faction.Index);
            var header = Row(y);
            if (InList(header))
            {
                bool hovered = Hovered(header, clip);
                Rect(header, hovered ? MainInk with { W = 0.1f } : new Vector4(0, 0, 0, 0.22f), clip);
                Text($"{(open ? "v" : ">")}  {faction.Name}", header with { X = header.X + 6 * scale, Width = header.Width - 60 * scale }, 14, faction.Index == population()?.PlayerFaction ? Ally : LightInk, clip: clip);
                Text(units.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), header with { Width = header.Width - 8 * scale }, 11, MutedInk, "Right VCenter", clip);
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
                Text(unit.Name, at with { X = at.X + 26 * scale, Width = at.Width - 90 * scale }, 13, picked ? LightInk : MainInk, clip: clip);
                if (unit.Animal) Text("animal", at with { Width = at.Width - 8 * scale }, 11, MutedInk, "Right VCenter", clip);
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
        Button("-", R(16, H - 98, 30, 26), () => zeusCount = Math.Max(1, zeusCount - 1), size: 14);
        Text(zeusCount.ToString(System.Globalization.CultureInfo.InvariantCulture), R(48, H - 98, 40, 26), 15, LightInk, "Center");
        Button("+", R(90, H - 98, 30, 26), () => zeusCount = Math.Min(ZeusMost, zeusCount + 1), size: 14);
        Segmented(R(130, H - 98, W - 146, 26), [.. ZeusCounts.Select(c => c.ToString(System.Globalization.CultureInfo.InvariantCulture))], Array.IndexOf(ZeusCounts, zeusCount), i => zeusCount = ZeusCounts[i]);

        Rect(R(16, H - 76, W - 32, 1), MainInk with { W = 0.12f });
        string hint = zeusPick is { } held ? $"{held.Name}: left-click the ground to place, right-click to stop." : "Pick a unit, then click the ground.";
        Text(hint, R(16, H - 72, W - 32, 22), 11, zeusPick is null ? MutedInk : Brass);
        if (notice.Length > 0 && Environment.TickCount64 - noticeAt < 6000) Text(notice, R(16, H - 52, W - 140, 22), 11, Good);
        Button("Cheat menu", R(W - 124, H - 46, 108, 30), () => { zeusMode = false; zeusPick = null; scroll = scrollTarget = 0; }, size: 12);
        painter.Draw(controls, width, height, pointer, null);
    }

    /// <summary>A see-through figure standing at <paramref name="feet"/> (a man's height), with <paramref name="named"/> the unit's name over it.</summary>
    void Ghost(Vector3 feet, ZeusUnit unit, bool named)
    {
        float tall = unit.Animal ? 12 : 18;
        if (player.Project(feet) is not { } f || player.Project(feet + new Vector3(0, tall, 0)) is not { } h) return;
        float px = MathF.Max(8, f.Y - h.Y);
        if (px > 2000) return;
        var ink = zeusSide switch { 1 => Ally, 2 => LightInk, 3 => Enemy, _ => unit.Faction == population()?.PlayerFaction ? Ally : Brass } with { W = 0.55f };
        UiRect Part(float x, float y, float w, float hgt) => new(MathF.Round(f.X + x * px), MathF.Round(f.Y - y * px), MathF.Max(1, MathF.Round(w * px)), MathF.Max(1, MathF.Round(hgt * px)));
        if (unit.Animal)
        {
            Rect(Part(-0.45f, 0.75f, 0.9f, 0.4f), ink);                 // body
            Rect(Part(0.32f, 0.95f, 0.25f, 0.25f), ink);                // head
            foreach (float x in new[] { -0.4f, -0.25f, 0.2f, 0.33f }) Rect(Part(x, 0.36f, 0.07f, 0.36f), ink);
        }
        else
        {
            Rect(Part(-0.07f, 1f, 0.14f, 0.14f), ink);                  // head
            Rect(Part(-0.13f, 0.84f, 0.26f, 0.36f), ink);               // body
            Rect(Part(-0.21f, 0.82f, 0.07f, 0.34f), ink);               // arms
            Rect(Part(0.14f, 0.82f, 0.07f, 0.34f), ink);
            Rect(Part(-0.12f, 0.47f, 0.09f, 0.47f), ink);               // legs
            Rect(Part(0.03f, 0.47f, 0.09f, 0.47f), ink);
        }
        Rect(Part(-0.3f, 0.01f, 0.6f, 0.02f), ink with { W = 0.8f });  // where it stands
        if (named) Text(zeusCount > 1 ? $"{zeusCount} x {unit.Name}" : unit.Name, new(f.X - 150, h.Y - 26, 300, 22), 14, LightInk, "Center");
    }
}
