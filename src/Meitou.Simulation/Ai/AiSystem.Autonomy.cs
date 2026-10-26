using System.Numerics;
using Meitou.Data.Gameplay.Ai;
using Meitou.Data.World;
using Meitou.Simulation.Autonomy;

namespace Meitou.Simulation.Ai;

// Squad autonomy (docs/simulation.md "Squad autonomy"; Meitou's version of the RE_Kenshi plugin Squad Autonomy): a player squad with autonomy on is run by the AI from the packages its
// settings list, as the plugin runs it by clearing the faction's player flag. A member takes its goals only while it has no order of the player's (PlayerHeld); the plugin's scoring hooks
// are AutonomyDesire and the stance rule; in a player town its posts, beds, patrols, labour and turrets are the outpost's (the base AI reads only placed towns).
public sealed partial class AiSystem
{
    static bool Autonomous(Squad squad) => squad.Autonomy is { AiRuns: true };

    /// <summary>The packages a squad's autonomy lists, at their slots (0..5, the plugin's priorities); ids the data lacks are left out (the plugin would crash on them).</summary>
    IReadOnlyList<SlottedPackage> AutonomyPackages(AutonomySettings settings)
    {
        var list = new List<SlottedPackage>();
        foreach (var p in settings.Packages)
            if (data.Ai.Package(p.Id) is { } package) list.Add(new SlottedPackage(Math.Clamp(p.Slot, 0, 5), package));
        return list;
    }

    /// <summary>
    /// A squad's new autonomy settings (the panel): the AI lets go of the squad (the plugin's <c>ResetAI</c>, which ends the members' actions but sleeping and sitting) and, while enabled,
    /// takes it up again on the next serial step with the new packages (<c>SetAI</c>). Only the player's squads.
    /// </summary>
    void SetAutonomy(World world, SetSquadAutonomy set)
    {
        if (world.Squads.Find(set.SquadId) is not { } squad || squad.Faction != world.Player.Faction || squad.Faction < 0) return;
        bool wasOn = Autonomous(squad);
        squad.Autonomy = set.Settings;
        if (wasOn || squad.Ai is not null) ReleaseSquad(world, squad, wake: !Autonomous(squad));
        looked.Remove(squad.Id);
    }

    /// <summary>The AI lets go of an autonomous squad: what it had its members doing stops (a sleeper stays in bed unless <paramref name="wake"/>), their minds and the blackboard go.</summary>
    void ReleaseSquad(World world, Squad squad, bool wake)
    {
        var table = world.Characters;
        foreach (var id in squad.Members)
        {
            if (!table.TryResolveNext(id, out int s) || table.Cold(s) is not { Ai: { } mind } cold) continue;
            ref var n = ref table.Next[s];
            if (cold.IsPlayer && PlayerHeld(n, cold, mind))
            {
                var task = (CharacterTask)n.Task;
                if (task == CharacterTask.Operate) Production.ProductionSystem.AutonomyEnd(table, s);
                else if (cold.InSomething == Locks.HeldState.Bed) { if (wake) { world.Fixtures.LeaveBed(world, id); Become(table, s, CharacterTask.Idle); } }
                else if (task is CharacterTask.GoTo or CharacterTask.Wander or CharacterTask.Follow or CharacterTask.Rest) Become(table, s, CharacterTask.Idle);
                if (cold.ActionClip is not null && cold.ActionClip == mind.WorkClip) cold.ActionClip = null;
            }
            cold.Ai = null;
        }
        squad.Ai = null;
    }

    /// <summary>
    /// Whether an autonomous player character is the AI's now (the plugin: no player orders): idle, or still doing what the AI's last act-out left it doing (the same walk, the same
    /// follow, the machine or turret it was sent to, its bed). A player order replaces that, and the character is the player's until it is idle again.
    /// </summary>
    public static bool PlayerHeld(in CharacterHot n, CharacterCold cold, AiMind mind)
    {
        if (mind.Squad.Autonomy is not { AiRuns: true } || cold.OrderQueue.Count > 0) return false;
        var task = (CharacterTask)n.Task;
        if (task == CharacterTask.Idle) return true;
        if (cold.InSomething == Locks.HeldState.Bed && mind.Behaviour == AiBehaviour.Sleep && task is CharacterTask.Sleep or CharacterTask.Rest) return true;
        if (n.Task != mind.IssuedTask) return false;
        return task switch
        {
            CharacterTask.GoTo => Vector2.DistanceSquared(n.Goal, mind.IssuedGoal) < 4,
            CharacterTask.Follow => cold.FollowOrder.IsNone,
            CharacterTask.Operate => mind.IssuedSite is { } site && cold.OperateSite == site && mind.Behaviour is AiBehaviour.Work or AiBehaviour.Turret,
            CharacterTask.Wander or CharacterTask.Rest => true,
            _ => false,
        };
    }

    /// <summary>Whether a player character fights on its own as an NPC would (autonomous, its packages give it a combat stance that is not passive): <see cref="AggroSystem"/>'s look then runs for it.</summary>
    public static bool AutonomyFights(CharacterCold cold) =>
        cold.IsPlayer && cold.Ai is { HasCombatGoals: true, Stance: not AiStance.Passive } mind && mind.Squad.Autonomy is { AiRuns: true };

    /// <summary>
    /// The plugin's combat options and rest time on the stance its goals give (its <c>Tasker::score</c> rows for 144 / 16 and 15 / 288 / 143): no attacking with Attack Enemies off or in
    /// rest time, no defending with Protect Allies off.
    /// </summary>
    static AiStance AutonomyStance(AiStance stance, AutonomySettings s, bool rest)
    {
        if ((!s.AttackEnemies || rest) && stance is AiStance.Aggressive or AiStance.AggressiveNeutrals or AiStance.Territorial) stance = s.ProtectAllies ? AiStance.Defensive : AiStance.Passive;
        if (!s.ProtectAllies && stance is AiStance.Defensive or AiStance.ProtectSquad) stance = AiStance.Passive;
        return stance;
    }

    /// <summary>
    /// How much an autonomous character wants a goal (the plugin's <c>Tasker::score</c> and sleep hooks over the base desire): beds by its rest rules; staying home and sitting nearly never in
    /// work hours; guarding, the gate and labour never in rest time; turrets only with Man Turrets and not in rest time; following not while the leader sleeps or relaxes. In a player town the
    /// labour, turret and patrol goals look at the outpost.
    /// </summary>
    float AutonomyDesire(World world, AutonomySettings s, AiBehaviour behaviour, TaskType type, Squad squad, Blackboard board, AiMind mind, CharacterId self, TaskTargetType targeting, float desire)
    {
        bool rest = s.IsRestTime(world.GameHours);
        var cold = world.Characters.Cold(self.Slot)!;
        switch (type)
        {
            case TaskType.GO_HOME_AND_GO_TO_BED or TaskType.GO_HOME_AND_GO_TO_BED_SECURE or TaskType.SLEEP_ON_FLOOR:
                return SleepWant(world, s, cold, mind, rest);
            case TaskType.STAY_IN_HOME or TaskType.SIT_AROUND:
                if (!rest) desire *= 0.0001f;
                break;
            case TaskType.MAN_A_TURRET or TaskType.MAN_A_TURRET_ON_BUILDING or TaskType.MAN_A_TURRET_PLAYER_JOB or TaskType.USE_TURRET:
                if (!s.ManTurrets || rest) return 0;
                break;
            case TaskType.BODYGUARD or TaskType.FOLLOW_SQUADLEADER:
                if (world.Characters.TryResolveNext(squad.Leader, out int l) && world.Characters.Cold(l) is { } lead
                    && (lead.InSomething == Locks.HeldState.Bed || lead.Ai?.Goal?.Task.Type == TaskType.RELAX_IN_TOWN_PACKAGE)) return 0;
                break;
            case TaskType.MAN_THE_GATE or TaskType.STAND_AT_GUARD_NODE_HOMEBUILDING or TaskType.STAND_AT_GUARD_NODE_HOMEBUILDING_INDOORS_ONLY
                or TaskType.STAND_AT_GUARD_NODE_HOMETOWN_OUTSIDE or TaskType.AUTO_LABOURING_MINES or TaskType.AUTO_LABOURING_MINES_PRETEND:
                if (rest) return 0;
                break;
            case TaskType.PATROL_TOWN:
                if (rest && world.Sight.BuildingOf(self.Slot) is not null) return 0;
                break;
        }
        if (board.AutonomyTown > 0)
            switch (behaviour)
            {
                case AiBehaviour.Work: return board.AutonomyLabour ? desire : 0;
                case AiBehaviour.Turret: return board.AutonomyTurret ? desire : 0;
                case AiBehaviour.TownNode: return desire;
                case AiBehaviour.Shop: return 0;
            }
        return desire * Factor(world, behaviour, squad, board, mind, self, targeting);
    }

    /// <summary>
    /// The plugin's bed score (<c>_NV_scoreGoToBed</c> / <c>_NV_scoreGetOutOfBed</c>): wounded with Rest Until Healed, 5 in bed or in rest time and 20 at or below the rest threshold; with
    /// sleep allowed in rest time, by the minutes since it last slept (under 120: 0, 480: 0.01, 720: 0.1, 1080: 0.5, else 1), and staying in bed then; otherwise none (it gets up).
    /// </summary>
    static float SleepWant(World world, AutonomySettings s, CharacterCold cold, AiMind mind, bool rest)
    {
        bool sleeping = cold.InSomething == Locks.HeldState.Bed;
        if (s.RestUntilHealed && AutonomyRules.Wounded(cold, s))
        {
            if (sleeping || rest) return 5;
            if (AutonomyRules.Rested(cold) <= s.RestThreshold / 100f) return 20;
        }
        if (s.DoSleep && rest)
        {
            if (sleeping) return 1;
            double minutes = (world.GameHours - mind.LastSlept) * 60;
            return minutes < 120 ? 0 : minutes < 480 ? 0.01f : minutes < 720 ? 0.1f : minutes < 1080 ? 0.5f : 1;
        }
        return 0;
    }

    /// <summary>Goals whose act-out in a player town is the autonomy's own, so the planner (which reads placed towns) is not asked about them.</summary>
    internal static bool AutonomyActs(AiMind mind, Blackboard board, TaskType type) =>
        mind.Squad.Autonomy is { AiRuns: true } && TaskBehaviours.Of(type) switch
        {
            AiBehaviour.Work or AiBehaviour.Turret or AiBehaviour.TownNode => board.AutonomyTown > 0,
            AiBehaviour.Post => board.AutonomySite is not null,
            AiBehaviour.Sleep => board.Home is null,
            _ => false,
        };

    // Set by an act-out that asked for work or a turret (AiMind.Request); the serial step starts them.
    int requesting;

    /// <summary>
    /// The act-out of an autonomous character where it differs from an NPC's (true: done here): work and turrets are asked of the serial step; a post is at its place (the gate's door, or
    /// round the building); a bed is its place's, else its outpost's, else the town's it is in; patrolling walks between the outpost's buildings.
    /// </summary>
    bool ActAutonomy(World world, int slot, AiMind mind, Blackboard board, AiBehaviour behaviour, TaskType type)
    {
        var table = world.Characters;
        var cold = table.Cold(slot)!;
        ref var n = ref table.Next[slot];
        var s = mind.Squad.Autonomy!;
        switch (behaviour)
        {
            case AiBehaviour.Work or AiBehaviour.Turret when board.AutonomyTown > 0:
                mind.Request = behaviour;
                Interlocked.Exchange(ref requesting, 1);
                return true;
            case AiBehaviour.Post when board.AutonomySite is { } site:
            {
                Vector3 spot;
                float? yaw = null;
                if (board.AutonomyGate is { } gate && type is TaskType.MAN_THE_GATE or TaskType.STAND_AT_GUARD_NODE_HOMEBUILDING or TaskType.STAND_AT_GUARD_NODE_HOMEBUILDING_INDOORS_ONLY)
                {
                    // The plugin's gate post (CharMovement hook): out of the gate, or 2 inside with Stay Inside Gate; the others flank it beyond the leaf.
                    var outward = new Vector3(gate.Out.X, 0, gate.Out.Y);
                    var side = new Vector3(gate.Out.Y, 0, -gate.Out.X);
                    float along = s.StayInsideGate ? -(type == TaskType.MAN_THE_GATE ? 2 + GateGuardOut * 0.4f : 4 + GateGuardOut * 0.6f) : GateGuardOut;
                    int k = mind.MemberIndex;
                    float sideways = type == TaskType.MAN_THE_GATE && k == 0 ? 0 : (gate.HalfWidth + 6 + 6 * (k / 2)) * (k % 2 == 0 ? 1 : -1);
                    spot = gate.Door + outward * along + side * sideways;
                    yaw = MathF.Atan2(gate.Out.X, gate.Out.Y) + (s.StayInsideGate ? MathF.PI : 0);
                }
                else
                {
                    // Round the building (engine choice: Meitou reads no nodes in a player building).
                    float angle = mind.MemberIndex * 2.39996f, r = 6 + 4 * (mind.MemberIndex % 3);
                    spot = site.Position + new Vector3(MathF.Sin(angle), 0, MathF.Cos(angle)) * r;
                }
                if (GoToSpot(world.Walkability, table, slot, mind, Flat(spot), GiveUp.Post, SpeedMode.Walk))
                {
                    Become(table, slot, CharacterTask.Idle);
                    if (yaw is { } y) n.Yaw = y;
                }
                return true;
            }
            case AiBehaviour.Sleep when board.Home is null:
            {
                if (cold.InSomething == Locks.HeldState.Bed) { mind.LastSlept = world.GameHours; return true; }
                if (AutonomyBed(world, board, mind, slot, s) is not { } bed) return false;   // none: on the floor at its post, as an NPC without a bed
                if (GoToSpot(world.Walkability, table, slot, mind, Flat(bed.Position), GiveUp.Home, SpeedMode.Walk, height: bed.Position.Y))
                {
                    Become(table, slot, CharacterTask.Rest);
                    mind.BedAt = bed.Id;
                    Interlocked.Exchange(ref bedding, 1);
                }
                return true;
            }
            case AiBehaviour.TownNode when board.AutonomyTown > 0:
            {
                mind.Target ??= OutpostSpot(world, board.AutonomyTown, table.IdOf(slot));
                if (mind.Target is not { } goTo) Become(table, slot, CharacterTask.Idle);
                else if (GoToSpot(world.Walkability, table, slot, mind, Flat(goTo), GiveUp.Target, SpeedMode.Walk)) Become(table, slot, CharacterTask.Idle);
                return true;
            }
        }
        return false;
    }

    /// <summary>A random complete building of player town <paramref name="town"/> to walk to (patrolling and wandering the outpost; the occupiers' rule), null without one.</summary>
    static Vector3? OutpostSpot(World world, int town, CharacterId self)
    {
        int count = 0;
        foreach (var site in world.Buildings.All) if (site.Complete && site.Town == town && site.Parent is null) count++;
        if (count == 0) return null;
        int pick = Rng.Int(Rng.Hash(world.Seed, Rng.Key(self), RngPurpose.Think, SeedSalts.AiTarget ^ (ulong)world.Tick << 8 ^ 0x41), count);
        foreach (var site in world.Buildings.All)
            if (site.Complete && site.Town == town && site.Parent is null && pick-- == 0) return site.Position;
        return null;
    }

    /// <summary>
    /// The bed an autonomous character sleeps in (the plugin's <c>FindOptimalBed</c>, simplified): a free bed of its place, else of its outpost, else of the placed town it is in, owned by its
    /// faction or an ally (any faction not hostile with Use Paid Beds; Meitou does not charge for it). Members take the beds round robin, so they keep theirs (the occupiers' rule).
    /// </summary>
    Locks.Fixture? AutonomyBed(World world, Blackboard board, AiMind mind, int slot, AutonomySettings s)
    {
        var table = world.Characters;
        if (table.Cold(slot) is not { } self) return null;
        bool Usable(Locks.Fixture f) =>
            f.Kind == Locks.FixtureKind.Bed && f.Bed.Function == TownBuildings.BedFunction && world.Fixtures.Reachable(world, f, self.Faction)
            && (Locks.FixtureRegistry.IsFree(table, f) || table.TryResolveNext(f.Occupant, out int o) && table.Cold(o)?.SquadId == mind.Squad.Id);
        Locks.Fixture? Pick(Func<Locks.Fixture, bool> where)
        {
            int count = 0;
            foreach (var f in world.Fixtures.All) if (where(f) && Usable(f)) count++;
            if (count == 0) return null;
            int want = mind.MemberIndex % count;
            foreach (var f in world.Fixtures.All)
                if (where(f) && Usable(f) && want-- == 0) return f;
            return null;
        }
        if (board.AutonomySite is { } place && Pick(f => f.Building == place.Id || world.Buildings.Find(f.Building)?.Parent == place.Id) is { } own) return own;
        if (board.AutonomyTown > 0 && Pick(f => world.Buildings.Find(f.Building) is { Complete: true } b && b.Town == board.AutonomyTown) is { } outpost) return outpost;
        if (board.InTown.Site is >= 0 and var town)
        {
            var r = Relations;
            bool Owner(int owner) => owner == self.Faction || owner >= 0 && r.Contains(owner) && r.Contains(self.Faction)
                && (r.IsAlly(owner, self.Faction) || s.UsePaidBeds && !r.IsHostile(owner, self.Faction));
            return Pick(f => f.Site == town && Owner(f.Owner));
        }
        return null;
    }

    /// <summary>The serial step's half of the act-outs that asked for work or a turret: the pick (<see cref="Production.ProductionSystem.AutonomyLabour"/>) and the start; nothing found, it stands.</summary>
    void StartRequests(World world)
    {
        var table = world.Characters;
        var production = world.SystemOf<Production.ProductionSystem>();
        for (int s = 0; s < table.HighWater; s++)
        {
            if (table.Cold(s) is not { Ai: { Request: not AiBehaviour.None } mind } cold) continue;
            var request = mind.Request;
            mind.Request = AiBehaviour.None;
            ref var n = ref table.Next[s];
            if (!n.Alive || production is null || mind.Squad.Autonomy is not { AiRuns: true } settings || mind.Squad.Ai is not { } board || mind.Behaviour != request || !PlayerHeld(n, cold, mind)) continue;
            string? site = request == AiBehaviour.Work
                ? production.AutonomyLabour(world, s, settings, board.AutonomyTown, board.AutonomySite?.Position)
                : production.AutonomyTurret(world, s, board.AutonomyTown);
            if (site is null)
            {
                if (n.Task == (byte)CharacterTask.Operate) Production.ProductionSystem.AutonomyEnd(table, s);
                Become(table, s, CharacterTask.Idle);
                continue;
            }
            if (!production.AutonomyBegin(world, s, site)) continue;
            (mind.IssuedTask, mind.IssuedSite, mind.IssuedGoal) = (n.Task, site, n.Goal);
        }
    }

    /// <summary>What each member of an autonomous squad does (the panel's status line): an order of the player's, or its goal as a word; down members as such.</summary>
    public static IReadOnlyList<string> AutonomyStatus(World world, Squad squad)
    {
        var table = world.Characters;
        var list = new List<string>(squad.Members.Count);
        foreach (var id in squad.Members)
        {
            if (!table.TryResolveNext(id, out int s) || table.Cold(s) is not { } c) continue;
            ref var n = ref table.Next[s];
            if (c.Medical is { Incapacitated: true }) { list.Add("down"); continue; }
            if (c.InCombat) { list.Add("fighting"); continue; }
            if (c.Ai is not { } mind) { list.Add("starting"); continue; }
            if (!PlayerHeld(n, c, mind)) { list.Add("following your order"); continue; }
            if (c.InSomething == Locks.HeldState.Bed) { list.Add("sleeping"); continue; }
            list.Add(mind.Behaviour switch
            {
                AiBehaviour.Work => (CharacterTask)n.Task == CharacterTask.Operate ? "working" : "looking for work",
                AiBehaviour.Turret => "on a turret",
                AiBehaviour.Post => mind.Goal?.Task.Type == TaskType.MAN_THE_GATE ? "at the gate" : mind.Goal?.Task.Type == TaskType.STAY_IN_HOME ? "at home" : "on guard",
                AiBehaviour.Sleep => "going to bed",
                AiBehaviour.Sit or AiBehaviour.Relax => "relaxing",
                AiBehaviour.Patrol or AiBehaviour.TownNode => "patrolling",
                AiBehaviour.Follow => "following the leader",
                AiBehaviour.Travel or AiBehaviour.ToTown => "travelling",
                AiBehaviour.Shop => "shopping",
                AiBehaviour.GoHome => "going home",
                _ => "idle",
            });
        }
        return list;
    }

    readonly Dictionary<string, TownBuilding> townBuildings = new(StringComparer.Ordinal);

    TownBuilding? TownBuildingOf(string key)
    {
        if (townBuildings.Count == 0)
            foreach (var site in data.Sites)
                foreach (var b in site.Buildings) townBuildings.TryAdd(b.Key, b);
        return townBuildings.GetValueOrDefault(key);
    }

    /// <summary>
    /// An autonomous squad's places on the cadence (serial): its place now (home in rest time, else work), whose player town it acts in (else the one it is in), the gate post of a gate,
    /// the centre and radius it guards (the territorial stance's home), and whether its outpost has labour and a free turret for it. A town building as its place is its home as an NPC's.
    /// </summary>
    void AutonomyCadence(World world, Squad squad, Blackboard board)
    {
        var s = squad.Autonomy!;
        string? place = s.PlaceNow(world.GameHours);
        var site = place is null ? null : world.Buildings.Find(place) is { Complete: true, Destroyed: false } b ? b : null;
        var building = site is null && place is not null ? TownBuildingOf(place) : null;
        board.AutonomySite = site;
        if (!ReferenceEquals(board.Home, building)) { board.Home = board.OwnHome = building; board.Touch(); }
        board.AutonomyTown = site?.Town ?? (board.InTown.PlayerTown > 0 ? board.InTown.PlayerTown : 0);
        var town = board.AutonomyTown > 0 ? world.Buildings.FindTown(board.AutonomyTown) : null;
        (squad.HomeCentre, squad.HomeRadius) = site is not null ? (Flat(site.Position), MathF.Max((town?.Radius ?? 400) * 0.5f, 60))
            : building is not null ? (Flat(building.Position), 60)
            : town is not null ? (Flat(town.Position), MathF.Max(town.Radius * 0.5f, 60))
            : board.InTown.Site >= 0 ? (Flat(data.Sites[board.InTown.Site].Position), MathF.Max(data.Sites[board.InTown.Site].Town.Radius * 0.5f, 60))
            : (LeaderPosition(world, squad) is { } at ? Flat(at) : squad.HomeCentre, 200);
        board.AutonomyGate = site is null ? null : GateOf(world, site, town is not null ? Flat(town.Position) : Flat(site.Position));
        var production = world.SystemOf<Production.ProductionSystem>();
        int probe = -1;
        foreach (var m in squad.Members) if (world.Characters.TryResolveNext(m, out int ms)) { probe = ms; break; }
        board.AutonomyLabour = production is not null && probe >= 0 && board.AutonomyTown > 0 && !s.IsRestTime(world.GameHours)
            && production.AutonomyHasLabour(world, probe, s, board.AutonomyTown);
        board.AutonomyTurret = production is not null && probe >= 0 && board.AutonomyTown > 0 && s.ManTurrets && production.AutonomyTurret(world, probe, board.AutonomyTown) is not null;
        if (s.CloseGate) KeepGateShut(world, squad, board);
    }

    /// <summary>The gate post of a player building with a door that is a wall gate (its first door's leaf, facing out of the town), else null.</summary>
    (Vector3 Door, Vector2 Out, float HalfWidth, string Fixture)? GateOf(World world, Construction.BuildSite site, Vector2 town)
    {
        if (data.Db.Find(site.Record)?.GetBool("is wall gate") != true) return null;
        Locks.Fixture? door = null;
        foreach (var f in world.Fixtures.OfBuilding(site.Id))
            if (f.Kind == Locks.FixtureKind.Door && (door is null || f.DoorIndex < door.DoorIndex)) door = f;
        if (door is null || world.Fixtures.DoorSpotOf(door) is not { } leaf) return null;
        var way = Vector2.Normalize(new Vector2(leaf.Normal.X, leaf.Normal.Z));
        if (Vector2.Dot(way, Flat(leaf.Centre) - town) < 0) way = -way;
        return (leaf.Centre with { Y = leaf.Foot }, way, leaf.HalfWidth, door.Id);
    }

    /// <summary>
    /// Close Gate (the plugin's gate guard rule): while a member mans the gate, the gate shuts when nobody of the player's faction is about to pass it (within 40 of the leaf) and opens
    /// for one that is.
    /// </summary>
    static void KeepGateShut(World world, Squad squad, Blackboard board)
    {
        if (board.AutonomyGate is not { } gate || world.Fixtures.Find(gate.Fixture) is not { } door) return;
        var table = world.Characters;
        bool manned = false;
        foreach (var m in squad.Members)
            if (table.TryResolveNext(m, out int s) && table.Cold(s)?.Ai?.Goal?.Task.Type == TaskType.MAN_THE_GATE) manned = true;
        if (!manned) return;
        bool passing = false;
        for (int i = 0; i < table.HighWater && !passing; i++)
        {
            if (!table.Next[i].Alive || table.Cold(i) is not { } c || c.Faction != world.Player.Faction || c.Ai?.Goal?.Task.Type == TaskType.MAN_THE_GATE) continue;
            var p = table.Next[i].Position;
            if (Vector2.DistanceSquared(Flat(p), Flat(gate.Door)) < 40 * 40 && (CharacterTask)table.Next[i].Task is CharacterTask.GoTo or CharacterTask.Follow) passing = true;
        }
        if (passing) Locks.FixtureRegistry.OpenDoor(door);
        else Locks.FixtureRegistry.CloseDoor(door);
    }
}
