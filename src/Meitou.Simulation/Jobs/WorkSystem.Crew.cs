namespace Meitou.Simulation.Jobs;

// Crews (docs/simulation.md "Squad jobs"): a material crew splits into miners, processor hands and haulers; the Haulers job is all haulers, for every machine of the outpost.
public sealed partial class WorkSystem
{
    public enum CrewRole : byte { Miner, Processor, Hauler }

    /// <summary>One pass's crew: the machines it works, the haulers' items (null: anything a machine makes), how many haulers, and each member's role.</summary>
    internal sealed record CrewPlan(List<Production.Machine> Mines, List<Production.Machine> Processors, HashSet<string>? Items, int Haulers, Dictionary<CharacterId, CrewRole> Roles,
        Construction.PlayerTown Town);

    /// <summary>Each member's last role, kept from pass to pass so nobody is shuffled while the counts hold.</summary>
    readonly Dictionary<CharacterId, CrewRole> crewRoles = [];

    /// <summary>
    /// The split (engine choice): Haulers are all haulers. A material crew: haulers 1 for 3 to 7 workers, 2 for 8 to 12 and so on (none below 3: then they carry their own); processor hands a
    /// third of the rest (at least 1 with a processor, at most its places); miners the rest up to the mines' places; anyone left over goes on the processors, then hauling.
    /// </summary>
    CrewPlan PlanCrew(World world, Production.ProductionSystem production, Squad squad, SquadWork work, JobInfo info, Construction.PlayerTown town)
    {
        var table = world.Characters;
        var only = work.Buildings.Count > 0 ? work.Buildings : null;
        var (mines, processors) = info.Hauls ? production.HaulMachines(world, town, only) : production.CrewMachines(world, town, info.Material, only);
        var workers = new List<CharacterId>();
        foreach (var id in squad.Members)
            if (table.TryResolveNext(id, out int s) && table.Cold(s) is { } c && c.Medical is not { Dead: true } and not { Incapacitated: true } && !c.IsImprisoned
                && WorkRules.OutpostAt(world, table.Next[s].Position)?.Number == town.Number) workers.Add(id);
        var roles = new Dictionary<CharacterId, CrewRole>();
        if (info.Hauls)
        {
            foreach (var id in workers) roles[id] = CrewRole.Hauler;
            return new(mines, processors, null, workers.Count, roles, town);
        }
        int w = workers.Count, mineSlots = mines.Sum(x => x.Data.MaxOperators), procSlots = processors.Sum(x => x.Data.MaxOperators);
        int haulers = w >= 3 ? Math.Max(1, (w + 2) / 5) : 0;
        int procs = procSlots == 0 ? 0 : w >= 3 ? Math.Min(procSlots, Math.Max(1, (w - haulers) / 3)) : w == 2 && mineSlots > 0 ? 1 : mineSlots == 0 ? Math.Min(procSlots, w) : 0;
        int miners = Math.Min(mineSlots, w - haulers - procs);
        int left = w - haulers - procs - miners;
        int more = Math.Min(left, procSlots - procs);
        procs += more;
        left -= more;
        if (haulers > 0 || w >= 2 && mineSlots + procSlots > 0) haulers += left;
        var open = new Dictionary<CrewRole, int> { [CrewRole.Miner] = miners, [CrewRole.Processor] = procs, [CrewRole.Hauler] = haulers };
        var unplaced = new List<CharacterId>();
        foreach (var id in workers)
            if (crewRoles.TryGetValue(id, out var was) && open[was] > 0) { roles[id] = was; open[was]--; }
            else unplaced.Add(id);
        foreach (var id in unplaced)
            foreach (var r in (ReadOnlySpan<CrewRole>)[CrewRole.Miner, CrewRole.Processor, CrewRole.Hauler])
                if (open[r] > 0) { roles[id] = r; open[r]--; break; }
        foreach (var (id, r) in roles) crewRoles[id] = r;
        return new(mines, processors, production.ChainItems(info.Material), roles.Count(r => r.Value == CrewRole.Hauler), roles, town);
    }

    /// <summary>A crew member's work this pass (its status).</summary>
    string Crew(World world, Production.ProductionSystem production, CrewPlan plan, CrewRole role, int slot, CharacterId id, CharacterCold c, CharacterTask task, bool ours)
    {
        if (role == CrewRole.Hauler)
        {
            if (ours && task == CharacterTask.Haul && c.HaulFetch is { } item && c.HaulTo is { } to) return $"hauling {ItemName(item)} to the {SiteName(world, to)}";
            if (production.CrewHaul(world, slot, plan.Town, plan.Items, plan.Mines, plan.Processors) is { } trip && production.CrewHaulBegin(world, slot, trip))
            {
                Send(world, id, trip.From);
                Log(world, $"{c.Name} to carry {ItemName(trip.Item)} to the {SiteName(world, trip.To)}");
                return $"hauling {ItemName(trip.Item)} to the {SiteName(world, trip.To)}";
            }
            if (ours) production.WorkStop(world, slot);
            return "hauling: nothing to carry";
        }
        var stations = role == CrewRole.Miner ? plan.Mines : plan.Processors;
        string doing = role == CrewRole.Miner ? "mining" : "processing";
        if (stations.Count == 0) return role == CrewRole.Miner ? "no mines for this crew in the outpost" : "no processor for this crew in the outpost";
        c.StationUntil = plan.Haulers > 0 ? world.Tick + (long)(3 * CheckSeconds / world.TickSeconds) : 0;
        if (ours && task == CharacterTask.Operate && c.OperateSite is { } on && stations.Any(x => x.Site == on)) return $"{doing} at the {SiteName(world, on)}";
        if (production.CrewStation(world, slot, stations) is not { } station)
        {
            if (ours) production.WorkStop(world, slot);
            return $"{doing}: waiting for a free place";
        }
        if (!production.WorkBegin(world, slot, station)) return $"{doing}: cannot reach the {SiteName(world, station)}";
        if (!sent.TryGetValue(id, out var was) || was.Site != station) Log(world, $"{c.Name} to {(role == CrewRole.Miner ? "mine at" : "work")} the {SiteName(world, station)}");
        Send(world, id, station);
        return $"{doing} at the {SiteName(world, station)}";
    }
}
