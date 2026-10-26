using System.Numerics;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Data.Gameplay.Construction;

namespace Meitou.Simulation.Jobs;

/// <summary>
/// Squad jobs (Meitou-only, engine choices throughout; docs/simulation.md "Squad jobs"). Every <see cref="CheckSeconds"/>, each member of a squad with a job who stands in one of the
/// player's outposts gets one decision, the first that applies:
/// <list type="number">
/// <item>down, imprisoned, carried, away from an outpost: nothing (said in the status);</item>
/// <item>fighting: left to it; on the player's own order (anything it was given since the work system last sent it), in a formation, a follow or a Loot All: left to it;</item>
/// <item>a fight at the outpost, by the squad's <see cref="Stance"/> (WorkSystem.Defence.cs);</item>
/// <item>rest: off shift, or at night when hurt (below <see cref="HurtBelow"/>; in bed until <see cref="HealedAt"/>), it goes to a free bed of the outpost; on shift in the day it gets up;</item>
/// <item>room: a full inventory puts its junk aside (<see cref="Production.ProductionSystem.MakeRoom"/>);</item>
/// <item>the job's work; else it waits, its status saying why.</item>
/// </list>
/// Nothing here is hashed: it acts through commands.
/// </summary>
public sealed partial class WorkSystem(PopulationData? data = null) : ITickSystem
{
    public const float CheckSeconds = 2;
    /// <summary>At most this many builders on one site.</summary>
    public const int BuildersPerSite = 3;
    /// <summary>Hurt below this share of the body's hit points: in bed at night; up again at <see cref="HealedAt"/>.</summary>
    public const float HurtBelow = 0.6f, HealedAt = 0.95f;
    /// <summary>A building a member was sent to and that it left again within 10 s (no player order between) is not given to it again for this long (it cannot reach it, or work it).</summary>
    const float StuckSeconds = 120;
    const int LogLines = 10;

    /// <summary>What each member is doing, for the window.</summary>
    readonly Dictionary<CharacterId, string> status = [];
    /// <summary>What the work system last sent each member to (a site, a bed, a haul) and when: its task since then is the system's, until the player gives it an order.</summary>
    readonly Dictionary<CharacterId, (string Site, long Tick)> sent = [];
    readonly Dictionary<(CharacterId, string), long> stuck = [];
    readonly Dictionary<int, List<string>> logs = [];
    /// <summary>Sites waiting for a material nobody can get, and the item (said once in the log until it can be had).</summary>
    readonly Dictionary<string, string> lacking = new(StringComparer.Ordinal);
    int currentSquad;

    public string? StatusOf(CharacterId id) => status.GetValueOrDefault(id);
    public IReadOnlyList<string> LogOf(int squad) => logs.TryGetValue(squad, out var log) ? [.. log] : [];

    void Log(World world, string line)
    {
        if (!logs.TryGetValue(currentSquad, out var log)) logs[currentSquad] = log = [];
        if (log.Count > 0 && log[^1].EndsWith(line, StringComparison.Ordinal)) return;
        double h = ((world.GameHours % 24) + 24) % 24;
        log.Add($"{(int)h:00}:{(int)(h % 1 * 60):00}  {line}");
        if (log.Count > LogLines) log.RemoveAt(0);
    }

    string SiteName(World world, string site) => world.Buildings.Find(site) is { Name.Length: > 0 } b ? b.Name
        : world.Machines.Find(site)?.Data.Name is { Length: > 0 } name ? name : world.Buildings.Find(site) is { } r ? data?.Db.Find(r.Record)?.Name ?? "a building" : "a building";

    string ItemName(string record) => data?.Db.Find(record)?.Name is { Length: > 0 } n ? n : "goods";

    public void Inputs(World world, IReadOnlyList<SimCommand> commands)
    {
        foreach (var command in commands)
        {
            if (command is not SetSquadWork set || world.Squads.Find(set.SquadId) is not { } squad || squad.Faction != world.Player.Faction) continue;
            squad.Work = set.Work is { On: true } w ? w : null;
            foreach (var id in squad.Members)
            {
                status.Remove(id);
                if (world.Characters.TryResolveNext(id, out int s) && world.Characters.Cold(s) is { } c) c.StationUntil = 0;
            }
            crewRoles.Clear();
        }
    }

    public void SlowWorld(World world)
    {
        int every = Math.Max(1, (int)MathF.Round(CheckSeconds / world.TickSeconds));
        if (world.Tick % every != 0) return;
        var table = world.Characters;
        var production = world.SystemOf<Production.ProductionSystem>();
        var combat = world.SystemOf<Combat.CombatSystem>();
        var threats = new Dictionary<int, OutpostThreats>();
        var pass = new Pass();
        foreach (var squad in world.Squads.All)
        {
            if (squad.Faction != world.Player.Faction || squad.Work is not { On: true } work) continue;
            currentSquad = squad.Id;
            var info = JobInfo.Of(work.Job);
            // A crew's split is planned for the outpost most of its members stand in.
            CrewPlan? plan = null;
            if ((info.Material != 0 || info.Hauls) && production is not null && MainOutpost(world, squad) is { } home) plan = PlanCrew(world, production, squad, work, info, home);
            foreach (var id in squad.Members)
            {
                if (!table.TryResolveNext(id, out int slot) || table.Cold(slot) is not { } c) continue;
                var town = WorkRules.OutpostAt(world, table.Next[slot].Position);
                OutpostThreats? fights = null;
                if (town is not null && !threats.TryGetValue(town.Number, out fights)) threats[town.Number] = fights = Threats(world, combat, town);
                Decide(world, production, squad, work, info, id, slot, c, town, fights, plan, pass);
            }
        }
    }

    /// <summary>What one pass hands out, so two members are not sent to the same bed or overfill a site.</summary>
    sealed class Pass
    {
        public readonly HashSet<string> Beds = new(StringComparer.Ordinal);
        public readonly Dictionary<string, int> Builders = new(StringComparer.Ordinal);
    }

    static Construction.PlayerTown? MainOutpost(World world, Squad squad)
    {
        var table = world.Characters;
        return squad.Members.Where(table.IsAlive).Select(id => WorkRules.OutpostAt(world, table.Next[id.Slot].Position)).OfType<Construction.PlayerTown>()
            .GroupBy(t => t.Number).OrderByDescending(g => g.Count()).ThenBy(g => g.Key).FirstOrDefault()?.First();
    }

    /// <summary>Whether the member's task is the one the work system gave it (sent, and no order from the player since).</summary>
    bool Ours(World world, CharacterId id, CharacterTask task) =>
        task != CharacterTask.Idle && sent.TryGetValue(id, out var was) && world.Trace.LastPlayerOrder(id) < was.Tick;

    void Send(World world, CharacterId id, string site) => sent[id] = (site, world.Tick);

    void Decide(World world, Production.ProductionSystem? production, Squad squad, SquadWork work, JobInfo info, CharacterId id, int slot, CharacterCold c,
        Construction.PlayerTown? town, OutpostThreats? fights, CrewPlan? plan, Pass pass)
    {
        var table = world.Characters;
        ref var n = ref table.Next[slot];
        var task = (CharacterTask)n.Task;
        if (c.Medical is { Dead: true }) { status[id] = "dead"; return; }
        if (c.Medical is { Incapacitated: true } || !c.CarriedBy.IsNone) { status[id] = "down"; return; }
        if (c.IsImprisoned) { status[id] = "imprisoned"; return; }
        if (town is null || fights is null) { status[id] = "away from the outpost"; return; }
        c.StationUntil = 0;

        // 2. Fighting, and the player's own orders: left alone.
        if (c.InCombat || task == CharacterTask.Attack) { status[id] = "fighting"; return; }
        bool ours = Ours(world, id, task);
        // In bed counts as free whoever put it there: the work system decides when it sleeps.
        bool free = task == CharacterTask.Idle || ours || c.InSomething == Locks.HeldState.Bed;
        if (!free)
        {
            status[id] = TaskWord(task);
            return;
        }
        if (c.Formation is not null) { status[id] = "in formation"; return; }
        if (c.LootAll is not null) { status[id] = "looting"; return; }
        if (!c.FollowOrder.IsNone) { status[id] = "following"; return; }

        // 3. A fight at the outpost.
        if (Defend(world, work, slot, id, c, task, fights, town)) return;
        task = (CharacterTask)n.Task;

        // 4. Rest: off shift, or hurt at night. In the day, on shift, out of bed.
        double hours = world.GameHours;
        bool onShift = work.OnShift(hours), night = !SquadWork.IsDay(hours), robot = c.Race is { IsRobot: true };
        float whole = WorkRules.Rested(c);
        bool inBed = c.InSomething == Locks.HeldState.Bed;
        bool rest = !onShift || night && !robot && whole < (inBed ? HealedAt : HurtBelow);
        if (inBed)
        {
            if (rest) status[id] = onShift ? "healing in bed" : "sleeping (off shift)";
            else
            {
                world.Defer(new Locks.GetOutOrder([id]) { Tick = world.Tick + 1 });
                status[id] = "getting up for work";
            }
            return;
        }
        if (rest)
        {
            if (ours && task == CharacterTask.Sleep) { status[id] = onShift ? "to bed: hurt" : "to bed: off shift"; return; }
            if (Bed(world, town, c, pass.Beds) is { } bed)
            {
                world.Defer(new Locks.UseBedOrder([id], bed) { Tick = world.Tick + 1 });
                Send(world, id, bed);
                status[id] = onShift ? "to bed: hurt" : "to bed: off shift";
                Log(world, $"{c.Name} to bed ({(onShift ? "hurt" : "off shift")})");
            }
            else
            {
                if (ours && production is not null) production.WorkStop(world, slot);
                status[id] = onShift ? "hurt: no free bed" : "off shift: no free bed";
            }
            return;
        }

        // 5. Room in its inventory.
        if (task == CharacterTask.Idle && c.Inventory is { } bag && Items.InventoryCommands.Cramped(bag) && Production.ProductionSystem.MakeRoom(world, slot, null) == 0)
        {
            Production.ProductionSystem.InventoryFull(world, id, c);
            status[id] = "inventory full of things it needs";
            return;
        }
        // Back from a building it was sent to this soon, with no order of the player's between: that one does not work for it for a while.
        if (task == CharacterTask.Idle && sent.TryGetValue(id, out var last) && (world.Tick - last.Tick) * world.TickSeconds < 10 && world.Trace.LastPlayerOrder(id) < last.Tick)
            stuck[(id, last.Site)] = world.Tick + (long)(StuckSeconds / world.TickSeconds);

        // 6. The job.
        if (production is null) { status[id] = "waiting"; return; }
        status[id] = Work(world, production, work, info, id, slot, c, task, ours, town, plan, pass);
    }

    bool Stuck(World world, CharacterId id, string site) => stuck.TryGetValue((id, site), out long until) && until > world.Tick;

    /// <summary>The job's work for a free member: what it is doing now (status), sending it when it is not on it yet.</summary>
    string Work(World world, Production.ProductionSystem production, SquadWork work, JobInfo info, CharacterId id, int slot, CharacterCold c, CharacterTask task, bool ours,
        Construction.PlayerTown town, CrewPlan? plan, Pass pass)
    {
        var only = work.Buildings.Count > 0 ? work.Buildings : null;
        if (plan is not null && plan.Roles.TryGetValue(id, out var role)) return Crew(world, production, plan, role, slot, id, c, task, ours);
        if (info.Builds)
        {
            if (ours && task is CharacterTask.Build or CharacterTask.DeliverMaterials or CharacterTask.FetchMaterials && c.BuildSite is { } on)
                return (task == CharacterTask.FetchMaterials ? "fetching materials for " : "building ") + SiteName(world, on);
            return Build(world, id, slot, c, town, only, pass);
        }
        if (info.Turrets)
        {
            if (ours && task == CharacterTask.Operate && c.OperateSite is { } gun) return "on the " + SiteName(world, gun);
            if (production.WorkTurret(world, slot, town) is { } turret && !Stuck(world, id, turret) && production.WorkBegin(world, slot, turret))
            {
                if (!sent.TryGetValue(id, out var was) || was.Site != turret) Log(world, $"{c.Name} to man the {SiteName(world, turret)}");
                Send(world, id, turret);
                return "on the " + SiteName(world, turret);
            }
            return "on guard";
        }
        // A machine job.
        bool Fits(MachineData m) => info.Stats.Contains((StatsEnumerated)m.Stat) && (m.Operable || work.Job == SquadJob.Workers) || work.Job == SquadJob.Workers && m.IsAutomatic && m.IsProducer;
        if (ours && task == CharacterTask.Operate && c.OperateSite is { } now && world.Machines.Find(now) is { } running && Fits(running.Data)) return "working the " + SiteName(world, now);
        if (production.WorkMachine(world, slot, town, Fits, only) is { } machine && !Stuck(world, id, machine) && production.WorkBegin(world, slot, machine))
        {
            if (!sent.TryGetValue(id, out var was) || was.Site != machine) Log(world, $"{c.Name} to the {SiteName(world, machine)}");
            Send(world, id, machine);
            return "working the " + SiteName(world, machine);
        }
        if (ours) production.WorkStop(world, slot);
        return production.HasMachine(world, town, Fits) ? "nothing to do right now" : "no machines for this job in the outpost";
    }

    /// <summary>A builder: the nearest site of the outpost (or of its list) to build or repair, with room for another builder and materials it can get.</summary>
    string Build(World world, CharacterId id, int slot, CharacterCold c, Construction.PlayerTown town, IReadOnlyList<string>? only, Pass pass)
    {
        var table = world.Characters;
        var at = table.Next[slot].Position;
        Construction.BuildSite? best = null;
        float bestD = float.MaxValue;
        string? waiting = null;
        int sites = 0;
        foreach (var site in world.Buildings.All)
        {
            if (site.Owner != world.Player.Faction || site.Paused || site.Dismantling || site.Ruined || site.Complete && !site.Destroyed) continue;
            if (only is not null ? !only.Contains(site.Id) : site.Town != town.Number) continue;
            sites++;
            if (Stuck(world, id, site.Id)) continue;
            if (Lacks(world, slot, site) is { } item) { waiting = $"{SiteName(world, site.Id)} needs {ItemName(item)}"; continue; }
            int builders = pass.Builders.GetValueOrDefault(site.Id);
            for (int s = 0; s < table.HighWater && builders < BuildersPerSite; s++)
                if (s != slot && table.Next[s].Alive && table.Next[s].Task is (byte)CharacterTask.Build or (byte)CharacterTask.DeliverMaterials or (byte)CharacterTask.FetchMaterials
                    && table.Cold(s)?.BuildSite == site.Id) builders++;
            if (builders >= BuildersPerSite) continue;
            float d = Vector3.DistanceSquared(at, site.Position);
            if (d < bestD) (best, bestD) = (site, d);
        }
        if (best is null) return sites == 0 ? "nothing to build" : waiting is not null ? "waiting: " + waiting : "every site has its builders";
        pass.Builders[best.Id] = pass.Builders.GetValueOrDefault(best.Id) + 1;
        if (!sent.TryGetValue(id, out var was) || was.Site != best.Id) Log(world, $"{c.Name} to {(best.Complete ? "repair" : "build")} the {SiteName(world, best.Id)}");
        world.Defer(new Construction.BuildOrder([id], best.Id, Repair: best.Complete && best.Destroyed) { Tick = world.Tick + 1 });
        Send(world, id, best.Id);
        return (best.Complete ? "repairing " : "building ") + SiteName(world, best.Id);
    }

    /// <summary>The material a site waits for that the member cannot get anywhere (carried, in storage, on the ground, a machine's output in range), noted once in the log; null: none.</summary>
    string? Lacks(World world, int slot, Construction.BuildSite site)
    {
        if (world.SystemOf<Construction.ConstructionSystem>()?.Missing(world, slot, site) is not { } item) { lacking.Remove(site.Id); return null; }
        if (!lacking.TryGetValue(site.Id, out var said) || said != item)
        {
            lacking[site.Id] = item;
            Log(world, $"! {SiteName(world, site.Id)} needs {ItemName(item)}: none in storage or on the ground");
        }
        return item;
    }

    static string TaskWord(CharacterTask task) => task switch
    {
        CharacterTask.GoTo => "walking (your order)",
        CharacterTask.Operate => "working (your order)",
        CharacterTask.Build or CharacterTask.DeliverMaterials or CharacterTask.FetchMaterials => "building (your order)",
        CharacterTask.Treat => "treating someone",
        CharacterTask.Sleep => "in bed (your order)",
        CharacterTask.Loot => "looting",
        CharacterTask.Haul => "hauling (your order)",
        _ => "busy (your order)",
    };

    /// <summary>A free bed of the outpost the character's faction may use, not handed out already this pass; null without one.</summary>
    static string? Bed(World world, Construction.PlayerTown town, CharacterCold c, HashSet<string> taken)
    {
        var table = world.Characters;
        foreach (var f in world.Fixtures.All)
        {
            if (f.Kind != Locks.FixtureKind.Bed || f.Bed.Function != Meitou.Data.World.TownBuildings.BedFunction || taken.Contains(f.Id)) continue;
            if (world.Buildings.Find(f.Building) is not { Complete: true } b || b.Town != town.Number || !Locks.FixtureRegistry.IsFree(table, f) || !world.Fixtures.Reachable(world, f, c.Faction)) continue;
            taken.Add(f.Id);
            return f.Id;
        }
        return null;
    }
}
