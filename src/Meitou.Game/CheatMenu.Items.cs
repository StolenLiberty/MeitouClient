using System.Globalization;
using System.Numerics;
using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay;
using Meitou.Simulation;

namespace Meitou.Game;

// Items, Factions and World pages.
sealed partial class CheatMenu
{
    static readonly (string Name, FcsRecordType[] Types)[] ItemCategories =
    [
        ("Weapons", [FcsRecordType.WEAPON]),
        ("Crossbows", [FcsRecordType.CROSSBOW]),
        ("Armour", [FcsRecordType.ARMOUR]),
        ("Backpacks", [FcsRecordType.CONTAINER]),
        ("Items", [FcsRecordType.ITEM]),
        ("Limbs", [FcsRecordType.LIMB_REPLACEMENT]),
        ("Maps", [FcsRecordType.MAP_ITEM]),
        ("Blueprints", [FcsRecordType.BLUEPRINT]),
    ];

    int itemCategory;
    List<GameRecord>? itemCache;
    (int Category, string Search)? itemCacheKey;
    string selectedItem = "";
    int itemMaker = -1, itemModel = -1;
    float itemQuantity, itemQuality = 1;
    const float ItemRow = 42;

    List<GameRecord> Items()
    {
        string search = itemSearch.Text.Trim();
        if (itemCache is not null && itemCacheKey == (itemCategory, search)) return itemCache;
        var types = ItemCategories[itemCategory].Types;
        var all = database() is { } db ? types.SelectMany(db.OfType) : [];
        itemCache = [.. all.Where(r => r.Name.Length > 0 && (search.Length == 0 || r.Name.Contains(search, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.StringId, StringComparer.Ordinal)];
        itemCacheKey = (itemCategory, search);
        return itemCache;
    }

    static string Source(GameRecord r) => r.DefinedBy switch
    {
        "gamedata.base" or "Newwworld.mod" or "rebirth.mod" or "Dialogue.mod" => "base game",
        var m => m.EndsWith(".mod", StringComparison.OrdinalIgnoreCase) ? m[..^4] : m,
    };

    /// <summary>The makers whose <c>weapon types</c> list the weapon (empty: none list it, then every maker with models).</summary>
    List<GameRecord> Makers(GameRecord item)
    {
        if (database() is not { } db) return [];
        var makers = db.OfType(FcsRecordType.WEAPON_MANUFACTURER).Where(m => m.GetReferences("weapon models").Count > 0).OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var listing = makers.Where(m => m.GetReferences("weapon types").Any(t => t.TargetStringId == item.StringId)).ToList();
        return listing.Count > 0 ? listing : makers;
    }

    List<(GameRecord Model, int Level)> Models(GameRecord maker) => database() is { } db
        ? [.. maker.GetReferences("weapon models").Select(r => (db.Find(r.TargetStringId)!, r.Values.Value0)).Where(m => m.Item1 is not null).OrderBy(m => m.Value0)]
        : [];

    float DrawItems()
    {
        var clip = viewport;
        pinned = true;
        Header("Items", 0);
        EditField(itemSearch, C(212, 44, 250, 30), "Search items", clip);
        Segmented(C(470, 44, 630, 30), [.. ItemCategories.Select(c => c.Name)], itemCategory, i =>
        {
            itemCategory = i; itemCache = null; scrollTarget = scroll = 0; selectedItem = ""; itemMaker = itemModel = -1;
        }, clip);
        pinned = false;

        var list = Items();
        // The list scrolls; the detail panel on the right stays put (drawn in window units).
        var listClip = R(212, ContentTop + 84, 568, ContentBottom - ContentTop - 84);
        Card(listClip);
        float listScroll = scroll / scale;
        int first = Math.Max(0, (int)(listScroll / ItemRow)), last = Math.Min(list.Count, first + (int)((ContentBottom - ContentTop - 84) / ItemRow) + 2);
        for (int i = first; i < last; i++)
        {
            var r = list[i];
            var row = R(212, ContentTop + 84 + i * ItemRow - listScroll, 568, ItemRow);
            bool selected = r.StringId == selectedItem;
            if (selected) { Rect(row, Brass with { W = 0.14f }, listClip); Rect(row with { Width = 3 * scale }, Brass, listClip); }
            else if (Hovered(row, listClip)) Rect(row, MainInk with { W = 0.06f }, listClip);
            Text(r.Name, row with { X = row.X + 12 * scale, Y = row.Y + 2 * scale, Width = 380 * scale, Height = 22 * scale }, 14, selected ? LightInk : MainInk, clip: listClip);
            Text($"{ItemCategories[itemCategory].Name} · {Source(r)}", row with { X = row.X + 12 * scale, Y = row.Y + 22 * scale, Width = 380 * scale, Height = 16 * scale }, 11, MutedInk, clip: listClip);
            int value = r.GetInt("value");
            if (value > 0) Text("c." + value.ToString("N0", CultureInfo.InvariantCulture), row with { X = row.X + 400 * scale, Width = 156 * scale }, 13, MainInk, "Right VCenter", listClip);
            Rect(row with { Y = row.Y + row.Height - 1, Height = 1 }, MainInk with { W = 0.07f }, listClip);
            var id = r.StringId;
            if (Clickable(row, listClip)) hits.Add(new(row, () => { selectedItem = id; itemMaker = itemModel = -1; }));
        }
        if (list.Count == 0) Text("No item matches.", R(228, ContentTop + 96, 400, 30), 14, MutedInk);
        DrawItemDetail();
        // The page's height is the list's, so the shared scrollbar scrolls it.
        return 84 + list.Count * ItemRow;
    }

    void DrawItemDetail()
    {
        var panel = R(796, ContentTop + 84, 304, ContentBottom - ContentTop - 84);
        Card(panel);
        if (database()?.Find(selectedItem) is not { } item)
        {
            Text("Pick an item from the list.", R(812, ContentTop + 100, 280, 30), 14, MutedInk);
            return;
        }
        var ids = Targets();
        var to = ids.Count > 0 ? Cold(ids[0]) : null;
        float y = ContentTop + 96;
        Text(item.Name, R(812, y, 280, 28), 20, LightInk); y += 28;
        Text($"{ItemCategories[itemCategory].Name} · {Source(item)}", R(812, y, 280, 18), 11, MutedInk); y += 30;
        if (item.Type is FcsRecordType.WEAPON or FcsRecordType.CROSSBOW)
        {
            var makers = Makers(item);
            if (makers.Count > 0)
            {
                if (itemMaker < 0 || itemMaker >= makers.Count) itemMaker = 0;
                Text("Maker", R(812, y, 80, 28), 14);
                DropdownButton("maker", R(892, y, 196, 28), [.. makers.Select(m => m.Name)], itemMaker, i => { itemMaker = i; itemModel = -1; });
                y += 36;
                var models = Models(makers[itemMaker]);
                if (models.Count > 0)
                {
                    if (itemModel < 0 || itemModel >= models.Count) itemModel = models.Count - 1;
                    Text("Grade", R(812, y, 80, 28), 14);
                    DropdownButton("grade", R(892, y, 196, 28), [.. models.Select(m => $"{m.Model.Name} ({m.Level})")], itemModel, i => itemModel = i, ink: GradeInk(models[itemModel].Level));
                    y += 36;
                }
            }
        }
        else if (item.Type is FcsRecordType.ARMOUR or FcsRecordType.LIMB_REPLACEMENT)
        {
            Text("Quality", R(812, y, 80, 28), 14);
            float q = Slider("quality", R(892, y + 2, 150, 24), itemQuality, f => itemQuality = f);
            Text(Number(MathF.Round(q * 100)), R(1044, y, 44, 28), 13, LightInk, "Right VCenter");
            y += 36;
        }
        Text("Quantity", R(812, y, 80, 28), 14);
        float qty = Slider("quantity", R(892, y + 2, 150, 24), itemQuantity, f => itemQuantity = f);
        int quantity = Quantity(Dragging("quantity") ? qty : itemQuantity);
        Text(quantity.ToString(CultureInfo.InvariantCulture), R(1044, y, 44, 28), 13, LightInk, "Right VCenter");
        y += 44;
        float bottom = ContentBottom - 12;
        Button(to is null ? "Give" : $"Give to {to.Name}", R(812, bottom - 80, 276, 36), () => GiveSelected(item, false), primary: true, enabled: to is not null);
        Button("Drop at their feet", R(812, bottom - 38, 276, 30), () => GiveSelected(item, true), enabled: to is not null);
    }

    /// <summary>Quantity slider: 1 to 10 in ones over the first half, then up to 500 (a stack of ammo or materials).</summary>
    static int Quantity(float f) => f <= 0.5f ? 1 + (int)MathF.Round(f * 18) : (int)MathF.Round(10 * MathF.Pow(50, (f - 0.5f) * 2));

    static Vector4 GradeInk(int level) => level switch
    {
        < 20 => new(0.6f, 0.6f, 0.6f, 1),
        < 50 => new(217 / 255f, 204 / 255f, 191 / 255f, 1),
        < 70 => new(64 / 255f, 153 / 255f, 179 / 255f, 1),
        < 85 => new(102 / 255f, 252 / 255f, 128 / 255f, 1),
        _ => new(252 / 255f, 235 / 255f, 13 / 255f, 1),
    };

    void GiveSelected(GameRecord item, bool atFeet)
    {
        var ids = Targets();
        if (ids.Count == 0) return;
        string company = "", model = "";
        if (item.Type is FcsRecordType.WEAPON or FcsRecordType.CROSSBOW && Makers(item) is { Count: > 0 } makers && itemMaker >= 0 && itemMaker < makers.Count)
        {
            company = makers[itemMaker].StringId;
            var models = Models(makers[itemMaker]);
            if (itemModel >= 0 && itemModel < models.Count) model = models[itemModel].Model.StringId;
        }
        int quantity = Quantity(itemQuantity);
        Send(new CheatGiveItem(ids[0], item.StringId, quantity, (int)MathF.Round(itemQuality * 100), company, model, atFeet) { Tick = Now });
        Notice($"{(atFeet ? "Dropped" : "Gave")} {quantity} x {item.Name}{(atFeet ? " at the feet of " : " to ")}{Cold(ids[0])?.Name}.");
    }

    // ------------------------------------------------------------------ Factions

    int factionSubject = -1;
    bool bothWays = true;

    float DrawFactions()
    {
        var clip = viewport;
        Header("Factions", 0);
        if (population() is not { } data || data.Factions.Count == 0) { Text("No factions loaded.", C(212, 56, 600, 30), 14, MutedInk, clip: clip); return 100; }
        var relations = data.Relations;
        if (factionSubject < 0 || factionSubject >= data.Factions.Count) factionSubject = Math.Max(0, data.PlayerFaction);
        var names = data.Factions.Select((f, i) => i == data.PlayerFaction ? f.Name + " (you)" : f.Name).ToList();
        var order = Enumerable.Range(0, names.Count).OrderBy(i => i != data.PlayerFaction).ThenBy(i => names[i], StringComparer.OrdinalIgnoreCase).ToList();
        Text("How", C(212, 44, 40, 30), 14, clip: clip);
        DropdownButton("subject", C(252, 44, 280, 30), [.. order.Select(i => names[i])], order.IndexOf(factionSubject), k => { factionSubject = order[k]; scrollTarget = scroll = 0; }, clip);
        Text("feels about", C(540, 44, 90, 30), 14, clip: clip);
        EditField(factionFilter, C(632, 44, 228, 30), "Filter factions", clip);
        Segmented(C(870, 46, 230, 26), ["One way", "Both ways"], bothWays ? 1 : 0, i => bothWays = i == 1, clip);

        string filter = factionFilter.Text.Trim();
        float y = 88;
        int a = factionSubject;
        foreach (int b in order)
        {
            if (b == a || filter.Length > 0 && !names[b].Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
            var row = C(212, y, 888, 36);
            if (InView(row))
            {
                float value = relations.Get(a, b);
                string id = $"rel{b}";
                Text(names[b], C(222, y, 220, 36), 14, b == data.PlayerFaction ? LightInk : MainInk, clip: clip);
                float f = Slider(id, C(446, y + 6, 250, 24), (value + 100) / 200, t => SetRelation(a, b, MathF.Round(t * 200 - 100)), clip);
                float shown = Dragging(id) ? MathF.Round(f * 200 - 100) : value;
                Text(Number(shown), C(700, y, 48, 36), 13, LightInk, "Right VCenter", clip);
                var (tag, ink) = shown <= FactionRelations.HostileThreshold ? ("Hostile", Enemy) : shown >= FactionRelations.AllyThreshold ? ("Allied", Ally) : ("Neutral", LightInk);
                var tagAt = C(760, y + 9, 70, 18);
                Rect(tagAt, ink with { W = 0.16f }, clip);
                Text(tag, tagAt, 11, ink, "Center", clip);
                Button("Ally", C(840, y + 5, 80, 26), () => SetRelation(a, b, 100), clip: clip, size: 12);
                Button("Neutral", C(926, y + 5, 80, 26), () => SetRelation(a, b, 0), clip: clip, size: 12);
                Button("Enemy", C(1012, y + 5, 80, 26), () => SetRelation(a, b, -100), clip: clip, size: 12);
                Rect(C(212, y + 35, 888, 1), MainInk with { W = 0.075f }, clip);
            }
            y += 36;
        }
        return y + 12;
    }

    void SetRelation(int a, int b, float value)
    {
        Send(new CheatSetRelation(a, b, value, bothWays) { Tick = Now });
        Notice($"{FactionName(a)} {(bothWays ? "and" : "towards")} {FactionName(b)}: {Number(value)}.");
    }

    // ------------------------------------------------------------------ World

    List<GameRecord>? spawnCache;
    string spawnPick = "";
    int spawnSide;

    float DrawWorld()
    {
        var clip = viewport;
        Header("World", 0);
        Card(C(212, 46, 434, 96), clip);
        Label("MAP AND RESEARCH", C(226, 54, 300, 18), clip);
        Button("Reveal the whole map", C(226, 80, 200, 36), () => Send(new CheatRevealMap() { Tick = Now }), clip: clip);
        Button("Finish all research", C(432, 80, 200, 36), () => { Send(new DebugUnlockResearch() { Tick = Now }); Notice("Every research finished."); }, clip: clip);
        Card(C(666, 46, 434, 96), clip);
        Label("TELEPORT TO THE CAMERA", C(680, 54, 300, 18), clip);
        var selection = world().Player.Selection.ToArray();
        Button("Selected characters", C(680, 80, 200, 36), () => Teleport(selection), clip: clip, enabled: selection.Length > 0);
        Button("Their whole squad", C(886, 80, 200, 36), () =>
        {
            var squad = world().Squads.Find(Cold(selection.FirstOrDefault())?.SquadId ?? -1);
            if (squad is not null) Teleport([.. squad.Members]);
        }, clip: clip, enabled: selection.Length > 0);

        Card(C(212, 156, 888, 300), clip);
        Label("SPAWN A CHARACTER AT THE CAMERA", C(226, 164, 400, 18), clip);
        EditField(spawnSearch, C(226, 190, 380, 30), "Search characters and animals", clip);
        Segmented(C(616, 192, 260, 26), ["Hostile", "Its own faction"], spawnSide, i => spawnSide = i, clip);
        Button("Spawn", C(886, 190, 200, 30), Spawn, primary: true, clip: clip, enabled: spawnPick.Length > 0);
        var matches = SpawnMatches();
        float y = 230;
        for (int i = 0; i < Math.Min(matches.Count, 14); i++)
        {
            var r = matches[i];
            int col = i % 2;
            var at = C(226 + col * 430, y + (i / 2) * 30, 420, 28);
            bool picked = r.StringId == spawnPick;
            if (picked) Rect(at, Brass with { W = 0.16f }, clip);
            else if (Hovered(at, clip)) Rect(at, MainInk with { W = 0.06f }, clip);
            Text($"{r.Name}  ·  {(r.Type == FcsRecordType.ANIMAL_CHARACTER ? "animal" : "character")} · {Source(r)}", at with { X = at.X + 8 * scale }, 13, picked ? LightInk : MainInk, clip: clip);
            var id = r.StringId;
            if (Clickable(at, clip)) hits.Add(new(at, () => spawnPick = id));
        }
        if (matches.Count == 0) Text(spawnSearch.Text.Trim().Length == 0 ? "Type part of a name." : "Nothing matches.", C(226, y, 600, 28), 13, MutedInk, clip: clip);
        return 470;
    }

    List<GameRecord> SpawnMatches()
    {
        string search = spawnSearch.Text.Trim();
        if (spawnCache is not null) return spawnCache;
        if (search.Length == 0 || database() is not { } db) return spawnCache = [];
        spawnCache = [.. db.OfType(FcsRecordType.CHARACTER).Concat(db.OfType(FcsRecordType.ANIMAL_CHARACTER))
            .Where(r => r.Name.Contains(search, StringComparison.OrdinalIgnoreCase))
            .OrderBy(r => !r.Name.StartsWith(search, StringComparison.OrdinalIgnoreCase)).ThenBy(r => r.Name.Length).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase).Take(14)];
        return spawnCache;
    }

    void Spawn()
    {
        if (database()?.Find(spawnPick) is not { } record) return;
        int? faction = null;
        if (spawnSide == 1 && population() is { } data)
        {
            var own = record.GetReferences("faction").Select(r => data.Relations.IndexOf(r.TargetStringId)).FirstOrDefault(i => i >= 0, -1);
            if (own >= 0) faction = own;
        }
        var at = cameraTarget();
        Send(new DebugSpawn(record.StringId, new Vector2(at.X, at.Z), faction) { Tick = Now });
        Notice($"Spawned {record.Name}{(faction is { } f ? $" ({FactionName(f)})" : " (hostile)")}.");
    }
}
