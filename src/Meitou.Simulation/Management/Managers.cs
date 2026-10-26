using System.Numerics;
using System.Text.Json;
using Meitou.Simulation.Autonomy;

namespace Meitou.Simulation.Management;

/// <summary>What a managed squad does when its outpost is attacked (docs/simulation.md "Managers").</summary>
public enum DefenceMode : byte
{
    /// <summary>Everyone takes on the enemies in the outpost.</summary>
    Fighters = 0,
    /// <summary>The men fight; the women go inside a building until it is over.</summary>
    Militia = 1,
    /// <summary>Everyone goes inside a building until it is over.</summary>
    Shelter = 2,
}

/// <summary>What a building is for, as the manager ranks sites to build (by its BUILDING <c>building category</c>; a bed is housing).</summary>
public enum BuildGroup : byte { Defence, Food, Power, Housing, Storage, Industry, Research, Other }

/// <summary>
/// A player squad's manager (Meitou-only): while <see cref="Manager"/> is up and in one of the player's outposts, <see cref="ManagerSystem"/> keeps the squad's members there busy. Work hours,
/// job priorities, turrets and rest thresholds are the squad's <see cref="AutonomySettings"/> (the same pages of the window) with autonomy off.
/// </summary>
public sealed record ManagementSettings
{
    /// <summary>The manager (any of the player's characters); None until resolved after a load (<see cref="ManagerSquadIndex"/>, <see cref="ManagerName"/>).</summary>
    public CharacterId Manager { get; init; } = CharacterId.None;
    /// <summary>The manager as a save keeps it: his squad's <see cref="Squad.Index"/> and his name.</summary>
    public int ManagerSquadIndex { get; init; } = -1;
    public string ManagerName { get; init; } = "";
    public DefenceMode Defence { get; init; } = DefenceMode.Militia;
    /// <summary>Whether he puts idle members on building and repairing the outpost's sites.</summary>
    public bool Build { get; init; } = true;
    /// <summary>How much each <see cref="BuildGroup"/> matters (0..10, 0: never built by him); a site goes before a machine whose job priority is lower.</summary>
    public IReadOnlyList<float> BuildPriority { get; init; } = DefaultBuildPriority;

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

    sealed record Stored(int S, string N, byte D, bool B, List<float>? P = null);

    public string Write() => JsonSerializer.Serialize(new Stored(ManagerSquadIndex, ManagerName, (byte)Defence, Build, [.. BuildPriority]));

    public static ManagementSettings? Read(string text)
    {
        Stored? s;
        try { s = JsonSerializer.Deserialize<Stored>(text); }
        catch (JsonException) { return null; }
        return s is null ? null : new ManagementSettings
        {
            ManagerSquadIndex = s.S, ManagerName = s.N ?? "", Defence = Enum.IsDefined((DefenceMode)s.D) ? (DefenceMode)s.D : DefenceMode.Militia, Build = s.B,
            BuildPriority = s.P is { Count: > 0 } p ? [.. p.Concat(DefaultBuildPriority.Skip(p.Count)).Take(DefaultBuildPriority.Count).Select(v => Math.Clamp(v, 0, 10))] : DefaultBuildPriority,
        };
    }
}

/// <summary>Replaces a player squad's management (null: no manager). Applied also while paused.</summary>
public sealed record SetSquadManagement(int SquadId, ManagementSettings? Settings) : SimCommand;

/// <summary>
/// Managers (Meitou-only, engine choices throughout; docs/simulation.md "Managers"). Every <see cref="CheckSeconds"/>, for each player squad with a manager and autonomy off: when the manager
/// is up and inside one of the player's outposts, each member in that outpost who is free (idle, no formation, follow or Loot All) is given something to do, in this order: a wounded one (rest
/// threshold) to bed; in rest hours to bed; else the best machine with work by the squad's job priorities (<see cref="Production.ProductionSystem.AutonomyLabour"/>), else a free turret (Man
/// Turrets), else a site to build or repair (<see cref="ManagementSettings.Build"/>). Members he sent to work go to bed at the end of their shift or when hurt, and are got up when the shift
/// starts and they are healed. While enemies are in the outpost the squad defends it by its <see cref="DefenceMode"/>, and goes back to work after.
/// </summary>
public sealed class ManagerSystem(PopulationData? data = null) : ITickSystem
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
    readonly HashSet<CharacterId> sheltering = [];

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
    string SiteName(World world, string site) => world.Buildings.Find(site) is { } b ? (b.Name.Length > 0 ? b.Name : data?.Db.Find(b.Record)?.Name ?? "a building") : "a building";

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
        var threats = new Dictionary<int, List<int>>();
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
            if (!threats.TryGetValue(town.Number, out var enemies)) threats[town.Number] = enemies = Enemies(world, combat, town);
            var settings = squad.Autonomy ?? new AutonomySettings();
            bool rest = settings.IsRestTime(world.GameHours);
            current = (squad.Id, manager.Name);
            foreach (var id in squad.Members)
                Manage(world, production, m, settings, rest, town, enemies, table.Next[boss].Position, id, beds, building);
        }
    }

    void Mark(World world, Squad squad, string text)
    {
        foreach (var id in squad.Members) status[id] = text;
        foreach (var id in squad.Members) sheltering.Remove(id);
    }

    void Manage(World world, Production.ProductionSystem? production, ManagementSettings m, AutonomySettings s, bool rest, Construction.PlayerTown town, List<int> enemies, Vector3 boss,
        CharacterId id, HashSet<string> beds, Dictionary<string, int> building)
    {
        var table = world.Characters;
        if (!table.TryResolveNext(id, out int slot) || table.Cold(slot) is not { } c) return;
        ref var n = ref table.Next[slot];
        if (c.Medical is { Dead: true }) { status[id] = "dead"; return; }
        if (c.Medical is { Incapacitated: true } || !c.CarriedBy.IsNone) { status[id] = "down"; return; }
        if (c.IsImprisoned) { status[id] = "imprisoned"; return; }
        if (OutpostAt(world, n.Position) is not { } here || here.Number != town.Number) { status[id] = "away from the outpost"; return; }
        var task = (CharacterTask)n.Task;

        // Defence.
        if (enemies.Count > 0)
        {
            bool fights = m.Defence == DefenceMode.Fighters || m.Defence == DefenceMode.Militia && c.Appearance?.Female != true;
            if (fights || c.InCombat)
            {
                status[id] = "defending";
                if (!c.InCombat && task != CharacterTask.Attack && Nearest(table, n.Position, enemies) is int foe)
                {
                    world.Defer(new Combat.AttackOrder([id], table.IdOf(foe)) { Tick = world.Tick + 1, Defensive = true });
                    Log(world, c, "to fight " + (table.Cold(foe)?.Name ?? "an enemy"));
                }
                return;
            }
            status[id] = "sheltering";
            if (sheltering.Add(id) && Shelter(world, town, n.Position) is { } inside)
            {
                world.Defer(new MoveOrder([id], inside) { Tick = world.Tick + 1 });
                Log(world, c, "inside to shelter");
            }
            return;
        }
        if (sheltering.Remove(id) && task == CharacterTask.GoTo) { MovementSystem.Stop(table, slot); task = CharacterTask.Idle; }

        bool wounded = s.RestUntilHealed && c.Race is not { IsRobot: true } && AutonomyRules.Rested(c) * 100 < s.RestThreshold;
        bool healed = !s.RestUntilHealed || AutonomyRules.Rested(c) * 100 >= s.HealedThreshold || c.Race is { IsRobot: true };
        // In bed: up when the shift starts and it is healed (only one the manager put there, or any while it is work time).
        if (c.InSomething == Locks.HeldState.Bed)
        {
            if (!rest && healed) { world.Defer(new Locks.GetOutOrder([id]) { Tick = world.Tick + 1 }); status[id] = "getting up"; }
            else status[id] = rest ? "sleeping (off shift)" : "resting (hurt)";
            return;
        }
        // One the manager sent to work stops for the end of its shift or a wound.
        bool ours = sent.TryGetValue(id, out var job) && (task == CharacterTask.Operate && c.OperateSite == job.Site || task is CharacterTask.Build && c.BuildSite == job.Site);
        if (ours && (rest || wounded))
        {
            if (Bed(world, town, c, beds) is { } bed) { world.Defer(new Locks.UseBedOrder([id], bed) { Tick = world.Tick + 1 }); status[id] = rest ? "off to bed" : "off to bed (hurt)"; Log(world, c, rest ? "to bed (shift over)" : "to bed (hurt)"); }
            else status[id] = "no free bed";
            return;
        }
        if (task != CharacterTask.Idle || c.InCombat || c.Formation is not null || c.LootAll is not null || !c.FollowOrder.IsNone)
        {
            status[id] = ours ? task == CharacterTask.Build ? "building" : "working" : c.Formation is not null ? "in formation" : TaskWord(task);
            return;
        }
        // Back from a building it was just sent to: that one does not work for it for a while.
        if (sent.TryGetValue(id, out var last) && (world.Tick - last.Tick) * world.TickSeconds < 10)
            stuck[(id, last.Site)] = world.Tick + (long)(StuckSeconds / world.TickSeconds);
        if (wounded || rest)
        {
            if (Bed(world, town, c, beds) is { } bed) { world.Defer(new Locks.UseBedOrder([id], bed) { Tick = world.Tick + 1 }); status[id] = wounded ? "off to bed (hurt)" : "off to bed"; Log(world, c, wounded ? "to bed (hurt)" : "to bed (off shift)"); }
            else status[id] = "no free bed";
            return;
        }
        // A site to build whose group matters more than the best machine's job goes first (Building page against the Jobs page).
        var site = m.Build ? Site(world, m, town, n.Position, id, building) : null;
        var machine = production?.AutonomyLabour(world, slot, s, town.Number, boss) is { } found && !Stuck(world, id, found) ? found : null;
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
        status[id] = "idle: nothing to do";
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
    static List<int> Enemies(World world, Combat.CombatSystem? combat, Construction.PlayerTown town)
    {
        var table = world.Characters;
        var list = new List<int>();
        var relations = combat?.Relations;
        int player = world.Player.Faction;
        float r2 = town.Radius * town.Radius;
        for (int s = 0; s < table.HighWater; s++)
        {
            ref readonly var n = ref table.Next[s];
            if (!n.Alive || table.Cold(s) is not { IsPlayer: false } c || c.Medical is { Incapacitated: true } || c.IsImprisoned || c.Faction == player) continue;
            if (Vector2.DistanceSquared(new(n.Position.X, n.Position.Z), new(town.Position.X, town.Position.Z)) > r2) continue;
            bool hostile = relations is not null && c.Faction >= 0 && player >= 0 && relations.Contains(c.Faction) && relations.Contains(player) && relations.IsHostile(c.Faction, player);
            bool attacking = c.InCombat && combat is not null && combat.StateOf(table.IdOf(s)) is { TargetSlot: >= 0 and var t } && t < table.HighWater && table.Cold(t) is { IsPlayer: true };
            if (hostile || attacking) list.Add(s);
        }
        return list;
    }

    static int? Nearest(CharacterTable table, Vector3 at, List<int> slots)
    {
        int? best = null;
        float bestD = float.MaxValue;
        foreach (int s in slots)
        {
            float d = Vector3.DistanceSquared(at, table.Next[s].Position);
            if (d < bestD) (best, bestD) = (s, d);
        }
        return best;
    }

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
            if (p <= 0 || p < bestP || Stuck(world, id, site.Id)) continue;
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
