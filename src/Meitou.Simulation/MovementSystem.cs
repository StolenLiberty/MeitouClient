using System.Numerics;
using System.Runtime.CompilerServices;
using Meitou.Data.World;
using Meitou.Simulation.Bodies;

namespace Meitou.Simulation;

/// <summary>
/// Movement on the walkability stand-in (docs/game/pathfinding.md "Movement") and the first tasks: wander within the squad's home
/// radius, go to (move orders), follow the squad leader.
/// <list type="bullet">
/// <item>Speed: the stat S in decimetres (world units) per second, capped by the speed mode (walk speed, 55, none); the character
/// follows a path at that speed at once (the nav agent, Observed), speeds up by 15 units/s per second only in combat (<see cref="Acceleration"/>, the combat
/// steering), and stops at once on arrival.</item>
/// <item>Separation: the repulsion along self-other is nonzero only closer than R and proportional to 100 x (1 - d/R), over at most
/// 26 neighbours (Observed); R is twice the footprint radius 4 of a human (<see cref="SeparationRadius"/>), and the scale from that
/// proportionality to a speed is an engine choice (<see cref="SeparationStrength"/>).</item>
/// <item>Body contact (engine choice; whether the original's characters collide is Unknown): two standing bodies (<see cref="BodyShare"/> of their footprints) do not stay
/// overlapped. Each takes half of the overlap between its stepped position and the other's last position, averaged over the bodies it overlaps, so a walker shoves a stander
/// aside; an imprisoned or bedded one does not give. Its own velocity into a body it already touches is dropped, so a crowd pressing on does not squash its front. A sitter is never moved, by contact or separation (the original pushes no sitter, Observed), and is not solid to others.</item>
/// <item>Paths: a character that wants one is flagged in Think; the Schedule step of the next tick asks the <see cref="PathService"/>;
/// an answer is applied in a serial step, so a path is never read while it is replaced.</item>
/// </list>
/// Water states cap or replace the top speed (<see cref="WaterStateAt"/>, <see cref="Speed.InWater"/>). Not modelled yet (Unknown or later): slopes (the original has no slope penalty), the combat speed
/// multiplier. Far trips take the roads in the path layer (<see cref="CharacterCold.RoadPreference"/>).
/// <item>Followers (the original's <c>Task_Follow</c>, docs/game/pathfinding.md "Squads travelling together"): a squad mate aims at its <see cref="Formation.Layout"/> slot turned
/// onto a heading that eases toward the leader's, one second ahead of the leader; anyone else keeps a drawn 20..40 from its target. Its speed is the original's desired speed
/// (<see cref="FollowSpeedFor"/>), capped by its top speed only (engine choice: the original's cap by speed mode is Unknown, and only this lets it settle in its slot). Within
/// <see cref="DirectDistance"/> it steers straight at the aim, else it takes paths, and for <see cref="FollowPathSeconds"/> after a blocked step (engine choice). Not done: the
/// original's aim outside a building the follower cannot enter, and its 50-100 hysteresis (practically nil).</item>
/// <item>Turning (engine choice; the original's rate is Unknown): the heading a path is followed along turns at most <see cref="TurnRate"/>, and the
/// speed is scaled by the cosine of what is left of the turn (none beyond 90 degrees), so a character turns about on the spot before it runs off.</item>
/// Queued orders (<see cref="CharacterCold.OrderQueue"/>) and the formation (<see cref="Formation"/>) exist.
/// </summary>
public sealed partial class MovementSystem(PathService paths) : ITickSystem, IDisposable
{
    /// <summary>The fights the steering reads (null: no fight steering, as in worlds without combat; the world's combat system unless set).</summary>
    public Combat.CombatSystem? Fights { get; set; }
    /// <summary>Speed gained per second in combat (docs/game/pathfinding.md, the combat / follow steering: "ramps up by 15 per second").</summary>
    public const float Acceleration = 15;
    /// <summary>R of the separation steering: neighbour radius plus margin; a human's footprint radius is 4.</summary>
    public const float SeparationRadius = 8;
    /// <summary>Speed in units/s of a repulsion of magnitude 100 (engine choice).</summary>
    public const float SeparationStrength = 0.15f;
    public const int MaxSeparationContributions = 26;
    /// <summary>The share of the footprint radius a body is solid over (engine choice: 2.4 units for a human, about half the shoulders' width).</summary>
    public const float BodyShare = 0.6f;
    /// <summary>A character within this distance of a waypoint has reached it.</summary>
    public const float ArriveRadius = 10;
    /// <summary>
    /// How near (units) a walker comes to a corner it may not cut (<see cref="CanCut"/>) before it turns for the next waypoint (engine choice: about a step).
    /// </summary>
    public const float CornerReach = 2;

    /// <summary>
    /// Whether a walker at <paramref name="from"/> can step straight toward <paramref name="to"/> for <see cref="ArriveRadius"/> units, its height carried on as each step's is: a corner
    /// is cut early only then (engine choice; Observed: zone 22.10, a walker beside a door stair's foot cut its corner up the stair's side and stood there).
    /// </summary>
    static bool CanCut(IWalkability walk, Vector3 from, Vector3 to)
    {
        var leg = new Vector2(to.X - from.X, to.Z - from.Z);
        float length = leg.Length();
        if (length < 1e-3f) return true;
        float y = from.Y;
        for (float d = 1; d <= MathF.Min(length, ArriveRadius); d += 1)
            if (!walk.TryStand(from.X + leg.X * (d / length), from.Z + leg.Y * (d / length), y, out y)) return false;
        return true;
    }
    /// <summary>The <see cref="CharacterCold.ArriveWithin"/> of a walk to a spot (engine choice: a barman stands at his node behind the counter, not a stride off it).</summary>
    public const float SpotArrive = 2;
    /// <summary>How fast a formation follower's heading eases toward its leader's, per second, while the leader moves (the original's 0.5).</summary>
    public const float HeadingEase = 0.5f;
    /// <summary>A leader slower than this counts as standing (its follower's speed bands then apply).</summary>
    public const float StandingSpeed = 0.1f;
    /// <summary>Seconds a follower blocked on its way straight to its slot takes paths instead (engine choice).</summary>
    public const float FollowPathSeconds = 3;
    /// <summary>A follower on paths (far off, or blocked) farther than this from its slot asks for a path to it.</summary>
    public const float FollowSlack = 14;
    /// <summary>Seconds between a follower's path requests, and the wait after a failed one.</summary>
    public const float RepathSeconds = 0.5f, FailedPathWait = 2;
    /// <summary>A wanderer waits this many seconds (uniform) after reaching a place.</summary>
    public const float WaitMin = 3, WaitMax = 10;
    /// <summary>A follower nearer its aim than this steers straight at it; farther, it takes paths (the original's 50).</summary>
    public const float DirectDistance = 50;
    /// <summary>
    /// Radians per second the heading of a path follower turns at most (engine choice: a Kenshi recording turns a standing character about 100 degrees in
    /// 0.15 s before it runs; docs/game/pathfinding.md "Turning").
    /// </summary>
    public const float TurnRate = 4 * MathF.PI;

    /// <summary>
    /// Meitou's dynamic movement (Settings, Advanced; off in the game and the tests): bodies turn at a body's pace instead of at once and gather speed when they set off.
    /// Standing they turn <see cref="DynamicTurnStanding"/> rad/s, at full speed <see cref="DynamicTurnRunning"/> (a runner curves round instead of pivoting); a fighter
    /// turns to its target at <see cref="DynamicFightTurn"/> and, once its swing has started, only <see cref="DynamicSwingTurn"/>: the blow goes where it was aimed. A walker
    /// reaches its speed in about <see cref="DynamicStartSeconds"/>. The combat system's half: <see cref="Combat.CombatSystem.Dynamic"/>.
    /// </summary>
    public bool Dynamic { get; set; }
    public const float DynamicTurnStanding = 5.2f, DynamicTurnRunning = 2.6f, DynamicFightTurn = 4.2f, DynamicSwingTurn = 0.8f,
        DynamicStartSeconds = 0.45f, DynamicFightStart = 0.2f, DynamicStopSeconds = 0.3f;

    /// <summary>The deceleration (units/s²) of a body whose top speed is <paramref name="top"/>: it stops from full speed in <see cref="DynamicStopSeconds"/>.</summary>
    static float Brake(float top) => MathF.Max(top, 10) / DynamicStopSeconds;

    /// <summary>How fast a body turns at <paramref name="speed"/> of its top <paramref name="top"/> (rad/s): the game's at once, or the dynamic movement's.</summary>
    float TurnRateFor(float speed, float top) =>
        !Dynamic ? TurnRate : DynamicTurnStanding + (DynamicTurnRunning - DynamicTurnStanding) * Math.Clamp(speed / MathF.Max(top, 1), 0, 1);

    /// <summary>A fighter's turn rate toward its target: slow once its swing has started (dynamic movement), else as it would turn.</summary>
    float FightTurnRate(World world, int slot) =>
        !Dynamic ? TurnRate : Fights is { } f && f.StateOf(world.Characters.IdOf(slot)).AttackTech >= 0 ? DynamicSwingTurn : DynamicFightTurn;
    /// <summary>
    /// Seconds between the requests of a walker on the provisional part of a long route (<see cref="PathResult.Exact"/>), which ask for the path again as
    /// zones load ahead of it, and how many times running it may be stopped there by ground the route did not know before it gives up (engine choices).
    /// </summary>
    public const float ProvisionalRepathSeconds = 2;
    public const int ProvisionalBlockedLimit = 5;
    /// <summary>
    /// How many times running a walker stopped on the navmesh part of its path (no ground it can stand on ahead) asks for the path again from where it stands before it drops it
    /// and the order (engine choice: from a slightly different spot the path can start elsewhere, docs/game/pathfinding.md "Own builder").
    /// </summary>
    public const int BlockedRepathLimit = 3;

    public PathService Paths { get; } = paths;

    /// <summary>The system owns its path service (the host hands it over at construction), so disposing the system stops the path thread.</summary>
    public void Dispose() => Paths.Dispose();

    public void Inputs(World world, IReadOnlyList<SimCommand> commands)
    {
        ApplyAnswers(world);
        var table = world.Characters;
        foreach (var command in commands)
        {
            if (command is RepathCommand repath)
            {
                for (int s = 0; s < table.Next.Length; s++)
                {
                    ref var c = ref table.Next[s];
                    if (!c.Alive || c.Task != (byte)CharacterTask.GoTo || (c.Flags & (ushort)(MoveFlags.Pending | MoveFlags.HasPath)) == 0) continue;
                    // A walker whose way on does not lead through a changed zone keeps its path (a long walk loads zones ahead all the way, and each would
                    // otherwise turn every walker about). One waiting for an answer asks again: it may have been made on the old meshes.
                    if (repath.Zones is { } zones && (c.Flags & (ushort)MoveFlags.Pending) == 0 && !Crosses(c.Position, table.Cold(s)!.Path, c.PathCursor, zones)) continue;
                    c.Flags = (ushort)((c.Flags & ~(ushort)(MoveFlags.Pending | MoveFlags.HasPath)) | (ushort)MoveFlags.NeedPath);
                    c.PathCursor = 0;
                    table.Cold(s)!.PathRequest = 0;
                }
                continue;
            }
            if (command is SandboxMovement sandbox)
            {
                if (table.TryResolveNext(sandbox.Character, out int target))
                {
                    var tc = table.Cold(target)!;
                    if (sandbox.Mode is { } mode) tc.ModeOverride = mode;
                    if (sandbox.Speed is { } fixedSpeed) tc.SpeedOverride = MathF.Max(fixedSpeed, 0);
                }
                continue;
            }
            if (command is FollowOrder follow)
            {
                Follow(world, follow);
                continue;
            }
            if (command is FormationOrder formation)
            {
                var members = new List<int>();
                var spots = new List<Vector3>();
                for (int k = 0; k < formation.Characters.Count && k < formation.Targets.Count; k++)
                {
                    if (!table.TryResolveNext(formation.Characters[k], out int s) || table.Cold(s)!.IsImprisoned) continue;
                    if (table.Cold(s)!.InSomething == Locks.HeldState.Bed) world.Fixtures.LeaveBed(world, formation.Characters[k]);
                    members.Add(s);
                    spots.Add(formation.Targets[k]);
                }
                StartFormation(world, members, spots, formation.Facing, formation.Queued, reform: true);
                continue;
            }
            if (command is not MoveOrder order) continue;
            // An empty list orders the selection (the UI sends orders for whatever is selected when the click happens).
            var who = order.Characters.Count > 0 ? order.Characters : world.Player.Selection.ToArray();
            var slots = new List<int>();
            foreach (var id in who)
            {
                if (!table.TryResolveNext(id, out int found) || table.Cold(found)!.IsImprisoned) continue;   // a prisoner stays in its cage
                if (table.Cold(found)!.InSomething == Locks.HeldState.Bed) world.Fixtures.LeaveBed(world, id);   // an order gets a sleeper up
                slots.Add(found);
            }
            // A whole formation ordered somewhere marches there as a block, keeping its shape, turned to face the way it goes (Total War's right click).
            if (!order.Queued && MarchOf(table, slots, new Vector3(order.Target.X, order.Target.Y, order.Target.Z)) is { } march)
            {
                StartFormation(world, slots, march.Spots, march.Facing, false);
                continue;
            }
            var targets = Formation.Place([.. slots.Select(s => new Vector2(table.Next[s].Position.X, table.Next[s].Position.Z))], new Vector2(order.Target.X, order.Target.Z));
            for (int k = 0; k < slots.Count; k++)
            {
                int slot = slots[k];
                ref var n = ref table.Next[slot];
                var cold = table.Cold(slot)!;
                var target = targets[k];
                cold.FollowOrder = CharacterId.None;
                LeaveFormation(cold);
                bool busy = n.Task == (byte)CharacterTask.GoTo && (n.Flags & (ushort)MoveFlags.AnyPath) != 0;
                if (order.Queued && busy)
                {
                    cold.OrderQueue.Add(target);   // after the order in hand (shift + right click)
                    continue;
                }
                GoToFloor(world, slot, new Vector3(target.X, order.Target.Y, target.Y), cold.RunSpeed);
                if ((cold.Orders & CharacterOrders.Hold) != 0) cold.HoldPoint = target;   // Task_Move sets the hold point to its destination while on Hold
            }
        }
        foreach (var squad in world.Squads.All) RefreshFormation(world, squad);
    }

    /// <summary>
    /// Rebuilds a squad's <see cref="Squad.FormationSlots"/> when its formation, head, members or their roles changed (serial). The head is the leader, or the squad mate the
    /// leader follows (the original's task-31 head). The draws are keyed by the squad and the key, so a rebuild of the same squad gives the same slots.
    /// </summary>
    static void RefreshFormation(World world, Squad squad)
    {
        var table = world.Characters;
        var head = FormationHead(table, squad);
        int type = squad.Faction >= 0 && world.FactionFormations is { } formations && squad.Faction < formations.Count ? formations[squad.Faction] : (int)SquadFormation.Random;
        ulong key = Rng.Mix((ulong)type ^ Rng.Key(head) << 2);
        foreach (var m in squad.Members) key = Rng.Mix(key ^ Rng.Key(m) ^ (ulong)(table.IsAlive(m) ? table.Cold(m.Slot)!.Role : 7) << 56);
        if (key == squad.FormationKey) return;
        squad.FormationKey = key;
        if (head.IsNone || !table.IsAlive(head))
        {
            squad.FormationSlots = [];
            return;
        }
        FormationMember Of(CharacterId id)
        {
            var c = table.Cold(id.Slot)!;
            return new FormationMember(id, c.Role, c.Animal is not null, Formation.RadiusScale * (c.FootprintRadius > 0 ? c.FootprintRadius : Combat.CombatTuning.DefaultFootprint));
        }
        var others = new List<FormationMember>();
        var slaves = new List<FormationMember>();
        foreach (var m in squad.Members)
        {
            if (m == head || !table.IsAlive(m)) continue;
            var member = Of(m);
            (member.Role == (int)SquadRole.Slave ? slaves : others).Add(member);
        }
        ulong draws = 0, seed = Rng.Hash(world.Seed, (ulong)squad.Id, RngPurpose.Formation, key);
        squad.FormationSlots = Formation.Layout((SquadFormation)type, Of(head), others, slaves, () => Rng.Float(Rng.Hash(seed, 0, RngPurpose.Formation, draws++)));
    }

    /// <summary>The character a squad's formation is laid out from: its leader, or the squad mate the leader follows.</summary>
    static CharacterId FormationHead(CharacterTable table, Squad squad)
    {
        var leader = squad.Leader;
        if (leader.IsNone || !table.IsAlive(leader)) return leader;
        var followed = table.Cold(leader.Slot)!.FollowOrder;
        return table.Next[leader.Slot].Task == (byte)CharacterTask.Follow && !followed.IsNone && squad.Members.Contains(followed) && table.IsAlive(followed) ? followed : leader;
    }

    /// <summary>How far above or below the given height the floor <see cref="GoToFloor"/> walks onto may be (engine choice: a storey is about 66).</summary>
    public const float FloorSlack = 20;

    /// <summary>
    /// <see cref="GoTo"/> onto the floor at <paramref name="at"/>'s height (serial phases only). Furniture cuts holes in a building's floor mesh and a goal there resolves to the
    /// storey above or the ground below, so the goal moves to the nearest floor point (<see cref="IWalkability.FloorNear"/>) and keeps its height. Returns the X/Z goal.
    /// </summary>
    public static Vector2 GoToFloor(World world, int slot, Vector3 at, byte mode)
    {
        var floor = world.Walkability.FloorNear(at.X, at.Z, at.Y, FloorSlack) ?? at;
        var flat = new Vector2(floor.X, floor.Z);
        GoTo(world.Characters, slot, flat, mode);
        var cold = world.Characters.Cold(slot)!;
        (cold.GoalHeight, cold.GoalHeightAt) = (floor.Y, flat);
        return flat;
    }

    /// <summary>
    /// Sends a character to a point at a speed mode, dropping its queue (serial phases only). A path it is following is kept until the new one
    /// arrives, so a new order given on the move (holding the right button) does not stop it for the ticks the path takes.
    /// </summary>
    public static void GoTo(CharacterTable table, int slot, Vector2 target, byte mode)
    {
        ref var n = ref table.Next[slot];
        var cold = table.Cold(slot)!;
        cold.OrderQueue.Clear();
        cold.ArriveWithin = 0;
        cold.ArriveYaw = float.NaN;
        cold.FormationWalk = false;
        cold.MarchSpeed = 0;
        cold.BlockedRepaths = 0;
        n.Task = (byte)CharacterTask.GoTo;
        n.Goal = target;
        n.Mode = mode;
        n.Flags = (ushort)((n.Flags & ~(ushort)MoveFlags.Pending) | (ushort)MoveFlags.NeedPath);
        cold.PathRequest = 0;
    }

    /// <summary>Drops a character's orders and path and makes it stand (serial phases only).</summary>
    /// <summary>
    /// <see cref="FollowOrder"/>: each awake, free character (not the target) drops what it does and follows the target (<see cref="CharacterCold.FollowOrder"/>); a sleeper gets up first.
    /// </summary>
    internal static void Follow(World world, FollowOrder order)
    {
        var table = world.Characters;
        if (!table.TryResolveNext(order.Target, out int led)) return;
        foreach (var id in order.Characters.Count > 0 ? order.Characters : world.Player.Selection.ToArray())
        {
            if (!table.TryResolveNext(id, out int s) || s == led || table.Cold(s) is not { IsImprisoned: false, Medical.Incapacitated: false, CarriedBy.IsNone: true } cold) continue;
            if (cold.InSomething == Locks.HeldState.Bed) world.Fixtures.LeaveBed(world, id);
            Stop(table, s);
            cold.FollowOrder = order.Target;
            cold.Guarding = order.Guard;
            cold.FollowPathTime = 0;
            cold.FollowHeading = Vector2.Zero;
            table.Next[s].Task = (byte)CharacterTask.Follow;
            table.Next[s].TaskTime = 0;
            BreakCircle(table, id);
        }
    }

    /// <summary>
    /// The original drops a follow order that would close a circle (<c>FUN_1405c8c10</c>, run by the order giver <c>FUN_1405c8da0</c> for orders 31 and 45, <b>Observed</b>;
    /// docs/game/animals.md "Following"): walking from whom <paramref name="leader"/> follows, the first character found following <paramref name="leader"/> stops (at most one).
    /// </summary>
    static void BreakCircle(CharacterTable table, CharacterId leader)
    {
        var at = table.Cold(leader.Slot)!.FollowOrder;
        for (int guard = 0; guard < 64 && !at.IsNone && table.TryResolveNext(at, out int slot); guard++)
        {
            var cold = table.Cold(slot)!;
            if (cold.FollowOrder == leader)
            {
                cold.FollowOrder = CharacterId.None;
                if (table.Next[slot].Task == (byte)CharacterTask.Follow) Stop(table, slot);
                return;
            }
            at = cold.FollowOrder;
        }
    }

    public static void Stop(CharacterTable table, int slot)
    {
        ref var n = ref table.Next[slot];
        n.Flags = (ushort)(n.Flags & ~(ushort)MoveFlags.AnyPath);
        n.Task = (byte)CharacterTask.Idle;
        n.PathCursor = 0;
        var cold = table.Cold(slot)!;
        cold.OrderQueue.Clear();
        cold.PathRequest = 0;
        cold.FollowHeading = Vector2.Zero;
    }

    public void Schedule(World world)
    {
        Fights ??= world.Systems.OfType<Combat.CombatSystem>().FirstOrDefault();
        KeepFormations(world);
        var table = world.Characters;
        var next = table.Next;
        for (int i = 0; i < next.Length; i++)
        {
            if (!next[i].Alive || (next[i].Flags & (ushort)MoveFlags.NeedPath) == 0) continue;
            var cold = table.Cold(i)!;
            // The goal's floor: the height the order was given at, else the character's own (a wander or follow goal on its level).
            float nearY = cold.GoalHeightAt == next[i].Goal && !float.IsNaN(cold.GoalHeight) ? cold.GoalHeight : next[i].Position.Y;
            var to = new Vector3(next[i].Goal.X, world.Walkability.GroundHeight(next[i].Goal.X, next[i].Goal.Y, nearY), next[i].Goal.Y);
            cold.PathRequest = Paths.Submit(new CharacterId(i, next[i].Generation), next[i].Position, to, cold.FootprintRadius,
                WaterCost.Of(cold.WaterFactor, next[i].MaxSpeed, cold.Race, cold.SwimSpeed), cold.RoadPreference);
            next[i].Flags = (ushort)((next[i].Flags & ~(ushort)MoveFlags.NeedPath) | (ushort)MoveFlags.Pending);
        }
        // The synchronous service has the answers already.
        if (Paths.IsSynchronous) ApplyAnswers(world);
    }

    void ApplyAnswers(World world)
    {
        var table = world.Characters;
        foreach (var answer in Paths.Drain())
        {
            if (!table.TryResolveNext(answer.Character, out int slot)) continue;
            var cold = table.Cold(slot)!;
            ref var n = ref table.Next[slot];
            if (cold.PathRequest != answer.Id)
            {
                // An older request: the character was given another goal since. Orders given again every tick (a held right button) are each replaced
                // before their path comes, so a found path newer than the one followed is still taken while the latest request is on its way.
                bool waiting = (n.Flags & (ushort)(MoveFlags.NeedPath | MoveFlags.Pending)) != 0;
                if (!answer.Path.Found || !waiting || n.Task != (byte)CharacterTask.GoTo || answer.Id <= cold.PathApplied) continue;
                cold.Path = [.. answer.Path.Points.Skip(1)];
                cold.PathExact = answer.Path.IsProvisional ? answer.Path.Exact - 1 : int.MaxValue;
                if (answer.Path.Exact > 1) cold.ProvisionalBlocked = 0;
                cold.PathApplied = answer.Id;
                n.PathCursor = 0;
                n.Flags |= (ushort)MoveFlags.HasPath;
                continue;
            }
            n.Flags = (ushort)(n.Flags & ~(ushort)MoveFlags.Pending);
            if (answer.Path.Found)
            {
                cold.PathFailed = false;
                cold.Path = [.. answer.Path.Points.Skip(1)];
                cold.PathExact = answer.Path.IsProvisional ? answer.Path.Exact - 1 : int.MaxValue;
                if (answer.Path.Exact > 1) cold.ProvisionalBlocked = 0;
                n.PathCursor = 0;
                n.Flags |= (ushort)MoveFlags.HasPath;
            }
            else
            {
                // No way there: the old path (an order given on the move) is dropped too.
                cold.PathFailed = true;
                n.Flags = (ushort)(n.Flags & ~(ushort)MoveFlags.HasPath);
                n.PathCursor = 0;
                n.TaskTime = FailedPathWait;
                if (n.Task == (byte)CharacterTask.GoTo) { n.Task = (byte)CharacterTask.Idle; cold.OrderQueue.Clear(); }
            }
        }
    }

    /// <summary>A formation move's facing (<see cref="FormationOrder"/>), taken where its walk ends (on its spot, or stopped short by a wall or a crowd).</summary>
    static void Face(ref CharacterHot n, CharacterCold cold)
    {
        if (float.IsNaN(cold.ArriveYaw)) return;
        n.Yaw = cold.ArriveYaw;
        cold.ArriveYaw = float.NaN;
    }

    public void Think(World world, Partition part)
    {
        var table = world.Characters;
        var prev = table.Previous;
        var next = table.Next;
        float dt = world.TickSeconds;
        for (int i = part.Start; i < part.End; i++)
        {
            if (!prev[i].Alive) continue;
            ref var n = ref next[i];
            var task = (CharacterTask)n.Task;
            var flags = (MoveFlags)n.Flags;
            var cold = table.Cold(i)!;
            if (cold.Medical is { Incapacitated: true }) continue;
            bool busy = (flags & MoveFlags.AnyPath) != 0;
            if (task == CharacterTask.Follow)
            {
                // A follow order whose target is gone ends: the character stands where it is.
                if (!cold.FollowOrder.IsNone && !table.IsAlive(cold.FollowOrder))
                {
                    cold.FollowOrder = CharacterId.None;
                    n.Task = (byte)CharacterTask.Idle;
                    continue;
                }
                var squad = cold.SquadId >= 0 ? world.Squads.Find(cold.SquadId) : null;
                var leader = Followed(squad, cold);
                if (leader.IsNone || leader.Slot == i || !table.IsAlive(leader))
                {
                    n.Task = (byte)CharacterTask.Wander;
                    continue;
                }
                if (table.Cold(leader.Slot)?.CarriedBy == table.IdOf(i)) continue;   // it carries its leader: the squad follows it, it does not follow itself
                var lead = prev[leader.Slot];
                var self = table.IdOf(i);
                var leadAt = new Vector2(lead.Position.X, lead.Position.Z);
                var leadDir = new Vector2(MathF.Sin(lead.Yaw), MathF.Cos(lead.Yaw));
                float leadSpeed = new Vector2(lead.Velocity.X, lead.Velocity.Z).Length();
                bool moving = leadSpeed > StandingSpeed;
                var here = new Vector2(n.Position.X, n.Position.Z);
                Vector2 slotPos;
                if (squad is not null && squad.FormationSlots.TryGetValue(self, out var slot) && squad.Members.Contains(leader))
                {
                    // A squad mate: its slot turned onto its eased heading, one second of the leader's travel ahead of the leader.
                    var heading = cold.FollowHeading;
                    if (heading == Vector2.Zero) heading = leadDir;
                    else if (moving)
                    {
                        heading += (leadDir - heading) * (HeadingEase * dt);
                        heading = heading.LengthSquared() > 1e-8f ? Vector2.Normalize(heading) : leadDir;
                    }
                    cold.FollowHeading = heading;
                    slotPos = leadAt + heading * leadSpeed + Formation.ToWorld(slot, heading);
                }
                else
                {
                    // Anyone else: its drawn distance from the target, on its own side.
                    var off = here - leadAt;
                    off = off.LengthSquared() > 1e-6f ? Vector2.Normalize(off) : -leadDir;
                    slotPos = leadAt + off * PlainDistance(world.Seed, self, leader);
                }
                float away = Vector2.Distance(here, slotPos);
                cold.FollowSpeed = FollowSpeedFor(away, moving, leadDir, new Vector2(MathF.Sin(prev[i].Yaw), MathF.Cos(prev[i].Yaw)));
                n.Mode = lead.Mode;
                n.TaskTime -= dt;
                if (cold.FollowPathTime > 0) cold.FollowPathTime = MathF.Max(cold.FollowPathTime - dt, 0);
                if (away < DirectDistance && cold.FollowPathTime <= 0)
                {
                    // Near its aim: Move steers it straight there (a path it had is dropped).
                    n.Goal = slotPos;
                    if (busy)
                    {
                        n.Flags = (ushort)(n.Flags & ~(ushort)MoveFlags.AnyPath);
                        n.PathCursor = 0;
                        cold.PathRequest = 0;
                    }
                    continue;
                }
                if (away > FollowSlack && (flags & (MoveFlags.NeedPath | MoveFlags.Pending)) == 0 && n.TaskTime <= 0
                    && (!busy || Vector2.DistanceSquared(slotPos, n.Goal) > 20 * 20))
                {
                    n.Goal = slotPos;
                    n.Flags |= (ushort)MoveFlags.NeedPath;
                    n.TaskTime = RepathSeconds;
                }
            }
            else if (task == CharacterTask.Wander)
            {
                n.Mode = SpeedMode.Walk;
                if (busy) continue;
                n.TaskTime -= dt;
                if (n.TaskTime > 0) continue;
                var squad = cold.SquadId >= 0 ? world.Squads.Find(cold.SquadId) : null;
                var centre = squad is not null ? squad.HomeCentre : new Vector2(n.Position.X, n.Position.Z);
                float radius = squad is not null ? squad.HomeRadius : 100;
                if (cold.Ai is { WanderRadius: > 0 } mind) (centre, radius) = (mind.WanderCentre, mind.WanderRadius);   // milling about its post (Ai.AiBehaviour.Mill)
                ulong roll = Rng.Hash(world.Seed, Rng.Key(table.IdOf(i)), RngPurpose.Wander, (ulong)world.Tick);
                n.Goal = Rng.PointInDisc(roll, centre, radius);
                n.Flags |= (ushort)MoveFlags.NeedPath;
            }
        }
    }

    /// <summary>Whom a follower keeps its slot by: the target of its <see cref="FollowOrder"/>, a contract's mission target (Ai.AiMind.FollowTarget), else its squad's leader.</summary>
    static CharacterId Followed(Squad? squad, CharacterCold cold) =>
        !cold.FollowOrder.IsNone ? cold.FollowOrder : cold.Ai is { FollowTarget.IsNone: false } follows ? follows.FollowTarget : squad?.Leader ?? CharacterId.None;

    /// <summary>How far a follower that is not its target's squad mate keeps from it: 20..40, drawn per follower and target (the original draws it when the follow task is made).</summary>
    static float PlainDistance(ulong seed, CharacterId self, CharacterId target) =>
        20 + 20 * Rng.Float(Rng.Hash(seed, Rng.Key(self), RngPurpose.Formation, Rng.Key(target)));

    /// <summary>
    /// The original's desired speed of a follower <paramref name="away"/> from its aim (docs/game/pathfinding.md "Squads travelling together"): behind a moving leader the distance
    /// itself, less while it faces away from the leader's heading (down to a tenth); beside a standing one 0 within 20, then 10-20, 20-30 and 30-999 at 50, 200 and 500 (linear in
    /// the distance squared).
    /// </summary>
    public static float FollowSpeedFor(float away, bool leaderMoving, Vector2 leaderDir, Vector2 ownDir)
    {
        float d2 = away * away;
        if (d2 < 1) return 0;
        if (leaderMoving) return away * MathF.Max(0.1f, 0.5f + 0.5f * Vector2.Dot(leaderDir, ownDir));
        if (d2 >= 40000) return 30 + 969 * Math.Clamp((d2 - 40000) / 210000, 0, 1);
        if (d2 >= 2500) return 20 + 10 * Math.Clamp((d2 - 2500) / 37500, 0, 1);
        if (d2 >= 400) return 10 + 10 * Math.Clamp((d2 - 400) / 2100, 0, 1);
        return 0;
    }

    /// <summary>
    /// The group speed (<see cref="SpeedMode.Group"/>): the lowest top speed among the squad's members walking a path in that mode (last tick's, so the order the
    /// partitions run in does not matter). Which characters the original counts as the group (the squad or the selection) is Unknown (docs/game/pathfinding.md).
    /// </summary>
    static float SlowestOfSquad(World world, CharacterCold cold)
    {
        var table = world.Characters;
        float slowest = float.MaxValue;
        if (cold.SquadId < 0 || world.Squads.Find(cold.SquadId) is not { } squad) return slowest;
        foreach (var member in squad.Members)
        {
            if (!table.IsAlive(member)) continue;
            ref readonly var p = ref table.Previous[member.Slot];
            if (p.Mode != SpeedMode.Group || ((MoveFlags)p.Flags & MoveFlags.HasPath) == 0 || table.Cold(member.Slot)?.Medical is { Incapacitated: true }) continue;
            slowest = MathF.Min(slowest, p.MaxSpeed);
        }
        return slowest;
    }

    public void Move(World world, Partition part)
    {
        var table = world.Characters;
        var prev = table.Previous;
        var next = table.Next;
        var walk = world.Walkability;
        float dt = world.TickSeconds;
        for (int i = part.Start; i < part.End; i++)
        {
            if (!prev[i].Alive) continue;
            ref readonly var p = ref prev[i];
            ref var n = ref next[i];
            var cold = table.Cold(i)!;
            if (cold.Medical is { Incapacitated: true })
            {
                // Lying down (knocked out or dead): no steering, no pushing; the body system has already dropped the orders.
                n.Velocity = Vector3.Zero;
                n.Animation = 0;
                continue;
            }
            if (n.Task != (byte)CharacterTask.Attack && cold.StaggeredUntil > world.Tick || cold.GettingUpUntil > world.Tick)
            {
                // A stagger outside a fight stops the movement while the stumble clip plays (Character::_startStumble), and getting up after a knockout in or out of
                // one (Task_GetUp); the order's path is kept and resumed.
                n.Velocity = Vector3.Zero;
                continue;
            }
            if (cold.IsImprisoned || cold.InSomething == Locks.HeldState.Bed && n.Task == (byte)CharacterTask.Sleep)
            {
                // Held in its cage or bed (put there by the fixture registry): no path, no steering, not pushed. Its position is left as it is: one laid in this
                // tick's inputs is already at its spot in Next.
                n.Velocity = Vector3.Zero;
                n.Flags = (ushort)(n.Flags & ~(ushort)MoveFlags.AnyPath);
                continue;
            }
            float speed = MathF.Sqrt(p.Velocity.X * p.Velocity.X + p.Velocity.Z * p.Velocity.Z), moving = speed;
            var dir = Vector2.Zero;
            bool facing = false;
            float desired = 0;
            bool finished = false, closing = false;
            int water = WaterStateAt(walk, p.Position, cold);
            cold.WaterState = (byte)water;
            // A fighter in its footwork: the fight steering's velocity, facing its target (docs/game/combat.md "Fight steering").
            bool steering = Fights is not null && cold.Steer is { Active: true } && n.Task == (byte)CharacterTask.Attack;

            if (steering)
            {
                var v = Combat.CombatSteering.Velocity(world, Fights!, i, cold, cold.Steer!, n.MaxSpeed, cold.Animation?.Strafing ?? false, speed > 0.01f, out var face);
                float vs = v.Length();
                (dir, speed) = vs > 1e-4f ? (v / vs, vs) : (Vector2.Zero, 0f);
                if (face != Vector2.Zero)
                {
                    facing = true;
                    var heading = Turn(p.Yaw, face, 1, dt, FightTurnRate(world, i)).Dir;
                    n.Yaw = MathF.Atan2(heading.X, heading.Y);
                }
            }
            else if (((MoveFlags)n.Flags & MoveFlags.HasPath) != 0 && n.PathCursor < cold.Path.Length)
            {
                var waypoint = cold.Path[n.PathCursor];
                var to = new Vector2(waypoint.X - p.Position.X, waypoint.Z - p.Position.Z);
                float dist = to.Length();
                bool last = n.PathCursor == cold.Path.Length - 1;
                closing = last && cold.ArriveWithin > 0 && dist <= ArriveRadius;
                if (dist <= (last ? cold.ArriveWithin > 0 ? cold.ArriveWithin : ArriveRadius : ArriveRadius * 0.5f)
                    && (last || dist <= CornerReach || CanCut(walk, p.Position, cold.Path[n.PathCursor + 1])))
                {
                    n.PathCursor++;
                    if (cold.BlockedRepaths != 0) cold.BlockedRepaths = 0;
                    if (last) finished = true;
                }
                else
                {
                    dir = to / dist;
                    byte mode = cold.ModeOverride ?? n.Mode;
                    // A carrier in walk mode is raised to run mode (the original's speed selector, Observed; docs/game/carry-loot.md "Speed and weight").
                    if (mode == SpeedMode.Walk && !cold.Carrying.IsNone) mode = SpeedMode.Run;
                    float top = water > 0 && cold.Race is { } race ? Speed.InWater(water, n.MaxSpeed, race, cold.SwimSpeed) : n.MaxSpeed;
                    desired = cold.SpeedOverride > 0 ? cold.SpeedOverride
                        : n.Task == (byte)CharacterTask.Follow ? MathF.Min(top, cold.FollowSpeed)   // a follower's own desired speed, not its mode's cap
                        : MathF.Min(top, SpeedMode.Cap(mode, n.WalkSpeed));
                    // A fighter in its footwork closes in at a shuffle (docs/game/combat.md "Combat stance").
                    if (cold.InCombat && !cold.CombatRunning && n.Task == (byte)CharacterTask.Attack) desired = MathF.Min(desired, Combat.CombatTuning.FootworkSpeed);
                    if (mode == SpeedMode.Group && cold.SpeedOverride <= 0 && n.Task != (byte)CharacterTask.Follow) desired = MathF.Min(desired, SlowestOfSquad(world, cold));
                    if ((cold.Orders & CharacterOrders.Sneak) != 0 && cold.SpeedOverride <= 0) desired = MathF.Min(desired, Orders.SneakSpeed(desired, Orders.Stealth(cold)));
                    if (cold.MarchSpeed > 0 && cold.SpeedOverride <= 0) desired = MathF.Min(desired, cold.MarchSpeed);   // in step with its formation (KeepFormations)
                    // Dynamic movement: it slows into its last point so that its weight carries it there and no further.
                    if (Dynamic && last)
                        desired = MathF.Min(desired, MathF.Sqrt(2 * Brake(n.MaxSpeed) * MathF.Max(dist - (cold.ArriveWithin > 0 ? cold.ArriveWithin : ArriveRadius), 0)) + 6);
                }
                // On the provisional part of a long route: ask again (the zones ahead may have loaded), following this one until the answer comes.
                if (n.PathCursor >= cold.PathExact) Reroute(world, ref n, cold);
            }
            else if (((MoveFlags)n.Flags & MoveFlags.HasPath) != 0)
            {
                finished = true;
            }
            else if (n.Task == (byte)CharacterTask.Follow && ((MoveFlags)n.Flags & MoveFlags.AnyPath) == 0 && cold.FollowPathTime <= 0 && cold.FollowSpeed > 0)
            {
                // Near its aim (Think set it as the goal): straight at it at its desired speed, capped by its top speed, never past it.
                var gap = new Vector2(n.Goal.X - p.Position.X, n.Goal.Y - p.Position.Z);
                float dist = gap.Length();
                if (dist > 1e-3f)
                {
                    float top = water > 0 && cold.Race is { } race ? Speed.InWater(water, n.MaxSpeed, race, cold.SwimSpeed) : n.MaxSpeed;
                    dir = gap / dist;
                    desired = MathF.Min(MathF.Min(cold.FollowSpeed, top), dist / dt);
                    if ((cold.Orders & CharacterOrders.Sneak) != 0 && cold.SpeedOverride <= 0) desired = MathF.Min(desired, Orders.SneakSpeed(desired, Orders.Stealth(cold)));
                }
            }

            if (steering) { }   // its velocity is the steering's
            else if (finished)
            {
                n.Flags = (ushort)(n.Flags & ~(ushort)MoveFlags.HasPath);
                n.PathCursor = 0;
                speed = 0;
                // The end of an old path while the new one is on its way (an order given on the move) leaves the task to the new path.
                bool replaced = ((MoveFlags)n.Flags & (MoveFlags.NeedPath | MoveFlags.Pending)) != 0;
                if (!replaced && n.Task == (byte)CharacterTask.GoTo)
                {
                    if (cold.OrderQueue.Count > 0)
                    {
                        // The next queued order (shift + right click): a path to it is asked for next tick.
                        n.Goal = cold.OrderQueue[0];
                        cold.OrderQueue.RemoveAt(0);
                        n.Flags |= (ushort)MoveFlags.NeedPath;
                    }
                    else
                    {
                        n.Task = (byte)CharacterTask.Idle;
                        // A formation move's facing (FormationOrder).
                        Face(ref n, cold);
                    }
                }
                else if (!replaced && n.Task == (byte)CharacterTask.Attack) n.TaskTime = PursuitSystem.RepathSeconds;   // a fighter at the end of its path to a moving target asks for the next one soon (PursuitSystem)
                else if (!replaced)
                {
                    ulong roll = Rng.Hash(world.Seed, Rng.Key(table.IdOf(i)), RngPurpose.Think, (ulong)world.Tick);
                    n.TaskTime = WaitMin + Rng.Float(roll) * (WaitMax - WaitMin);
                }
            }
            else
            {
                // A path is followed at the top speed at once (the nav agent sets its velocity to top speed x direction); the 15 / s ramp is the combat steering's.
                speed = cold.InCombat && desired > speed ? MathF.Min(speed + Acceleration * dt, desired) : desired;
                if (dir != Vector2.Zero && FaceOf(prev, n, cold) is { } face)
                {
                    // In its footwork a fighter walks its path whichever way it lies and turns to face its target (the side steps, docs/animation.md "Side steps").
                    facing = true;
                    var heading = Turn(p.Yaw, face, 1, dt, FightTurnRate(world, i)).Dir;
                    n.Yaw = MathF.Atan2(heading.X, heading.Y);
                }
                else if (dir != Vector2.Zero)
                {
                    (dir, speed) = Turn(p.Yaw, dir, speed, dt, TurnRateFor(moving, n.MaxSpeed));   // the turn rate of how fast it is going now
                    n.Yaw = MathF.Atan2(dir.X, dir.Y);   // also while it turns on the spot
                }
            }

            // Dynamic movement: the body's weight. Its velocity goes toward the one wanted at most so fast: speeding up over DynamicStartSeconds (a fighter's
            // footwork quicker), slowing, stopping or changing direction over DynamicStopSeconds; so it carries on a little round a turn and comes to a stop.
            if (Dynamic && cold.CarriedBy.IsNone && !Seated(n, cold))
            {
                var current = new Vector2(p.Velocity.X, p.Velocity.Z);
                var wanted = dir * speed;
                float top = MathF.Max(n.MaxSpeed, 10);
                bool slowing = wanted.LengthSquared() < current.LengthSquared() || Vector2.Dot(wanted, current) < 0;
                float limit = (slowing ? Brake(top) : top / (steering || cold.InCombat ? DynamicFightStart : DynamicStartSeconds)) * dt;
                var change = wanted - current;
                float length = change.Length();
                if (length > limit) change *= limit / length;
                var velocity = current + change;
                float vs = velocity.Length();
                (dir, speed) = vs > 0.05f ? (velocity / vs, vs) : (Vector2.Zero, 0f);
            }

            // Separation from the neighbours of the last tick, visited in the grid's fixed order.
            var push = new SeparationState { Table = table, Self = i };
            world.Grid.Query(p.Position.X, p.Position.Z, SeparationRadius, ref push, static (int other, ref SeparationState s) =>
                {
                    if (other == s.Self || s.Count >= MaxSeparationContributions) return;
                    if (s.Table.Cold(other) is { CarriedBy.IsNone: false }) return;   // a carried body is on its carrier's shoulder, not in anyone's way
                    var all = s.Table.Previous;
                    var d = new Vector2(all[s.Self].Position.X - all[other].Position.X, all[s.Self].Position.Z - all[other].Position.Z);
                    float len = d.Length();
                    if (len >= SeparationRadius) return;
                    s.Near[s.Count++] = other;
                    // Exactly on top of each other: the lower slot is pushed one way, the other the opposite.
                    s.Sum += (len > 1e-4f ? d / len : (s.Self < other ? Vector2.UnitX : -Vector2.UnitX)) * (100 * (1 - len / SeparationRadius));
                });

            // Closing on its spot it is not held off by those standing round it (they are pushed aside as it arrives); a sitter is not moved at all.
            bool seated = Seated(n, cold);
            if (closing || seated || steering) push.Sum = Vector2.Zero;   // a steering fighter's repulsion is its separation
            var drive = closing || seated ? dir * speed : Blocked(world, cold, ref push, new Vector2(p.Position.X, p.Position.Z), dir * speed);
            var step = (drive + push.Sum * SeparationStrength) * dt;
            var shove = closing || seated ? Vector2.Zero : Contact(world, i, cold, ref push, new Vector2(p.Position.X, p.Position.Z) + step);
            step += shove;
            float x = p.Position.X + step.X, z = p.Position.Z + step.Y, y = 0;
            bool stands = false;
            // Pushed by its neighbours where it cannot stand (over a hole furniture cuts in a floor): its own step alone.
            if ((push.Sum != Vector2.Zero || shove != Vector2.Zero) && !(stands = walk.TryStand(x, z, p.Position.Y, out y)))
            {
                step = dir * speed * dt;
                (x, z) = (p.Position.X + step.X, p.Position.Z + step.Y);
            }
            if (step != Vector2.Zero)
            {
                // One already off every surface near its height (a stale height) is not held there: it steps down onto what is below.
                if (!stands) stands = walk.TryStand(x, z, p.Position.Y, out y);
                if (stands || !walk.IsWalkable(p.Position.X, p.Position.Z, p.Position.Y) && walk.IsWalkable(x, z))
                {
                    n.Position = new Vector3(x, y, z);
                    if (speed > 0.1f && !facing) n.Yaw = MathF.Atan2(dir.X, dir.Y);
                }
                else if (steering) speed = 0;   // into a wall or off the ground: it stands this tick
                else
                {
                    // Blocked on the provisional part of a long route (the terrain-only route met a wall or a building): stand and ask again.
                    // Blocked on its navmesh part: ask again from where it stands a few times before giving up.
                    if (n.PathCursor >= cold.PathExact ? ++cold.ProvisionalBlocked <= ProvisionalBlockedLimit : ++cold.BlockedRepaths <= BlockedRepathLimit)
                    {
                        speed = 0;
                        dir = Vector2.Zero;
                        Reroute(world, ref n, cold, now: true);
                        goto moved;
                    }
                    // Blocked (no surface to walk on there): drop the path and wait before trying again; a follower steering at its slot takes paths for a while.
                    speed = 0;
                    if (n.Task == (byte)CharacterTask.Follow) cold.FollowPathTime = FollowPathSeconds;
                    n.Flags = (ushort)(n.Flags & ~(ushort)MoveFlags.HasPath);
                    n.TaskTime = FailedPathWait;
                    if (n.Task == (byte)CharacterTask.GoTo) { n.Task = (byte)CharacterTask.Idle; cold.OrderQueue.Clear(); Face(ref n, cold); }
                }
            }
        moved:
            n.Velocity = new Vector3(dir.X * speed, 0, dir.Y * speed);
            n.Animation = (ushort)(speed < 1 ? 0 : speed <= n.WalkSpeed + 1 ? 1 : 2);
            n.AnimationTime = n.Animation == p.Animation ? p.AnimationTime + dt : 0;
        }
    }

    /// <summary>
    /// The water state of a character (<see cref="Speed.WaterState"/>, a race's own characters only, not a carried body): the original reads the height it stands at, which in water is the
    /// seabed, while here a character on the water keeps the navmesh's flat water surface for its height, so the depth is taken from the terrain below while it is at the surface
    /// (<see cref="Speed.WaterSlack"/> over the water level; a deck or floor higher up counts as dry). The race's size scale is taken as 1 (what humans have, <b>Observed</b>).
    /// </summary>
    static int WaterStateAt(IWalkability walk, Vector3 at, CharacterCold cold)
    {
        if (cold.Race is not { } race || !cold.CarriedBy.IsNone) return 0;
        float standY = at.Y <= WorldWater.Height + Speed.WaterSlack ? MathF.Min(at.Y, walk.TerrainHeight(at.X, at.Z)) : at.Y;
        return Speed.WaterState(standY, 1, race.Swims);
    }

    /// <summary>Asks for the path to the goal again (partition-safe: the character's own state), at most every <see cref="ProvisionalRepathSeconds"/> unless <paramref name="now"/>.</summary>
    static void Reroute(World world, ref CharacterHot n, CharacterCold cold, bool now = false)
    {
        if ((n.Flags & (ushort)(MoveFlags.NeedPath | MoveFlags.Pending)) != 0 || !now && world.Tick < cold.ProvisionalRepathTick) return;
        n.Flags |= (ushort)MoveFlags.NeedPath;
        cold.ProvisionalRepathTick = world.Tick + (long)MathF.Ceiling(ProvisionalRepathSeconds / world.TickSeconds);
    }

    /// <summary>Spacing (units) of the points a path is tested at for the zones it crosses (<see cref="Crosses"/>; engine choice, well under a zone).</summary>
    const float ZoneProbeStep = 100;

    /// <summary>Whether the way from <paramref name="at"/> along <paramref name="path"/> from <paramref name="cursor"/> on enters one of <paramref name="zones"/>.</summary>
    static bool Crosses(Vector3 at, Vector3[] path, int cursor, IReadOnlySet<ZoneCoordinate> zones)
    {
        var a = new Vector2(at.X, at.Z);
        if (zones.Contains(WorldLayout.ZoneOf(a.X, a.Y))) return true;
        for (int i = cursor; i < path.Length; i++)
        {
            var b = new Vector2(path[i].X, path[i].Z);
            float length = Vector2.Distance(a, b);
            for (float d = ZoneProbeStep; d < length + ZoneProbeStep; d += ZoneProbeStep)
            {
                var p = Vector2.Lerp(a, b, MathF.Min(d, length) / MathF.Max(length, 1e-3f));
                if (zones.Contains(WorldLayout.ZoneOf(p.X, p.Y))) return true;
            }
            a = b;
        }
        return false;
    }

    /// <summary>The solid radius of a body: <see cref="BodyShare"/> of its footprint (a human's when it has no race data).</summary>
    static float BodyRadius(CharacterCold cold) => BodyShare * (cold.FootprintRadius > 0 ? cold.FootprintRadius : Combat.CombatTuning.DefaultFootprint);

    /// <summary>
    /// How far a character stepped to <paramref name="at"/> is put out of the standing bodies among its separation neighbours (partition-safe: the last tick's state of the
    /// others): half of each overlap with the other's last position, all of it against one held in a bed or cage, averaged over the bodies it overlaps (a sum overshoots in a
    /// packed crowd and the whole crowd shakes). Lying, sitting and carried bodies are not in the way (a walker only steers past a sitter, so a ring of sitters cannot wall it in),
    /// and a carried or bedded one is not moved. Not where the other's velocity takes it: in a jam that is where it wants to go, not where it gets to.
    /// </summary>
    static Vector2 Contact(World world, int self, CharacterCold cold, ref SeparationState near, Vector2 at)
    {
        if (!cold.CarriedBy.IsNone || cold.InSomething != Locks.HeldState.None) return Vector2.Zero;
        var table = world.Characters;
        var all = table.Previous;
        float mine = BodyRadius(cold);
        var shove = Vector2.Zero;
        int contacts = 0;
        for (int k = 0; k < near.Count; k++)
        {
            int other = near.Near[k];
            if (table.Cold(other) is not { } them || them.Medical is { Incapacitated: true }) continue;
            ref readonly var o = ref all[other];
            if (Seated(o, them)) continue;
            var d = at - new Vector2(o.Position.X, o.Position.Z);
            float len = d.Length(), reach = mine + BodyRadius(them);
            if (len >= reach) continue;
            var normal = len > 1e-4f ? d / len : self < other ? Vector2.UnitX : -Vector2.UnitX;
            shove += normal * ((reach - len) * (them.InSomething != Locks.HeldState.None ? 1 : 0.5f));
            contacts++;
        }
        return contacts > 1 ? shove / contacts : shove;
    }

    /// <summary>
    /// The velocity a character steps by without the part of it into a standing body it already touches (engine choice), so the ranks behind a blocked one do not press it
    /// into those in front and a crowd does not squash; what is left slides it round them. The bodies counted are <see cref="Contact"/>'s.
    /// </summary>
    static Vector2 Blocked(World world, CharacterCold cold, ref SeparationState near, Vector2 at, Vector2 drive)
    {
        if (drive == Vector2.Zero || !cold.CarriedBy.IsNone) return drive;
        var table = world.Characters;
        var all = table.Previous;
        float mine = BodyRadius(cold);
        for (int k = 0; k < near.Count; k++)
        {
            int other = near.Near[k];
            if (table.Cold(other) is not { } them || them.Medical is { Incapacitated: true }) continue;
            ref readonly var o = ref all[other];
            if (Seated(o, them)) continue;
            var d = at - new Vector2(o.Position.X, o.Position.Z);
            float len = d.Length();
            if (len >= mine + BodyRadius(them) || len < 1e-4f) continue;
            var normal = d / len;
            float into = -Vector2.Dot(drive, normal);
            if (into > 0) drive += normal * into;
        }
        return drive;
    }

    /// <summary>Sitting (resting, on a chair or the floor, or on a seat it uses) with no path to follow: the original does not push sitters (Observed in play).</summary>
    static bool Seated(in CharacterHot h, CharacterCold c) => ((MoveFlags)h.Flags & MoveFlags.HasPath) == 0
        && (h.Task == (byte)CharacterTask.Rest || h.Task == (byte)CharacterTask.Operate && c.Implicit == Meitou.Data.Gameplay.Ai.TaskType.SIT_AROUND);

    /// <summary>
    /// The heading turned from <paramref name="yaw"/> towards <paramref name="wanted"/> by at most <see cref="TurnRate"/> x <paramref name="dt"/>,
    /// and the speed scaled by the cosine of the turn still to make (zero beyond 90 degrees: the character turns where it stands).
    /// </summary>
    /// <summary>The way to the target a fighter in its footwork faces (<see cref="CharacterCold.FaceSlot"/>, the last tick's place), or null.</summary>
    static Vector2? FaceOf(ReadOnlySpan<CharacterHot> prev, in CharacterHot n, CharacterCold cold)
    {
        int t = cold.FaceSlot;
        if (t < 0 || t >= prev.Length || !prev[t].Alive || n.Task != (byte)CharacterTask.Attack || !cold.InCombat || cold.CombatRunning) return null;
        var d = new Vector2(prev[t].Position.X - n.Position.X, prev[t].Position.Z - n.Position.Z);
        return d.LengthSquared() > 1e-4f ? Vector2.Normalize(d) : null;
    }

    public static (Vector2 Dir, float Speed) Turn(float yaw, Vector2 wanted, float speed, float dt, float rate = TurnRate)
    {
        float target = MathF.Atan2(wanted.X, wanted.Y);
        float delta = MathF.IEEERemainder(target - yaw, MathF.Tau);
        float max = rate * dt;
        float heading = MathF.Abs(delta) <= max ? target : yaw + MathF.CopySign(max, delta);
        float left = MathF.IEEERemainder(target - heading, MathF.Tau);
        return (new Vector2(MathF.Sin(heading), MathF.Cos(heading)), speed * MathF.Max(0, MathF.Cos(left)));
    }

    struct SeparationState
    {
        public CharacterTable Table;
        public int Self;
        public int Count;
        public Vector2 Sum;
        /// <summary>The neighbours counted, for the body contact.</summary>
        public Neighbours Near;
    }

    [InlineArray(MaxSeparationContributions)]
    struct Neighbours
    {
        int first;
    }
}
