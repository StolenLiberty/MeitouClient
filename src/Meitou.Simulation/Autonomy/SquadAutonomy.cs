using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Meitou.Data.Gameplay.Ai;

namespace Meitou.Simulation.Autonomy;

/// <summary>
/// One player squad's autonomy (Meitou's version of the RE_Kenshi plugin Squad Autonomy, Workshop 3800729618, docs/simulation.md "Squad autonomy"): while
/// <see cref="Enabled"/>, the AI runs the squad from <see cref="Packages"/> like an NPC squad, with these settings. Defaults are the plugin's (<c>SquadSettingsInfo</c>).
/// </summary>
public sealed record AutonomySettings
{
    public bool Enabled { get; init; }
    /// <summary>The AI_PACKAGE string ids and the slot each runs at (the plugin's priority 0..5: the blackboard tries the highest slot first).</summary>
    public IReadOnlyList<AutonomyPackage> Packages { get; init; } = [];
    /// <summary>Where the squad rests (<see cref="Construction.BuildSite.Id"/> of a player building, or a town building's key); null: none.</summary>
    public string? Home { get; init; }
    /// <summary>Where it spends work hours; null: the home.</summary>
    public string? Work { get; init; }
    /// <summary>Work hours (0..24; the plugin compares whole hours). Start = end: always rest time; the defaults 0..24: never.</summary>
    public float StartWork { get; init; }
    public float EndWork { get; init; } = 24;
    public bool RestUntilHealed { get; init; } = true;
    /// <summary>Percent of health at or below which a wounded member goes to bed even in work hours, and at which it counts as healed.</summary>
    public float RestThreshold { get; init; } = 50;
    public float HealedThreshold { get; init; } = 90;
    public bool UsePaidBeds { get; init; }
    /// <summary>"Allow Sleep Outside Work Hours".</summary>
    public bool DoSleep { get; init; }
    /// <summary>Labour priority per <see cref="LabourKind"/> (0..10, 0 forbids; the plugin's twelve sliders, default 1).</summary>
    public IReadOnlyList<float> Labour { get; init; } = DefaultLabour;
    public bool AttackEnemies { get; init; } = true;
    public bool ProtectAllies { get; init; } = true;
    public bool DoMedic { get; init; } = true;
    public bool DoRescue { get; init; } = true;
    public bool ManTurrets { get; init; }
    public bool StayInsideGate { get; init; }
    public bool CloseGate { get; init; }

    public static readonly IReadOnlyList<float> DefaultLabour = [1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1];

    /// <summary>The plugin's <c>isRestTime</c>: whole hours; start = end always rest; start &lt; end rest outside [start, end); start &gt; end rest in [end, start).</summary>
    public bool IsRestTime(double gameHours)
    {
        int h = (int)(((gameHours % 24) + 24) % 24), s = (int)StartWork, e = (int)EndWork;
        if (e == s) return true;
        return e > s ? h >= e || h < s : h >= e && h < s;
    }

    /// <summary>The priority of a kind of work (0: forbidden).</summary>
    public int LabourPriority(LabourKind kind) => (int)(Labour.Count > (int)kind ? Labour[(int)kind] : 1);

    /// <summary>The place the squad keeps now: in rest time its home, in work hours its work place, else its home.</summary>
    public string? PlaceNow(double gameHours) => IsRestTime(gameHours) ? Home : Work ?? Home;

    // ---- save (a player platoon's string `meitou autonomy`, JSON) ----

    sealed record Stored(bool On, List<AutonomyPackage>? P, string? H, string? W, float S, float E, bool Ruh, float Rt, float Ht, bool Paid, bool Sl, List<float>? L,
        bool Atk, bool Prot, bool Med, bool Res, bool Tur, bool In, bool Shut);

    public string Write() => JsonSerializer.Serialize(new Stored(Enabled, [.. Packages], Home, Work, StartWork, EndWork, RestUntilHealed, RestThreshold, HealedThreshold, UsePaidBeds,
        DoSleep, [.. Labour], AttackEnemies, ProtectAllies, DoMedic, DoRescue, ManTurrets, StayInsideGate, CloseGate));

    /// <summary>The settings a save stored, or null when the text is not readable (a squad without autonomy).</summary>
    public static AutonomySettings? Read(string text)
    {
        Stored? s;
        try { s = JsonSerializer.Deserialize<Stored>(text); }
        catch (JsonException) { return null; }
        if (s is null) return null;
        var labour = s.L is { Count: > 0 } l ? [.. l.Concat(DefaultLabour.Skip(l.Count)).Take(DefaultLabour.Count).Select(v => Math.Clamp(v, 0, 10))] : DefaultLabour;
        return new AutonomySettings
        {
            Enabled = s.On, Packages = s.P ?? [], Home = s.H, Work = s.W, StartWork = Math.Clamp(s.S, 0, 24), EndWork = Math.Clamp(s.E, 0, 24), RestUntilHealed = s.Ruh,
            RestThreshold = Math.Clamp(s.Rt, 0, 100), HealedThreshold = Math.Clamp(s.Ht, 0, 100), UsePaidBeds = s.Paid, DoSleep = s.Sl, Labour = labour, AttackEnemies = s.Atk,
            ProtectAllies = s.Prot, DoMedic = s.Med, DoRescue = s.Res, ManTurrets = s.Tur, StayInsideGate = s.In, CloseGate = s.Shut,
        };
    }
}

/// <summary>A package of a squad's autonomy: the AI_PACKAGE string id and its slot.</summary>
public sealed record AutonomyPackage([property: JsonPropertyName("id")] string Id, [property: JsonPropertyName("slot")] int Slot);

/// <summary>The plugin's labour groups (<c>getLabourPriority</c>): a machine's <c>stat used</c>, automatic machines (no operator places), anything else.</summary>
public enum LabourKind : byte
{
    Science, Labouring, Farming, Engineer, Cooking, Robotics, Medic, WeaponSmith, ArmourSmith, CrossbowSmith, AutomaticMachine, Other,
}

/// <summary>Replaces a player squad's autonomy settings (the panel sends the whole record; null clears them). Applied also while paused.</summary>
public sealed record SetSquadAutonomy(int SquadId, AutonomySettings? Settings) : SimCommand;

public static class AutonomyRules
{
    /// <summary>The suffix the plugin offers packages by (case-sensitive), and its own packages' names without it.</summary>
    public const string Suffix = "<SquadAutonomy>";

    /// <summary>
    /// The labour group of a machine (the plugin's <c>getLabourPriority</c>): no operator places is an automatic machine; else by its <c>stat used</c> (StatsEnumerated: SCIENCE 8,
    /// LABOURING 7, FARMING 18, ENGINEERING 9, COOKING 19, ROBOTICS 10, MEDIC 13, SMITHING_WEAPON 11, SMITHING_ARMOUR 12, SMITHING_BOW 37).
    /// </summary>
    public static LabourKind KindOf(Meitou.Data.Gameplay.Construction.MachineData m)
    {
        if (m.MaxOperators <= 0 || m.IsAutomatic) return LabourKind.AutomaticMachine;
        return (Meitou.Data.Gameplay.Bodies.StatsEnumerated)m.Stat switch
        {
            Meitou.Data.Gameplay.Bodies.StatsEnumerated.Science => LabourKind.Science,
            Meitou.Data.Gameplay.Bodies.StatsEnumerated.Labouring => LabourKind.Labouring,
            Meitou.Data.Gameplay.Bodies.StatsEnumerated.Farming => LabourKind.Farming,
            Meitou.Data.Gameplay.Bodies.StatsEnumerated.Engineering => LabourKind.Engineer,
            Meitou.Data.Gameplay.Bodies.StatsEnumerated.Cooking => LabourKind.Cooking,
            Meitou.Data.Gameplay.Bodies.StatsEnumerated.Robotics => LabourKind.Robotics,
            Meitou.Data.Gameplay.Bodies.StatsEnumerated.Medic => LabourKind.Medic,
            Meitou.Data.Gameplay.Bodies.StatsEnumerated.SmithingWeapon => LabourKind.WeaponSmith,
            Meitou.Data.Gameplay.Bodies.StatsEnumerated.SmithingArmour => LabourKind.ArmourSmith,
            Meitou.Data.Gameplay.Bodies.StatsEnumerated.SmithingBow => LabourKind.CrossbowSmith,
            _ => LabourKind.Other,
        };
    }

    /// <summary>
    /// How healed a character is (the plugin compares <c>MedicalSystem::restedState</c> with its thresholds): Meitou's reading is the body's hit points over their maximum, summed over the
    /// parts (<b>engine choice</b>: the original's rested state is not traced). 1 without a body.
    /// </summary>
    public static float Rested(CharacterCold c)
    {
        if (c.Medical is not { } medical || medical.Parts.Count == 0) return 1;
        float have = 0, max = 0;
        foreach (var p in medical.Parts)
        {
            if (p.MaxHp <= 0) continue;
            max += p.MaxHp;
            have += Math.Clamp(p.Fraction, 0, 1) * p.MaxHp;
        }
        return max > 0 ? have / max : 1;
    }

    /// <summary>Wounded as the plugin's rest rule reads it: not a robot, and less rested than the healed threshold.</summary>
    public static bool Wounded(CharacterCold c, AutonomySettings s) => c.Race is not { IsRobot: true } && Rested(c) < s.HealedThreshold / 100f;

    /// <summary>The squad of a character when it runs on autonomy now (enabled), else null.</summary>
    public static AutonomySettings? Of(World world, CharacterCold c) =>
        c.IsPlayer && c.SquadId >= 0 && world.Squads.Find(c.SquadId)?.Autonomy is { Enabled: true } s ? s : null;

    /// <summary>
    /// Whether an autonomous character's squad wants a job its packages list (for the first aid and rescue systems, which run those jobs for player characters): the option allows it and
    /// a goal of that type is in its role list or race goals at this hour.
    /// </summary>
    public static bool Wants(World world, CharacterId id, CharacterCold c, params TaskType[] types)
    {
        if (Of(world, c) is not { } s || c.Ai is not { Squad.Ai: { } board } mind) return false;
        bool allowed = types.Any(t => t switch
        {
            TaskType.JOB_MEDIC or TaskType.JOB_REPAIR_ROBOT => s.DoMedic,
            TaskType.FIND_AND_RESCUE or TaskType.FIND_AND_RESCUE_IF_THERES_BEDS or TaskType.FIND_AND_RESCUE_LEADER => s.DoRescue,
            _ => true,
        });
        if (!allowed) return false;
        int hour = world.Hour;
        var roles = board.Roles[(int)Ai.AiSystem.RoleOf(mind.Squad, id, c)];
        foreach (var g in roles) if (types.Contains(g.Task.Type) && g.InWindow(hour)) return true;
        foreach (var g in mind.RaceGoals) if (types.Contains(g.Task.Type) && g.InWindow(hour)) return true;
        return false;
    }

    public static string Hours(float h) => ((int)h).ToString("00", CultureInfo.InvariantCulture) + ":00";
}
