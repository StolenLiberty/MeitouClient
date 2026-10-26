using System.Numerics;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Data.Gameplay.Construction;
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
    internal string? AutonomyLabour(World world, int slot, AutonomySettings settings, int town, Vector3? home, int material = 0)
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
            // A turret is manned only with Man turrets (the plugin's couldIOperate), not as "other" labour.
            if (m.Data.Function == Meitou.Data.World.ObjectRanges.TurretFunction && !settings.ManTurrets) continue;
            if (material != 0 && !InChain(m.Data, material)) continue;
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

    readonly Dictionary<int, HashSet<string>> chains = [];

    /// <summary>
    /// Whether a machine is part of one material's chain (Meitou, engine choice): a mine of that world resource (<c>world resource mining</c> 1 iron, 2 stone, 3 copper), or a
    /// refinery worked with Labouring whose input is that mine's output or a further such refinery's (stone: the processors; iron: the iron and steel refineries; copper: the mines only).
    /// </summary>
    internal bool InChain(MachineData m, int resource)
    {
        if (m.Function is MachineData.MineFunction or MachineData.NaturalMineFunction) return m.Resource == resource && m.Farm is null;
        if (m.Function != MachineData.RefineryFunction || (StatsEnumerated)m.Stat != StatsEnumerated.Labouring) return false;
        var items = ChainItems(resource);
        return m.Inputs.Any(i => items.Contains(i.Item));
    }

    /// <summary>The items of a material's chain, from every BUILDING of the data (read once per material).</summary>
    HashSet<string> ChainItems(int resource)
    {
        if (chains.TryGetValue(resource, out var items)) return items;
        chains[resource] = items = new(StringComparer.Ordinal);
        var all = db.OfType(FcsRecordType.BUILDING).Select(r => Data(r.StringId)).OfType<MachineData>().ToList();
        foreach (var m in all)
            if (m.Function is MachineData.MineFunction or MachineData.NaturalMineFunction && m.Resource == resource && m.Farm is null && m.Output is { } o) items.Add(o.Item);
        for (bool grew = items.Count > 0; grew;)
        {
            grew = false;
            foreach (var m in all)
                if (m.Function == MachineData.RefineryFunction && (StatsEnumerated)m.Stat == StatsEnumerated.Labouring && m.Output is { } o
                    && m.Inputs.Any(i => items.Contains(i.Item)) && items.Add(o.Item)) grew = true;
        }
        return items;
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

    /// <summary>
    /// Whether the character can work site <paramref name="siteId"/> now (a manager's assigned building, Meitou): a complete machine of the player's it may operate (or haul for, an automatic one)
    /// with work and a free place (<see cref="Blocked"/>), or a turret nobody else mans.
    /// </summary>
    internal bool AutonomyCanWork(World world, int slot, string siteId)
    {
        if (world.Characters.Cold(slot) is not { } cold || !Able(cold) || Site(world, siteId) is not { Complete: true } site || site.Owner != world.Player.Faction || world.Machines.Find(siteId) is not { } m) return false;
        if (m.Data.Function == Meitou.Data.World.ObjectRanges.TurretFunction) return !Blocked(world, cold, new PermaJob(TaskType.MAN_A_TURRET_PLAYER_JOB, site.Id));
        bool automatic = m.Data.IsAutomatic && m.Data.IsProducer;
        if (!automatic && !m.Data.Operable) return false;
        return !Blocked(world, cold, new PermaJob(automatic ? TaskType.OPERATE_AUTOMATIC_MACHINERY : TaskType.OPERATE_MACHINERY, site.Id));
    }

    /// <summary>Why an assigned machine cannot go on (for the manager's log), or null: no power; an input with nothing to fill it from; a full output no storage takes.</summary>
    internal string? AutonomyStuck(World world, string siteId)
    {
        if (Site(world, siteId) is not { Complete: true } site || world.Machines.Find(siteId) is not { } m || m.Data.IsStorage || m.Data.IsResearch || m.Farm is not null) return null;
        string name = site.Name.Length > 0 ? site.Name : m.Data.Name;
        if (WorkFactor(world, m) <= 0 && m.Data.Operable) return $"{name} has no power";
        foreach (var input in m.Inputs)
            if (input.Rate > 0 && input.Level + input.Count <= 0 && NearestSource(world, site, input.Item) is null)
                return $"{name} is out of {items.Create(input.Item, 1)?.Name ?? input.Item}";
        if (m.Output is { Full: true } o && items.Create(o.Recipe.Item, 1) is { } one && NearestStorage(world, site, x => x.Data.Accepts(one.Record, one.Type) && Fits(x.Storage!, one)) is null)
            return $"{name} is full: no storage with room for {one.Name}";
        return null;
    }

    /// <summary>Ends an operator's work (its task goes back to idle).</summary>
    internal static void AutonomyEnd(CharacterTable table, int slot) => End(table, slot);
}
