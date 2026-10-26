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

    sealed record Stored(int S, string N, byte D, bool B);

    public string Write() => JsonSerializer.Serialize(new Stored(ManagerSquadIndex, ManagerName, (byte)Defence, Build));

    public static ManagementSettings? Read(string text)
    {
        Stored? s;
        try { s = JsonSerializer.Deserialize<Stored>(text); }
        catch (JsonException) { return null; }
        return s is null ? null : new ManagementSettings
        {
            ManagerSquadIndex = s.S, ManagerName = s.N ?? "", Defence = Enum.IsDefined((DefenceMode)s.D) ? (DefenceMode)s.D : DefenceMode.Militia, Build = s.B,
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
public sealed class ManagerSystem : ITickSystem
{
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
            if (squad.Management is not { } m || squad.Faction != world.Player.Faction) continue;
            if (m.Manager.IsNone || !table.IsAlive(m.Manager)) squad.Management = m = Resolve(world, m);
            if (squad.Autonomy is { Enabled: true })
            {
                Mark(world, squad, "autonomy runs this squad");
                continue;
            }
            if (!table.TryResolveNext(m.Manager, out int boss) || table.Cold(boss) is not { } manager || manager.Medical is { Dead: true })
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
                    world.Defer(new Combat.AttackOrder([id], table.IdOf(foe)) { Tick = world.Tick + 1, Defensive = true });
                return;
            }
            status[id] = "sheltering";
            if (sheltering.Add(id) && Shelter(world, town, n.Position) is { } inside)
                world.Defer(new MoveOrder([id], inside) { Tick = world.Tick + 1 });
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
            if (Bed(world, town, c, beds) is { } bed) { world.Defer(new Locks.UseBedOrder([id], bed) { Tick = world.Tick + 1 }); status[id] = rest ? "off to bed" : "off to bed (hurt)"; }
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
            if (Bed(world, town, c, beds) is { } bed) { world.Defer(new Locks.UseBedOrder([id], bed) { Tick = world.Tick + 1 }); status[id] = wounded ? "off to bed (hurt)" : "off to bed"; }
            else status[id] = "no free bed";
            return;
        }
        if (production is not null && production.AutonomyLabour(world, slot, s, town.Number, boss) is { } machine && !Stuck(world, id, machine) && production.AutonomyBegin(world, slot, machine))
        {
            sent[id] = (machine, world.Tick);
            status[id] = "working";
            return;
        }
        if (production is not null && s.ManTurrets && production.AutonomyTurret(world, slot, town.Number) is { } turret && !Stuck(world, id, turret) && production.AutonomyBegin(world, slot, turret))
        {
            sent[id] = (turret, world.Tick);
            status[id] = "on a turret";
            return;
        }
        if (m.Build && Site(world, town, n.Position, id, building) is { } site)
        {
            building[site.Id] = building.GetValueOrDefault(site.Id) + 1;
            world.Defer(new Construction.BuildOrder([id], site.Id, Repair: site.Complete && site.Destroyed) { Tick = world.Tick + 1 });
            sent[id] = (site.Id, world.Tick);
            status[id] = site.Complete ? "repairing" : "building";
            return;
        }
        status[id] = "idle: nothing to do";
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

    /// <summary>The nearest site of the outpost to build (placed, not paused or being taken apart) or repair (complete and damaged), at most two builders each this pass, not stuck for it.</summary>
    Construction.BuildSite? Site(World world, Construction.PlayerTown town, Vector3 at, CharacterId id, Dictionary<string, int> building)
    {
        Construction.BuildSite? best = null;
        float bestD = float.MaxValue;
        foreach (var site in world.Buildings.All)
        {
            if (site.Town != town.Number || site.Owner != world.Player.Faction || site.Paused || site.Dismantling || site.Ruined) continue;
            if (site.Complete && !site.Destroyed) continue;
            if (building.GetValueOrDefault(site.Id) >= 2 || Stuck(world, id, site.Id)) continue;
            float d = Vector3.DistanceSquared(at, site.Position);
            if (d < bestD) (best, bestD) = (site, d);
        }
        return best;
    }
}
