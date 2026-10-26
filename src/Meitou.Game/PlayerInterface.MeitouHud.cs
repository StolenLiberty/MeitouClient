using System.Globalization;
using System.Numerics;
using Meitou.Data.Ui;
using Meitou.Engine.Input;
using Meitou.Rendering;
using Meitou.Simulation;
using Meitou.Simulation.Construction;
using Meitou.Simulation.Production;
using EngineKey = Meitou.Engine.Input.Key;
using Meitou.Ui;

namespace Meitou.Game;

/// <summary>
/// The Meitou UI mode's HUD (docs/meitou-ui.md "In-game HUD"), in place of <c>Kenshi_MainPanel</c>: see-through plates on the screen's edges and the middle left to the world.
/// Top left the day, time, game speed and money; top centre where the camera is (the floor buttons inside a building, the fight's count, Paused); top right the outpost on one line
/// (its full panel below it on demand) and the alerts; bottom left the character card (minimised to its header or full); bottom centre the orders and the squad; bottom right the
/// windows; at the pointer a card for another faction's character. Layout units are pixels at 1080 lines, scaled to the window's height.
/// </summary>
sealed partial class PlayerInterface
{
    static readonly Vector4 HudInk = Rgb(175, 166, 139), HudLight = Rgb(217, 206, 184), HudMuted = Rgb(179, 170, 146), HudBrass = Rgb(196, 160, 98),
        HudBrassText = Rgb(227, 200, 143), HudGoodInk = Rgb(168, 183, 116), HudHealthy = Rgb(140, 138, 90), HudHurt = Rgb(197, 138, 62), HudHurtInk = Rgb(224, 165, 92),
        HudOut = Rgb(143, 47, 34), HudDanger = Rgb(238, 106, 82), HudBadFill = Rgb(191, 72, 52), HudBlood = Rgb(168, 88, 74), HudHunger = Rgb(186, 168, 127),
        HudLoad = Rgb(126, 122, 79), HudEnemy = Rgb(230, 81, 57);
    static readonly Vector4 HudPlateTint = new(18 / 255f, 14 / 255f, 11 / 255f, 0.7f), HudRimTint = new(175 / 255f, 166 / 255f, 139 / 255f, 0.16f),
        HudButtonFill = new(1, 1, 1, 0.04f), HudTrack = new(10 / 255f, 8 / 255f, 6 / 255f, 0.45f), HudTile = new(43 / 255f, 35 / 255f, 29 / 255f, 0.55f);
    static Vector4 Rgb(int r, int g, int b) => new(r / 255f, g / 255f, b / 255f, 1);

    /// <summary>The character card shows only its header (Meitou HUD; kept in the settings file).</summary>
    public bool HudCardMinimised { get; set; }
    /// <summary>Whether a player character is inside a building (the host's interior view): the HUD then shows the floor buttons.</summary>
    public Func<bool>? InsideBuilding { get; set; }

    enum CardTab { Overview, Skills, Jobs }
    CardTab cardTab;
    bool outpostOpen, townPlaced;
    List<UiControl> hudControls = [];
    float hs = 1;
    int hudSerial;

    /// <summary>The virtual screen width in layout units.</summary>
    float HudWidth => Width / hs;
    UiRect V(float x, float y, float w, float h) => new(MathF.Round(x * hs), MathF.Round(y * hs), MathF.Round(w * hs), MathF.Round(h * hs));
    string HudId(string tag) => "hud-" + tag + "-" + (hudSerial++).ToString(CultureInfo.InvariantCulture);
    static string Tint(Vector4 c) => string.Create(CultureInfo.InvariantCulture, $"{c.X} {c.Y} {c.Z} {c.W}");
    float HudMeasure(string text, float px) => uiPainter is { } p ? p.MeasureStandard(text, px * hs) / hs
        : text.Sum(c => char.IsUpper(c) ? 0.66f : char.IsDigit(c) ? 0.56f : c == ' ' ? 0.26f : 0.52f) * px;   // no painter (a test): Exo 2's average widths

    /// <summary><paramref name="text"/> cut short with an ellipsis to fit <paramref name="width"/> layout units at <paramref name="px"/>.</summary>
    string HudFit(string text, float px, float width)
    {
        if (HudMeasure(text, px) <= width) return text;
        int lo = 0, hi = text.Length;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (HudMeasure(text[..mid].TrimEnd() + "…", px) <= width) lo = mid; else hi = mid - 1;
        }
        return lo == 0 ? "…" : text[..lo].TrimEnd() + "…";
    }

    // ------------------------------------------------------------------ drawing

    void HudImage(string texture, UiRect at, Vector4 tint, float radius = -1, bool pickable = false, string? name = null)
    {
        var p = new Dictionary<string, string> { ["ImageTexture"] = texture, ["Colour"] = Tint(tint) };
        if (radius >= 0)
        {
            p["ImageBorder"] = MeitouHudArt.PlateBorder.ToString(CultureInfo.InvariantCulture);
            p["ImageBorderScale"] = (MathF.Max(radius, 0.5f) * hs / MeitouHudArt.PlateRadius).ToString(CultureInfo.InvariantCulture);
        }
        hudControls.Add(new(name ?? HudId("img"), "ImageBox", at, "", "", "", true, p, Vector4.Zero, Pickable: pickable));
    }

    /// <summary>A see-through plate that takes the mouse (so a click on it is not an order in the world), with its faint rim.</summary>
    void HudPlate(float x, float y, float w, float h, float radius = 10, Vector4? tint = null, Vector4? rim = null)
    {
        var at = V(x, y, w, h);
        HudImage(MeitouHudArt.Plate, at, tint ?? HudPlateTint, radius, pickable: true);
        HudImage(MeitouHudArt.Rim, at, rim ?? HudRimTint, radius);
    }

    void HudRound(float x, float y, float w, float h, Vector4 colour, float radius) => HudImage(MeitouHudArt.Plate, V(x, y, w, h), colour, radius);

    void HudFill(float x, float y, float w, float h, Vector4 colour) =>
        hudControls.Add(new(HudId("fill"), "Panel", V(x, y, w, h), "", "", "", true, new Dictionary<string, string>(), colour, Pickable: false));

    void HudText(string text, float x, float y, float w, float h, float px, Vector4 colour, string align = "Left VCenter", string? tip = null)
    {
        if (text.Length == 0) return;
        var p = new Dictionary<string, string>
        {
            ["TextAlign"] = align, ["FontName"] = "Kenshi_StandardFont",
            ["FontPixels"] = Math.Max(7, (int)MathF.Round(px * hs)).ToString(CultureInfo.InvariantCulture),
        };
        if (tip is not null) foreach (var pair in UiTooltips.TextProperties(tip)) p[pair.Key] = pair.Value;
        hudControls.Add(new(HudId("text"), "TextBox", V(x, y, w, h), text, "", "", true, p, Vector4.Zero, colour, Pickable: tip is not null, TextScale: px * hs / 16));
    }

    void HudIcon(string icon, float x, float y, float size, Vector4 colour) => HudImage(MeitouHudArt.Icon(icon), V(x, y, size, size), colour);

    void HudDivider(float x, float y, float h) => HudFill(x, y, 1, h, HudInk with { W = 0.16f });

    /// <summary>A bar: the track, and the fill to <paramref name="fraction"/>.</summary>
    void HudBar(float x, float y, float w, float h, float fraction, Vector4 colour)
    {
        HudRound(x, y, w, h, HudTrack, h / 2);
        float f = Math.Clamp(fraction, 0, 1);
        if (f > 0) HudRound(x, y, MathF.Max(w * f, h), h, colour, h / 2);
    }

    /// <summary>
    /// A button: its plate (brass while <paramref name="active"/>, lighter under the pointer) and the invisible control that takes the click (<paramref name="action"/>:
    /// a registered <c>meitou:</c> action, or a <c>hud:</c> one of <see cref="MeitouHudAction"/>). Returns the colour for what is drawn on it.
    /// </summary>
    Vector4 HudButton(string id, float x, float y, float w, float h, string action, string argument = "", bool active = false, string? tip = null, bool enabled = true, float radius = 7)
    {
        var at = V(x, y, w, h);
        bool hover = enabled && at.Contains(mouse.X, mouse.Y);
        HudImage(MeitouHudArt.Plate, at, active ? HudBrass with { W = 0.2f } : hover ? HudInk with { W = 0.12f } : HudButtonFill, radius);
        HudImage(MeitouHudArt.Rim, at, active ? HudBrass with { W = 0.9f } : HudRimTint, radius);
        var p = tip is null ? new Dictionary<string, string>() : UiTooltips.TextProperties(tip);
        hudControls.Add(new("hud-" + id, "Button", at, "", action, "", enabled, p, Vector4.Zero, Argument: argument, Pickable: true));
        return !enabled ? HudMuted with { W = 0.45f } : active ? HudBrassText : hover ? HudLight : HudInk;
    }

    /// <summary>A plain click target over drawn content (no plate of its own).</summary>
    void HudTarget(string id, UiRect at, string action, string argument = "", string? tip = null) =>
        hudControls.Add(new("hud-" + id, "Button", at, "", action, "", true, tip is null ? new Dictionary<string, string>() : UiTooltips.TextProperties(tip), Vector4.Zero,
            Argument: argument, Pickable: true));

    /// <summary>The first key bound to <paramref name="action"/> as a short label ("Num 0", "F2"), or "".</summary>
    string HudKey(InputAction action)
    {
        foreach (var b in session.Bindings.Get(action))
            if (!b.IsMouse) return KeyLabel(b.Key);
        return "";
    }

    static string KeyLabel(EngineKey key) => key switch
    {
        >= EngineKey.D0 and <= EngineKey.D9 => ((int)(key - EngineKey.D0)).ToString(CultureInfo.InvariantCulture),
        EngineKey.Grave => "`", EngineKey.PageUp => "PgUp", EngineKey.PageDown => "PgDn", EngineKey.Apostrophe => "'",
        _ when key.ToString().StartsWith("Keypad", StringComparison.Ordinal) => "Num " + key.ToString()[6..],
        _ => key.ToString(),
    };

    static string WithKey(string name, string key) => key.Length == 0 ? name : $"{name} ({key})";

    // ------------------------------------------------------------------ the HUD

    /// <summary>Adds the HUD's controls for this frame (build mode shows none, as the original hides its bar).</summary>
    void AddMeitouHud(List<UiControl> controls)
    {
        if (uiPainter is { } painter) MeitouHudArt.Ensure(painter);
        hudControls = controls;
        hs = Height / 1080f;
        hudSerial = 0;
        if (buildMode) return;
        HudClock();
        HudPlace();
        float below = HudOutpost();
        HudAlerts(below);
        var lead = Lead();
        float cardRight = HudCard(lead);
        float windowsLeft = HudWindows();
        HudSquad(cardRight, windowsLeft);
        HudHoverCard();
    }

    /// <summary>Top left: the day and time, pause and the speeds, the money.</summary>
    void HudClock()
    {
        string day = session.Clock.DayText, time = session.Clock.TimeText;
        string money = session.World.Player.Money.ToString("N0", CultureInfo.InvariantCulture);
        int speeds = Math.Min(ButtonSpeeds.Length, 4);
        float wDay = HudMeasure(day, 16), wTime = HudMeasure(time, 16), wMoney = HudMeasure(money, 16), wC = HudMeasure("c.", 13);
        float width = 14 + 18 + 12 + wDay + 8 + wTime + 12 + 1 + 12 + speeds * 40 + (speeds - 1) * 4 + 12 + 1 + 12 + wC + 4 + wMoney + 14;
        const float x0 = 24, y0 = 20;
        HudPlate(x0, y0, width, 48);
        float x = x0 + 14;
        HudIcon("clock", x, y0 + 15, 18, HudInk); x += 18 + 12;
        HudText(day, x, y0, wDay + 4, 48, 16, HudLight); x += wDay + 8;
        HudText(time, x, y0, wTime + 4, 48, 16, HudInk); x += wTime + 12;
        HudDivider(x, y0 + 12, 24); x += 1 + 12;
        string[] keys = [HudKey(InputAction.Pause), HudKey(InputAction.Speed1), HudKey(InputAction.Speed2), HudKey(InputAction.Speed3)];
        for (int i = 0; i < speeds; i++)
        {
            bool on = i == 0 ? session.Simulation.Speed == 0 : session.Simulation.Speed == ButtonSpeeds[i];
            string name = i == 0 ? Loc.Tr("Pause") : Loc.Tr("Speed {1}", ButtonSpeeds[i].ToString("0", CultureInfo.InvariantCulture)) + "x";
            var ink = HudButton("speed-" + i.ToString(CultureInfo.InvariantCulture), x, y0 + 8, 40, 32, $"meitou:speed.{i}", active: on, tip: WithKey(name, keys[i]));
            if (i == 0) HudIcon("pause", x + 12, y0 + 16, 16, ink);
            else HudText(ButtonSpeeds[i].ToString("0", CultureInfo.InvariantCulture) + "x", x, y0 + 8, 40, 32, 14, ink, "Center");
            x += 44;
        }
        x += 8;
        HudDivider(x, y0 + 12, 24); x += 1 + 12;
        HudText("c.", x, y0 + 2, wC + 2, 48, 13, HudInk); x += wC + 4;
        HudText(money, x, y0, wMoney + 4, 48, 16, HudLight);
    }

    /// <summary>Top centre: the region under the camera, Paused, the fight's count and (inside a building) the floor and its buttons. Nothing when there is nothing to say.</summary>
    void HudPlace()
    {
        var pivot = session.Camera.Current.Target;
        string region = RegionAt?.Invoke(pivot.X, pivot.Z) is { } here && here.Name != "NONE" ? here.Name : "";
        bool paused = session.Simulation.Speed == 0;
        var characters = session.CurrentSnapshot.Characters;
        int ours = characters.Count(c => c.IsPlayer && c.InCombat && c.Body is not { Dead: true }), theirs = ours == 0 ? 0 : characters.Count(c => !c.IsPlayer && c.FightsPlayer && c.Body is not { Dead: true });
        bool inside = InsideBuilding?.Invoke() == true && Floor is not null;
        if (region.Length == 0 && !paused && ours == 0 && !inside) return;
        string floor = inside ? Loc.Tr("Floor {1,num}", Floor!.Value.Get()) : "";
        string fight = ours > 0 ? $"{ours} vs {theirs}" : "";
        float width = 16, wRegion = HudMeasure(region, 16), wPaused = HudMeasure(Loc.Tr("Paused"), 14), wFight = HudMeasure(Loc.Tr("Fighting"), 14) + 8 + HudMeasure(fight, 14), wFloor = HudMeasure(floor, 14);
        var parts = new List<float>();
        if (region.Length > 0) parts.Add(wRegion);
        if (paused) parts.Add(wPaused);
        if (ours > 0) parts.Add(16 + wFight);
        if (inside) parts.Add(wFloor + 8 + 28 + 4 + 28);
        width += parts.Sum() + (parts.Count - 1) * 25 + 16 - (inside ? 10 : 0);
        float x = MathF.Round((HudWidth - width) / 2), y0 = 20;
        HudPlate(x, y0, width, 40);
        x += 16;
        bool first = true;
        void Gap() { if (!first) { x += 12; HudDivider(x, y0 + 10, 20); x += 13; } first = false; }
        if (region.Length > 0) { Gap(); HudText(region, x, y0, wRegion + 4, 40, 16, HudLight); x += wRegion; }
        if (paused) { Gap(); HudText(Loc.Tr("Paused"), x, y0, wPaused + 4, 40, 14, HudBrassText); x += wPaused; }
        if (ours > 0)
        {
            Gap();
            HudRound(x, y0 + 16, 8, 8, HudEnemy, 4); x += 16;
            HudText(Loc.Tr("Fighting"), x, y0, wFight, 40, 14, HudDanger); x += HudMeasure(Loc.Tr("Fighting"), 14) + 8;
            HudText(fight, x, y0, HudMeasure(fight, 14) + 4, 40, 14, HudLight); x += HudMeasure(fight, 14);
        }
        if (inside)
        {
            Gap();
            HudText(floor, x, y0, wFloor + 4, 40, 14, HudInk); x += wFloor + 8;
            var up = HudButton("floor-up", x, y0 + 6, 28, 28, "meitou:floor-up", tip: WithKey(Loc.Tr("Floor up"), HudKey(InputAction.FloorUp)));
            HudIcon("up", x + 7, y0 + 13, 14, up); x += 32;
            var down = HudButton("floor-down", x, y0 + 6, 28, 28, "meitou:floor-down", tip: WithKey(Loc.Tr("Floor down"), HudKey(InputAction.FloorDown)));
            HudIcon("down", x + 7, y0 + 13, 14, down);
        }
    }

    /// <summary>Top right: the outpost the camera is in on one line, its full panel under it on demand. Returns where the next thing down the right edge may start.</summary>
    float HudOutpost()
    {
        const float y0 = 20;
        if (PickTown() is not { } town) { outpostOpen = false; return y0; }
        bool own = town.Owner == session.World.Player.Faction;
        var p = town.Power;
        var research = session.CurrentSnapshot.Research.Queue;
        string name = town.Name.Length > 0 ? town.Name : Loc.Tr("Outpost");
        string access = town.Public ? Loc.Tr("Open to public") : Loc.Tr("Closed to public");
        string power = $"{(int)p.Generated} / {(int)p.Demand}";
        string battery = p.Capacity > 0 ? ((int)MathF.Round(p.Charge / p.Capacity * 100)).ToString(CultureInfo.InvariantCulture) + "%" : "";
        string study = research.Count > 0 ? research[0].Name : Loc.Tr("No research");
        float wName = MathF.Max(HudMeasure(name, 14), HudMeasure(access, 11)), wPower = HudMeasure(power, 14), wBattery = battery.Length > 0 ? 26 + 6 + HudMeasure(battery, 14) : 0;
        float wStudy = HudMeasure(study, 14) + (research.Count > 0 ? 8 + 64 : 0);
        float width = 14 + 18 + 10 + wName + 14 + 1 + 14 + 16 + 6 + wPower + (wBattery > 0 ? 14 + wBattery : 0) + 14 + 16 + 8 + wStudy + 14 + 32 + 8;
        float x = HudWidth - 24 - width;
        HudPlate(x, y0, width, 48);
        float cx = x + 14;
        HudIcon("build", cx, y0 + 15, 18, HudInk); cx += 18 + 10;
        HudText(name, cx, y0 + 6, wName + 4, 20, 14, HudLight);
        if (own)
        {
            HudText(access, cx, y0 + 25, wName + 4, 16, 11, HudMuted);
            HudTarget("town-public", V(cx, y0 + 25, wName, 16), "hud:town-public", tip: Loc.Tr("Click to open or close the outpost to the public"));
        }
        else HudText(access, cx, y0 + 25, wName + 4, 16, 11, HudMuted);
        cx += wName + 14;
        HudDivider(cx, y0 + 12, 24); cx += 1 + 14;
        HudIcon("bolt", cx, y0 + 16, 16, HudBrass); cx += 16 + 6;
        HudText(power, cx, y0, wPower + 4, 48, 14, p.Demand > p.Generated ? HudDanger : HudInk, tip: Loc.Tr("Power made / used")); cx += wPower;
        if (wBattery > 0)
        {
            cx += 14;
            HudRound(cx, y0 + 18, 26, 12, HudInk with { W = 0.5f }, 3);
            HudRound(cx + 2, y0 + 20, 22 * Math.Clamp(p.Charge / p.Capacity, 0, 1), 8, HudGoodInk, 2);
            string arrows = p.Charging > 0 ? " >>" : p.Draining > 0 ? " <<" : "";
            HudText(battery, cx + 32, y0, HudMeasure(battery, 14) + 4, 48, 14, HudInk, tip: Loc.Tr("Battery charge") + arrows);
            cx += wBattery;
        }
        cx += 14;
        HudIcon("research", cx, y0 + 16, 16, HudInk); cx += 16 + 8;
        HudText(study, cx, y0, HudMeasure(study, 14) + 4, 48, 14, research.Count > 0 ? HudInk : HudMuted); cx += HudMeasure(study, 14);
        if (research.Count > 0) { cx += 8; HudBar(cx, y0 + 21, 64, 6, research[0].Progress, HudHunger); cx += 64; }
        cx += 14;
        var ink = HudButton("outpost", cx, y0 + 8, 32, 32, "hud:outpost", active: outpostOpen, tip: Loc.Tr(outpostOpen ? "Close the outpost panel" : "Open the outpost panel"));
        HudIcon(outpostOpen ? "up" : "down", cx + 8, y0 + 16, 16, ink);
        return y0 + 48 + 8 + (outpostOpen ? townHeight + 8 : 0);
    }

    /// <summary>The height of the outpost panel opened under the line (layout units), from its last placing.</summary>
    float townHeight;

    // ------------------------------------------------------------------ alerts

    /// <summary>An alert: what happened, a second line, its icon and colour, who to show when it is clicked, how many times it came and its age.</summary>
    sealed class HudAlert(string title, string detail, string icon, Vector4 colour, CharacterId who)
    {
        public string Title { get; } = title;
        public string Detail { get; set; } = detail;
        public string Icon { get; } = icon;
        public Vector4 Colour { get; } = colour;
        public CharacterId Who { get; } = who;
        public int Count { get; set; } = 1;
        public float Age { get; set; }
    }

    const int AlertLines = 5;
    const float AlertSeconds = 8, AlertFade = 1;
    readonly List<HudAlert> alerts = [];
    readonly Dictionary<CharacterId, (bool Down, bool Dead)> watched = [];
    bool squadFighting;
    long alertsAt;

    void PushAlert(string title, string detail, string icon, Vector4 colour, CharacterId? who = null)
    {
        if (alerts.FirstOrDefault(a => a.Title == title) is { } same)
        {
            same.Count++;
            same.Age = 0;
            same.Detail = detail;
            alerts.Remove(same);
            alerts.Insert(0, same);
            return;
        }
        alerts.Insert(0, new HudAlert(title, detail, icon, colour, who ?? CharacterId.None));
        if (alerts.Count > AlertLines) alerts.RemoveAt(alerts.Count - 1);
    }

    /// <summary>Once a frame: ages the alerts and adds those the snapshot shows happened (a character of the player's knocked out or killed, the squad pulled into a fight).</summary>
    void PollAlerts()
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        float dt = alertsAt == 0 ? 0 : MathF.Min((float)System.Diagnostics.Stopwatch.GetElapsedTime(alertsAt, now).TotalSeconds, 0.25f);
        alertsAt = now;
        foreach (var a in alerts) a.Age += dt;
        alerts.RemoveAll(a => a.Age >= AlertSeconds + AlertFade);
        if (UiMode != UiMode.Meitou || TitleOpen) return;
        bool first = watched.Count == 0;
        var seen = new HashSet<CharacterId>();
        int fighting = 0, foes = 0;
        CharacterId firstFighter = CharacterId.None;
        foreach (var c in session.CurrentSnapshot.Characters)
        {
            if (!c.IsPlayer) { if (c.FightsPlayer && c.Body is not { Dead: true }) foes++; continue; }
            seen.Add(c.Id);
            bool down = c.Body is { Unconscious: true }, dead = c.Body is { Dead: true };
            if (c.InCombat && !dead) { fighting++; if (firstFighter.IsNone) firstFighter = c.Id; }
            if (watched.TryGetValue(c.Id, out var was) && !first)
            {
                if (dead && !was.Dead) PushAlert(Loc.Tr("{1} has died", c.Name), "", "knocked", HudDanger, c.Id);
                else if (down && !was.Down && !dead)
                    PushAlert(Loc.Tr("{1} is knocked out", c.Name), c.Body is { KoTimer: > 0 } b ? Loc.Tr("Up in about {1} s", (int)b.KoTimer) : "", "knocked", HudDanger, c.Id);
            }
            watched[c.Id] = (down, dead);
        }
        foreach (var gone in watched.Keys.Where(id => !seen.Contains(id)).ToArray()) watched.Remove(gone);
        if (fighting > 0 && !squadFighting && !first) PushAlert(Loc.Tr("Your squad is fighting"), Loc.Tr("{1} against you", foes), "attack", HudDanger, firstFighter);
        squadFighting = fighting > 0;
    }

    /// <summary>Top right, under the outpost: the alerts, newest on top, fading out; a click shows the character it is about and dismisses it.</summary>
    void HudAlerts(float top)
    {
        const float width = 340;
        float x = HudWidth - 24 - width, y = top;
        for (int i = 0; i < alerts.Count; i++)
        {
            var a = alerts[i];
            float alpha = a.Age <= AlertSeconds ? 1 : Math.Clamp(1 - (a.Age - AlertSeconds) / AlertFade, 0, 1);
            bool two = a.Detail.Length > 0;
            float h = two ? 48 : 40;
            HudPlate(x, y, width, h, tint: HudPlateTint with { W = HudPlateTint.W * alpha }, rim: HudRimTint with { W = HudRimTint.W * alpha });
            HudRound(x + 14, y + (h - 28) / 2, 28, 28, a.Colour with { W = 0.2f * alpha }, 14);
            HudIcon(a.Icon, x + 21, y + (h - 14) / 2, 14, a.Colour with { W = alpha });
            string title = HudFit(a.Count > 1 ? $"{a.Title}  x{a.Count}" : a.Title, 14, width - 70);
            if (two)
            {
                HudText(title, x + 54, y + 7, width - 68, 18, 14, HudLight with { W = alpha });
                HudText(a.Detail, x + 54, y + 25, width - 68, 16, 12, HudMuted with { W = alpha });
            }
            else HudText(title, x + 54, y, width - 68, h, 14, HudLight with { W = alpha });
            HudTarget("alert-" + i.ToString(CultureInfo.InvariantCulture), V(x, y, width, h), "hud:alert", i.ToString(CultureInfo.InvariantCulture),
                a.Who.IsNone ? Loc.Tr("Click to dismiss") : Loc.Tr("Click to show"));
            y += h + 6;
        }
    }

    // ------------------------------------------------------------------ the character card

    /// <summary>Bottom left: the character (or building) the HUD describes. Returns its right edge (layout units).</summary>
    float HudCard(CharacterSnapshot? lead)
    {
        const float x0 = 24, width = 452, pad = 16;
        if (BuildingFocus() is { } building) return HudBuildingCard(building, x0, width, pad);
        if (lead is null) return 0;
        var body = lead.Body;
        var lines = StateLines(lead);
        var injuries = lead.HealthParts.Select(part => (part, health: part.Flesh + part.Rig - part.Stun, max: MathF.Max(part.Maximum, 1e-3f)))
            .Where(p => p.health < p.max - 0.5f || p.part.Bandage > 0).OrderBy(p => p.health / p.max).ToList();
        string? status = null;
        if (lead.Bounties.Count > 0) { var bounty = BountyLine(lead.Bounties); status = $"{bounty.Label} {bounty.Value}"; }
        string? slave = lead.Slave is { } s ? s.State == 2 ? Loc.Tr("Escaped slave") : Loc.Tr("Slave of {1}", s.Owner.Length > 0 ? s.Owner : Loc.Tr("someone")) : null;

        float content = HudCardMinimised ? 0 : cardTab switch
        {
            CardTab.Skills => MathF.Max(196, 22 * (int)MathF.Ceiling((lead.Hud?.Stats.Count ?? 0) / 2f) + 12 + 22 * ExtendedLines(lead, SpeedUnit).Length),
            CardTab.Jobs => MathF.Max(120, 34 * lead.Jobs.Count + 8),
            _ => 196,
        };
        float height = HudCardMinimised ? pad + 56 + pad : pad + 56 + 12 + 32 + 12 + content + (cardTab == CardTab.Overview ? 12 + 44 : 0) + pad;
        float y0 = 1080 - 24 - height;
        HudPlate(x0, y0, width, height);

        // Header: portrait, name and faction, the health word, minimise / maximise.
        float y = y0 + pad;
        HudPortrait(lead, x0 + pad, y, 56);
        string faction = FactionName?.Invoke(lead.Faction) ?? "";
        HudText(HudFit(lead.Name, 20, 220), x0 + pad + 56 + 12, y + 4, 230, 26, 20, HudLight);
        HudText(HudFit(slave ?? status ?? faction, 12, 220), x0 + pad + 56 + 12, y + 30, 230, 18, 12, slave is not null || status is not null ? HudDanger : HudMuted);
        if (body is { } b)
        {
            var (word, colour) = HealthWord(b);
            HudText(Loc.Tr("Health"), x0 + width - pad - 36 - 120, y + 6, 120, 18, 12, HudMuted, "Right VCenter");
            HudText(word, x0 + width - pad - 36 - 160, y + 24, 160, 22, 15, colour is null ? HudGoodInk : HudDanger, "Right VCenter");
        }
        var ink = HudButton("card-size", x0 + width - pad - 28, y, 28, 28, "hud:card", tip: Loc.Tr(HudCardMinimised ? "Show the whole card" : "Show only the name"));
        HudIcon(HudCardMinimised ? "maximise" : "minimise", x0 + width - pad - 22, y + 6, 16, ink);
        if (HudCardMinimised) return x0 + width;

        // Tabs.
        y += 56 + 12;
        float tx = x0 + pad;
        foreach (var (tab, label) in new[] { (CardTab.Overview, Loc.Tr("Overview")), (CardTab.Skills, Loc.Tr("Skills")), (CardTab.Jobs, Loc.Tr("Jobs")) })
        {
            string text = tab == CardTab.Jobs && lead.Jobs.Count > 0 ? $"{label}  {lead.Jobs.Count}" : label;
            float w = HudMeasure(text, 14) + 24;
            bool on = cardTab == tab;
            var at = V(tx, y, w, 32);
            bool hover = at.Contains(mouse.X, mouse.Y);
            HudText(text, tx, y, w, 30, 14, on ? HudLight : hover ? HudInk : HudMuted, "Center");
            if (on) HudFill(tx, y + 30, w, 2, HudBrass);
            HudTarget("tab-" + tab, at, "hud:tab", ((int)tab).ToString(CultureInfo.InvariantCulture));
            tx += w + 2;
        }
        var gear = HudButton("card-gear", x0 + width - pad - 72, y + 2, 72, 28, "meitou:inventory", active: faithfulScreens.IsOpen("inventory"), tip: WithKey(Loc.Tr("Inventory"), HudKey(InputAction.ToggleInventory)));
        HudText(Loc.Tr("Gear"), x0 + width - pad - 72, y + 2, 72, 28, 13, gear, "Center");
        HudFill(x0 + pad, y + 32, width - 2 * pad, 1, HudInk with { W = 0.14f });
        y += 32 + 12;

        switch (cardTab)
        {
            case CardTab.Skills: HudSkills(lead, x0 + pad, y, width - 2 * pad); break;
            case CardTab.Jobs: HudJobs(lead, x0 + pad, y, width - 2 * pad); break;
            default:
                HudBody(lead, x0 + pad, y);
                HudOverview(lead, lines, injuries, x0 + pad + 112 + 18, y, width - 2 * pad - 112 - 18);
                y += content + 12;
                HudFill(x0 + pad, y - 6, width - 2 * pad, 1, HudInk with { W = 0.14f });
                HudSkillStrip(lead, x0 + pad, y + 4, width - 2 * pad);
                break;
        }
        return x0 + width;
    }

    /// <summary>A character's portrait: its picture from the portrait atlas when it is in the squad bar, else its initials on a tile.</summary>
    void HudPortrait(CharacterSnapshot c, float x, float y, float size, bool ring = true)
    {
        int cell = -1;
        var bar = SquadBarMembers();
        for (int i = 0; i < bar.Count; i++) if (bar[i].Id == c.Id) { cell = i; break; }
        HudRound(x, y, size, size, HudTile, 4);
        if (cell >= 0)
            hudControls.Add(new(HudId("portrait"), "ImageBox", V(x, y, size, size), "", "", "", true,
                new Dictionary<string, string> { ["ImageTexture"] = PortraitTexture, ["ImageFlipV"] = "true", ["ImageRect"] = PortraitRect(cell) }, Vector4.Zero, Pickable: false));
        else HudText(Initials(c.Name), x, y, size, size, size * 0.36f, HudLight, "Center");
        if (ring) HudImage(MeitouHudArt.Ring, V(x - 2, y - 2, size + 4, size + 4), HudBrass, 6);
    }

    static string Initials(string name)
    {
        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length == 0 ? "?" : words.Length == 1 ? words[0][..Math.Min(2, words[0].Length)].ToUpperInvariant() : string.Concat(words[0][0], words[^1][0]).ToUpperInvariant();
    }

    /// <summary>The colour of a body part (or a portrait's strip) by how much of it is left: healthy, hurt, out.</summary>
    static Vector4 HealthColour(float fraction) => fraction >= 0.8f ? HudHealthy : fraction > 0 ? HudHurt : HudOut;

    /// <summary>The body outline: head, chest, stomach, arms and legs, each in the colour of what is left of it; a part's numbers are its tooltip.</summary>
    void HudBody(CharacterSnapshot lead, float x, float y)
    {
        // Placed by type, and for the limbs by side (the parts' names say Left / Right); the viewer's left is the character's right.
        (float X, float Y, float W, float H, float R) Place(Meitou.Simulation.HealthPartView part, int armSeen, int legSeen)
        {
            bool left = part.Name.Contains("Left", StringComparison.OrdinalIgnoreCase) || part.Name.Contains(Loc.Tr("Left"), StringComparison.OrdinalIgnoreCase);
            bool right = part.Name.Contains("Right", StringComparison.OrdinalIgnoreCase) || part.Name.Contains(Loc.Tr("Right"), StringComparison.OrdinalIgnoreCase);
            return part.Type switch
            {
                Meitou.Data.Gameplay.Bodies.BodyPartType.Head => (40, 0, 32, 32, 16),
                Meitou.Data.Gameplay.Bodies.BodyPartType.Arm => left || !right && armSeen > 0 ? (86, 38, 18, 74, 9) : (8, 38, 18, 74, 9),
                Meitou.Data.Gameplay.Bodies.BodyPartType.Leg => left || !right && legSeen > 0 ? (58, 116, 20, 80, 9) : (34, 116, 20, 80, 9),
                _ => part.Name.Contains("Stomach", StringComparison.OrdinalIgnoreCase) || part.Name.Contains(Loc.Tr("Stomach"), StringComparison.OrdinalIgnoreCase)
                    ? (34, 83, 44, 30, 6) : (30, 36, 52, 44, 8),
            };
        }
        int arms = 0, legs = 0;
        foreach (var part in lead.HealthParts)
        {
            var (px, py, pw, ph, pr) = Place(part, arms, legs);
            if (part.Type == Meitou.Data.Gameplay.Bodies.BodyPartType.Arm) arms++;
            if (part.Type == Meitou.Data.Gameplay.Bodies.BodyPartType.Leg) legs++;
            float max = MathF.Max(part.Maximum, 1e-3f), health = part.Flesh + part.Rig - part.Stun;
            var colour = part.Robotic ? Rgb(114, 109, 104) : HealthColour(health / max);
            HudRound(x + px, y + py, pw, ph, colour, pr);
            if (part.Bandage > 0 && health < max) HudRound(x + px + pw / 2 - 2, y + py + 4, 4, ph - 8, HudHunger with { W = 0.6f }, 2);
            HudTarget("body-" + part.Id, V(x + px, y + py, pw, ph), "", tip: $"{part.Name}  {(int)MathF.Floor(health + 0.1f)} / {(int)max}");
        }
    }

    /// <summary>The overview beside the body: blood, hunger and load bars, what the character is doing, and its hurt parts.</summary>
    void HudOverview(CharacterSnapshot lead, HudLine[] lines, List<(Meitou.Simulation.HealthPartView part, float health, float max)> injuries, float x, float y, float width)
    {
        var body = lead.Body;
        var hud = lead.Hud;
        float capacity = MathF.Max(hud?.BloodCapacity ?? 1, 1e-3f), blood = hud?.Blood ?? (body?.Blood ?? 0) * capacity;
        string bleeding = hud is { Bleeding: > 0.001f } h ? "  -" + (h.Bleeding).ToString("0.0", CultureInfo.InvariantCulture) + "/s" : "";
        float hunger = body?.Hunger ?? 0;
        string fed = hunger < 1 ? Loc.Tr("Starving") : hunger < 2 ? Loc.Tr("Hungry") : Loc.Tr("Fed");
        int load = (int)((1 - Math.Clamp(hud?.Encumbrance ?? 1, 0, 1)) * 100);
        var bars = new List<(string Name, string Value, float Fraction, Vector4 Colour, Vector4 Ink)>
        {
            (Loc.Tr("Blood"), $"{(int)MathF.Floor(blood + 0.1f)} / {(int)capacity}{bleeding}", blood / capacity, HudBlood, bleeding.Length > 0 ? HudDanger : HudLight),
        };
        if (hud is not { Robot: true }) bars.Add((Loc.Tr("Hunger"), $"{fed} · {(int)(hunger * 100)} / 300", hunger / 3, HudHunger, hunger < 2 ? HudDanger : HudLight));
        bars.Add((Loc.Tr("Load"), load.ToString(CultureInfo.InvariantCulture) + "%", load / 100f, HudLoad, load >= 50 ? HudDanger : HudLight));
        foreach (var bar in bars)
        {
            HudText(bar.Name, x, y, width / 2, 16, 12, HudMuted);
            HudText(bar.Value, x + width / 2, y, width / 2, 16, 12, bar.Ink, "Right VCenter");
            HudBar(x, y + 19, width, 6, bar.Fraction, bar.Colour);
            y += 32;
        }
        y += 2;
        // What it is doing: the state (when it is not plain Normal) and the goal.
        var rows = new List<(string Label, string Value, Vector4 Ink)>();
        if (lines.Length > 0 && lines[0].Value != Loc.Tr("Normal")) rows.Add((Loc.Tr("State"), lines[0].Value, lines[0].ValueColour is null ? HudLight : HudDanger));
        if (lines.Length > 1 && lines[1].Value.Length > 0) rows.Add((Loc.Tr("Doing"), lines[1].Value, HudLight));
        foreach (var row in rows.Take(2))
        {
            HudText(row.Label, x, y, 50, 18, 13, HudMuted);
            HudText(row.Value, x + 50, y, width - 50, 18, 13, row.Ink);
            y += 22;
        }
        // The hurt parts, worst first: the room left holds three, then "+N more".
        int room = Math.Max(0, (int)((196 - 3 * 32 - 2 - rows.Take(2).Count() * 22 - 4) / 30));
        for (int i = 0; i < injuries.Count && i < room; i++)
        {
            var (part, health, max) = injuries[i];
            bool more = i == room - 1 && injuries.Count > room;
            float f = health / max;
            var colour = f <= 0 ? HudOut : HudHurt;
            HudRound(x, y + 2, width, 24, (f <= 0 ? HudBadFill : HudHurt) with { W = 0.16f }, 7);
            HudRound(x + 8, y + 10, 8, 8, f <= 0 ? HudEnemy : colour, 4);
            HudText(HudFit(part.Name, 13, width * 0.42f), x + 22, y + 2, width * 0.44f, 24, 13, HudLight);
            HudText($"{(int)MathF.Floor(health + 0.1f)} / {(int)max}", x + width * 0.48f, y + 2, width * 0.26f, 24, 13, f <= 0 ? HudDanger : HudHurtInk);
            string note = more ? Loc.Tr("+{1} more", injuries.Count - room + 1) : f <= -1 ? Loc.Tr("crippled") : f <= 0 ? Loc.Tr("out") : part.Bandage > 0 ? Loc.Tr("bandaged") : "";
            HudText(note, x + width * 0.72f, y + 2, width * 0.28f - 8, 24, 12, HudMuted, "Right VCenter");
            y += 30;
        }
    }

    /// <summary>The six skills the fights use, in one row under the overview.</summary>
    void HudSkillStrip(CharacterSnapshot lead, float x, float y, float width)
    {
        if (lead.Skills is not { } s) return;
        (string Label, float Value)[] cells = [("ATK", s.Attack), ("DEF", s.Defence), ("DDG", s.Dodge), ("TGH", s.Toughness), ("STR", s.Strength), ("ATH", s.Athletics)];
        float w = width / cells.Length;
        for (int i = 0; i < cells.Length; i++)
        {
            HudText(cells[i].Label, x + i * w, y, w, 14, 10, HudMuted, "Center");
            HudText(((int)cells[i].Value).ToString(CultureInfo.InvariantCulture), x + i * w, y + 15, w, 22, 16, HudLight, "Center");
        }
    }

    /// <summary>The Skills tab: the stats panel's lines in two columns (the fight value, its change from the trained one), then the extended lines.</summary>
    void HudSkills(CharacterSnapshot lead, float x, float y, float width)
    {
        var stats = StatsLines(lead);
        float column = (width - 16) / 2;
        for (int i = 0; i < stats.Length; i++)
        {
            float cx = x + i % 2 * (column + 16), cy = y + i / 2 * 22;
            HudText(stats[i].Label, cx, cy, column * 0.6f, 20, 13, HudMuted);
            HudText(stats[i].Value, cx + column * 0.5f, cy, column * 0.5f, 20, 13, InkOf(stats[i].ValueColour), "Right VCenter");
        }
        y += 22 * MathF.Ceiling(stats.Length / 2f) + 12;
        foreach (var line in ExtendedLines(lead, SpeedUnit))
        {
            HudText(line.Label, x, y, width * 0.6f, 20, 13, HudMuted, tip: line.Tooltip);
            HudText(line.Value, x + width * 0.5f, y, width * 0.5f, 20, 13, InkOf(line.ValueColour), "Right VCenter");
            y += 22;
        }
    }

    static Vector4 InkOf(string? colour) => colour == BadColour ? HudDanger : colour == GoodColour ? HudGoodInk : HudLight;

    /// <summary>The Jobs tab: the lead's jobs, one row each with an X (dragged to reorder, as the original's list), or the hint.</summary>
    void HudJobs(CharacterSnapshot lead, float x, float y, float width)
    {
        if (lead.Jobs.Count == 0)
        {
            HudText(Loc.Tr("[No jobs assigned]"), x, y, width, 22, 13, HudMuted);
            HudText(Loc.Tr("[To add jobs, hold shift key when giving an order]"), x, y + 24, width, 22, 12, HudMuted);
            return;
        }
        for (int i = 0; i < lead.Jobs.Count; i++)
        {
            var job = lead.Jobs[i];
            string text = Loc.Tr(PermaJobs.Text(job.Type) ?? job.Type.ToString());
            if (job.Site is { } id && session.CurrentSnapshot.Buildings.FirstOrDefault(s => s.Id == id) is { } site) text += ": " + SiteName(site);
            string argument = "job:" + i.ToString(CultureInfo.InvariantCulture);
            float ry = y + i * 34;
            var row = V(x, ry, width, 30);
            bool hover = row.Contains(mouse.X, mouse.Y);
            HudRound(x, ry, width, 30, hover ? HudInk with { W = 0.1f } : HudButtonFill, 7);
            HudText((i + 1).ToString(CultureInfo.InvariantCulture), x + 8, ry, 18, 30, 12, HudMuted);
            HudText(HudFit(text, 13, width - 28 - 40), x + 28, ry, width - 28 - 36, 30, 13, HudLight);
            // The row takes presses as the jobs list's rows do (JobDragStart reads ListOwner and the argument).
            hudControls.Add(new("hud-job-" + i.ToString(CultureInfo.InvariantCulture), "Widget", row, "", "", "", true,
                new Dictionary<string, string> { ["ListOwner"] = "JobsPanel" }, Vector4.Zero, Argument: argument, Pickable: true));
            var ink = HudButton("job-remove-" + i.ToString(CultureInfo.InvariantCulture), x + width - 30, ry + 3, 24, 24, "meitou:job-remove", argument, tip: Loc.Tr("Remove this job"));
            HudIcon("close", x + width - 25, ry + 8, 14, ink);
        }
    }

    /// <summary>The card for a selected building: its name and owner, its status, its condition rows, then its lines (with their buttons).</summary>
    float HudBuildingCard(SelectedBuilding focus, float x0, float width, float pad)
    {
        var (name, state, stats, status, bad) = BuildingLines(focus);
        var rows = BuildingRows(focus);
        float height = pad + 48 + 12 + rows.Count * 32 + (rows.Count > 0 ? 6 : 0) + (state.Length + stats.Length) * 28 + pad;
        float y0 = 1080 - 24 - height, y = y0 + pad, inner = width - 2 * pad, x = x0 + pad;
        HudPlate(x0, y0, width, height);
        HudRound(x, y, 48, 48, HudTile, 4);
        HudIcon("build", x + 12, y + 12, 24, HudInk);
        HudText(name.Label, x + 60, y + 2, inner - 60 - 140, 26, 18, HudLight);
        HudText(name.Value, x + 60, y + 28, inner - 60 - 140, 18, 12, HudMuted);
        HudText(status, x + inner - 140, y, 140, 48, 15, bad ? HudDanger : HudGoodInk, "Right VCenter");
        y += 48 + 12;
        foreach (var row in rows)
        {
            HudText(row.Name, x, y, inner / 2, 16, 12, HudMuted, tip: row.Tooltip);
            HudText(row.Value, x + inner / 2, y, inner / 2, 16, 12, HudLight, "Right VCenter");
            HudBar(x, y + 19, inner, 6, row.Green, HealthColour(row.Green));
            y += 32;
        }
        if (rows.Count > 0) y += 6;
        int n = 0;
        foreach (var line in state.Concat(stats))
        {
            n++;
            if (line.Action is { } action)
            {
                float bw = inner * Math.Clamp(line.ButtonWidth, 0.1f, 1);
                var ink = HudButton("building-" + n.ToString(CultureInfo.InvariantCulture), x + inner - bw, y + 1, bw, 24, action, tip: line.Tooltip is { } t ? Loc.Tr(t) : null);
                HudText(Loc.Tr(line.Value), x + inner - bw, y + 1, bw, 24, 13, InkOf(line.ValueColour) == HudLight ? ink : InkOf(line.ValueColour), "Center");
                HudText(Loc.Tr(line.Label), x, y, inner - bw - 8, 26, 13, HudMuted);
            }
            else
            {
                HudText(Loc.Tr(line.Label), x, y, inner * 0.5f, 26, 13, HudMuted, tip: line.Tooltip is { } t ? Loc.Tr(t) : null);
                HudText(Loc.Tr(line.Value), x + inner * 0.4f, y, inner * 0.6f, 26, 13, InkOf(line.ValueColour), "Right VCenter");
            }
            y += 28;
        }
        return x0 + width;
    }

    // ------------------------------------------------------------------ the windows

    /// <summary>Bottom right: a button per window, lit while it is open. Returns its left edge (layout units).</summary>
    float HudWindows()
    {
        bool overview = faithfulScreens.IsOpen("overview");
        int tab = overview ? faithfulScreens.GetTab("overview", "TabsMain") : -1;
        (string Name, string Short, string Icon, string Action, string Argument, InputAction? Key, bool On)[] windows =
        [
            (Loc.Tr("Inventory"), Loc.Tr("Bag"), "inventory", "meitou:inventory", "", InputAction.ToggleInventory, faithfulScreens.IsOpen("inventory")),
            (Loc.Tr("Character"), Loc.Tr("Stats"), "character", "meitou:stats", "", InputAction.ToggleCharacterStats, faithfulScreens.IsOpen("stats")),
            (Loc.Tr("Map"), Loc.Tr("Map"), "map", "hud:overview", "0", InputAction.ToggleMap, tab == 0),
            (Loc.Tr("Faction"), Loc.Tr("Faction"), "faction", "hud:overview", "1", InputAction.ToggleFaction, tab == 1),
            (Loc.Tr("Research"), Loc.Tr("Tech"), "research", "hud:overview", "2", InputAction.ToggleResearch, tab == 2),
            (Loc.Tr("Crafting"), Loc.Tr("Craft"), "crafting", "hud:overview", "3", InputAction.ToggleCrafting, tab == 3),
            (Loc.Tr("Squads"), Loc.Tr("Squads"), "squad", "hud:overview", "4", null, tab == 4),
            (Loc.Tr("Build"), Loc.Tr("Build"), "build", "meitou:build", "", InputAction.ToggleBuild, buildMode),
        ];
        float width = 8 + windows.Length * 56 + (windows.Length - 1) * 4 + 8, x = HudWidth - 24 - width, y = 1080 - 24 - 72;
        HudPlate(x, y, width, 72);
        float bx = x + 8;
        foreach (var w in windows)
        {
            string key = w.Key is { } k ? HudKey(k) : "";
            var ink = HudButton("window-" + w.Icon, bx, y + 8, 56, 56, w.Action, w.Argument, w.On, WithKey(w.Name, key));
            HudIcon(w.Icon, bx + 18, y + 16, 20, ink);
            HudText(w.Short, bx, y + 40, 56, 16, 10, ink, "Center");
            if (key.Length > 0 && key.Length <= 2) HudText(key, bx + 40, y + 9, 14, 12, 9, HudMuted, "Right VCenter");
            bx += 60;
        }
        return x;
    }

    // ------------------------------------------------------------------ the orders and the squad

    /// <summary>Bottom centre, between the card and the windows: the orders for the selection, and under them the squad tabs and portraits.</summary>
    void HudSquad(float left, float right)
    {
        var tabs = SquadTabSquads();
        var members = SquadBarMembers();
        if (PlayerCharacters().Count == 0) { squadBarRow = 0; return; }
        float space = right - 24 - (left + 24), centre = (left + right) / 2;

        // The portraits: up to two rows, cells shrinking to fit.
        const float gap = 8;
        int n = Math.Max(1, members.Count);
        float cell = 72;
        int columns = Math.Max(1, (int)((space - 28 + gap) / (cell + gap)));
        if (n > columns)
        {
            float fit = (space - 28 + gap) / MathF.Ceiling(n / 2f) - gap;
            cell = Math.Clamp(fit, 48, 72);
            columns = Math.Max(1, (int)((space - 28 + gap) / (cell + gap)));
        }
        int rows = (n + columns - 1) / columns, shownRows = Math.Min(rows, 2);
        float pitch = cell + 26, rowWidth = Math.Min(n, columns) * (cell + gap) - gap;

        // The tabs' row.
        float tabsWidth = 0;
        var tabLabels = tabs.Select(t => HudFit(t.Name.ToUpperInvariant(), 13, 150)).ToList();
        var tabWidths = tabs.Select((t, i) => HudMeasure(tabLabels[i], 13) + 8 + HudMeasure(t.Members.Count.ToString(CultureInfo.InvariantCulture), 13) + 20).ToList();
        tabsWidth = tabWidths.Sum() + 2 * tabs.Count + 30;
        float dockWidth = MathF.Min(space, MathF.Max(rowWidth, tabsWidth) + 28);
        float dockHeight = 10 + 26 + 8 + shownRows * pitch - 4 + 12;
        float dx = MathF.Round(centre - dockWidth / 2), dy = 1080 - 24 - dockHeight;
        HudPlate(dx, dy, dockWidth, dockHeight);
        float tx = dx + 14;
        var active = session.CurrentSnapshot.ActiveSquad;
        for (int i = 0; i < tabs.Count; i++)
        {
            bool on = tabs[i].Id == active;
            var at = V(tx, dy + 10, tabWidths[i], 26);
            bool hover = at.Contains(mouse.X, mouse.Y);
            string label = tabLabels[i];
            HudText(label, tx + 10, dy + 10, tabWidths[i] - 10, 24, 13, on ? HudLight : hover ? HudInk : HudMuted);
            HudText(tabs[i].Members.Count.ToString(CultureInfo.InvariantCulture), tx + 10 + HudMeasure(label, 13) + 8, dy + 10, 30, 24, 13, HudMuted);
            if (on) HudFill(tx, dy + 34, tabWidths[i], 2, HudBrass);
            HudTarget("squad-tab-" + i.ToString(CultureInfo.InvariantCulture), at, "hud:squad-tab", i.ToString(CultureInfo.InvariantCulture),
                WithKey(Loc.Tr("Show this squad; double-click selects it"), HudKey(InputAction.ChangeSquad)));
            tx += tabWidths[i] + 2;
        }
        var plus = HudButton("squad-new", tx + 4, dy + 11, 24, 24, "hud:squad-new", tip: Loc.Tr("New squad"));
        HudIcon("plus", tx + 10, dy + 17, 12, plus);
        if (rows > shownRows && tabsWidth + 170 <= dockWidth)
        {
            int hidden = members.Count - shownRows * columns;
            HudText(Loc.Tr("Scroll for {1} more", Math.Max(hidden, 0)), dx + dockWidth - 14 - 160, dy + 10, 160, 24, 12, HudMuted, "Right VCenter");
        }

        // The portraits' page (the squad bar's: box selection and the wheel use it).
        float py = dy + 10 + 26 + 8, px = MathF.Round(centre - rowWidth / 2);
        squadBarPage = V(px, py, columns * (cell + gap), shownRows * pitch);
        squadBarRow = MathF.Round((cell + gap) * hs);
        squadBarScroll = Math.Clamp(squadBarScroll, 0, Math.Max(0, (rows - shownRows) * pitch * hs));
        var camera = session.Camera.Current;
        PrunePortraitFlashes(members);
        for (int i = 0; i < members.Count; i++)
        {
            float cx = px + i % columns * (cell + gap), cy = py + i / columns * pitch - squadBarScroll / hs;
            if (cy < py - 1 || cy + pitch > py + shownRows * pitch + 1) continue;
            HudMember(members[i], i, cx, cy, cell, PortraitStateOf(members[i], camera.Eye, camera.Target));
        }

        // The orders over the dock.
        HudOrders(centre, dy - 10, space);
    }

    /// <summary>A squad member's cell: its portrait, number, marks (hungry, needs first aid, knocked out with the time left, dead), name and health strip; the click selects.</summary>
    void HudMember(CharacterSnapshot m, int index, float x, float y, float size, PortraitState state)
    {
        bool down = m.Body is { Unconscious: true }, dead = m.Body is { Dead: true };
        HudRound(x, y, size, size, (state & PortraitState.Hurt) != 0 ? HudBadFill with { W = 0.55f * PortraitFlash(m.Id, state) } : HudTile, 4);
        var portrait = new Dictionary<string, string> { ["ImageTexture"] = PortraitTexture, ["ImageFlipV"] = "true", ["ImageRect"] = PortraitRect(index) };
        if ((state & PortraitState.Faded) != 0) portrait["Alpha"] = "0.35";
        hudControls.Add(new(HudId("member-image"), "ImageBox", V(x, y, size, size), "", "", "", true, portrait, Vector4.Zero, Pickable: false));
        if (m.Selected) HudImage(MeitouHudArt.Ring, V(x - 2, y - 2, size + 4, size + 4), HudBrass, 6);
        else HudImage(MeitouHudArt.Rim, V(x, y, size, size), down || dead ? HudEnemy with { W = 0.5f } : HudRimTint, 4);
        if (index < 10) HudText(index == 9 ? "0" : (index + 1).ToString(CultureInfo.InvariantCulture), x + 4, y + 2, 16, 14, 11, HudMuted);
        float badge = x + size - 20;
        if ((state & PortraitState.Hunger) != 0) { HudRound(badge, y + 4, 16, 16, HudHunger, 8); HudIcon("food", badge + 2, y + 6, 12, Rgb(20, 8, 6)); badge -= 18; }
        if (m.Body is { NeedsAid: true } && !dead) { HudRound(badge, y + 4, 16, 16, HudBadFill, 8); HudIcon("drop", badge + 2, y + 6, 12, Rgb(20, 8, 6)); }
        if (down || dead)
        {
            HudRound(x, y + size - 20, size, 20, new Vector4(8 / 255f, 6 / 255f, 5 / 255f, 0.8f), 4);
            string text = dead ? Loc.Tr("DEAD") : m.Body is { KoTimer: > 0 } b ? $"{Loc.Tr("OUT")} {(int)b.KoTimer / 60}:{(int)b.KoTimer % 60:00}" : Loc.Tr("OUT");
            HudText(text, x, y + size - 20, size, 20, 11, HudDanger, "Center");
        }
        HudText(HudFit(m.Name, 12, size + 6), x - 4, y + size + 2, size + 8, 14, 12, m.Selected ? HudLight : down || dead ? HudMuted : HudInk, "Center");
        float health = m.Body is { } h ? MathF.Min(h.WorstPart, h.Blood) : 1;
        HudBar(x, y + size + 18, size, 4, MathF.Max(health, 0.03f), dead ? HudOut : HealthColour(health));
        hudControls.Add(new("hud-member-" + index.ToString(CultureInfo.InvariantCulture), "Button", V(x, y, size, size + 22), "", "meitou:member", "", true,
            UiTooltips.TextProperties(Loc.Tr("{1}: click selects, double-click follows", m.Name)), Vector4.Zero, Argument: index.ToString(CultureInfo.InvariantCulture), Pickable: true));
    }

    /// <summary>The order toggles for the selection in groups (stance, engage, move, duties), each lit while the lead selected character has it; the hotkeys in the corners.</summary>
    void HudOrders(float centre, float bottom, float space)
    {
        var own = Selected().FirstOrDefault() ?? Lead();
        bool any = own is not null && Selected().Count > 0;
        bool Has(CharacterOrders o) => own is not null && (own.Orders & o) != 0;
        string run = own is not null ? Loc.Tr(RunSpeedIcons[Math.Min((int)own.RunSpeed, RunSpeedIcons.Length - 1)]) : Loc.Tr("Walk");
        (string Group, (string Icon, string Name, string Action, bool On, InputAction? Key, string? Label)[] Items)[] groups =
        [
            (Loc.Tr("STANCE"), [("block", Loc.Tr("Block"), "meitou:order.Block", Has(CharacterOrders.Block), InputAction.ToggleBlock, null),
                ("hold", Loc.Tr("Hold position"), "meitou:order.Hold", Has(CharacterOrders.Hold), InputAction.ToggleHold, null),
                ("passive", Loc.Tr("Passive"), "meitou:order.Passive", Has(CharacterOrders.Passive), InputAction.TogglePassive, null)]),
            (Loc.Tr("ENGAGE"), [("ranged", Loc.Tr("Ranged"), "meitou:order.Ranged", Has(CharacterOrders.Ranged), InputAction.ToggleRanged, null),
                ("taunt", Loc.Tr("Taunt"), "meitou:order.Taunt", Has(CharacterOrders.Taunt), InputAction.ToggleTaunt, null),
                ("attack", Loc.Tr("Attack all"), "meitou:order.AttackAll", Has(CharacterOrders.AttackAll), null, null)]),
            (Loc.Tr("MOVE"), [("sneak", Loc.Tr("Sneak"), "meitou:order.Sneak", Has(CharacterOrders.Sneak), InputAction.ToggleSneak, null),
                ("run", Loc.Tr("Run speed"), "meitou:runspeed.1", false, InputAction.CycleRunSpeed, run)]),
            (Loc.Tr("DUTIES"), [("medic", Loc.Tr("Medic"), "meitou:medic", own?.Body is { MedicJob: true }, InputAction.Medic, null),
                ("rescue", Loc.Tr("Rescue"), "meitou:rescue", own?.Body is { RescueJob: true }, InputAction.Rescue, null),
                ("prospect", Loc.Tr("Prospect"), "meitou:prospect", faithfulScreens.IsOpen("prospect"), InputAction.Prospect, null),
                ("jobs", Loc.Tr("Do jobs"), "meitou:order.Jobs", Has(CharacterOrders.Jobs), InputAction.ToggleJobs, null)]),
        ];
        float ButtonWidth((string Icon, string Name, string Action, bool On, InputAction? Key, string? Label) b) => b.Label is { } l ? 12 + 20 + 6 + HudMeasure(l, 13) + 12 : 44;
        float width = 10 + groups.Sum(g => g.Items.Sum(ButtonWidth) + (g.Items.Length - 1) * 4) + (groups.Length - 1) * 10 + 10;
        // Narrow screens: the strip keeps its order and shrinks to the room between the card and the windows.
        float k = Math.Clamp(space / width, 0.6f, 1);
        float height = 10 + 14 + 4 + 44 + 10;
        float x = MathF.Round(centre - width * k / 2), y = bottom - height * k;
        HudPlate(x, y, width * k, height * k);
        float gx = x + 10 * k;
        foreach (var (group, items) in groups)
        {
            HudText(group, gx + 2 * k, y + 8 * k, 120 * k, 14 * k, 10 * k, HudMuted);
            float bx = gx, by = y + (10 + 14 + 4) * k;
            foreach (var item in items)
            {
                float bw = ButtonWidth(item) * k;
                string key = item.Key is { } ik ? HudKey(ik) : "";
                bool enabled = any || item.Action is "meitou:prospect";
                var ink = HudButton("order-" + item.Icon, bx, by, bw, 44 * k, item.Action, active: item.On && any, tip: WithKey(item.Name, key), enabled: enabled);
                if (item.Label is { } label)
                {
                    HudIcon(item.Icon, bx + 12 * k, by + 12 * k, 20 * k, ink);
                    HudText(label, bx + (12 + 20 + 6) * k, by, bw - (12 + 20 + 6) * k, 44 * k, 13 * k, ink);
                }
                else HudIcon(item.Icon, bx + 12 * k, by + 12 * k, 20 * k, ink);
                if (key.Length > 0) HudText(key.StartsWith("Num ", StringComparison.Ordinal) ? key[4..] : key, bx + bw - 16 * k, by + 30 * k, 13 * k, 12 * k, 9 * k, HudMuted, "Right VCenter");
                bx += bw + 4 * k;
            }
            gx = bx - 4 * k + 10 * k;
        }
    }

    // ------------------------------------------------------------------ the pointer's card

    /// <summary>
    /// Another faction's character under the pointer: its name in the colour of the order a right click would give, its faction, how it is, its health and what a right click does.
    /// Drawn beside the pointer and never in its way (it takes no clicks).
    /// </summary>
    void HudHoverCard()
    {
        if (pointerOverUi || dragging || HoverTarget is not { } hover) return;
        if (session.CurrentSnapshot.Characters.FirstOrDefault(c => c.Id == hover.Id) is not { IsPlayer: false } c) return;
        string faction = FactionName?.Invoke(c.Faction) ?? "";
        var (word, bad) = c.Body is { } body ? HealthWord(body) : ("", null);
        string hint = PointerName switch
        {
            "attk" => Loc.Tr("Right-click to attack"), "talk" => Loc.Tr("Right-click to talk"), "med" => Loc.Tr("Right-click to treat"),
            "lift" => Loc.Tr("Right-click to pick up"), "search" => Loc.Tr("Right-click to loot"), _ => "",
        };
        const float width = 248;
        float height = 12 + 22 + 16 + 8 + 22 + (hint.Length > 0 ? 18 : 0) + 12;
        float x = mouse.X / hs + 24, y = mouse.Y / hs + 24;
        if (x + width > HudWidth - 8) x = mouse.X / hs - 24 - width;
        if (y + height > 1080 - 8) y = mouse.Y / hs - 24 - height;
        var tint = hover.Colour with { W = 1 };
        var start = hudControls.Count;
        HudPlate(x, y, width, height, rim: tint with { W = 0.45f });
        float cy = y + 12;
        HudRound(x + 14, cy + 7, 8, 8, tint, 4);
        HudText(c.Name, x + 28, cy, width - 42, 22, 16, HudLight); cy += 22;
        HudText(faction, x + 14, cy, width - 28, 16, 12, HudMuted); cy += 16 + 8;
        float health = c.Body is { } h ? MathF.Min(h.WorstPart, h.Blood) : 1;
        HudText(word, x + 14, cy, width - 28, 14, 12, bad is null ? HudGoodInk : HudDanger);
        HudText(((int)MathF.Round(Math.Clamp(health, 0, 1) * 100)).ToString(CultureInfo.InvariantCulture) + "%", x + 14, cy, width - 28, 14, 12, HudLight, "Right VCenter");
        HudBar(x + 14, cy + 16, width - 28, 6, health, HealthColour(health)); cy += 22;
        if (hint.Length > 0) HudText(hint, x + 14, cy + 2, width - 28, 16, 11, HudMuted);
        // The card never takes the mouse: a click goes to the character under it.
        for (int i = start; i < hudControls.Count; i++) hudControls[i] = hudControls[i] with { Pickable = false };
    }

    // ------------------------------------------------------------------ clicks

    /// <summary>A click on one of the HUD's own (<c>hud:</c>) targets.</summary>
    void MeitouHudAction(string action, string argument)
    {
        int.TryParse(argument, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n);
        switch (action)
        {
            case "card": HudCardMinimised = !HudCardMinimised; Sound("General_Click"); break;
            case "tab": cardTab = (CardTab)Math.Clamp(n, 0, 2); Sound("General_Click"); break;
            case "outpost": outpostOpen = !outpostOpen; townPlaced = false; Sound("General_Click"); break;
            case "town-public":
                if (PickTown() is { } town && town.Owner == session.World.Player.Faction)
                {
                    Sound("General_Click");
                    Say(new SetTownPublic(town.Number, !town.Public) { Tick = session.World.Tick });
                }
                break;
            case "alert":
                if (n >= 0 && n < alerts.Count)
                {
                    var a = alerts[n];
                    alerts.RemoveAt(n);
                    if (!a.Who.IsNone) CentreOn(a.Who, follow: false);
                }
                break;
            case "overview":
                if (faithfulScreens.IsOpen("overview") && faithfulScreens.GetTab("overview", "TabsMain") == n) faithfulScreens.Close("overview");
                else { faithfulScreens.Open("overview"); faithfulScreens.SetTab("overview", "TabsMain", n); }
                UpdateMenuPause();
                break;
            case "squad-tab": PickSquadTab(n); break;
            case "squad-new": Say(new NewSquadCommand(session.Camera.Current.Target) { Tick = session.World.Tick }); break;
        }
    }

    /// <summary>The outpost panel opened under the outpost line: moved there once, after it was first laid out (the original's place is under the old bar).</summary>
    void PlaceTownScreen(MyGuiScreenFrame screen)
    {
        if (townPlaced || UiMode != UiMode.Meitou || screen.Controls.Count == 0) return;
        var root = screen.Controls.FirstOrDefault(c => c.Name == "TownPanel")?.Bounds ?? screen.Controls[0].Bounds;
        var want = new Vector2(Width - 24 * hs - root.Width, (20 + 48 + 8) * hs);
        faithfulScreens.SetOffset(TownScreen, faithfulScreens.GetOffset(TownScreen) + want - new Vector2(root.X, root.Y));
        townHeight = root.Height / hs;
        townPlaced = true;
    }
}
