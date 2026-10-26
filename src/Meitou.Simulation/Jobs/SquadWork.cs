using System.Text.Json;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Data.Gameplay.Construction;

namespace Meitou.Simulation.Jobs;

/// <summary>What a squad of the player's does on its own (Meitou-only; docs/simulation.md "Squad jobs"). <see cref="Off"/>: nothing, plain Kenshi.</summary>
public enum SquadJob : byte { Off, Builders, Workers, StoneCrew, IronCrew, CopperCrew, Farmers, Cooks, Crafters, Researchers, Medics, Haulers, Soldiers }

/// <summary>When a squad works: <see cref="Day"/> 06:00 to 20:00, <see cref="Night"/> 20:00 to 06:00, or around the clock. Off shift they sleep.</summary>
public enum Shift : byte { Day, Night, AllDay }

/// <summary>What a squad does about a fight at its outpost (<see cref="WorkSystem"/>, defence).</summary>
public enum Stance : byte
{
    /// <summary>Fights only a threat at hand (one attacking it, or anyone within <see cref="WorkSystem.LocalReach"/>), otherwise keeps working. Every work job.</summary>
    StandGround,
    /// <summary>The quick reaction force: runs to any attack on the player's people at the outpost, and clears hostiles out of it.</summary>
    Soldiers,
    /// <summary>The men run to any attack on the player's people (no going after hostiles); the women go inside until it is over.</summary>
    Militia,
    /// <summary>Everyone goes inside a building while the player's people are under attack.</summary>
    Shelter,
}

/// <summary>
/// A squad's job (saved as the platoon string <c>meitou job</c>, JSON): the job, its shift, its stance in a fight, and optionally the only buildings it works (site ids; empty: the whole
/// outpost it stands in).
/// </summary>
public sealed record SquadWork
{
    public SquadJob Job { get; init; }
    public Shift Shift { get; init; } = Shift.Day;
    public Stance Stance { get; init; } = Stance.StandGround;
    public IReadOnlyList<string> Buildings { get; init; } = [];

    public bool On => Job != SquadJob.Off;

    /// <summary>The day shift's hours (the night shift is the rest of the day).</summary>
    public const int DayStarts = 6, DayEnds = 20;

    public static bool IsDay(double gameHours)
    {
        int h = (int)(((gameHours % 24) + 24) % 24);
        return h >= DayStarts && h < DayEnds;
    }

    public bool OnShift(double gameHours) => Shift switch { Shift.Day => IsDay(gameHours), Shift.Night => !IsDay(gameHours), _ => true };

    /// <summary>A squad set to <paramref name="job"/>: its shift and buildings kept, the job's own stance.</summary>
    public static SquadWork For(SquadJob job, SquadWork? was) => new() { Job = job, Shift = was?.Shift ?? Shift.Day, Stance = JobInfo.Of(job).Stance, Buildings = was?.Buildings ?? [] };

    sealed record Stored(byte J, byte S, byte T, List<string>? B);

    public string Write() => JsonSerializer.Serialize(new Stored((byte)Job, (byte)Shift, (byte)Stance, [.. Buildings]));

    public static SquadWork? Read(string text)
    {
        Stored? s;
        try { s = JsonSerializer.Deserialize<Stored>(text); }
        catch (JsonException) { return null; }
        if (s is null || !Enum.IsDefined((SquadJob)s.J)) return null;
        return new SquadWork
        {
            Job = (SquadJob)s.J,
            Shift = Enum.IsDefined((Shift)s.S) ? (Shift)s.S : Shift.Day,
            Stance = Enum.IsDefined((Stance)s.T) ? (Stance)s.T : Stance.StandGround,
            Buildings = s.B is { } b ? [.. b.Where(x => x.Length > 0).Distinct()] : [],
        };
    }
}

/// <summary>Sets a player squad's job (null or <see cref="SquadJob.Off"/>: plain Kenshi). Applied also while paused.</summary>
public sealed record SetSquadWork(int SquadId, SquadWork? Work) : SimCommand;

/// <summary>What each job is: its name and line for the window, its stance, the machines it works (by the stat they train), the material of a crew, and whether it builds or mans turrets.</summary>
public sealed record JobInfo(SquadJob Job, string Name, string Blurb, Stance Stance, StatsEnumerated[] Stats, int Material = 0, bool Builds = false, bool Turrets = false, bool Hauls = false)
{
    public static readonly JobInfo[] All =
    [
        new(SquadJob.Builders, "Builders", "Build and repair everything you place. They fetch the materials themselves.", Stance.StandGround, [], Builds: true),
        new(SquadJob.Workers, "Workers", "Run any machine in the outpost that has work.", Stance.StandGround,
            [StatsEnumerated.Labouring, StatsEnumerated.Farming, StatsEnumerated.Cooking, StatsEnumerated.Science, StatsEnumerated.Engineering, StatsEnumerated.Robotics,
             StatsEnumerated.Medic, StatsEnumerated.SmithingWeapon, StatsEnumerated.SmithingArmour, StatsEnumerated.SmithingBow]),
        new(SquadJob.StoneCrew, "Stone crew", "Stone only: mine it, process it, haul it. They split into miners, processor hands and haulers.", Stance.StandGround, [], Material: ResourceField.StoneResource),
        new(SquadJob.IronCrew, "Iron crew", "Iron only: mine ore, refine plates and steel, haul it.", Stance.StandGround, [], Material: ResourceField.IronResource),
        new(SquadJob.CopperCrew, "Copper crew", "Copper only: mine it and haul it.", Stance.StandGround, [], Material: ResourceField.CopperResource),
        new(SquadJob.Farmers, "Farmers", "Plant, tend and harvest the farms.", Stance.StandGround, [StatsEnumerated.Farming]),
        new(SquadJob.Cooks, "Cooks", "Keep the stoves, ovens and stills going.", Stance.StandGround, [StatsEnumerated.Cooking]),
        new(SquadJob.Crafters, "Crafters", "Weapon, armour and crossbow smithing, robotics and engineering benches.", Stance.StandGround,
            [StatsEnumerated.SmithingWeapon, StatsEnumerated.SmithingArmour, StatsEnumerated.SmithingBow, StatsEnumerated.Robotics, StatsEnumerated.Engineering]),
        new(SquadJob.Researchers, "Researchers", "Work the research benches.", Stance.StandGround, [StatsEnumerated.Science]),
        new(SquadJob.Medics, "Medics", "Patch up the wounded and carry the downed to bed; medical benches when all are well.", Stance.StandGround, [StatsEnumerated.Medic]),
        new(SquadJob.Haulers, "Haulers", "Carry outputs to storage and feed the machines' inputs.", Stance.StandGround, [], Hauls: true),
        new(SquadJob.Soldiers, "Soldiers", "Quick reaction force: answer any attack, clear hostiles out, man the turrets when it is calm.", Stance.Soldiers, [], Turrets: true),
    ];

    public static JobInfo Of(SquadJob job) => All.FirstOrDefault(j => j.Job == job) ?? new(SquadJob.Off, "Off", "Plain Kenshi: they only do what you tell them.", Stance.StandGround, []);
}
