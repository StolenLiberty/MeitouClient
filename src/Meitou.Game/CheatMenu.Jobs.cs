using System.Numerics;
using Meitou.Data.Ui;
using Meitou.Rendering.Ui;
using Meitou.Simulation;
using Meitou.Simulation.Jobs;

namespace Meitou.Game;

// The Squad Jobs window (the HUD's AUT button; docs/simulation.md "Squad jobs", docs/game/ui-screens.md): one page. A job card per job (Off: plain Kenshi), the shift, the stance in a
// fight, the buildings it keeps to, what each member is doing and why, and the latest decisions. Drawn with the cheat menu's kit; each change goes to the world (SetSquadWork) at once.
sealed partial class CheatMenu
{
    bool jobsMode;
    int jobsSquad = -1;
    /// <summary>A job sent and not yet in the snapshot (shown meanwhile, so the window does not flicker back).</summary>
    (int Squad, SquadWork? Work)? jobsPending;
    /// <summary>Picking buildings in the world for a squad's list: the squad and the building selected when the last one was taken.</summary>
    (int Squad, string? Before)? assigning;
    UiRect pickBanner;
    /// <summary>Where the window was dragged to (its top left, pixels), and the pointer's offset in it while its title strip is held.</summary>
    Vector2? jobsPlace, jobsGrab;
    /// <summary>The window's background opacity (the world shows through a little).</summary>
    const float WindowAlpha = 0.86f;

    static readonly (Shift Shift, string Name, string Blurb)[] Shifts =
    [
        (Shift.Day, "Day shift", "Work 06:00 to 20:00, sleep at night."),
        (Shift.Night, "Night shift", "Work 20:00 to 06:00, sleep in the day."),
        (Shift.AllDay, "All day", "Work around the clock; the badly hurt still rest at night."),
    ];

    static readonly (Stance Stance, string Name, string Blurb)[] Stances =
    [
        (Stance.StandGround, "Stand ground", "Keep working; fight only a threat right on them. Never go looking for a fight."),
        (Stance.Soldiers, "Soldiers", "Quick reaction force: run to any attack on your people, clear hostiles out."),
        (Stance.Militia, "Militia", "The men run to any attack on your people; the women go inside."),
        (Stance.Shelter, "Shelter", "Everyone goes inside while your people are under attack."),
    ];

    /// <summary>Opens the window for a player squad (the HUD's AUT button: the active squad); pressed again for the same squad it closes.</summary>
    public void OpenJobs(int squadId)
    {
        if (visible && jobsMode && jobsSquad == squadId && assigning is null) { Visible = false; return; }
        Visible = false;
        (jobsMode, townsMode, jobsSquad, assigning) = (true, false, squadId, null);
        Visible = true;
    }

    WorldSnapshot Snap => player.Snapshot;

    SquadView? JobsSquad() => Snap.PlayerSquads.FirstOrDefault(s => s.Id == jobsSquad) ?? Snap.PlayerSquads.FirstOrDefault();

    /// <summary>The squad's job as the window shows it: one just sent, until the snapshot has it.</summary>
    SquadWork? WorkOf(SquadView squad)
    {
        if (jobsPending is { } p && p.Squad == squad.Id)
        {
            if (Same(squad.Work, p.Work)) jobsPending = null;
            else return p.Work;
        }
        return squad.Work;
    }

    static bool Same(SquadWork? a, SquadWork? b) =>
        a is null || b is null ? a is null && b is null : a.Job == b.Job && a.Shift == b.Shift && a.Stance == b.Stance && a.Buildings.SequenceEqual(b.Buildings);

    void SendWork(SquadView squad, SquadWork? work)
    {
        if (work is { On: false }) work = null;
        world().Commands.Enqueue(new SetSquadWork(squad.Id, work) { Tick = Now });
        jobsPending = (squad.Id, work);
    }

    /// <summary>Moves the window while its title strip is held; true while dragging.</summary>
    bool JobsDrag(Vector2 at)
    {
        if (jobsGrab is not { } grab) return false;
        jobsPlace = at - grab;
        return true;
    }

    void DrawJobsWindow(int width, int height, UiPainter painter)
    {
        const float W = 1080, H = 700;
        scale = Math.Max(0.35f, Math.Min(1.25f, Math.Min(height / 900f, width / 1280f))) * player.HudScale;
        controls.Clear(); hits.Clear();
        if (assigning is { } assign) { DrawAssigning(width, height, painter, assign); return; }
        // Near the top of the screen (clear of the HUD's bottom panels) unless dragged by its title strip; the place is kept while the game runs.
        float ww = MathF.Round(W * scale), wh = MathF.Round(H * scale);
        var place = jobsPlace ?? new Vector2((width - ww) / 2, MathF.Round(16 * scale));
        place = new(Math.Clamp(place.X, 40 * scale - ww, width - 40 * scale), Math.Clamp(place.Y, 0, height - 44 * scale));
        bounds = new(MathF.Round(place.X), MathF.Round(place.Y), ww, wh);
        Surface(bounds, "window", alpha: WindowAlpha);
        Text("Squad Jobs", R(20, 6, 400, 32), 22, LightInk);
        hits.Add(new(R(0, 0, W - 60, 44), () => jobsGrab = new(pointer.X - bounds.X, pointer.Y - bounds.Y)));
        var close = R(W - 51, 6, 31, 32);
        CloseButton(close, () => Visible = false);
        Rect(R(20, 44, W - 40, 1), MainInk with { W = 0.12f });

        if (JobsSquad() is not { } squad)
        {
            Text("You have no squads.", R(20, 60, 600, 30), 16, MutedInk);
            painter.Draw(controls, width, height, pointer, null);
            return;
        }
        jobsSquad = squad.Id;
        var work = WorkOf(squad);
        var job = work?.Job ?? SquadJob.Off;
        var squads = Snap.PlayerSquads;
        int index = Math.Max(0, squads.ToList().FindIndex(q => q.Id == squad.Id));
        DropdownButton("jobs-squad", R(20, 54, 250, 38), [.. squads.Select(q => $"{q.Name}  ({q.Members.Count})")], index, k => { jobsSquad = squads[k].Id; jobsPending = null; });
        Text(Summary(squad, work), R(286, 54, W - 306, 38), 13, work is null ? MutedInk : LightInk);
        Rect(R(20, 100, W - 40, 1), MainInk with { W = 0.12f });

        // Left: the job, its shift and stance, its buildings.
        const float LX = 20, LW = 620;
        CardTitle("What should this squad do?", LX, 110);
        var cards = new List<(SquadJob Job, string Name, string Blurb)> { (SquadJob.Off, "Off", "Plain Kenshi: they only do what you tell them.") };
        cards.AddRange(JobInfo.All.Select(j => (j.Job, j.Name, j.Blurb)));
        float cw = (LW - 30) / 4, ch = 54;
        for (int i = 0; i < cards.Count; i++)
        {
            var (cardJob, name, blurb) = cards[i];
            var at = R(LX + (i % 4) * (cw + 10), 132 + (i / 4) * (ch + 8), cw, ch);
            bool chosen = cardJob == job;
            bool hover = Hovered(at, null);
            Rect(at, chosen ? Brass with { W = 0.14f } : new Vector4(0, 0, 0, hover ? 0.28f : 0.18f));
            Outline(at, chosen ? Brass : MainInk with { W = hover ? 0.35f : 0.12f });
            if (chosen) Rect(at with { Width = 3 * scale }, Brass);
            Text(name, new(at.X + 10 * scale, at.Y + 3 * scale, at.Width - 16 * scale, 22 * scale), 14, chosen ? LightInk : MainInk);
            Text(blurb, new(at.X + 10 * scale, at.Y + 24 * scale, at.Width - 16 * scale, 28 * scale), 10, MutedInk, "Left Top");
            hits.Add(new(at, () =>
            {
                SendWork(squad, cardJob == SquadJob.Off ? null : SquadWork.For(cardJob, work));
                Notice(cardJob == SquadJob.Off ? $"{squad.Name}: off, plain Kenshi." : $"{squad.Name}: {name}.");
            }));
        }
        float y = 132 + 4 * (ch + 8) + 6;
        bool on = work is { On: true };
        CardTitle("Shift", LX, y);
        float bw = (LW - 16) / 3;
        for (int i = 0; i < Shifts.Length; i++)
        {
            var (shift, name, _) = Shifts[i];
            Button(name, R(LX + i * (bw + 8), y + 20, bw, 30), () => SendWork(squad, work! with { Shift = shift }), selected: on && work!.Shift == shift, enabled: on, size: 13);
        }
        Text(on ? Shifts.First(s => s.Shift == work!.Shift).Blurb : "Pick a job first.", R(LX, y + 54, LW, 18), 11, MutedInk);
        y += 82;
        CardTitle("In a fight", LX, y);
        bw = (LW - 24) / 4;
        for (int i = 0; i < Stances.Length; i++)
        {
            var (stance, name, _) = Stances[i];
            Button(name, R(LX + i * (bw + 8), y + 20, bw, 30), () => SendWork(squad, work! with { Stance = stance }), selected: on && work!.Stance == stance, enabled: on, size: 12);
        }
        Text(on ? Stances.First(s => s.Stance == work!.Stance).Blurb : "", R(LX, y + 54, LW, 18), 11, MutedInk);
        y += 82;
        CardTitle("Buildings", LX, y);
        int count = work?.Buildings.Count ?? 0;
        Text(count == 0 ? "The whole outpost they stand in." : $"Only {count} building{(count == 1 ? "" : "s")} you picked.", R(LX, y + 20, 260, 30), 13, on ? MainInk : MutedInk);
        Button("Pick in the world", R(LX + 270, y + 20, 180, 30), () => { assigning = (squad.Id, player.SelectedBuildingInstance); openDropdown = null; }, enabled: on, size: 12);
        Button("Whole outpost", R(LX + 460, y + 20, 160, 30), () => SendWork(squad, work! with { Buildings = [] }), enabled: on && count > 0, size: 12);

        // Right: what each one is doing and why, and the latest decisions.
        const float RX = 660, RW = W - RX - 20;
        Rect(R(RX - 10, 110, 1, H - 166), MainInk with { W = 0.12f });
        CardTitle("What they're doing", RX, 110);
        Card(R(RX, 132, RW, 290));
        float ry = 140;
        var doing = squad.Doing.Count > 0 ? squad.Doing : [.. squad.Members.Select(id => (id, Snap.Characters.FirstOrDefault(c => c.Id == id)?.Name ?? "?", on ? "starting" : "off"))];
        foreach (var (_, name, what) in doing.Take(13))
        {
            Text(name, R(RX + 10, ry, 120, 20), 12, LightInk);
            Text(what, R(RX + 132, ry, RW - 142, 20), 12, Problem(what) ? Bad : MainInk);
            ry += 21;
        }
        if (doing.Count > 13) Text($"and {doing.Count - 13} more", R(RX + 10, ry, RW - 20, 20), 11, MutedInk);
        CardTitle("Latest", RX, 430);
        Card(R(RX, 452, RW, H - 46 - 460));
        float ly = 458;
        var log = squad.Log;
        if (log.Count == 0) Text(on ? "Nothing yet." : "No job.", R(RX + 10, ly, RW - 20, 20), 11, MutedInk);
        foreach (var line in log.Skip(Math.Max(0, log.Count - 9))) { Text(line, R(RX + 10, ly, RW - 20, 20), 11, line.Contains("  ! ", StringComparison.Ordinal) ? Bad : MainInk); ly += 19; }

        // Footer.
        Rect(R(20, H - 46, W - 40, 1), MainInk with { W = 0.12f });
        string hint = notice.Length > 0 && Environment.TickCount64 - noticeAt < 8000 ? notice : "Your orders always come first; when one is done they go back to the job.";
        Text(hint, R(20, H - 38, 600, 28), 12, Brass);
        var others = squads.Where(q => q.Id != squad.Id).ToList();
        if (others.Count > 0 && on)
            DropdownButton("jobs-copy", R(W - 440, H - 38, 280, 28), [.. others.Select(q => q.Name)], -1, k =>
            {
                var to = others[k];
                world().Commands.Enqueue(new SetSquadWork(to.Id, work) { Tick = Now });
                Notice($"Copied to {to.Name}.");
            }, caption: "Same job for another squad...");
        Button("Done", R(W - 140, H - 38, 120, 28), () => Visible = false, primary: true);
        if (openDropdown is { } open) DrawDropdown(open);
        painter.Draw(controls, width, height, pointer, null);
    }

    /// <summary>A status that needs the player (shown red).</summary>
    static bool Problem(string what) => what.StartsWith("waiting:", StringComparison.Ordinal) || what.Contains("no free bed", StringComparison.Ordinal) || what.StartsWith("no ", StringComparison.Ordinal)
        || what.Contains("full", StringComparison.Ordinal) || what.Contains("cannot", StringComparison.Ordinal);

    /// <summary>The line beside the squad: its job, shift, and how many are doing what.</summary>
    string Summary(SquadView squad, SquadWork? work)
    {
        if (work is not { On: true }) return $"{squad.Members.Count} member{(squad.Members.Count == 1 ? "" : "s")} · no job: plain Kenshi";
        string shift = Shifts.First(s => s.Shift == work.Shift).Name.ToLowerInvariant();
        var groups = squad.Doing.GroupBy(d => d.Doing.Split(' ')[0]).OrderByDescending(g => g.Count()).Take(3).Select(g => $"{g.Count()} {g.Key}");
        return $"{JobInfo.Of(work.Job).Name} · {shift} · " + string.Join(" · ", groups);
    }

    void CardTitle(string title, float x, float y) => Text(title.ToUpperInvariant(), R(x, y, 400, 18), 11, MutedInk);

    /// <summary>A faint card behind a group of rows.</summary>
    void Card(UiRect at)
    {
        Rect(at, new Vector4(0, 0, 0, 0.18f));
        Outline(at, MainInk with { W = 0.12f });
    }

    /// <summary>The world-picking banner: every building of yours clicked goes on the squad's list (clicked again, off it); Done ends it.</summary>
    void DrawAssigning(int width, int height, UiPainter painter, (int Squad, string? Before) pick)
    {
        string? now = player.SelectedBuildingInstance;
        var squad = Snap.PlayerSquads.FirstOrDefault(q => q.Id == pick.Squad);
        if (squad is null || WorkOf(squad) is not { On: true } work) { assigning = null; return; }
        if (now is not null && now != pick.Before && Snap.Buildings.Any(b => b.Id == now))
        {
            bool had = work.Buildings.Contains(now);
            SendWork(squad, work with { Buildings = had ? [.. work.Buildings.Where(b => b != now)] : [.. work.Buildings, now] });
            Notice($"{player.PlaceName(now, population()) ?? "Building"} {(had ? "taken off" : "added to")} {squad.Name}'s buildings.");
            assigning = (pick.Squad, now);
        }
        float w = 720, h = 54;
        pickBanner = new(MathF.Round((width - w * scale) / 2), MathF.Round(24 * scale), MathF.Round(w * scale), MathF.Round(h * scale));
        bounds = pickBanner;
        Surface(pickBanner, "window");
        int count = WorkOf(squad)?.Buildings.Count ?? 0;
        Text($"Click your buildings for {squad.Name} ({count} picked). Click again to take one off.", new(pickBanner.X + 16 * scale, pickBanner.Y, pickBanner.Width - 150 * scale, pickBanner.Height), 14, LightInk);
        Button("Done", new(pickBanner.X + pickBanner.Width - 116 * scale, pickBanner.Y + 12 * scale, 100 * scale, 30 * scale), () => assigning = null, primary: true);
        painter.Draw(controls, width, height, pointer, null);
    }

    // ------------------------------------------------------------------ the HUD's AUT button

    UiRect jobsButton;

    /// <summary>The AUT button on the game's HUD (0.69, 0.788 of the screen; the Modern HUD draws its own): opens the window for the squad on the squad bar; lit while that squad has a job. Drawn while the HUD shows and the window is closed.</summary>
    public void DrawHudButton(int width, int height)
    {
        jobsButton = default;
        // The Modern HUD has the button in its squad dock.
        if (visible || Modern || !player.UiVisible || player.TitleOpen || player.EditorOpen || player.ModalOpen || !world().Player.Exists || player.SettingsPainter is not { } painter) return;
        art.Ensure(painter, MeitouLook, player.UiAtlas);
        scale = Math.Max(0.35f, Math.Min(1.25f, Math.Min(height / 900f, width / 1280f))) * player.HudScale;
        jobsButton = new(MathF.Round(width * 0.69f), MathF.Round(height * 0.788f), MathF.Round(Math.Max(width * 0.015f, 34 * scale)), MathF.Round(height * 0.0358f));
        controls.Clear(); hits.Clear();
        bool lit = Snap.PlayerSquads.FirstOrDefault(q => q.Id == world().Player.Squad)?.Work is { On: true };
        bool hover = jobsButton.Contains(pointer.X, pointer.Y);
        Surface(jobsButton, "button", lit, hover);
        Text("AUT", jobsButton, 12, lit ? Brass : hover ? LightInk : MainInk, "Center");
        painter.Draw(controls, width, height, pointer, null);
        controls.Clear();
    }

    /// <summary>A press on the AUT button opens the window; true when it was there.</summary>
    bool JobsButtonDown(Vector2 at)
    {
        if (jobsButton.Width <= 0 || !jobsButton.Contains(at.X, at.Y)) return false;
        OpenJobs(world().Player.Squad);
        return true;
    }
}
