using System.Numerics;
using Meitou.Data.Gameplay.Ai;
using Meitou.Simulation.Autonomy;
using Meitou.Simulation.Construction;

namespace Meitou.Simulation.Production;

// Squad autonomy's labour and turrets (docs/simulation.md "Squad autonomy"; the plugin's FindLabourToDo and couldIOperate hooks): an autonomous player character the AI sends to work
// runs a machine of its outpost as an operator does (Begin), chosen by its squad's labour priorities; one sent to man a turret mans a free turret of the outpost.
public sealed partial class ProductionSystem
{
    /// <summary>
    /// The machine of player town <paramref name="town"/> an autonomous character works next (the plugin's <c>FindLabourToDo</c> / <c>FindOptimalLabourFromList</c>), or null: a complete
    /// building of the player's whose labour group (<see cref="AutonomyRules.KindOf"/>) has a priority above 0 and that has work (<see cref="Blocked"/> as the operating job tests it: free
    /// operator places, power, inputs it can fill, room for the output, a queued research or craft; an automatic machine as its hauling job). Highest priority first; on a tie the one it works
    /// at now, then fewer operators, then the nearest (distance squared from the character, plus twice that from <paramref name="home"/>, the squad's work place, when it has one).
    /// </summary>
    internal string? AutonomyLabour(World world, int slot, AutonomySettings settings, int town, Vector3? home)
    {
        var table = world.Characters;
        if (table.Cold(slot) is not { } cold || !Able(cold) || town <= 0) return null;
        var at = table.Next[slot].Position;
        string? best = null;
        (int Priority, bool Current, int Operators, float Distance) top = (0, false, 0, 0);
        foreach (var m in world.Machines.All)
        {
            if (Site(world, m.Site) is not { Complete: true } site || site.Town != town || site.Owner != world.Player.Faction) continue;
            bool automatic = m.Data.IsAutomatic && m.Data.IsProducer;
            if (!automatic && !m.Data.Operable) continue;
            int priority = settings.LabourPriority(AutonomyRules.KindOf(m.Data));
            if (priority <= 0) continue;
            var job = new PermaJob(automatic ? TaskType.OPERATE_AUTOMATIC_MACHINERY : TaskType.OPERATE_MACHINERY, site.Id);
            if (Blocked(world, cold, job)) continue;
            bool current = cold.OperateSite == site.Id;
            int operators = OperatorsOf(world, site.Id) - (current ? 1 : 0);
            float d = Vector3.DistanceSquared(at, site.Position);
            if (home is { } h) d += 2 * Vector3.DistanceSquared(h, site.Position);
            var key = (priority, current, operators, d);
            if (best is not null && !Better(key, top)) continue;
            (best, top) = (site.Id, key);
        }
        return best;

        static bool Better((int P, bool C, int O, float D) a, (int P, bool C, int O, float D) b) =>
            a.P != b.P ? a.P > b.P : a.C != b.C ? a.C : a.O != b.O ? a.O < b.O : a.D < b.D;
    }

    /// <summary>Whether any machine of the town has work for the squad (the AI's desire for its labour goal).</summary>
    internal bool AutonomyHasLabour(World world, int slot, AutonomySettings settings, int town) => AutonomyLabour(world, slot, settings, town, null) is not null;

    /// <summary>The free turret of player town <paramref name="town"/> nearest the character (the one it mans now first), or null; the plugin's <c>couldIOperate</c> refuses turrets unless Man Turrets is on.</summary>
    internal string? AutonomyTurret(World world, int slot, int town)
    {
        var table = world.Characters;
        if (table.Cold(slot) is not { } cold || !Able(cold) || town <= 0) return null;
        var at = table.Next[slot].Position;
        string? best = null;
        float bestD = float.MaxValue;
        foreach (var m in world.Machines.All)
        {
            if (m.Data.Function != Meitou.Data.World.ObjectRanges.TurretFunction || Site(world, m.Site) is not { Complete: true } site || site.Town != town
                || site.Owner != world.Player.Faction) continue;
            if (cold.OperateSite == site.Id) return site.Id;
            if (Blocked(world, cold, new PermaJob(TaskType.MAN_A_TURRET_PLAYER_JOB, site.Id))) continue;
            float d = Vector3.DistanceSquared(at, site.Position);
            if (d < bestD) (best, bestD) = (site.Id, d);
        }
        return best;
    }

    /// <summary>Sets the character operating the site (walking there first), as an operate order does; true when it operates it now.</summary>
    internal bool AutonomyBegin(World world, int slot, string siteId)
    {
        if (Site(world, siteId) is not { Complete: true } site || Ensure(world, site) is not { } m || world.Characters.Cold(slot) is not { } cold || !Able(cold)) return false;
        if (cold.OperateSite != site.Id || world.Characters.Next[slot].Task != (byte)CharacterTask.Operate) Begin(world, slot, site, m);
        return true;
    }

    /// <summary>Ends an operator's work (its task goes back to idle).</summary>
    internal static void AutonomyEnd(CharacterTable table, int slot) => End(table, slot);
}
