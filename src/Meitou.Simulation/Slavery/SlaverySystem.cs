using System.Numerics;
using Meitou.Simulation.Bodies;

namespace Meitou.Simulation.Slavery;

/// <summary>"Join me or die": <see cref="Talker"/> (one of the player's) walks up to a captive held in a cage of the player's (a jail cell, a crucifix) or wearing locked shackles, and threatens it.</summary>
public sealed record JoinOrDieOrder(CharacterId Talker, CharacterId Captive) : SimCommand;

/// <summary>
/// Taking slaves (Meitou-only, engine choices throughout; docs/simulation.md "Slaves"): the original lets slavers enslave the player but gives the player no way back. A captive of the
/// player's (in a player cage, on a player crucifix, or in locked shackles) can be told to join or die; one who gives in comes out of the cage as a player character in the player's
/// "Slaves" squad (a new one when there is none with room), in slave state 1 owned by the player's faction, its shackles kept on. One who refuses says so and is not asked again for a
/// game hour. The chance: 35 %, up to 45 % more the more hurt it is, 20 % more when starving, 10 % more in shackles; at most 95 %.
/// </summary>
public sealed class SlaverySystem(PopulationData data) : ITickSystem
{
    /// <summary>The talker speaks from this close (engine: a cage's bars between them).</summary>
    public const float Reach = 60;
    public const string SquadName = "Slaves";
    /// <summary>The name a captive who gives in is given.</summary>
    public const string SlaveName = "Slave";

    /// <summary>Threats under way: the talker walking to the captive.</summary>
    readonly SortedDictionary<int, (CharacterId Talker, CharacterId Captive)> walking = [];
    /// <summary>Captives who refused, and the game hour they can be asked again.</summary>
    readonly Dictionary<CharacterId, double> refused = [];

    /// <summary>Whether a character is the player's captive: not the player's, alive and awake, in a player cage or in locked shackles.</summary>
    /// <summary>The chance a captive gives in (0..0.95).</summary>
    public static float Chance(CharacterCold captive)
    {
        float rested = Autonomy.AutonomyRules.Rested(captive);
        bool starving = captive.Medical is { } m && m.Hunger < MedicalState.MaxHunger * 0.4f;
        bool shackled = Locks.LockSystem.WornShackles(captive) is not null;
        return Math.Min(0.95f, 0.35f + 0.45f * (1 - rested) + (starving ? 0.2f : 0) + (shackled ? 0.1f : 0));
    }

    public static bool IsCaptive(World world, CharacterCold c)
    {
        if (c.IsPlayer || c.Medical is { Dead: true } or { Incapacitated: true } || world.Player.Faction < 0) return false;
        if (c.IsImprisoned && c.InWhat is { } cage && world.Fixtures.Find(cage) is { } f && f.Owner == world.Player.Faction) return true;
        return Locks.LockSystem.WornShackles(c) is not null;
    }

    public void Inputs(World world, IReadOnlyList<SimCommand> commands)
    {
        var table = world.Characters;
        foreach (var command in commands)
        {
            if (command is not JoinOrDieOrder order) continue;
            if (!table.TryResolveNext(order.Talker, out int t) || table.Cold(t) is not { IsPlayer: true, Medical.Incapacitated: false } talker || talker.SlaveState == 1) continue;
            if (!table.TryResolveNext(order.Captive, out int c) || table.Cold(c) is not { } captive || !IsCaptive(world, captive)) continue;
            walking[t] = (order.Talker, order.Captive);
            if (Gap(table.Next[t].Position, table.Next[c].Position) > Reach)
                MovementSystem.GoTo(table, t, new Vector2(table.Next[c].Position.X, table.Next[c].Position.Z), talker.RunSpeed);
        }
    }

    public void SlowWorld(World world)
    {
        if (walking.Count == 0) return;
        var table = world.Characters;
        foreach (var (slot, (talkerId, captiveId)) in walking.ToList())
        {
            if (!table.TryResolveNext(talkerId, out int t) || table.Cold(t) is not { Medical.Incapacitated: false } || !table.TryResolveNext(captiveId, out int c)
                || table.Cold(c) is not { } captive || !IsCaptive(world, captive))
            {
                walking.Remove(slot);
                continue;
            }
            float gap = Gap(table.Next[t].Position, table.Next[c].Position);
            // Still walking, or stopped short behind the bars: close enough once the walk has ended within twice the reach.
            if (gap > Reach && (table.Next[t].Task == (byte)CharacterTask.GoTo || gap > Reach * 2))
            {
                if (table.Next[t].Task != (byte)CharacterTask.GoTo) walking.Remove(slot);   // another order took it off
                continue;
            }
            walking.Remove(slot);
            if (table.Next[t].Task == (byte)CharacterTask.GoTo) MovementSystem.Stop(table, t);
            Threaten(world, talkerId, t, captiveId, c, captive);
        }
    }

    void Threaten(World world, CharacterId talkerId, int t, CharacterId captiveId, int c, CharacterCold captive)
    {
        var table = world.Characters;
        world.Bark(talkerId, "Join me or die.");
        if (refused.TryGetValue(captiveId, out double until) && world.GameHours < until)
        {
            world.Bark(captiveId, "I already gave you my answer.");
            return;
        }
        float chance = Chance(captive);
        float roll = Rng.Float(Rng.Hash(world.Seed, Rng.Key(captiveId), RngPurpose.Enslave, (ulong)world.Tick));
        if (roll >= chance)
        {
            refused[captiveId] = world.GameHours + 1;
            world.Bark(captiveId, roll - chance > 0.3f ? "I'd rather die." : "Never!");
            return;
        }
        if (Dialogue.DialogueSystem.PlayerHeads(world) >= data.Bodies.Constants.MaxFactionSize)
        {
            world.Post("It looks like your squad is a little full.");
            return;
        }
        if (captive.IsImprisoned) world.Fixtures.Release(world, captiveId);
        var squad = world.Squads.All.FirstOrDefault(s => s.Faction == world.Player.Faction && s.Name == SquadName && s.Members.Count < data.Bodies.Constants.MaxSquadSize);
        if (squad is null)
        {
            squad = SquadManagement.NewSquad(world, data.Db, table.Next[c].Position, SquadName);
            squad.Name = SquadName;
        }
        Dialogue.DialogueSystem.JoinSquad(world, data, c, squad);
        (captive.SlaveState, captive.SlaveOwner) = (1, world.Player.Faction);
        refused.Remove(captiveId);
        world.Bark(captiveId, "...I'll work.");
        string was = captive.Name;
        captive.Name = SlaveName;   // renamed, as the slavers' own slaves are
        world.Post($"{was} is now your slave ({squad.Name}).");
    }

    static float Gap(Vector3 a, Vector3 b) => Vector2.Distance(new(a.X, a.Z), new(b.X, b.Z));
}
