using System.Globalization;
using System.Numerics;
using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Simulation;
using Meitou.Simulation.Bodies;

namespace Meitou.Game;

// The cheat menu's six pages. Each draws into the scrolled content area (C(...), clipped to the viewport) and returns its content height in layout units.
sealed partial class CheatMenu
{
    readonly Field nameField = new("name");
    readonly Field moneyField = new("money", numeric: true);
    readonly Field itemSearch = new("item-search");
    readonly Field spawnSearch = new("spawn-search");
    readonly Field factionFilter = new("faction-filter");
    bool altTeleport;

    /// <summary>Whether Alt + right-click teleports the selection (the Character page's switch; the host asks before it makes a move order).</summary>
    public bool AltTeleport => altTeleport;

    void RefreshFields() => nameField.Text = Cold(Primary)?.Name ?? "";

    // ------------------------------------------------------------------ Character

    float DrawCharacter()
    {
        var clip = viewport;
        var ids = Targets();
        var cold = ids.Count > 0 ? Cold(ids[0]) : null;
        if (focused != nameField && cold is not null && nameField.Text.Length == 0) nameField.Text = cold.Name;

        Header("Character", 0);
        // Identity
        Card(C(212, 46, 434, 150), clip);
        Label("IDENTITY", C(226, 54, 300, 18), clip);
        Text("Name", C(226, 80, 100, 30), 14, clip: clip);
        EditField(nameField, C(316, 80, 222, 30), "Name", clip);
        Button("Rename", C(546, 80, 86, 30), () => Rename(nameField.Text), clip: clip, enabled: cold is not null);
        Text("Appearance", C(226, 118, 100, 30), 14, clip: clip);
        Button("Open character editor", C(316, 118, 316, 30), EditAppearance, clip: clip, enabled: cold is { IsPlayer: true });
        Text("Faction", C(226, 156, 100, 30), 14, clip: clip);
        Text(cold is null ? "" : FactionName(cold.Faction) + (cold.IsPlayer ? " (yours)" : ""), C(316, 156, 316, 30), 14, LightInk, clip: clip);

        // Money
        long money = world().Player.Money;
        Card(C(666, 46, 434, 150), clip);
        Label("MONEY", C(680, 54, 300, 18), clip);
        Text("c." + money.ToString("N0", CultureInfo.InvariantCulture), C(680, 76, 400, 30), 20, LightInk, clip: clip);
        moneyField.Text = moneyField.Text.Length == 0 && focused != moneyField ? "" : moneyField.Text;
        EditField(moneyField, C(680, 114, 200, 30), "Amount", clip);
        Button("Add", C(888, 114, 96, 30), () => SetMoney(money + (long)Parse(moneyField.Text, 0)), clip: clip);
        Button("Set", C(990, 114, 96, 30), () => SetMoney((long)Parse(moneyField.Text, money)), clip: clip);
        long[] steps = [1_000, 10_000, 100_000];
        for (int i = 0; i < steps.Length; i++)
        {
            long step = steps[i];
            Button("+" + step.ToString("N0", CultureInfo.InvariantCulture), C(680 + i * 136, 154, 128, 30), () => SetMoney(money + step), clip: clip);
        }

        // Quick actions
        Card(C(212, 210, 888, 178), clip);
        Label("QUICK ACTIONS", C(226, 218, 300, 18), clip);
        bool any = ids.Count > 0;
        string who = ids.Count == 1 ? cold?.Name ?? "" : $"{ids.Count} characters";
        Button("Heal fully", C(226, 244, 280, 36), () => { Send(new DebugHeal(ids) { Tick = Now }); Notice($"Healed {who}."); }, primary: true, clip: clip, enabled: any);
        Button("Max all skills", C(516, 244, 280, 36), () => { Send(new CheatSetStat(ids, StatsEnumerated.None, 100) { Tick = Now }); Notice($"Every skill 100 for {who}."); }, clip: clip, enabled: any);
        Button("Restore lost limbs", C(806, 244, 280, 36), () => { Send(new CheatRegrowLimbs(ids) { Tick = Now }); Notice($"Lost limbs regrown for {who}."); }, clip: clip, enabled: any);
        Button("Teleport to camera", C(226, 288, 280, 36), () => Teleport(ids), clip: clip, enabled: any);
        bool recruitable = ids.Any(id => Cold(id) is { IsPlayer: false });
        Button("Recruit into your squad", C(516, 288, 280, 36), () =>
        {
            foreach (var id in ids) if (Cold(id) is { IsPlayer: false }) Send(new CheatRecruit(id) { Tick = Now });
        }, clip: clip, enabled: recruitable);
        Button("Knock out", C(806, 288, 280, 36), () => { Send(new CheatBody(ids, CheatBodyAction.KnockOut) { Tick = Now }); Notice($"Knocked out {who}."); }, clip: clip, enabled: any);
        Text("Alt + right-click on the ground teleports the selected characters", C(226, 340, 640, 30), 14, clip: clip);
        Segmented(C(926, 342, 160, 26), ["Off", "On"], altTeleport ? 1 : 0, i => altTeleport = i == 1, clip);
        return 400;
    }

    void Rename(string name)
    {
        name = name.Trim();
        if (name.Length == 0 || Primary.IsNone) { Notice("Type a name first."); return; }
        Send(new CheatRename(Primary, name) { Tick = Now });
        focused = null;
        Notice($"Renamed to {name}.");
    }

    void SetMoney(long amount)
    {
        amount = Math.Max(0, amount);
        Send(new CheatSetMoney(amount) { Tick = Now });
        moneyField.Text = "";
        focused = null;
        Notice("Money set to c." + amount.ToString("N0", CultureInfo.InvariantCulture) + ".");
    }

    void EditAppearance()
    {
        var mine = Targets().Where(id => Cold(id) is { IsPlayer: true }).ToList();
        if (mine.Count == 0) { Notice("The character editor works on your own characters."); return; }
        Visible = false;
        if (!player.OpenEditor(Meitou.Simulation.Editing.EditKind.Recruit, mine)) Notice("The character editor could not open.");
    }

    void Teleport(IReadOnlyList<CharacterId> ids)
    {
        var at = cameraTarget();
        Send(new DebugTeleport(ids, at) { Tick = Now });
        Notice($"Teleported {ids.Count} to the camera.");
    }

    /// <summary>The host's Alt + right-click: teleports the selection to <paramref name="ground"/>.</summary>
    public void TeleportSelection(Vector3 ground)
    {
        var selection = world().Player.Selection.ToArray();
        if (selection.Length == 0) return;
        Send(new DebugTeleport(selection, ground) { Tick = Now });
    }

    // ------------------------------------------------------------------ Health

    float DrawHealth()
    {
        var clip = viewport;
        Header("Health", 0);
        var ids = Targets();
        var cold = ids.Count > 0 ? Cold(ids[0]) : null;
        if (cold?.Medical is not { } medical)
        {
            Text("Choose a character with a body: select one of yours or inspect another.", C(212, 56, 888, 30), 14, MutedInk, clip: clip);
            return 100;
        }
        var id = ids[0];
        float capacity = cold.Race is { } race ? MedicalState.BloodCapacity(race, cold.Stats?.Strength ?? 50, cold.Stats is not null) : medical.Blood;
        Card(C(212, 46, 434, 44), clip);
        Text("Blood", C(226, 46, 90, 44), 14, clip: clip);
        float blood = Slider("blood", C(316, 46, 220, 44), capacity > 0 ? medical.Blood / capacity : 0,
            f => Send(new CheatSetVitals(ids, Blood: f * capacity) { Tick = Now }), clip);
        Text($"{Number(Dragging("blood") ? blood * capacity : medical.Blood)} / {Number(capacity)}", C(540, 46, 96, 44), 13, LightInk, "Right VCenter", clip);
        Card(C(666, 46, 434, 44), clip);
        Text("Hunger", C(680, 46, 90, 44), 14, clip: clip);
        float hunger = Slider("hunger", C(770, 46, 220, 44), medical.Hunger / MedicalState.MaxHunger,
            f => Send(new CheatSetVitals(ids, Hunger: f * MedicalState.MaxHunger) { Tick = Now }), clip);
        Text($"{Number((Dragging("hunger") ? hunger * MedicalState.MaxHunger : medical.Hunger) * 100)} / {Number(MedicalState.MaxHunger * 100)}", C(994, 46, 96, 44), 13, LightInk, "Right VCenter", clip);

        float y = 104;
        int rows = medical.Parts.Count;
        Card(C(212, y, 888, 36 + rows * 40 + 54), clip);
        string[] heads = ["PART", "HEALTH", "HP", "FLESH", "STUN", "BANDAGE"];
        float[] xs = [226, 340, 470, 560, 720, 880];
        for (int i = 0; i < heads.Length; i++) Text(heads[i], C(xs[i], y + 8, 140, 20), 11, MutedInk, clip: clip);
        y += 36;
        for (int i = 0; i < rows; i++, y += 40)
        {
            var p = medical.Parts[i];
            int part = i;
            float max = MathF.Max(p.MaxHp, 0.001f);
            Rect(C(226, y + 39, 860, 1), MainInk with { W = 0.075f }, clip);
            Text(p.Template.Name.Length > 0 ? p.Template.Name : p.Template.StringId, C(226, y, 110, 40), 14, clip: clip);
            if (p.Limb == LimbState.Stump || p.Limb == LimbState.Crushed || p.Limb == LimbState.Replaced)
            {
                string state = p.Limb switch { LimbState.Replaced => "Prosthetic", LimbState.Crushed => "Crushed", _ => "Lost" };
                Text(state, C(340, y, 300, 40), 13, MutedInk, clip: clip);
                Button(p.Limb == LimbState.Replaced ? "Regrow (prosthetic off)" : "Regrow", C(p.Limb == LimbState.Replaced ? 856 : 986, y + 7, p.Limb == LimbState.Replaced ? 230 : 100, 26),
                    () => { Send(new CheatRegrowLimbs([id], Prosthetics: true, Part: part) { Tick = Now }); Notice($"{p.Template.Name} regrown."); }, clip: clip, size: 12);
                if (p.Limb != LimbState.Replaced) continue;
            }
            HealthBar(C(340, y + 15, 120, 10), p.Flesh, p.Stun, p.Bandage, max, clip);
            var hp = p.Flesh - p.Stun;
            Text($"{Number(hp)} / {Number(max)}", C(462, y, 88, 40), 13, hp < 0 ? Bad : LightInk, "Right VCenter", clip);
            float flesh = Slider($"flesh{i}", C(560, y + 8, 140, 24), (p.Flesh + max) / (2 * max), f => Send(new CheatSetPart(id, part, Flesh: f * 2 * max - max) { Tick = Now }), clip);
            Slider($"stun{i}", C(720, y + 8, 140, 24), p.Stun / max, f => Send(new CheatSetPart(id, part, Stun: f * max) { Tick = Now }), clip);
            float room = MathF.Max(0.001f, max - p.Flesh);
            Slider($"bandage{i}", C(880, y + 8, 100, 24), Math.Clamp(p.Bandage / room, 0, 1), f => Send(new CheatSetPart(id, part, Bandage: f * room) { Tick = Now }), clip);
            if (Dragging($"flesh{i}")) Text(Number(flesh * 2 * max - max), C(560, y - 6, 140, 16), 11, Brass, "Center", clip);
            Button("Heal", C(1000, y + 7, 86, 26), () => Send(new CheatHealPart(id, part) { Tick = Now }), clip: clip, size: 12);
        }
        Button("Heal everything", C(226, y + 10, 200, 32), () => { Send(new DebugHeal(ids) { Tick = Now }); Notice("Healed."); }, primary: true, clip: clip);
        Button("Clear all stun", C(436, y + 10, 180, 32), () => Send(new CheatBody(ids, CheatBodyAction.ClearStun) { Tick = Now }), clip: clip);
        Button("Bandage everything", C(626, y + 10, 200, 32), () => Send(new CheatBody(ids, CheatBodyAction.BandageAll) { Tick = Now }), clip: clip);
        Button("Regrow all limbs", C(836, y + 10, 250, 32), () => { Send(new CheatRegrowLimbs(ids, Prosthetics: true) { Tick = Now }); Notice("Every limb regrown."); }, clip: clip);
        return y + 60;
    }

    // ------------------------------------------------------------------ Skills

    static readonly (string Group, (StatsEnumerated Stat, string Name)[] Stats)[] StatGroups =
    [
        ("ATTRIBUTES", [(StatsEnumerated.Strength, "Strength"), (StatsEnumerated.Toughness, "Toughness"), (StatsEnumerated.Dexterity, "Dexterity"), (StatsEnumerated.Perception, "Perception")]),
        ("COMBAT", [(StatsEnumerated.MeleeAttack, "Melee Attack"), (StatsEnumerated.MeleeDefence, "Melee Defence"), (StatsEnumerated.Dodge, "Dodge"), (StatsEnumerated.MartialArts, "Martial Arts")]),
        ("WEAPONS", [(StatsEnumerated.Katanas, "Katanas"), (StatsEnumerated.Sabres, "Sabres"), (StatsEnumerated.Hackers, "Hackers"), (StatsEnumerated.HeavyWeapons, "Heavy Weapons"),
            (StatsEnumerated.Blunt, "Blunt"), (StatsEnumerated.Polearms, "Polearms")]),
        ("RANGED", [(StatsEnumerated.Turrets, "Turrets"), (StatsEnumerated.Crossbows, "Precision Shooting")]),
        ("THIEVERY", [(StatsEnumerated.Stealth, "Stealth"), (StatsEnumerated.Lockpicking, "Lockpicking"), (StatsEnumerated.Thieving, "Thievery"), (StatsEnumerated.Assassination, "Assassination")]),
        ("ATHLETICS", [(StatsEnumerated.Athletics, "Athletics"), (StatsEnumerated.Swimming, "Swimming")]),
        ("SCIENCE", [(StatsEnumerated.Medic, "Field Medic"), (StatsEnumerated.Engineering, "Engineer"), (StatsEnumerated.Robotics, "Robotics"), (StatsEnumerated.Science, "Science"),
            (StatsEnumerated.SmithingWeapon, "Weapon Smith"), (StatsEnumerated.SmithingArmour, "Armour Smith"), (StatsEnumerated.SmithingBow, "Crossbow Smith")]),
        ("LABOUR", [(StatsEnumerated.Labouring, "Labouring"), (StatsEnumerated.Farming, "Farming"), (StatsEnumerated.Cooking, "Cooking")]),
    ];

    /// <summary>The skills sliders run 0 to this; a typed value (click the number) goes up to <see cref="CheatSetStat.Max"/>.</summary>
    const float SkillSliderMax = 200;
    float setAll = 0.25f;
    readonly Dictionary<StatsEnumerated, Field> statFields = [];
    readonly Field setAllField = new("setall", numeric: true);

    Field StatField(StatsEnumerated stat, IReadOnlyList<CharacterId> ids)
    {
        if (!statFields.TryGetValue(stat, out var field))
            statFields[stat] = field = new Field("stat" + (int)stat, text => { Send(new CheatSetStat(Targets(), stat, Math.Clamp(Parse(text, 0), 0, CheatSetStat.Max)) { Tick = Now }); focused = null; }, numeric: true);
        return field;
    }

    float DrawSkills()
    {
        var clip = viewport;
        Header("Skills", 0);
        var ids = Targets();
        var stats = ids.Count > 0 ? Cold(ids[0])?.Stats : null;
        if (stats is null)
        {
            Text("Choose a character with skills: select one of yours or inspect another.", C(212, 56, 888, 30), 14, MutedInk, clip: clip);
            return 100;
        }
        Card(C(212, 46, 888, 44), clip);
        Text("Set all to", C(226, 46, 110, 44), 14, clip: clip);
        float all = Slider("setall", C(340, 46, 360, 44), setAll, f => { setAll = f; setAllField.Text = Number(MathF.Round(f * SkillSliderMax)); }, clip);
        if (setAllField.Text.Length == 0 && !ReferenceEquals(focused, setAllField)) setAllField.Text = Number(MathF.Round(setAll * SkillSliderMax));
        if (Dragging("setall")) setAllField.Text = Number(MathF.Round(all * SkillSliderMax));
        EditField(setAllField, C(708, 53, 64, 30), "", clip);
        float Wanted() => Math.Clamp(Parse(setAllField.Text, setAll * SkillSliderMax), 0, CheatSetStat.Max);
        Button("Apply", C(780, 54, 140, 28), () => { float v = Wanted(); Send(new CheatSetStat(ids, StatsEnumerated.None, v) { Tick = Now }); focused = null; Notice($"Every skill set to {Number(v)}."); }, clip: clip);
        Button("Max all (100)", C(928, 54, 158, 28), () => { Send(new CheatSetStat(ids, StatsEnumerated.None, 100) { Tick = Now }); Notice("Every skill 100."); }, clip: clip);
        Text("Drag up to 200, or click a number to type any value up to 1000.", C(212, 92, 888, 16), 11, MutedInk, clip: clip);

        float[] columnY = [116, 116];
        int[] columnOf = [0, 0, 1, 1, 0, 0, 1, 1];
        for (int g = 0; g < StatGroups.Length; g++)
        {
            int col = columnOf[g];
            float x = col == 0 ? 212 : 666, y = columnY[col];
            var (group, list) = StatGroups[g];
            Card(C(x, y, 434, 30 + list.Length * 30 + 6), clip);
            Label(group, C(x + 14, y + 8, 300, 18), clip);
            y += 30;
            foreach (var (stat, name) in list)
            {
                var s = stat;
                Text(name, C(x + 14, y, 150, 30), 14, clip: clip);
                float v = Slider("stat" + (int)s, C(x + 170, y + 3, 190, 24), stats[s] / SkillSliderMax, f => Send(new CheatSetStat(ids, s, MathF.Round(f * SkillSliderMax)) { Tick = Now }), clip);
                var field = StatField(s, ids);
                var numberAt = C(x + 366, y + 3, 56, 24);
                if (ReferenceEquals(focused, field)) EditField(field, numberAt, "", clip);
                else
                {
                    float shown = Dragging("stat" + (int)s) ? MathF.Round(v * SkillSliderMax) : stats[s];
                    if (Hovered(numberAt, clip)) Rect(numberAt, MainInk with { W = 0.08f }, clip);
                    Text(Number(shown), numberAt with { Width = numberAt.Width - 4 * scale }, 13, shown > 100 ? Good : LightInk, "Right VCenter", clip);
                    if (Clickable(numberAt, clip)) hits.Add(new(numberAt, () => { field.Text = Number(stats[s]); focused = field; openDropdown = null; }));
                }
                y += 30;
            }
            columnY[col] = y + 18;
        }
        return Math.Max(columnY[0], columnY[1]);
    }

    // ------------------------------------------------------------------ shared bits

    void Header(string title, float y)
    {
        var at = C(212, y, 888, 34);
        if (!InView(at)) return;
        Surface(at, "paper", clip: viewport);
        Text(title, at with { X = at.X + 12 * scale, Width = at.Width - 24 * scale }, 18, MeitouLook && !Modern ? PaperInk : LightInk, clip: viewport);
    }

    void Label(string text, Meitou.Data.Ui.UiRect at, Meitou.Data.Ui.UiRect clip) => Text(text, at, 11, MutedInk, "Left VCenter", clip);
}
