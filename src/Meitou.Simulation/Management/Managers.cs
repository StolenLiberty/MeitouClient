using System.Numerics;
using System.Text.Json;
using Meitou.Simulation.Autonomy;

namespace Meitou.Simulation.Management;

/// <summary>What a building is for, as the manager ranks sites to build (by its BUILDING <c>building category</c>; a bed is housing).</summary>
public enum BuildGroup : byte { Defence, Food, Power, Housing, Storage, Industry, Research, Other }

/// <summary>
/// A player squad's manager (Meitou-only): while the squad's autonomy is on and <see cref="Manager"/> is up and in one of the player's outposts, <see cref="ManagerSystem"/> runs the
/// squad's members there. Work hours, job priorities, turrets and rest thresholds are the squad's <see cref="AutonomySettings"/> (the same window).
/// </summary>
public sealed record ManagementSettings
{
    /// <summary>The manager (any of the player's characters); None until resolved after a load (<see cref="ManagerSquadIndex"/>, <see cref="ManagerName"/>).</summary>
    public CharacterId Manager { get; init; } = CharacterId.None;
    /// <summary>The manager as a save keeps it: his squad's <see cref="Squad.Index"/> and his name.</summary>
    public int ManagerSquadIndex { get; init; } = -1;
    public string ManagerName { get; init; } = "";
    public DefenceMode Defence { get; init; } = DefenceMode.StandGround;
    /// <summary>Whether he puts idle members on building and repairing the outpost's sites.</summary>
    public bool Build { get; init; } = true;
    /// <summary>How much each <see cref="BuildGroup"/> matters (0..10, 0: never built by him); a site goes before a machine whose job priority is lower.</summary>
    public IReadOnlyList<float> BuildPriority { get; init; } = DefaultBuildPriority;
    /// <summary>
    /// The buildings assigned to the squad (site ids, first = most important); empty: the whole outpost by the job and building priorities. With some, its people work, man and build
    /// only those, in this order, and are moved up the list as work turns up there (<see cref="ManagerSystem"/> "the chain").
    /// </summary>
    public IReadOnlyList<string> Buildings { get; init; } = [];
    /// <summary>
    /// The one material the squad's machines handle (a world resource: <see cref="Meitou.Data.Gameplay.Construction.ResourceField.IronResource"/>, stone, copper; 0 any): its mines and
    /// the labouring refineries down its chain (<see cref="Production.ProductionSystem.InChain"/>), so a stone crew never mines iron.
    /// </summary>
    public int Material { get; init; }

    public static readonly IReadOnlyList<float> DefaultBuildPriority = [6, 8, 7, 5, 4, 4, 5, 2];

    public int PriorityOf(BuildGroup group) => (int)(BuildPriority.Count > (int)group ? BuildPriority[(int)group] : DefaultBuildPriority[(int)group]);

    /// <summary>The group of a <c>building category</c> (the base game's values; anything else is Other).</summary>
    public static BuildGroup GroupOf(string? category, bool bed) => bed ? BuildGroup.Housing : category?.ToUpperInvariant() switch
    {
        "WALLS" or "DEFENCE" => BuildGroup.Defence,
        "FARMING" or "FOOD" => BuildGroup.Food,
        "POWER" => BuildGroup.Power,
        "BUILDINGS" => BuildGroup.Housing,
        "STORAGE" => BuildGroup.Storage,
        "MINING" or "CRAFTING" => BuildGroup.Industry,
        "TECH" => BuildGroup.Research,
        _ => BuildGroup.Other,
    };

    sealed record Stored(int S, string N, byte D, bool B, List<float>? P = null, List<string>? A = null, int M = 0);

    public string Write() => JsonSerializer.Serialize(new Stored(ManagerSquadIndex, ManagerName, (byte)Defence, Build, [.. BuildPriority], [.. Buildings], Material));

    public static ManagementSettings? Read(string text)
    {
        Stored? s;
        try { s = JsonSerializer.Deserialize<Stored>(text); }
        catch (JsonException) { return null; }
        return s is null ? null : new ManagementSettings
        {
            ManagerSquadIndex = s.S, ManagerName = s.N ?? "", Defence = Enum.IsDefined((DefenceMode)s.D) ? (DefenceMode)s.D : DefenceMode.StandGround, Build = s.B,
            Buildings = s.A is { } a ? [.. a.Where(x => x.Length > 0).Distinct()] : [], Material = Math.Clamp(s.M, 0, 4),
            BuildPriority = s.P is { Count: > 0 } p ? [.. p.Concat(DefaultBuildPriority.Skip(p.Count)).Take(DefaultBuildPriority.Count).Select(v => Math.Clamp(v, 0, 10))] : DefaultBuildPriority,
        };
    }
}

/// <summary>Replaces a player squad's management (null: no manager). Applied also while paused.</summary>
public sealed record SetSquadManagement(int SquadId, ManagementSettings? Settings) : SimCommand;

/// <summary>
/// Managers (Meitou-only, engine choices throughout; docs/simulation.md "Managers"). Every <see cref="CheckSeconds"/>, for each player squad with autonomy on (and no old-mod behaviours): when the manager
/// is up and inside one of the player's outposts, each member in that outpost who is free (idle, no formation, follow or Loot All) is given something to do, in this order: a wounded one (rest
/// threshold) to bed; in rest hours to bed; else the best machine with work by the squad's job priorities (<see cref="Production.ProductionSystem.AutonomyLabour"/>), else a free turret (Man
/// Turrets), else a site to build or repair (<see cref="ManagementSettings.Build"/>). Members he sent to work go to bed at the end of their shift or when hurt, and are got up when the shift
/// starts and they are healed. A fight at the outpost comes before all of it, by the squad's <see cref="DefenceMode"/> (Defence.cs).
/// </summary>
public sealed partial class ManagerSystem(PopulationData? data = null) : ITickSystem
{
    /// <summary>At most this many builders on one site (engine choice).</summary>
    public const int BuildersPerSite = 3;
    readonly Dictionary<string, BuildGroup> groups = new(StringComparer.Ordinal);

    /// <summary>The group of a site's BUILDING record (cached by record id).</summary>
    BuildGroup GroupOfRecord(string record)
    {
        if (groups.TryGetValue(record, out var g)) return g;
        var r = data?.Db.Find(record);
        bool bed = r is not null && Locks.FixtureRegistry.BedDataOf(data!.Db, r) is { Function: Meitou.Data.World.TownBuildings.BedFunction };
        return groups[record] = ManagementSettings.GroupOf(r?.GetString("building category"), bed);
    }
    public const float CheckSeconds = 2;
    /// <summary>A building the manager sent one to and that it left again this soon is not given to it again for a while (no materials, a site it cannot reach).</summary>
    const float StuckSeconds = 120;

    /// <summary>What each managed member is doing, for the window (by character).</summary>
    readonly Dictionary<CharacterId, string> status = [];
    /// <summary>The building each member was last sent to and when (work and build), to spot one it cannot work.</summary>
    readonly Dictionary<CharacterId, (string Site, long Tick)> sent = [];
    readonly Dictionary<(CharacterId, string), long> stuck = [];

    public string? StatusOf(CharacterId id) => status.GetValueOrDefault(id);

    /// <summary>Each managed squad's latest decisions, newest last (at most <see cref="LogLines"/>).</summary>
    readonly Dictionary<int, List<string>> logs = [];
    const int LogLines = 8;
    (int Squad, string Manager) current;

    public IReadOnlyList<string> LogOf(int squad) => logs.TryGetValue(squad, out var log) ? [.. log] : [];

    /// <summary>A decision for the window's log: "{manager} sent {member} to {what}" with the game time.</summary>
    void Log(World world, CharacterCold member, string what)
    {
        if (!logs.TryGetValue(current.Squad, out var log)) logs[current.Squad] = log = [];
        double h = ((world.GameHours % 24) + 24) % 24;
        string line = member.Name == current.Manager ? $"{member.Name} went {what}" : $"{current.Manager} sent {member.Name} {what}";
        if (log.Count > 0 && log[^1].EndsWith(line, StringComparison.Ordinal)) return;   // the same again (an order not taken up yet)
        log.Add($"{(int)h:00}:{(int)(h % 1 * 60):00}  {line}");
        if (log.Count > LogLines) log.RemoveAt(0);
    }

    /// <summary>The name of a site's building, for the log.</summary>
    string SiteName(World world, string site) => world.Buildings.Find(site) is { Name.Length: > 0 } b ? b.Name
        : world.Machines.Find(site)?.Data.Name is { Length: > 0 } name ? name : world.Buildings.Find(site) is { } r ? data?.Db.Find(r.Record)?.Name ?? "a building" : "a building";

    public void Inputs(World world, IReadOnlyList<SimCommand> commands)
    {
        foreach (var command in commands)
            if (command is SetSquadManagement set && world.Squads.Find(set.SquadId) is { } squad && squad.Faction == world.Player.Faction)
            {
                squad.Management = set.Settings is { } m ? Resolve(world, m) : null;
                if (squad.Management is null) foreach (var id in squad.Members) status.Remove(id);
            }
    }

    /// <summary>Fills in the manager's id from his save reference, or the reference from the id.</summary>
    static ManagementSettings Resolve(World world, ManagementSettings m)
    {
        var table = world.Characters;
        if (!m.Manager.IsNone && table.TryResolveNext(m.Manager, out int slot) && table.Cold(slot) is { } c)
            return m with { ManagerName = c.Name, ManagerSquadIndex = world.Squads.Find(c.SquadId)?.Index ?? -1 };
        foreach (var squad in world.Squads.All)
        {
            if (squad.Faction != world.Player.Faction || squad.Index != m.ManagerSquadIndex) continue;
            foreach (var id in squad.Members)
                if (table.TryResolveNext(id, out int s) && table.Cold(s)?.Name == m.ManagerName) return m with { Manager = id };
        }
        return m;
    }

    public void SlowWorld(World world)
    {
        int every = Math.Max(1, (int)MathF.Round(CheckSeconds / world.TickSeconds));
        if (world.Tick % every != 0) return;
        var table = world.Characters;
        var production = world.SystemOf<Production.ProductionSystem>();
        var combat = world.SystemOf<Combat.CombatSystem>();
        var threats = new Dictionary<int, OutpostThreats>();
        var beds = new HashSet<string>();
        var building = new Dictionary<string, int>();
        foreach (var squad in world.Squads.All)
        {
            // Autonomy on (without the old mod's behaviours) is the manager's: the one picked, else the squad's leader. Off is plain Kenshi.
            if (squad.Faction != world.Player.Faction || squad.Autonomy is not { ManagerRuns: true }) continue;
            var m = squad.Management ?? new ManagementSettings();
            if (!m.Manager.IsNone && !table.IsAlive(m.Manager) || m.Manager.IsNone && m.ManagerName.Length > 0) squad.Management = m = Resolve(world, m);
            var bossId = m.Manager.IsNone ? squad.Leader : m.Manager;
            if (!table.TryResolveNext(bossId, out int boss) || table.Cold(boss) is not { } manager || manager.Medical is { Dead: true })
            {
                Mark(world, squad, "no manager");
                continue;
            }
            if (manager.Medical is { Incapacitated: true } || manager.IsImprisoned || !manager.CarriedBy.IsNone || OutpostAt(world, table.Next[boss].Position) is not { } town)
            {
                Mark(world, squad, $"{manager.Name} is away");
                continue;
            }
            if (!threats.TryGetValue(town.Number, out var fights)) threats[town.Number] = fights = Threats(world, combat, town);
            var settings = squad.Autonomy ?? new AutonomySettings();
            bool rest = settings.IsRestTime(world.GameHours);
            current = (squad.Id, manager.Name);
            var plan = m.Material != 0 && production is not null ? PlanCrew(world, production, squad, m, town, bossId) : null;
            foreach (var id in squad.Members)
                Manage(world, production, m, settings, rest, town, fights, table.Next[boss].Position, id, beds, building, plan);
            if (fights.Attackers.Count == 0 && !rest && plan is null) Chain(world, production, squad, m);
        }
    }

    void Mark(World world, Squad squad, string text)
    {
        foreach (var id in squad.Members) status[id] = text;
        foreach (var id in squad.Members) sheltering.Remove(id);
    }

    void Manage(World world, Production.ProductionSystem? production, ManagementSettings m, AutonomySettings s, bool rest, Construction.PlayerTown town, OutpostThreats threats, Vector3 boss,
        CharacterId id, HashSet<string> beds, Dictionary<string, int> building, CrewPlan? plan = null)
    {
        var table = world.Characters;
        if (!table.TryResolveNext(id, out int slot) || table.Cold(slot) is not { } c) return;
        ref var n = ref table.Next[slot];
        if (c.Medical is { Dead: true }) { status[id] = "dead"; return; }
        if (c.Medical is { Incapacitated: true } || !c.CarriedBy.IsNone) { status[id] = "down"; return; }
        if (c.IsImprisoned) { status[id] = "imprisoned"; return; }
        if (OutpostAt(world, n.Position) is not { } here || here.Number != town.Number) { status[id] = "away from the outpost"; return; }
        var task = (CharacterTask)n.Task;

        // A fight at the outpost first (Defence.cs).
        if (Defend(world, m, slot, id, c, task, threats, town)) return;
        task = (CharacterTask)n.Task;

        bool mayRest = AutonomyRules.MayRestHurt(s, world.GameHours);
        bool wounded = mayRest && c.Race is not { IsRobot: true } && AutonomyRules.Rested(c) * 100 < s.RestThreshold;
        // Up once healed, or as soon as it is day in work hours (healing waits for the night).
        bool healed = !mayRest || AutonomyRules.Rested(c) * 100 >= s.HealedThreshold || c.Race is { IsRobot: true };
        // In bed: up when the shift starts and it is healed (only one the manager put there, or any while it is work time).
        if (c.InSomething == Locks.HeldState.Bed)
        {
            if (!rest && healed) { world.Defer(new Locks.GetOutOrder([id]) { Tick = world.Tick + 1 }); status[id] = "getting up"; }
            else status[id] = rest ? "sleeping (off shift)" : "resting (hurt)";
            return;
        }
        // One the manager sent to work stops for the end of its shift or a wound.
        bool ours = sent.TryGetValue(id, out var job) && (task == CharacterTask.Operate && c.OperateSite == job.Site || task is CharacterTask.Build or CharacterTask.DeliverMaterials or CharacterTask.FetchMaterials && c.BuildSite == job.Site);
        if (ours && (rest || wounded))
        {
            if (Bed(world, town, c, beds) is { } bed) { world.Defer(new Locks.UseBedOrder([id], bed) { Tick = world.Tick + 1 }); status[id] = rest ? "off to bed" : "off to bed (hurt)"; Log(world, c, rest ? "to bed (shift over)" : "to bed (hurt)"); }
            else status[id] = rest ? "off shift, no free bed" : "hurt, no free bed";
            return;
        }
        // Sitting on a chair (the idle sit job) is free time: work takes him off it.
        bool sitting = c.Implicit == Meitou.Data.Gameplay.Ai.TaskType.SIT_AROUND && task == CharacterTask.Operate;
        if (sitting) task = CharacterTask.Idle;
        // A material crew: each member keeps his job (mining, processing, hauling).
        if (plan is not null && production is not null && !wounded && !rest && !c.InCombat && c.Formation is null && c.LootAll is null && c.FollowOrder.IsNone
            && task is CharacterTask.Idle or CharacterTask.Operate or CharacterTask.Haul && plan.Roles.TryGetValue(id, out var role)
            && !(task == CharacterTask.Idle && c.Inventory is { } bag && Items.InventoryCommands.Cramped(bag))
            && Crew(world, production, plan, role, slot, id, c, task)) return;
        if (task != CharacterTask.Idle || c.InCombat || c.Formation is not null || c.LootAll is not null || !c.FollowOrder.IsNone)
        {
            status[id] = ours ? task == CharacterTask.FetchMaterials ? "fetching materials" : task is CharacterTask.Build or CharacterTask.DeliverMaterials ? "building: " + SiteName(world, job.Site) : "working: " + SiteName(world, job.Site) : c.Formation is not null ? "in formation" : TaskWord(task);
            return;
        }
        // Too full to carry anything: it waits for the player to empty it (and says so now and then).
        if (c.Inventory is { } carried && Items.InventoryCommands.Cramped(carried) && Production.ProductionSystem.MakeRoom(world, slot, null) == 0)
        {
            Production.ProductionSystem.InventoryFull(world, id, c);
            status[id] = "inventory full";
            return;
        }
        // Back from a building it was just sent to: that one does not work for it for a while.
        // (Not when the player took it off: his order is no sign the building cannot be worked.)
        if (sent.TryGetValue(id, out var last) && (world.Tick - last.Tick) * world.TickSeconds < 10 && world.Trace.LastPlayerOrder(id) < last.Tick)
            stuck[(id, last.Site)] = world.Tick + (long)(StuckSeconds / world.TickSeconds);
        if (wounded || rest)
        {
            if (Bed(world, town, c, beds) is { } bed) { world.Defer(new Locks.UseBedOrder([id], bed) { Tick = world.Tick + 1 }); status[id] = wounded ? "off to bed (hurt)" : "off to bed"; Log(world, c, wounded ? "to bed (hurt)" : "to bed (off shift)"); }
            else status[id] = wounded ? "hurt, no free bed" : "off shift, no free bed";
            return;
        }
        // Assigned buildings: only those, the first in the list that needs building or has work for it.
        if (m.Buildings.Count > 0)
        {
            if (Assigned(world, production, m, slot, id, c, building)) return;
            status[id] = "idle: nothing to do at his buildings";
            return;
        }
        // A site to build whose group matters more than the best machine's job goes first (Building page against the Jobs page).
        var site = m.Build ? Site(world, m, town, n.Position, id, building) : null;
        var machine = production?.AutonomyLabour(world, slot, s, town.Number, boss, m.Material) is { } found && !Stuck(world, id, found) ? found : null;
        int machinePriority = machine is null ? -1 : MachinePriority(world, s, machine);
        if (site is { } first && (machine is null || site.Value.Priority > machinePriority))
        {
            StartBuild(world, id, c, first.Site, building);
            return;
        }
        if (production is not null && machine is not null && production.AutonomyBegin(world, slot, machine))
        {
            if (!sent.TryGetValue(id, out var was) || was.Site != machine) Log(world, c, "to work the " + SiteName(world, machine));
            sent[id] = (machine, world.Tick);
            status[id] = "working: " + SiteName(world, machine);
            return;
        }
        if (production is not null && s.ManTurrets && production.AutonomyTurret(world, slot, town.Number) is { } turret && !Stuck(world, id, turret) && production.AutonomyBegin(world, slot, turret))
        {
            if (!sent.TryGetValue(id, out var was) || was.Site != turret) Log(world, c, "to man the " + SiteName(world, turret));
            sent[id] = (turret, world.Tick);
            status[id] = "on a turret";
            return;
        }
        if (site is { } later)
        {
            StartBuild(world, id, c, later.Site, building);
            return;
        }
        status[id] = sitting ? "sitting: nothing to do" : "idle: nothing to do";
    }

    /// <summary>
    /// An assigned building for a free member, the list in order: one placed or damaged is built or repaired (at most <see cref="BuildersPerSite"/>), a machine or turret with work and a
    /// free place is worked. True when it was sent.
    /// </summary>
    bool Assigned(World world, Production.ProductionSystem? production, ManagementSettings m, int slot, CharacterId id, CharacterCold c, Dictionary<string, int> building)
    {
        foreach (var siteId in m.Buildings)
        {
            if (world.Buildings.Find(siteId) is not { } site || site.Owner != world.Player.Faction || Stuck(world, id, siteId)) continue;
            if (!site.Complete || site.Destroyed)
            {
                if (!m.Build || site.Paused || site.Dismantling || site.Ruined || Builders(world, siteId) + building.GetValueOrDefault(siteId) >= BuildersPerSite || Lacks(world, slot, site)) continue;
                StartBuild(world, id, c, site, building);
                return true;
            }
            if (production is null || !production.AutonomyCanWork(world, slot, siteId) || !production.AutonomyBegin(world, slot, siteId)) continue;
            if (!sent.TryGetValue(id, out var was) || was.Site != siteId) Log(world, c, "to work the " + SiteName(world, siteId));
            sent[id] = (siteId, world.Tick);
            status[id] = "working: " + SiteName(world, siteId);
            return true;
        }
        return false;
    }

    static int Builders(World world, string siteId)
    {
        var table = world.Characters;
        int count = 0;
        for (int s = 0; s < table.HighWater; s++)
            if (table.Next[s].Alive && table.Next[s].Task is (byte)CharacterTask.Build or (byte)CharacterTask.DeliverMaterials or (byte)CharacterTask.FetchMaterials && table.Cold(s)?.BuildSite == siteId) count++;
        return count;
    }

    /// <summary>Seconds between the chain's checks, and how long one must have been at a building before it is moved up the list.</summary>
    public const float ChainSeconds = 20, ChainStay = 30;
    readonly Dictionary<int, long> nextChain = [];
    readonly Dictionary<string, string> stuckSaid = new(StringComparer.Ordinal);

    /// <summary>
    /// The chain (assigned buildings, every <see cref="ChainSeconds"/>): one member working a building lower on the list, there at least <see cref="ChainStay"/>, is moved to the first
    /// building above it that has work and a free place for it (stone piling up in storage takes a miner to the processor; the processor empty, he is idle and goes back down the list).
    /// Then a stuck building (no power, out of an input, full with no storage) is said once in the log.
    /// </summary>
    void Chain(World world, Production.ProductionSystem? production, Squad squad, ManagementSettings m)
    {
        if (production is null || m.Buildings.Count == 0) return;
        long every = (long)(ChainSeconds / world.TickSeconds);
        if (nextChain.TryGetValue(squad.Id, out long due) && world.Tick < due) return;
        nextChain[squad.Id] = world.Tick + every;
        var table = world.Characters;
        var order = m.Buildings.Select((id, k) => (id, k)).ToDictionary(p => p.id, p => p.k, StringComparer.Ordinal);
        (int Slot, CharacterId Id, int At)? lowest = null;
        foreach (var id in squad.Members)
        {
            if (!table.TryResolveNext(id, out int s) || table.Cold(s) is not { } c || c.InCombat || table.Next[s].Task != (byte)CharacterTask.Operate) continue;
            if (c.OperateSite is not { } here || !order.TryGetValue(here, out int at) || !sent.TryGetValue(id, out var job) || job.Site != here) continue;
            if ((world.Tick - job.Tick) * world.TickSeconds < ChainStay) continue;
            if (lowest is null || at > lowest.Value.At) lowest = (s, id, at);
        }
        if (lowest is { } mover)
            for (int k = 0; k < mover.At; k++)
            {
                string up = m.Buildings[k];
                if (world.Buildings.Find(up) is not { Complete: true, Destroyed: false } || !production.AutonomyCanWork(world, mover.Slot, up)) continue;
                var cold = table.Cold(mover.Slot)!;
                string from = SiteName(world, m.Buildings[mover.At]);
                if (!production.AutonomyBegin(world, mover.Slot, up)) continue;
                Log(world, cold, $"from the {from} to the {SiteName(world, up)}");
                sent[mover.Id] = (up, world.Tick);
                status[mover.Id] = "working: " + SiteName(world, up);
                break;
            }
        foreach (var siteId in m.Buildings)
        {
            string? why = production.AutonomyStuck(world, siteId);
            if (why is null) { stuckSaid.Remove(siteId); continue; }
            if (stuckSaid.TryGetValue(siteId, out var said) && said == why) continue;
            stuckSaid[siteId] = why;
            Note(world, why);
        }
    }

    /// <summary>A line of the log that is not a decision (a stuck building).</summary>
    void Note(World world, string text)
    {
        if (!logs.TryGetValue(current.Squad, out var log)) logs[current.Squad] = log = [];
        double h = ((world.GameHours % 24) + 24) % 24;
        log.Add($"{(int)h:00}:{(int)(h % 1 * 60):00}  ! {text}");
        if (log.Count > LogLines) log.RemoveAt(0);
    }

    /// <summary>Sites waiting for a material none of the squad can get, and the item (said once in the log until it can be had).</summary>
    readonly Dictionary<string, string> lacking = new(StringComparer.Ordinal);

    /// <summary>Whether a site waits for a material the member cannot get (none carried, in storage, on the ground or in a machine's output in range); notes it once.</summary>
    bool Lacks(World world, int slot, Construction.BuildSite site)
    {
        if (world.SystemOf<Construction.ConstructionSystem>()?.Missing(world, slot, site) is not { } item) { lacking.Remove(site.Id); return false; }
        if (!lacking.TryGetValue(site.Id, out var said) || said != item)
        {
            lacking[site.Id] = item;
            string name = data?.Db.Find(item)?.Name ?? "a material";
            Note(world, $"{SiteName(world, site.Id)} needs {name}: none in storage or on the ground");
        }
        return true;
    }

    void StartBuild(World world, CharacterId id, CharacterCold c, Construction.BuildSite site, Dictionary<string, int> building)
    {
        if (!sent.TryGetValue(id, out var was) || was.Site != site.Id) Log(world, c, (site.Complete ? "to repair the " : "to build the ") + SiteName(world, site.Id));
        building[site.Id] = building.GetValueOrDefault(site.Id) + 1;
        world.Defer(new Construction.BuildOrder([id], site.Id, Repair: site.Complete && site.Destroyed) { Tick = world.Tick + 1 });
        sent[id] = (site.Id, world.Tick);
        status[id] = (site.Complete ? "repairing: " : "building: ") + SiteName(world, site.Id);
    }

    /// <summary>The job priority (Jobs page) of the machine on a site.</summary>
    static int MachinePriority(World world, AutonomySettings s, string siteId)
    {
        foreach (var machine in world.Machines.All)
            if (machine.Site == siteId) return s.LabourPriority(AutonomyRules.KindOf(machine.Data));
        return 1;
    }

    bool Stuck(World world, CharacterId id, string site) => stuck.TryGetValue((id, site), out long until) && until > world.Tick;

    static string TaskWord(CharacterTask task) => task switch
    {
        CharacterTask.GoTo => "following your order",
        CharacterTask.Attack => "fighting",
        CharacterTask.Operate => "working (your order)",
        CharacterTask.Build or CharacterTask.DeliverMaterials => "building (your order)",
        CharacterTask.Treat => "treating someone",
        CharacterTask.Rest => "resting",
        CharacterTask.Loot => "looting",
        _ => "busy",
    };

    /// <summary>The player's outpost whose radius holds <paramref name="at"/> (the nearest), or null.</summary>
    static Construction.PlayerTown? OutpostAt(World world, Vector3 at)
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

    /// <summary>The enemies in the outpost: up, not the player's, hostile to the player's faction or fighting one of the player's characters.</summary>
    /// <summary>The nearest complete building of the outpost with a door (one has an inside), its centre; null without one.</summary>
    static Vector3? Shelter(World world, Construction.PlayerTown town, Vector3 at)
    {
        var doors = new HashSet<string>(world.Fixtures.All.Where(f => f.Kind == Locks.FixtureKind.Door).Select(f => f.Building), StringComparer.Ordinal);
        Vector3? best = null;
        float bestD = float.MaxValue;
        foreach (var site in world.Buildings.All)
        {
            if (!site.Complete || site.Destroyed || site.Town != town.Number || site.Parent is not null || !doors.Contains(site.Id)) continue;
            float d = Vector3.DistanceSquared(at, site.Position);
            if (d < bestD) (best, bestD) = (site.Position, d);
        }
        return best;
    }

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

    /// <summary>
    /// The site of the outpost to build (placed, not paused or being taken apart) or repair (complete and damaged) whose group matters most (<see cref="ManagementSettings.BuildPriority"/>, 0
    /// never), the nearest on a tie; with fewer than <see cref="BuildersPerSite"/> builders (on it now and sent this pass), not stuck for this one. Null: none.
    /// </summary>
    // ------------------------------------------------------------------ material crews

    public enum CrewRole : byte { Miner, Processor, Hauler, Manager }

    /// <summary>One pass's crew: the material's machines in the outpost, how many haulers, and each member's job.</summary>
    internal sealed record CrewPlan(List<Production.Machine> Mines, List<Production.Machine> Processors, int Haulers, Dictionary<CharacterId, CrewRole> Roles, Construction.PlayerTown Town, int Material);

    /// <summary>Each member's last crew job, kept from pass to pass so nobody is shuffled while the counts hold.</summary>
    readonly Dictionary<CharacterId, CrewRole> crewRoles = [];

    /// <summary>
    /// The split (engine choice): with 4 or more the manager only runs the crew; haulers 1 for 3-7 workers, 2 for 8-12 and so on (none below 3: then they carry their own); processor
    /// workers a third of the rest (at least 1 with a processor, at most its places); miners the rest up to the mines' places; anyone left over goes on the processors, then hauling.
    /// </summary>
    CrewPlan PlanCrew(World world, Production.ProductionSystem production, Squad squad, ManagementSettings m, Construction.PlayerTown town, CharacterId boss)
    {
        var table = world.Characters;
        var (mines, processors) = production.CrewMachines(world, town, m.Material, m.Buildings);
        var workers = new List<CharacterId>();
        foreach (var id in squad.Members)
            if (table.TryResolveNext(id, out int s) && table.Cold(s) is { } c && c.Medical is not { Dead: true } and not { Incapacitated: true } && !c.IsImprisoned
                && OutpostAt(world, table.Next[s].Position)?.Number == town.Number) workers.Add(id);
        var roles = new Dictionary<CharacterId, CrewRole>();
        if (workers.Count >= 4 && workers.Remove(boss)) roles[boss] = CrewRole.Manager;
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
            if (crewRoles.TryGetValue(id, out var was) && was != CrewRole.Manager && open[was] > 0) { roles[id] = was; open[was]--; }
            else unplaced.Add(id);
        foreach (var id in unplaced)
            foreach (var r in (ReadOnlySpan<CrewRole>)[CrewRole.Miner, CrewRole.Processor, CrewRole.Hauler])
                if (open[r] > 0) { roles[id] = r; open[r]--; break; }
        foreach (var (id, r) in roles) crewRoles[id] = r;
        foreach (var id in squad.Members) if (!roles.ContainsKey(id)) crewRoles.Remove(id);
        return new(mines, processors, roles.Count(r => r.Value == CrewRole.Hauler), roles, town, m.Material);
    }

    /// <summary>A crew member's job this pass; false to leave him to the usual rules (no machine of his kind).</summary>
    bool Crew(World world, Production.ProductionSystem production, CrewPlan plan, CrewRole role, int slot, CharacterId id, CharacterCold c, CharacterTask task)
    {
        string what = Production.ProductionSystem.MaterialName(plan.Material);
        if (role == CrewRole.Manager)
        {
            if (task is CharacterTask.Operate or CharacterTask.Haul) production.AutonomyStop(world, slot);
            status[id] = $"managing the {what} crew";
            return true;
        }
        if (role == CrewRole.Hauler)
        {
            if (task == CharacterTask.Haul && c.HaulFetch is { } item && c.HaulTo is { } to)
            {
                status[id] = $"hauling: {ItemName(item)} to the {SiteName(world, to)}";
                return true;
            }
            if (production.CrewHaul(world, slot, plan.Town, plan.Material, plan.Mines, plan.Processors) is { } trip && production.CrewHaulBegin(world, slot, trip))
            {
                Log(world, c, $"to carry {ItemName(trip.Item)} to the {SiteName(world, trip.To)}");
                status[id] = $"hauling: {ItemName(trip.Item)} to the {SiteName(world, trip.To)}";
                return true;
            }
            if (task is CharacterTask.Operate or CharacterTask.Haul) production.AutonomyStop(world, slot);
            status[id] = "hauling: nothing to carry";
            return true;
        }
        var stations = role == CrewRole.Miner ? plan.Mines : plan.Processors;
        if (stations.Count == 0) return false;
        c.StationUntil = plan.Haulers > 0 ? world.Tick + (long)(3 * CheckSeconds / world.TickSeconds) : 0;
        string doing = role == CrewRole.Miner ? "mining" : "processing";
        if (task == CharacterTask.Operate && c.OperateSite is { } on && stations.Any(x => x.Site == on))
        {
            status[id] = $"{doing}: {SiteName(world, on)}";
            return true;
        }
        if (production.CrewStation(world, slot, stations) is not { } station)
        {
            if (task is CharacterTask.Operate or CharacterTask.Haul) production.AutonomyStop(world, slot);
            status[id] = $"{doing}: waiting for a free place";
            return true;
        }
        if (!production.AutonomyBegin(world, slot, station)) return false;
        if (!sent.TryGetValue(id, out var was) || was.Site != station) Log(world, c, $"to {(role == CrewRole.Miner ? "mine at" : "work")} the {SiteName(world, station)}");
        sent[id] = (station, world.Tick);
        status[id] = $"{doing}: {SiteName(world, station)}";
        return true;
    }

    string ItemName(string record) => data?.Db.Find(record)?.Name is { Length: > 0 } n ? n.ToLowerInvariant() : "goods";

    (Construction.BuildSite Site, int Priority)? Site(World world, ManagementSettings m, Construction.PlayerTown town, Vector3 at, CharacterId id, Dictionary<string, int> building)
    {
        var table = world.Characters;
        Construction.BuildSite? best = null;
        int bestP = 0;
        float bestD = float.MaxValue;
        foreach (var site in world.Buildings.All)
        {
            if (site.Town != town.Number || site.Owner != world.Player.Faction || site.Paused || site.Dismantling || site.Ruined) continue;
            if (site.Complete && !site.Destroyed) continue;
            int p = m.PriorityOf(GroupOfRecord(site.Record));
            if (p <= 0 || p < bestP || Stuck(world, id, site.Id) || table.TryResolveNext(id, out int me) && Lacks(world, me, site)) continue;
            float d = Vector3.DistanceSquared(at, site.Position);
            if (p == bestP && d >= bestD) continue;
            int builders = building.GetValueOrDefault(site.Id);
            for (int s = 0; s < table.HighWater && builders < BuildersPerSite; s++)
                if (table.Next[s].Alive && table.Next[s].Task is (byte)CharacterTask.Build or (byte)CharacterTask.DeliverMaterials && table.Cold(s)?.BuildSite == site.Id) builders++;
            if (builders >= BuildersPerSite) continue;
            (best, bestP, bestD) = (site, p, d);
        }
        return best is null ? null : (best, bestP);
    }
}
