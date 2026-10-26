using System.Globalization;
using System.Numerics;
using Meitou.Data.Fcs;
using Meitou.Data.Ui;
using Meitou.Rendering.Ui;
using Meitou.Simulation;
using Meitou.Simulation.Autonomy;

namespace Meitou.Game;

// The Squad Autonomy window (the HUD's AUT button; Meitou's overhaul of the RE_Kenshi plugin Squad Autonomy's panel, docs/simulation.md "Squad autonomy"): a role for the squad
// (presets of the plugin's packages), its places and hours, labour priorities and combat options. Drawn with the cheat menu's kit; the settings go to the world whole
// (SetSquadAutonomy), and the AI runs the squad from them.
sealed partial class CheatMenu
{
    enum AutPage { Squad, Priorities, Buildings, Schedule, Advanced, AdvancedCombat }
    static readonly string[] AutPageNames = ["Squad", "Priorities", "Buildings", "Schedule", "Advanced", "Advanced: combat"];

    bool autoMode;
    AutPage autPage;
    int autSquad = -1;
    /// <summary>Settings sent and not yet in the snapshot (shown meanwhile, so the window does not flicker back).</summary>
    (int Squad, AutonomySettings Settings)? autPending;
    /// <summary>Pick in world: the squad, home (true) or work, and the building selected when it started.</summary>
    (int Squad, bool Home, string? Before)? picking;
    UiRect pickBanner;

    sealed record Role(string Name, string Blurb, (string Package, int Slot)[] Packages, Func<AutonomySettings, AutonomySettings> Tweak);

    static readonly Role[] Roles =
    [
        new("Workers", "Farm, mine, cook and run machines at their outpost. Rest after work.", [("Labour at home", 0)],
            s => s with { StartWork = 6, EndWork = 18, DoSleep = true }),
        new("Gate Guards", "Hold the gate you set as their work place. Can keep it shut.", [("Guard gate", 0)], s => s),
        new("Town Guards", "Patrol and stand guard around your outpost.", [("Guard town", 0)], s => s),
        new("Turret Crew", "Man your turrets during work hours.", [("Man Turrets", 0)], s => s with { ManTurrets = true }),
        new("Building Guards", "Stand guard at the building you set as their work place.", [("Guard building", 0)], s => s),
        new("Wanderers", "Travel town to town, hang out at bars, sleep at inns. Set no places.", [("Wandering from town to town", 1), ("Hang out in a town", 0)],
            s => s with { Home = null, Work = null, UsePaidBeds = true }),
        new("Bar Regulars", "Stay in the town they're in and hang out at its bar.", [("Hang out in a town", 0)], s => s),
        new("Solo Squad", "Follow the leader, protect each other, first aid, rescue.", [("Solo Squad", 0)], s => s),
        new("Stay Home", "Relax at home and sleep there.", [("Relax at home", 0)], s => s with { StartWork = 0, EndWork = 0, DoSleep = true }),
    ];

    static readonly (LabourKind Kind, string Name)[] LabourRows =
    [
        (LabourKind.Labouring, "Mining"), (LabourKind.Farming, "Farming"), (LabourKind.Cooking, "Cooking"), (LabourKind.Science, "Research"), (LabourKind.Engineer, "Engineering"),
        (LabourKind.Robotics, "Robotics"), (LabourKind.Medic, "Medical"), (LabourKind.WeaponSmith, "Weapon smithing"), (LabourKind.ArmourSmith, "Armour smithing"),
        (LabourKind.CrossbowSmith, "Crossbow smithing"), (LabourKind.AutomaticMachine, "Automatic machines"), (LabourKind.Other, "Other"),
    ];

    /// <summary>Opens the window for a player squad (the HUD's AUT button: the active squad); pressed again for the same squad it closes.</summary>
    public void OpenAutonomy(int squadId)
    {
        if (visible && autoMode && autSquad == squadId && picking is null && assigning is null) { Visible = false; return; }
        Visible = false;
        (autoMode, townsMode, autSquad, picking) = (true, false, squadId, null); assigning = null;
        Visible = true;
    }

    WorldSnapshot Snap => player.Snapshot;

    SquadView? AutSquadView() => Snap.PlayerSquads.FirstOrDefault(s => s.Id == autSquad) ?? Snap.PlayerSquads.FirstOrDefault();

    AutonomySettings AutSettings(SquadView squad)
    {
        if (autPending is { } p && p.Squad == squad.Id)
        {
            if (ReferenceEquals(squad.Autonomy, p.Settings)) autPending = null;
            else return p.Settings;
        }
        return squad.Autonomy ?? new AutonomySettings();
    }

    void AutSend(SquadView squad, AutonomySettings settings)
    {
        autPending = (squad.Id, settings);
        world().Commands.Enqueue(new SetSquadAutonomy(squad.Id, settings) { Tick = Now });
    }

    /// <summary>The offered packages (the plugin's list: names ending in "&lt;SquadAutonomy&gt;"), then every other AI package, by name; the id and the shown name.</summary>
    IReadOnlyList<(string Id, string Name)> OfferedPackages()
    {
        if (offered is not null || database() is not { } db) return offered ?? [];
        var all = db.OfType(FcsRecordType.AI_PACKAGE).Select(r => (r.StringId, r.Name)).ToList();
        offered = [.. all.Where(p => p.Name.EndsWith(AutonomyRules.Suffix, StringComparison.Ordinal)).OrderBy(p => p.Name, StringComparer.Ordinal)
            .Concat(all.Where(p => !p.Name.EndsWith(AutonomyRules.Suffix, StringComparison.Ordinal)).OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))];
        return offered;
    }
    IReadOnlyList<(string Id, string Name)>? offered;

    static string Shown(string name) => name.EndsWith(AutonomyRules.Suffix, StringComparison.Ordinal) ? name[..^AutonomyRules.Suffix.Length] : name;

    string PackageName(string id) => OfferedPackages().FirstOrDefault(p => p.Id == id) is { Id: not null } p ? Shown(p.Name) : id;

    /// <summary>A role's packages as ids (null when the Squad Autonomy mod's packages are not loaded).</summary>
    IReadOnlyList<AutonomyPackage>? RolePackages(Role role)
    {
        var list = new List<AutonomyPackage>();
        foreach (var (name, slot) in role.Packages)
        {
            if (OfferedPackages().FirstOrDefault(p => p.Name == name + AutonomyRules.Suffix) is not { Id: not null } found) return null;
            list.Add(new AutonomyPackage(found.Id, slot));
        }
        return list;
    }

    bool SamePackages(IReadOnlyList<AutonomyPackage> a, IReadOnlyList<AutonomyPackage>? b) =>
        b is not null && a.Count == b.Count && a.OrderBy(p => p.Id, StringComparer.Ordinal).ThenBy(p => p.Slot).SequenceEqual(b.OrderBy(p => p.Id, StringComparer.Ordinal).ThenBy(p => p.Slot));

    // ------------------------------------------------------------------ the window

    /// <summary>Where the window was dragged to (its top left, pixels), and the pointer's offset in it while its title strip is held.</summary>
    Vector2? autPlace, autGrab;
    /// <summary>The window's background opacity (the world shows through a little).</summary>
    const float WindowAlpha = 0.86f;

    /// <summary>Moves the window while its title strip is held; true while dragging.</summary>
    bool AutDrag(Vector2 at)
    {
        if (autGrab is not { } grab) return false;
        autPlace = at - grab;
        return true;
    }

    void DrawAutonomyWindow(int width, int height, UiPainter painter)
    {
        const float W = 1080, H = 700;
        scale = Math.Max(0.35f, Math.Min(1.25f, Math.Min(height / 900f, width / 1280f)));
        controls.Clear(); hits.Clear();
        if (picking is { } pick) { DrawPicking(width, height, painter, pick); return; }
        if (assigning is { } assign) { DrawAssigning(width, height, painter, assign); return; }
        // Meitou: near the top of the screen (clear of the HUD's bottom panels) unless dragged by its title strip; the place is kept while the game runs.
        float ww = MathF.Round(W * scale), wh = MathF.Round(H * scale), top = MathF.Round(16 * scale);
        var place = autPlace ?? new Vector2((width - ww) / 2, top);
        place = new(Math.Clamp(place.X, 40 * scale - ww, width - 40 * scale), Math.Clamp(place.Y, 0, height - 44 * scale));
        bounds = new(MathF.Round(place.X), MathF.Round(place.Y), ww, wh);
        Surface(bounds, "window", alpha: WindowAlpha);
        Text("Squad Autonomy", R(20, 6, 400, 32), 22, LightInk);
        hits.Add(new(R(0, 0, W - 60, 44), () => autGrab = new(pointer.X - bounds.X, pointer.Y - bounds.Y)));
        var close = R(W - 51, 6, 31, 32);
        controls.Add(new("aut-close", "Button", close, "", "aut:close", "Kenshi_CloseButtonSkin", true, new Dictionary<string, string>(), Vector4.Zero));
        hits.Add(new(close, () => Visible = false));
        Rect(R(20, 44, W - 40, 1), MainInk with { W = 0.12f });

        if (AutSquadView() is not { } squad)
        {
            Text("You have no squads.", R(20, 60, 600, 30), 16, MutedInk);
            painter.Draw(controls, width, height, pointer, null);
            return;
        }
        autSquad = squad.Id;
        var s = AutSettings(squad);

        // Squad, status, the switch.
        var squads = Snap.PlayerSquads;
        int index = Math.Max(0, squads.ToList().FindIndex(q => q.Id == squad.Id));
        DropdownButton("aut-squad", R(20, 56, 250, 40), [.. squads.Select(q => $"{q.Name}  ({q.Members.Count})")], index, k => { autSquad = squads[k].Id; autPending = null; });
        Text(StatusLine(squad, s), R(286, 56, 560, 40), 12, MutedInk);
        Toggle(R(W - 200, 60, 180, 32), "Autonomy", s.Enabled, () => AutSend(squad, s with { Enabled = !s.Enabled }));
        Rect(R(20, 108, W - 40, 1), MainInk with { W = 0.12f });

        for (int i = 0; i < AutPageNames.Length; i++)
        {
            int k = i;
            var at = R(20, 120 + i * 40, 176, 34);
            Button("", at, () => { autPage = (AutPage)k; openDropdown = null; focused = null; }, autPage == (AutPage)i);
            if (autPage == (AutPage)i) Rect(R(20, 120 + i * 40, 3, 34), Brass);
            Text(AutPageNames[i], R(36, 120 + i * 40, 150, 34), 14, autPage == (AutPage)i ? LightInk : MainInk);
        }
        Text("Your orders always come first. When an order is done they go back to their routine.", R(24, 560, 168, 80), 11, MutedInk, "Left Bottom");
        Rect(R(212, 120, 1, 528), MainInk with { W = 0.12f });

        const float X = 232, CW = W - 252;
        switch (autPage)
        {
            case AutPage.Squad: DrawSquadPage(squad, s, X, CW); break;
            case AutPage.Priorities: DrawPrioritiesPage(squad, s, X, CW); break;
            case AutPage.Buildings: DrawBuildingsPage(squad, s, X, CW); break;
            case AutPage.Schedule: DrawPlacesPage(squad, s, X, CW); break;
            case AutPage.Advanced: DrawRolePage(squad, s, X, CW); break;
            default: DrawCombatPage(squad, s, X, CW); break;
        }

        Rect(R(20, H - 46, W - 40, 1), MainInk with { W = 0.12f });
        string hint = notice.Length > 0 && Environment.TickCount64 - noticeAt < 8000 ? notice
            : !s.Enabled ? "Autonomy is off: they only do what you order, as in plain Kenshi. Turn it on (top right) and their manager runs them."
            : s.AiRuns ? "The old mod's behaviours run this squad (Advanced). Clear them for the manager to run it." : "Changes apply right away.";
        Text(hint, R(20, H - 38, 560, 28), 12, Brass);
        var others = squads.Where(q => q.Id != squad.Id).ToList();
        if (others.Count > 0)
            DropdownButton("aut-copy", R(W - 470, H - 38, 300, 28), [.. others.Select(q => q.Name)], -1, k =>
            {
                var to = others[k];
                world().Commands.Enqueue(new SetSquadAutonomy(to.Id, s with { Home = s.Home, Work = s.Work }) { Tick = Now });
                Notice($"Copied to {to.Name}.");
            }, caption: "Copy settings to another squad...");
        Button("Done", R(W - 140, H - 38, 120, 28), () => Visible = false, primary: true);
        if (openDropdown is { } open) DrawDropdown(open);
        painter.Draw(controls, width, height, pointer, null);
    }

    string StatusLine(SquadView squad, AutonomySettings s)
    {
        if (!s.Enabled) return $"{squad.Members.Count} member{(squad.Members.Count == 1 ? "" : "s")} · autonomy off";
        if (s.ManagerRuns)
        {
            var doing = squad.Managed.GroupBy(d => d.Doing.Split(':')[0]).OrderByDescending(g => g.Count()).Take(3).Select(g => $"{g.Count()} {g.Key}");
            int a = (int)s.StartWork, b = (int)s.EndWork;
            string hours = a == b ? "never works (Schedule)" : a == 0 && b == 24 ? "works all day" : $"works {a:00}:00-{b % 24:00}:00";
            return $"Run by {ManagerName(squad, MgmtSettings(squad))} · {hours} · " + string.Join(" · ", doing);
        }
        if (squad.Doing.Count == 0) return "Starting up...";
        var parts = squad.Doing.GroupBy(d => d).OrderByDescending(g => g.Count()).Select(g => $"{g.Count()} {g.Key}");
        return "Now: " + string.Join(" · ", parts);
    }

    /// <summary>An on/off switch with its label, the brass knob right when on.</summary>
    void Toggle(UiRect at, string label, bool on, Action flip)
    {
        float sw = 46 * scale, sh = 24 * scale;
        var track = new UiRect(at.X + at.Width - sw, at.Y + (at.Height - sh) / 2, sw, sh);
        Text(label, at with { Width = at.Width - sw - 10 * scale }, 15, LightInk, "Right VCenter");
        Rect(track, on ? Brass with { W = 0.35f } : new Vector4(0.07f, 0.055f, 0.045f, 1));
        Outline(track, on ? Brass : MainInk with { W = 0.2f });
        float knob = 18 * scale;
        Rect(new(on ? track.X + track.Width - knob - 3 * scale : track.X + 3 * scale, track.Y + 3 * scale, knob, knob), on ? LightInk : MutedInk);
        hits.Add(new(at, flip));
    }

    /// <summary>A tick box with its label and a one-line hint below.</summary>
    void Check(UiRect at, string label, string hint, bool on, Action flip, bool enabled = true)
    {
        var box = new UiRect(at.X, at.Y + 4 * scale, 16 * scale, 16 * scale);
        Rect(box, on ? Brass : new Vector4(0.07f, 0.055f, 0.045f, 1));
        Outline(box, on ? PaperInk : MainInk with { W = 0.35f });
        Text(label, new(at.X + 26 * scale, at.Y, at.Width - 26 * scale, 22 * scale), 14, enabled ? Hovered(at, null) ? LightInk : MainInk : MutedInk);
        Text(hint, new(at.X + 26 * scale, at.Y + 20 * scale, at.Width - 26 * scale, 18 * scale), 11, MutedInk);
        if (enabled) hits.Add(new(at, flip));
    }

    void Header(string title, float x, float y, float w)
    {
        var at = R(x, y, w, 34);
        Surface(at, "paper");
        Text(title, new(at.X + 12 * scale, at.Y, at.Width - 24 * scale, at.Height), 17, MeitouLook ? PaperInk : LightInk);
    }

    void CardTitle(string title, float x, float y) => Text(title.ToUpperInvariant(), R(x, y, 400, 18), 11, MutedInk);

    // ------------------------------------------------------------------ pages

    void DrawRolePage(SquadView squad, AutonomySettings s, float x, float w)
    {
        Header("Advanced: the old Squad Autonomy mod's behaviours (they replace the manager)", x, 120, w);
        bool modLoaded = RolePackages(Roles[0]) is not null;
        float cw = (w - 20) / 3, ch = 84;
        for (int i = 0; i < Roles.Length; i++)
        {
            var role = Roles[i];
            var packages = RolePackages(role);
            var at = R(x + (i % 3) * (cw + 10), 166 + (i / 3) * (ch + 10), cw, ch);
            bool chosen = SamePackages(s.Packages, packages);
            bool hover = packages is not null && Hovered(at, null);
            Rect(at, chosen ? Brass with { W = 0.12f } : new Vector4(0, 0, 0, hover ? 0.28f : 0.18f));
            Outline(at, chosen ? Brass : MainInk with { W = hover ? 0.35f : 0.12f });
            if (chosen) Rect(at with { Width = 3 * scale }, Brass);
            Text(role.Name, new(at.X + 14 * scale, at.Y + 8 * scale, at.Width - 28 * scale, 22 * scale), 15, packages is null ? MutedInk : LightInk);
            Text(role.Blurb, new(at.X + 14 * scale, at.Y + 32 * scale, at.Width - 28 * scale, 44 * scale), 11, MutedInk, "Left Top");
            if (packages is not null)
                hits.Add(new(at, () =>
                {
                    AutSend(squad, role.Tweak(s with { Packages = packages, Enabled = true }));
                    Notice($"{squad.Name}: {role.Name}." + (role.Name is "Gate Guards" or "Building Guards" && s.Work is null ? " Set their work place on Places & Hours." : ""));
                }));
        }
        if (!modLoaded) Text("Turn on the Squad Autonomy mod in the launcher's mod list: its behaviours come from it.", R(x, 448, w, 20), 12, Bad);

        // The packages themselves (advanced).
        float top = 470;
        Card(R(x, top, w, 178));
        CardTitle("Behaviours (advanced)", x + 14, top + 10);
        float y = top + 32;
        var list = s.Packages.OrderByDescending(p => p.Slot).ToList();
        if (list.Count == 0) Text("None yet: pick a role above, or add a behaviour.", R(x + 14, y, w - 28, 26), 13, MutedInk);
        foreach (var p in list.Take(3))
        {
            Text(PackageName(p.Id), R(x + 14, y, w - 260, 26), 14, MainInk);
            Button("-", R(x + w - 230, y + 1, 26, 24), () => AutSend(squad, s with { Packages = [.. s.Packages.Select(q => q == p ? q with { Slot = Math.Max(0, q.Slot - 1) } : q)] }), enabled: p.Slot > 0);
            Text($"Priority {p.Slot}", R(x + w - 200, y, 90, 26), 13, LightInk, "Center");
            Button("+", R(x + w - 106, y + 1, 26, 24), () => AutSend(squad, s with { Packages = [.. s.Packages.Select(q => q == p ? q with { Slot = Math.Min(5, q.Slot + 1) } : q)] }), enabled: p.Slot < 5);
            Button("x", R(x + w - 66, y + 1, 26, 24), () => AutSend(squad, s with { Packages = [.. s.Packages.Where(q => q != p)] }));
            y += 30;
        }
        if (list.Count > 3) Text($"and {list.Count - 3} more", R(x + 14, y, 300, 20), 12, MutedInk);
        var offeredList = OfferedPackages();
        DropdownButton("aut-add", R(x + 14, top + 140, w - 28 - 150, 28), [.. offeredList.Select(p => Shown(p.Name))], -1, k =>
        {
            var chosen = offeredList[k];
            if (s.Packages.Any(p => p.Id == chosen.Id)) return;
            AutSend(squad, s with { Packages = [.. s.Packages, new AutonomyPackage(chosen.Id, s.Packages.Count == 0 ? 0 : Math.Min(5, s.Packages.Max(p => p.Slot) + 1))] });
        }, caption: "Add a behaviour...");
        Button("Clear all", R(x + w - 150, top + 140, 136, 28), () => AutSend(squad, s with { Packages = [] }), enabled: s.Packages.Count > 0);
    }

    void DrawPlacesPage(SquadView squad, AutonomySettings s, float x, float w)
    {
        Header("Places & Hours", x, 120, w);
        float left = (w - 14) * 0.58f, right = w - 14 - left;
        Card(R(x, 166, left, 196));
        CardTitle("Places", x + 14, 176);
        PlaceRow(squad, s, true, x + 14, 200, left - 28);
        PlaceRow(squad, s, false, x + 14, 266, left - 28);
        Text("Home: where they rest and sleep. Work: where they spend work hours (a gate for gate guards).", R(x + 14, 334, left - 28, 26), 11, MutedInk, "Left Top");

        float rx = x + left + 14;
        Card(R(rx, 166, right, 196));
        CardTitle("Health", rx + 14, 176);
        Check(R(rx + 14, 198, right - 28, 42), "Rest until healed", "Wounded members go to bed, even in work hours.", s.RestUntilHealed, () => AutSend(squad, s with { RestUntilHealed = !s.RestUntilHealed }));
        PercentRow(squad, s, "Go to bed at", "aut-rest", s.RestThreshold, v => s with { RestThreshold = v }, rx + 14, 252, right - 28, s.RestUntilHealed);
        PercentRow(squad, s, "Back to work at", "aut-healed", s.HealedThreshold, v => s with { HealedThreshold = v }, rx + 14, 296, right - 28, s.RestUntilHealed);

        Card(R(x, 376, w, 272));
        CardTitle("Work hours", x + 14, 386);
        DayBar(s, R(x + 14, 410, w - 28, 42));
        for (int h = 0; h <= 24; h += 3)
            Text(h.ToString(CultureInfo.InvariantCulture), R(x + 14 + (w - 28) * h / 24f - 15, 456, 30, 16), 11, MutedInk, "Center");
        Text("Start", R(x + 14, 480, 60, 26), 13, MainInk);
        float startF = Slider("aut-start", R(x + 80, 480, w / 2 - 110, 26), s.StartWork / 24f, f => AutSend(squad, s with { StartWork = MathF.Round(f * 24) }));
        Text(AutonomyRules.Hours(MathF.Round(startF * 24)), R(x + w / 2 - 26, 480, 50, 26), 13, LightInk, "Right VCenter");
        Text("End", R(x + w / 2 + 34, 480, 50, 26), 13, MainInk);
        float endF = Slider("aut-end", R(x + w / 2 + 84, 480, w / 2 - 152, 26), s.EndWork / 24f, f => AutSend(squad, s with { EndWork = MathF.Round(f * 24) }));
        Text(AutonomyRules.Hours(MathF.Round(endF * 24)), R(x + w - 64, 480, 50, 26), 13, LightInk, "Right VCenter");
        Button("All day", R(x + 14, 516, 110, 26), () => AutSend(squad, s with { StartWork = 0, EndWork = 24 }), selected: (int)s.StartWork == 0 && (int)s.EndWork == 24, size: 12);
        Button("Day shift", R(x + 132, 516, 110, 26), () => AutSend(squad, s with { StartWork = 6, EndWork = 18 }), selected: (int)s.StartWork == 6 && (int)s.EndWork == 18, size: 12);
        Button("Night shift", R(x + 250, 516, 110, 26), () => AutSend(squad, s with { StartWork = 18, EndWork = 6 }), selected: (int)s.StartWork == 18 && (int)s.EndWork == 6, size: 12);
        Button("Never work", R(x + 368, 516, 110, 26), () => AutSend(squad, s with { StartWork = 0, EndWork = 0 }), selected: (int)s.StartWork == (int)s.EndWork, size: 12);
        Check(R(x + 14, 560, w / 2 - 28, 42), "Sleep outside work hours", "Otherwise they relax at home.", s.DoSleep, () => AutSend(squad, s with { DoSleep = !s.DoSleep }));
        Check(R(x + w / 2, 560, w / 2 - 14, 42), "Use paid beds", "Allow beds of other factions (inns, bars).", s.UsePaidBeds, () => AutSend(squad, s with { UsePaidBeds = !s.UsePaidBeds }));
    }

    void PlaceRow(SquadView squad, AutonomySettings s, bool home, float x, float y, float w)
    {
        string? key = home ? s.Home : s.Work;
        Text(home ? "Home" : "Work", R(x, y, 60, 24), 15, LightInk);
        string name = key is null ? home ? "None (they use the town they are in)" : "None (their home)" : player.PlaceName(key, population()) ?? "Gone";
        Text(name, R(x + 64, y, w - 64, 24), 14, key is null ? MutedInk : LightInk);
        Button("Pick in world", R(x + 64, y + 28, 120, 28), () => { picking = (squad.Id, home, player.SelectedBuildingInstance); openDropdown = null; }, size: 12);
        string? selected = player.SelectedBuildingInstance;
        Button("Use selected", R(x + 192, y + 28, 110, 28), () => SetPlace(squad, s, home, selected!), enabled: selected is not null, size: 12);
        Button("Clear", R(x + 310, y + 28, 70, 28), () => AutSend(squad, home ? s with { Home = null } : s with { Work = null }), enabled: key is not null, size: 12);
    }

    void SetPlace(SquadView squad, AutonomySettings s, bool home, string instance)
    {
        AutSend(squad, home ? s with { Home = instance } : s with { Work = instance });
        Notice($"{squad.Name} {(home ? "home" : "work place")}: {player.PlaceName(instance, population()) ?? instance}.");
    }

    void PercentRow(SquadView squad, AutonomySettings s, string label, string id, float value, Func<float, AutonomySettings> with, float x, float y, float w, bool enabled)
    {
        Text(label, R(x, y, 120, 26), 13, enabled ? MainInk : MutedInk);
        float f = Slider(id, R(x + 124, y, w - 124 - 92, 26), value / 100f, v => AutSend(squad, with(MathF.Round(v * 100))));
        Text($"{MathF.Round(f * 100):0}% health", R(x + w - 92, y, 92, 26), 13, LightInk, "Right VCenter");
    }

    /// <summary>The day as a bar: the work hours brass (wrapping round midnight), the hour now a green line.</summary>
    void DayBar(AutonomySettings s, UiRect at)
    {
        Rect(at, new(0.07f, 0.055f, 0.045f, 1));
        Outline(at, MainInk with { W = 0.14f });
        int a = (int)s.StartWork, b = (int)s.EndWork;
        void Block(int from, int to)
        {
            if (to <= from) return;
            var r = new UiRect(at.X + at.Width * from / 24f, at.Y, at.Width * (to - from) / 24f, at.Height);
            Rect(r, Brass with { W = 0.32f });
            Outline(r, Brass);
        }
        if (a == b) Text("Never works: always rest time", at, 12, MutedInk, "Center");
        else if (b > a) Block(a, b);
        else { Block(a, 24); Block(0, b); }
        if (a != b) Text($"Work {AutonomyRules.Hours(a)} – {AutonomyRules.Hours(b % 24 == 0 && b > 0 ? 24 : b)}", new(at.X + at.Width * (b > a ? a : 0) / 24f + 8 * scale, at.Y, 220 * scale, at.Height), 12, LightInk);
        float now = (float)((world().GameHours % 24 + 24) % 24);
        Rect(new(at.X + at.Width * now / 24f, at.Y - 2 * scale, 2 * scale, at.Height + 4 * scale), Good);
    }

    /// <summary>The squad's labour priorities from its members' best skills: each group 1 + its best member's level / 10 (engine choice for a quick start).</summary>
    IReadOnlyList<float> BestSkills(SquadView squad)
    {
        var stat = new Dictionary<LabourKind, Meitou.Data.Gameplay.Bodies.StatsEnumerated>
        {
            [LabourKind.Science] = Meitou.Data.Gameplay.Bodies.StatsEnumerated.Science, [LabourKind.Labouring] = Meitou.Data.Gameplay.Bodies.StatsEnumerated.Labouring,
            [LabourKind.Farming] = Meitou.Data.Gameplay.Bodies.StatsEnumerated.Farming, [LabourKind.Engineer] = Meitou.Data.Gameplay.Bodies.StatsEnumerated.Engineering,
            [LabourKind.Cooking] = Meitou.Data.Gameplay.Bodies.StatsEnumerated.Cooking, [LabourKind.Robotics] = Meitou.Data.Gameplay.Bodies.StatsEnumerated.Robotics,
            [LabourKind.Medic] = Meitou.Data.Gameplay.Bodies.StatsEnumerated.Medic, [LabourKind.WeaponSmith] = Meitou.Data.Gameplay.Bodies.StatsEnumerated.SmithingWeapon,
            [LabourKind.ArmourSmith] = Meitou.Data.Gameplay.Bodies.StatsEnumerated.SmithingArmour, [LabourKind.CrossbowSmith] = Meitou.Data.Gameplay.Bodies.StatsEnumerated.SmithingBow,
        };
        var result = new float[12];
        for (int k = 0; k < 12; k++)
        {
            if (!stat.TryGetValue((LabourKind)k, out var st)) { result[k] = 1; continue; }
            float best = 0;
            foreach (var id in squad.Members)
                if (Cold(id)?.Stats is { } stats) best = MathF.Max(best, stats[st]);
            result[k] = Math.Clamp(1 + MathF.Floor(best / 10), 1, 10);
        }
        return result;
    }

    void DrawCombatPage(SquadView squad, AutonomySettings s, float x, float w)
    {
        Header("Combat & Guard", x, 120, w);
        float half = (w - 14) / 2;
        Card(R(x, 166, half, 260));
        CardTitle("Combat", x + 14, 176);
        Check(R(x + 14, 200, half - 28, 44), "Attack enemies", "Go after enemies near their place (not in rest hours).", s.AttackEnemies, () => AutSend(squad, s with { AttackEnemies = !s.AttackEnemies }));
        Check(R(x + 14, 252, half - 28, 44), "Protect allies", "Help squadmates and allies who are attacked.", s.ProtectAllies, () => AutSend(squad, s with { ProtectAllies = !s.ProtectAllies }));
        Check(R(x + 14, 304, half - 28, 44), "First aid", "Patch up wounded squadmates (and repair skeletons).", s.DoMedic, () => AutSend(squad, s with { DoMedic = !s.DoMedic }));
        Check(R(x + 14, 356, half - 28, 44), "Rescue downed allies", "Carry knocked-out squadmates to a bed.", s.DoRescue, () => AutSend(squad, s with { DoRescue = !s.DoRescue }));
        float gx = x + half + 14;
        Card(R(gx, 166, half, 260));
        CardTitle("Guard", gx + 14, 176);
        Check(R(gx + 14, 200, half - 28, 44), "Man turrets", "Use your turrets during work hours.", s.ManTurrets, () => AutSend(squad, s with { ManTurrets = !s.ManTurrets }));
        Check(R(gx + 14, 252, half - 28, 44), "Stay inside the gate", "Gate guards stand on the inside.", s.StayInsideGate, () => AutSend(squad, s with { StayInsideGate = !s.StayInsideGate }));
        Check(R(gx + 14, 304, half - 28, 44), "Keep the gate shut", "Close it; open it for your people passing.", s.CloseGate, () => AutSend(squad, s with { CloseGate = !s.CloseGate }));
        Text("These apply to the behaviours that use them (for example the gate options to Gate Guards).", R(x, 440, w, 20), 12, MutedInk);
    }

    // ------------------------------------------------------------------ the manager (Meitou-only, ManagerSystem)

    /// <summary>Management sent and not yet in the snapshot (shown meanwhile).</summary>
    (int Squad, Meitou.Simulation.Management.ManagementSettings? Settings, long At)? mgmtPending;

    Meitou.Simulation.Management.ManagementSettings? MgmtSettings(SquadView squad)
    {
        if (mgmtPending is { } p && p.Squad == squad.Id)
        {
            bool arrived = p.Settings is null ? squad.Management is null
                : squad.Management is { } m && m.Manager == p.Settings.Manager && m.Defence == p.Settings.Defence && m.Build == p.Settings.Build
                    && m.BuildPriority.SequenceEqual(p.Settings.BuildPriority) && m.Buildings.SequenceEqual(p.Settings.Buildings);
            if (arrived || Environment.TickCount64 - p.At > 3000) mgmtPending = null;
            else return p.Settings;
        }
        return squad.Management;
    }

    void MgmtSend(SquadView squad, Meitou.Simulation.Management.ManagementSettings? settings)
    {
        mgmtPending = (squad.Id, settings, Environment.TickCount64);
        world().Commands.Enqueue(new Meitou.Simulation.Management.SetSquadManagement(squad.Id, settings) { Tick = Now });
    }

    /// <summary>The squad's manager as shown: the one picked, else its leader.</summary>
    string ManagerName(SquadView squad, Meitou.Simulation.Management.ManagementSettings? m) =>
        m is not null && !m.Manager.IsNone && Snap.Characters.FirstOrDefault(c => c.Id == m.Manager)?.Name is { Length: > 0 } name ? name
        : m is { ManagerName.Length: > 0 } && m.Manager.IsNone ? m.ManagerName
        : Snap.Characters.FirstOrDefault(c => c.Id == squad.Leader)?.Name ?? "the leader";

    static readonly (Meitou.Simulation.Management.DefenceMode Mode, string Name, string Blurb)[] DefenceModes =
    [
        (Meitou.Simulation.Management.DefenceMode.Fighters, "Fighters", "Everyone drops their work and takes on enemies in the outpost."),
        (Meitou.Simulation.Management.DefenceMode.Militia, "Militia", "The men take up arms; the women go inside a building until it's over."),
        (Meitou.Simulation.Management.DefenceMode.Shelter, "Shelter", "Everyone goes inside a building until the enemies are gone."),
    ];

    static readonly (Meitou.Simulation.Management.BuildGroup Group, string Name, string Examples)[] BuildRows =
    [
        (Meitou.Simulation.Management.BuildGroup.Defence, "Defence", "walls, gates, turrets"),
        (Meitou.Simulation.Management.BuildGroup.Food, "Food & water", "farms, wells, stills"),
        (Meitou.Simulation.Management.BuildGroup.Power, "Power", "generators, batteries"),
        (Meitou.Simulation.Management.BuildGroup.Housing, "Housing", "houses, beds"),
        (Meitou.Simulation.Management.BuildGroup.Storage, "Storage", "boxes and stores"),
        (Meitou.Simulation.Management.BuildGroup.Industry, "Industry", "mining and crafting"),
        (Meitou.Simulation.Management.BuildGroup.Research, "Research", "research benches"),
        (Meitou.Simulation.Management.BuildGroup.Other, "Other", "lights, furniture, doors, signs"),
    ];

    // ------------------------------------------------------------------ the Squad and Priorities pages (Meitou's autonomy: the manager runs the squad)

    sealed record WorkRole(string Name, string Blurb, float[] Labour, float[] Build, bool Turrets, Meitou.Simulation.Management.DefenceMode? Defence = null, int Material = 0);

    // Labour order: Science, Labouring, Farming, Engineer, Cooking, Robotics, Medic, WeaponSmith, ArmourSmith, CrossbowSmith, AutomaticMachine, Other. Build order: BuildRows.
    static readonly WorkRole[] WorkRoles =
    [
        new("Engineers", "Only build and repair what you place, fetching the materials. Nothing else.", [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0], [9, 9, 9, 9, 9, 9, 9, 6], false),
        new("Workers", "Run every machine in the outpost. Build when nothing else needs doing.", [5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5], [3, 3, 3, 3, 3, 3, 3, 2], false),
        new("Farmers", "Plant, tend and harvest the farms; help cook.", [0, 0, 10, 0, 6, 0, 0, 0, 0, 0, 3, 0], [0, 0, 0, 0, 0, 0, 0, 0], false),
        new("Cooks", "Keep the stoves and ovens going; help on the farms.", [0, 0, 4, 0, 10, 0, 0, 0, 0, 0, 3, 0], [0, 0, 0, 0, 0, 0, 0, 0], false),
        new("Stone crew", "Stone only: mine it, process it into building materials.", [0, 10, 0, 0, 0, 0, 0, 0, 0, 0, 4, 0], [0, 0, 0, 0, 0, 0, 0, 0], false, Material: Meitou.Data.Gameplay.Construction.ResourceField.StoneResource),
        new("Iron crew", "Iron only: mine ore, refine plates, make steel.", [0, 10, 0, 0, 0, 0, 0, 0, 0, 0, 4, 0], [0, 0, 0, 0, 0, 0, 0, 0], false, Material: Meitou.Data.Gameplay.Construction.ResourceField.IronResource),
        new("Copper crew", "Copper only: mine it.", [0, 10, 0, 0, 0, 0, 0, 0, 0, 0, 4, 0], [0, 0, 0, 0, 0, 0, 0, 0], false, Material: Meitou.Data.Gameplay.Construction.ResourceField.CopperResource),
        new("Crafters", "Smith weapons, armour and crossbows; make parts.", [0, 0, 0, 3, 0, 3, 0, 10, 10, 10, 0, 0], [0, 0, 0, 0, 0, 0, 0, 0], false),
        new("Researchers", "Work the research benches.", [10, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0], [0, 0, 0, 0, 0, 0, 0, 0], false),
        new("Medics", "Run the medical benches.", [0, 0, 0, 0, 0, 0, 10, 0, 0, 0, 0, 0], [0, 0, 0, 0, 0, 0, 0, 0], false),
        new("Guards", "Man the turrets and fight off anyone who attacks the outpost.", [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0], [0, 0, 0, 0, 0, 0, 0, 0], true,
            Meitou.Simulation.Management.DefenceMode.Fighters),
    ];

    bool IsRole(WorkRole role, AutonomySettings s, Meitou.Simulation.Management.ManagementSettings m) =>
        s.Labour.Take(12).SequenceEqual(role.Labour) && m.BuildPriority.Take(8).SequenceEqual(role.Build) && s.ManTurrets == role.Turrets && m.Material == role.Material;

    /// <summary>The main page: what the squad does (a role sets the priorities), who runs it, what happens when attacked, and what each one is doing now.</summary>
    void DrawSquadPage(SquadView squad, AutonomySettings s, float x, float w)
    {
        var m = MgmtSettings(squad) ?? new Meitou.Simulation.Management.ManagementSettings();
        Header("What should this squad do?", x, 120, w);
        float cw = (w - 30) / 4, ch = 56;
        for (int i = 0; i < WorkRoles.Length; i++)
        {
            var role = WorkRoles[i];
            var at = R(x + (i % 4) * (cw + 10), 162 + (i / 4) * (ch + 8), cw, ch);
            bool chosen = s.Enabled && IsRole(role, s, m);
            bool hover = Hovered(at, null);
            Rect(at, chosen ? Brass with { W = 0.12f } : new Vector4(0, 0, 0, hover ? 0.28f : 0.18f));
            Outline(at, chosen ? Brass : MainInk with { W = hover ? 0.35f : 0.12f });
            if (chosen) Rect(at with { Width = 3 * scale }, Brass);
            Text(role.Name, new(at.X + 12 * scale, at.Y + 4 * scale, at.Width - 24 * scale, 22 * scale), 15, LightInk);
            Text(role.Blurb, new(at.X + 12 * scale, at.Y + 26 * scale, at.Width - 24 * scale, 28 * scale), 11, MutedInk, "Left Top");
            hits.Add(new(at, () =>
            {
                // A role turns autonomy on for the manager (clearing the old mod's behaviours) and sets every priority.
                AutSend(squad, s with { Enabled = true, Packages = [], Labour = role.Labour, ManTurrets = role.Turrets });
                MgmtSend(squad, m with { BuildPriority = role.Build, Build = role.Build.Any(v => v > 0) || m.Build, Defence = role.Defence ?? m.Defence, Material = role.Material });
                Notice($"{squad.Name}: {role.Name}. {ManagerName(squad, m)} runs them.");
            }));
        }

        // Who runs them, and when the outpost is attacked.
        float half = (w - 14) / 2, top = 362;
        Card(R(x, top, half, 86));
        CardTitle("Manager", x + 14, top + 8);
        var people = Snap.Characters.Where(c => c.IsPlayer).OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
        string leader = Snap.Characters.FirstOrDefault(c => c.Id == squad.Leader)?.Name ?? "none";
        int current = m.Manager.IsNone ? 0 : people.FindIndex(c => c.Id == m.Manager) + 1;
        DropdownButton("mgr-who", R(x + 14, top + 28, half - 28, 30), [$"Squad leader ({leader})", .. people.Select(c => c.Name)], Math.Max(0, current), k =>
        {
            var who = k == 0 ? CharacterId.None : people[k - 1].Id;
            MgmtSend(squad, m with { Manager = who, ManagerName = "", ManagerSquadIndex = -1 });
        });
        Text("He works too, and must be up and in your outpost for it to run.", R(x + 14, top + 62, half - 28, 20), 11, MutedInk);
        float gx = x + half + 14;
        Card(R(gx, top, half, 86));
        CardTitle("When the outpost is attacked", gx + 14, top + 8);
        float bw = (half - 28 - 16) / 3;
        for (int i = 0; i < DefenceModes.Length; i++)
        {
            var (mode, name, _) = DefenceModes[i];
            Button(name, R(gx + 14 + i * (bw + 8), top + 28, bw, 30), () => MgmtSend(squad, m with { Defence = mode }), selected: m.Defence == mode, size: 13);
        }
        Text(DefenceModes.First(d => d.Mode == m.Defence).Blurb, R(gx + 14, top + 62, half - 28, 20), 11, MutedInk);

        // What each one is doing, and the manager's latest decisions.
        float bottom = 458, rosterW = (w - 14) * 0.6f, logW = w - 14 - rosterW;
        Card(R(x, bottom, rosterW, 190));
        CardTitle("What they're doing", x + 14, bottom + 8);
        if (!s.Enabled) Text("Autonomy is off: they only do what you order.", R(x + 14, bottom + 30, rosterW - 28, 20), 12, MutedInk);
        else if (s.AiRuns) Text("The old mod's behaviours run them (Advanced page).", R(x + 14, bottom + 30, rosterW - 28, 20), 12, MutedInk);
        else
        {
            var rows = squad.Managed;
            float colW = (rosterW - 28) / 2;
            for (int k = 0; k < rows.Count && k < 16; k++)
            {
                var (_, name, doing) = rows[k];
                var at = R(x + 14 + (k % 2) * colW, bottom + 28 + (k / 2) * 19, colW - 8, 19);
                bool idle = doing.StartsWith("idle", StringComparison.Ordinal) || doing.StartsWith("no ", StringComparison.Ordinal) || doing.Contains("away", StringComparison.Ordinal);
                Text($"{name}: {doing}", at, 11, idle ? Bad : MainInk);
            }
            if (rows.Count > 16) Text($"and {rows.Count - 16} more", R(x + 14, bottom + 182 - 14, 200, 14), 10, MutedInk);
        }
        float lx = x + rosterW + 14;
        Card(R(lx, bottom, logW, 190));
        CardTitle("Manager's log", lx + 14, bottom + 8);
        var log = squad.ManagerLog;
        if (log.Count == 0) Text("Nothing yet.", R(lx + 14, bottom + 30, logW - 28, 20), 12, MutedInk);
        for (int k = 0; k < log.Count; k++)
            Text(log[log.Count - 1 - k], R(lx + 14, bottom + 28 + k * 19, logW - 28, 19), 11, k == 0 ? LightInk : MainInk);
    }

    /// <summary>The pips of a 0..10 priority: "off" then ten bars; a click sets it.</summary>
    void Pips(UiRect area, int value, Action<int> set)
    {
        float cell = (area.Width - 30 * scale) / 11f;
        for (int v = 0; v <= 10; v++)
        {
            int to = v;
            var at = new UiRect(area.X + v * (cell + 3 * scale), area.Y, cell, area.Height);
            bool on = v > 0 && v <= value, off = v == 0 && value == 0;
            Rect(at, off ? Bad with { W = 0.45f } : on ? Brass with { W = 0.75f } : new Vector4(0.10f, 0.08f, 0.07f, 1));
            Outline(at, MainInk with { W = Hovered(at, null) ? 0.5f : 0.1f });
            if (v == 0) Text("off", at, 10, off ? LightInk : MutedInk, "Center");
            hits.Add(new(at, () => set(to)));
        }
    }

    /// <summary>Machine work and building on one page: more bars = done first; a building whose bars beat a job's is built before that job is worked.</summary>
    void DrawPrioritiesPage(SquadView squad, AutonomySettings s, float x, float w)
    {
        var m = MgmtSettings(squad) ?? new Meitou.Simulation.Management.ManagementSettings();
        Header("Priorities: more bars = done first, off = never", x, 120, w);
        float half = (w - 14) / 2;
        Card(R(x, 166, half, 482));
        CardTitle("Machine work", x + 14, 176);
        Button("Their best skills", R(x + half - 150, 172, 136, 24), () => AutSend(squad, s with { Labour = BestSkills(squad) }), size: 11);
        float y = 204;
        foreach (var (kind, name) in LabourRows)
        {
            Text(name, R(x + 14, y, 140, 24), 13, MainInk);
            Pips(R(x + 156, y + 4, half - 170, 16), s.LabourPriority(kind), v =>
            {
                var labour = s.Labour.ToArray();
                if (labour.Length < 12) labour = [.. labour, .. Enumerable.Repeat(1f, 12 - labour.Length)];
                labour[(int)kind] = v;
                AutSend(squad, s with { Labour = labour });
            });
            y += 30;
        }
        Text("Operators fetch inputs from your storage and take the output back.", R(x + 14, y + 2, half - 28, 18), 10, MutedInk);

        float gx = x + half + 14;
        Card(R(gx, 166, half, 482));
        CardTitle("Building what you place", gx + 14, 176);
        y = 204;
        foreach (var (group, name, examples) in BuildRows)
        {
            Text(name, R(gx + 14, y - 2, 140, 18), 13, MainInk);
            Text(examples, R(gx + 14, y + 13, 140, 14), 9, MutedInk);
            Pips(R(gx + 156, y + 4, half - 170, 16), m.PriorityOf(group), v =>
            {
                var list = m.BuildPriority.ToArray();
                if (list.Length < 8) list = [.. list, .. Meitou.Simulation.Management.ManagementSettings.DefaultBuildPriority.Skip(list.Length)];
                list[(int)group] = v;
                MgmtSend(squad, m with { BuildPriority = list, Build = true });
            });
            y += 34;
        }
        Check(R(gx + 14, y + 6, half - 28, 42), "Build and repair", "They fetch materials from storage and the ground.", m.Build, () => MgmtSend(squad, m with { Build = !m.Build }));
        Check(R(gx + 14, y + 54, half - 28, 42), "Man turrets", "When nothing above needs them.", s.ManTurrets, () => AutSend(squad, s with { ManTurrets = !s.ManTurrets }));
        Text("At most 3 builders per building.", R(gx + 14, y + 102, half - 28, 18), 10, MutedInk);
    }

    // ------------------------------------------------------------------ the Buildings page (the manager's assigned buildings, Meitou)

    /// <summary>Clicking buildings in the world to add to (or take off) a squad's list: the squad and the building selected when the last one was taken.</summary>
    (int Squad, string? Before)? assigning;

    void DrawBuildingsPage(SquadView squad, AutonomySettings s, float x, float w)
    {
        var m = MgmtSettings(squad) ?? new Meitou.Simulation.Management.ManagementSettings();
        Header("Buildings: the ones this squad looks after", x, 120, w);
        Card(R(x, 166, w, 482));
        if (m.Buildings.Count == 0)
        {
            CardTitle("None assigned: they work the whole outpost by the Priorities page", x + 14, 176);
            Text("Assign buildings to keep this squad on them only, e.g. two stone mines, a stone processor and the building material storage for a stone crew. "
                + "The list's order is what comes first: put the processor above the mines and, when stone piles up in storage, one of the miners is moved to it; "
                + "when it runs dry he goes back to mining. Unbuilt ones on the list get built. Stuck ones (full, no storage, no power) show in the manager's log.",
                R(x + 14, 200, w - 28, 120), 12, MutedInk, "Left Top");
        }
        else CardTitle("Top first. They only work, build and man these.", x + 14, 176);
        float y = m.Buildings.Count == 0 ? 330 : 200;
        for (int k = 0; k < m.Buildings.Count && k < 12; k++)
        {
            string id = m.Buildings[k];
            int at = k;
            string name = player.PlaceName(id, population()) ?? "Gone";
            Text($"{k + 1}.", R(x + 14, y, 28, 26), 13, MutedInk);
            Text(name, R(x + 44, y, w - 300, 26), 14, name == "Gone" ? Bad : LightInk);
            Button("Up", R(x + w - 238, y + 1, 56, 24), () => MgmtSend(squad, m with { Buildings = Swap(m.Buildings, at, at - 1) }), enabled: k > 0, size: 11);
            Button("Down", R(x + w - 176, y + 1, 64, 24), () => MgmtSend(squad, m with { Buildings = Swap(m.Buildings, at, at + 1) }), enabled: k < m.Buildings.Count - 1, size: 11);
            Button("Remove", R(x + w - 106, y + 1, 84, 24), () => MgmtSend(squad, m with { Buildings = [.. m.Buildings.Where((_, j) => j != at)] }), size: 11);
            y += 30;
        }
        if (m.Buildings.Count > 12) Text($"and {m.Buildings.Count - 12} more", R(x + 14, y, 300, 20), 11, MutedInk);
        float by = 606;
        Button("Pick buildings in the world", R(x + 14, by, 230, 30), () => { assigning = (squad.Id, player.SelectedBuildingInstance); openDropdown = null; }, primary: true, size: 12);
        string? selected = player.SelectedBuildingInstance;
        Button("Add the selected building", R(x + 254, by, 210, 30), () => MgmtSend(squad, m with { Buildings = [.. m.Buildings, selected!] }),
            enabled: selected is not null && !m.Buildings.Contains(selected) && Snap.Buildings.Any(b => b.Id == selected), size: 12);
        Button("Clear", R(x + w - 106, by, 84, 30), () => MgmtSend(squad, m with { Buildings = [] }), enabled: m.Buildings.Count > 0, size: 12);
    }

    static IReadOnlyList<string> Swap(IReadOnlyList<string> list, int a, int b)
    {
        var copy = list.ToArray();
        if (a >= 0 && b >= 0 && a < copy.Length && b < copy.Length) (copy[a], copy[b]) = (copy[b], copy[a]);
        return copy;
    }

    /// <summary>The world-picking banner: every building of yours clicked goes on the squad's list (clicked again, off it); Done ends it.</summary>
    void DrawAssigning(int width, int height, UiPainter painter, (int Squad, string? Before) pick)
    {
        string? now = player.SelectedBuildingInstance;
        var squad = Snap.PlayerSquads.FirstOrDefault(q => q.Id == pick.Squad);
        if (squad is null) { assigning = null; return; }
        var m = MgmtSettings(squad) ?? new Meitou.Simulation.Management.ManagementSettings();
        if (now is not null && now != pick.Before && Snap.Buildings.Any(b => b.Id == now))
        {
            bool on = m.Buildings.Contains(now);
            MgmtSend(squad, m with { Buildings = on ? [.. m.Buildings.Where(b => b != now)] : [.. m.Buildings, now] });
            Notice($"{player.PlaceName(now, population()) ?? "Building"} {(on ? "taken off" : "added to")} {squad.Name}'s buildings.");
            assigning = (pick.Squad, now);
        }
        float w = 720, h = 54;
        pickBanner = new(MathF.Round((width - w * scale) / 2), MathF.Round(24 * scale), MathF.Round(w * scale), MathF.Round(h * scale));
        bounds = pickBanner;
        Surface(pickBanner, "window");
        Text($"Click your buildings for {squad.Name} ({m.Buildings.Count} assigned). Click again to take one off.", new(pickBanner.X + 16 * scale, pickBanner.Y, pickBanner.Width - 150 * scale, pickBanner.Height), 14, LightInk);
        Button("Done", new(pickBanner.X + pickBanner.Width - 116 * scale, pickBanner.Y + 12 * scale, 100 * scale, 30 * scale), () => assigning = null, primary: true);
        painter.Draw(controls, width, height, pointer, null);
    }

    // ------------------------------------------------------------------ pick in world

    void DrawPicking(int width, int height, UiPainter painter, (int Squad, bool Home, string? Before) pick)
    {
        string? now = player.SelectedBuildingInstance;
        var squad = Snap.PlayerSquads.FirstOrDefault(q => q.Id == pick.Squad);
        if (squad is not null && now is not null && now != pick.Before)
        {
            picking = null;
            SetPlace(squad, AutSettings(squad), pick.Home, now);
            bounds = default;
            return;
        }
        float w = 620, h = 54;
        pickBanner = new(MathF.Round((width - w * scale) / 2), MathF.Round(24 * scale), MathF.Round(w * scale), MathF.Round(h * scale));
        bounds = pickBanner;
        Surface(pickBanner, "window");
        Text($"Click a building for {squad?.Name ?? "the squad"}'s {(pick.Home ? "home" : "work place")}", new(pickBanner.X + 16 * scale, pickBanner.Y, pickBanner.Width - 150 * scale, pickBanner.Height), 15, LightInk);
        Button("Cancel", new(pickBanner.X + pickBanner.Width - 116 * scale, pickBanner.Y + 12 * scale, 100 * scale, 30 * scale), () => picking = null);
        painter.Draw(controls, width, height, pointer, null);
    }

    // ------------------------------------------------------------------ the HUD's AUT button

    UiRect autButton;

    /// <summary>
    /// The AUT button on the HUD (the plugin's main bar button, its default place 0.69, 0.788 of the screen, 1.5 % x 3.58 %): opens the window for the squad on the squad bar. Drawn while the
    /// game's HUD shows and the window is closed.
    /// </summary>
    public void DrawHudButton(int width, int height)
    {
        autButton = default;
        if (visible || !player.UiVisible || player.TitleOpen || player.EditorOpen || player.ModalOpen || !world().Player.Exists || player.SettingsPainter is not { } painter) return;
        art.Ensure(painter, MeitouLook, player.UiAtlas);
        scale = Math.Max(0.35f, Math.Min(1.25f, Math.Min(height / 900f, width / 1280f)));
        autButton = new(MathF.Round(width * 0.69f), MathF.Round(height * 0.788f), MathF.Round(Math.Max(width * 0.015f, 34 * scale)), MathF.Round(height * 0.0358f));
        controls.Clear(); hits.Clear();
        bool on = Snap.PlayerSquads.FirstOrDefault(q => q.Id == world().Player.Squad)?.Autonomy is { Enabled: true };
        bool hover = autButton.Contains(pointer.X, pointer.Y);
        Surface(autButton, "button", on, hover);
        Text("AUT", autButton, 12, on ? Brass : hover ? LightInk : MainInk, "Center");
        painter.Draw(controls, width, height, pointer, null);
        controls.Clear();
    }

    /// <summary>A press on the AUT button opens the window; true when it was there.</summary>
    bool AutButtonDown(Vector2 at)
    {
        if (autButton.Width <= 0 || !autButton.Contains(at.X, at.Y)) return false;
        OpenAutonomy(world().Player.Squad);
        return true;
    }
}
