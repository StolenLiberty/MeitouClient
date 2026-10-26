using System.Numerics;

namespace Meitou.Simulation.Jobs;

/// <summary>Small shared rules of squad jobs (docs/simulation.md "Squad jobs").</summary>
public static class WorkRules
{
    /// <summary>
    /// How whole a character's body is: hit points over their maximum, summed over the parts (engine choice); 1 without a body. Below <see cref="WorkSystem.HurtBelow"/> it lies down at night,
    /// and it is up again at <see cref="WorkSystem.HealedAt"/>.
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

    /// <summary>The job of a character's squad (Off when it has none).</summary>
    public static SquadJob JobOf(World world, CharacterCold c) =>
        c.IsPlayer && c.SquadId >= 0 && world.Squads.Find(c.SquadId)?.Work is { } w ? w.Job : SquadJob.Off;

    /// <summary>Whether the character's squad has a job (the work system runs it: its own job list and the idle jobs wait).</summary>
    public static bool OnJob(World world, CharacterCold c) => JobOf(world, c) != SquadJob.Off;

    /// <summary>The player's outpost whose radius holds <paramref name="at"/> (the nearest), or null.</summary>
    public static Construction.PlayerTown? OutpostAt(World world, Vector3 at)
    {
        Construction.PlayerTown? best = null;
        float bestD = float.MaxValue;
        foreach (var town in world.Buildings.Towns)
        {
            if (town.Owner != world.Player.Faction) continue;
            float d = Vector2.DistanceSquared(new(at.X, at.Z), new(town.Position.X, town.Position.Z));
            if (d <= town.Radius * town.Radius && d < bestD) (best, bestD) = (town, d);
        }
        return best;
    }
}
