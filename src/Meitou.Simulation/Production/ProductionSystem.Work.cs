using System.Numerics;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay.Ai;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Data.Gameplay.Construction;
using Meitou.Simulation.Construction;

namespace Meitou.Simulation.Production;

// Squad jobs' machine work (Meitou; docs/simulation.md "Squad jobs"): the work system sends a member to run a machine of its outpost as an operator order does (Begin), picked here.
public sealed partial class ProductionSystem
{
    /// <summary>
    /// The machine of the outpost the character should run for a job, or null: complete, the player's (or a natural rock), in the outpost's radius, one <paramref name="fits"/> takes, with work
    /// (<see cref="Blocked"/> as the operating job tests it: a free place, power, inputs it can fill, room for the output, a queued research or craft). The one it runs now first, then fewer
    /// operators, then the nearest. <paramref name="only"/> (non-empty): those sites only.
    /// </summary>
    internal string? WorkMachine(World world, int slot, PlayerTown town, Func<MachineData, bool> fits, IReadOnlyCollection<string>? only)
    {
        var table = world.Characters;
        if (table.Cold(slot) is not { } cold || !Able(cold)) return null;
        var at = table.Next[slot].Position;
        string? best = null;
        (bool Current, int Operators, float Distance) top = (false, 0, 0);
        foreach (var m in world.Machines.All)
        {
            if (m.Data.Function == Meitou.Data.World.ObjectRanges.TurretFunction || m.Data.IsTraining || !fits(m.Data) || !Workable(world, town, m)) continue;
            if (only is { Count: > 0 } && !only.Contains(m.Site)) continue;
            bool automatic = m.Data.IsAutomatic && m.Data.IsProducer;
            if (!automatic && !m.Data.Operable) continue;
            if (Blocked(world, cold, new PermaJob(automatic ? TaskType.OPERATE_AUTOMATIC_MACHINERY : TaskType.OPERATE_MACHINERY, m.Site))) continue;
            bool current = cold.OperateSite == m.Site;
            int operators = OperatorsOf(world, m.Site) - (current ? 1 : 0);
            float d = Site(world, m.Site) is { } s ? Vector3.DistanceSquared(at, s.Position) : float.MaxValue;
            var key = (current, operators, d);
            if (best is not null && !(key.current != top.Current ? key.current : key.operators != top.Operators ? key.operators < top.Operators : key.d < top.Distance)) continue;
            (best, top) = (m.Site, key);
        }
        return best;
    }

    /// <summary>Whether the outpost has a machine of the job at all (for the idle reason: "no farms" against "farms: nothing to do").</summary>
    internal bool HasMachine(World world, PlayerTown town, Func<MachineData, bool> fits) =>
        world.Machines.All.Any(m => m.Data.Function != Meitou.Data.World.ObjectRanges.TurretFunction && fits(m.Data) && Workable(world, town, m));

    /// <summary>The free turret of the outpost nearest the character (the one it mans now first), or null.</summary>
    internal string? WorkTurret(World world, int slot, PlayerTown town)
    {
        var table = world.Characters;
        if (table.Cold(slot) is not { } cold || !Able(cold)) return null;
        var at = table.Next[slot].Position;
        string? best = null;
        float bestD = float.MaxValue;
        foreach (var m in world.Machines.All)
        {
            if (m.Data.Function != Meitou.Data.World.ObjectRanges.TurretFunction || !Workable(world, town, m)) continue;
            if (cold.OperateSite == m.Site) return m.Site;
            if (Blocked(world, cold, new PermaJob(TaskType.MAN_A_TURRET_PLAYER_JOB, m.Site))) continue;
            float d = Site(world, m.Site) is { } s ? Vector3.DistanceSquared(at, s.Position) : float.MaxValue;
            if (d < bestD) (best, bestD) = (m.Site, d);
        }
        return best;
    }

    /// <summary>Sets the character running the site (walking there first), as an operate order does; true when it runs it now.</summary>
    internal bool WorkBegin(World world, int slot, string siteId)
    {
        if (Site(world, siteId) is not { Complete: true } site || Ensure(world, site) is not { } m || world.Characters.Cold(slot) is not { } cold || !Able(cold)) return false;
        if (cold.OperateSite != site.Id || world.Characters.Next[slot].Task != (byte)CharacterTask.Operate) Begin(world, slot, site, m);
        return true;
    }

    /// <summary>Takes a character off its machine or haul (its task back to idle).</summary>
    internal void WorkStop(World world, int slot) => End(world.Characters, slot);

    /// <summary>Why a machine cannot go on (for the status), or null: no power; an input with nothing to fill it from; a full output no storage takes.</summary>
    internal string? WhyStuck(World world, string siteId)
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

    readonly Dictionary<int, HashSet<string>> chains = [];

    /// <summary>
    /// Whether a machine is part of one material's chain (engine choice): a mine of that world resource (<c>world resource mining</c> 1 iron, 2 stone, 3 copper), or a refinery worked with
    /// Labouring whose input is that mine's output or a further such refinery's (stone: the processors; iron: the iron and steel refineries; copper: the mines only).
    /// </summary>
    internal bool InChain(MachineData m, int resource)
    {
        if (m.Function is MachineData.MineFunction or MachineData.NaturalMineFunction) return m.Resource == resource && m.Farm is null;
        if (m.Function != MachineData.RefineryFunction || (StatsEnumerated)m.Stat != StatsEnumerated.Labouring) return false;
        var items = ChainItems(resource);
        return m.Inputs.Any(i => items.Contains(i.Item));
    }

    /// <summary>The items of a material's chain, from every BUILDING of the data (read once per material).</summary>
    internal HashSet<string> ChainItems(int resource)
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
}
