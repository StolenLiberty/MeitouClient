using System.Numerics;

namespace Meitou.Simulation;

/// <summary>
/// A formation (Meitou-only, after Total War; docs/simulation.md "Formations"): the characters of a <see cref="FormationOrder"/>, each holding its spot
/// (<see cref="CharacterCold.FormationSpot"/>). Lists run in step with <see cref="Members"/>.
/// </summary>
public sealed class FormationGroup
{
    public List<CharacterId> Members { get; } = [];
    /// <summary>Each member's place across (X, to the right of <see cref="Facing"/>) and ahead (Y) of <see cref="Centre"/>.</summary>
    public List<Vector2> Offsets { get; } = [];
    /// <summary>Each member's straight distance to its spot when the march began (0: not marching).</summary>
    internal List<float> MarchFrom { get; } = [];
    /// <summary>The tick each member may next be sent back to its spot.</summary>
    internal List<long> NextReturn { get; } = [];
    /// <summary>Where each member's march ends (its spot, or its place in the old block short of the new line, <see cref="MovementSystem"/> "re-forming"); the march keeps step to it.</summary>
    internal List<Vector2> MarchTo { get; } = [];
    public Vector2 Centre { get; set; }
    public Vector2 Facing { get; set; }
    /// <summary>The tick until which the formation counts as in a fight (a member fought within <see cref="MovementSystem.FormationCalmSeconds"/>).</summary>
    internal long FightUntil { get; set; }
    /// <summary>In a fight: its members chase the enemies about it instead of holding their spots, and go back once it is over (<see cref="Orders.Holding"/>).</summary>
    public bool Engaged { get; internal set; }

    internal void RemoveAt(int k)
    {
        Members.RemoveAt(k);
        Offsets.RemoveAt(k);
        MarchFrom.RemoveAt(k);
        NextReturn.RemoveAt(k);
        MarchTo.RemoveAt(k);
    }
}

// Formations: holding the spots, fighting back and returning to them, and marching as a block (docs/simulation.md "Formations").
public sealed partial class MovementSystem
{
    readonly List<FormationGroup> formations = [];

    /// <summary>The formations standing now.</summary>
    public IReadOnlyList<FormationGroup> Formations => formations;

    /// <summary>An idle member farther than this from its spot walks back to it (engine choice).</summary>
    public const float FormationReturnSlack = 4;
    /// <summary>Seconds between a member's walks back (so a spot it cannot reach is not asked for every tick).</summary>
    public const float FormationReturnSeconds = 1;
    /// <summary>How much faster (per unit of progress) a marcher behind the block walks, and slower one ahead of it (engine choice).</summary>
    const float MarchCatchUp = 3;
    /// <summary>The least share of the march speed a member with a short way to go walks at.</summary>
    const float MarchLeast = 0.3f;

    /// <summary>The direction to the right of a facing (X across in <see cref="FormationGroup.Offsets"/>).</summary>
    static Vector2 RightOf(Vector2 facing) => new(facing.Y, -facing.X);

    /// <summary>Takes the character out of its formation (any other order: a plain move, a follow, a job, a picked attack; or it left the world).</summary>
    public static void LeaveFormation(CharacterCold cold)
    {
        cold.Formation = null;
        cold.FormationWalk = false;
        cold.MarchSpeed = 0;
    }

    /// <summary>
    /// Makes <paramref name="slots"/> a formation, each sent onto its spot of <paramref name="spots"/> to face <paramref name="facing"/> (serial). Two or more march in step;
    /// <paramref name="queued"/> walks the spot after the walk in hand.
    /// </summary>
    void StartFormation(World world, List<int> slots, IReadOnlyList<Vector3> spots, Vector2 facing, bool queued, bool reform = false)
    {
        var table = world.Characters;
        int count = Math.Min(slots.Count, spots.Count);
        if (count == 0) return;
        facing = facing.LengthSquared() > 1e-6f ? Vector2.Normalize(facing) : Vector2.UnitY;
        var centre = Vector2.Zero;
        for (int k = 0; k < count; k++) centre += new Vector2(spots[k].X, spots[k].Z);
        centre /= count;
        // A formation given a new shape farther off marches there in its old one and spreads into the new one only at the line (Total War's march, then deploy).
        var approach = reform && !queued ? ApproachOf(table, slots, spots, count, centre, facing) : null;
        if (approach is { } a) spots = a.Spots;
        var group = new FormationGroup { Centre = centre, Facing = facing };
        var right = RightOf(facing);
        float yaw = MathF.Atan2(facing.X, facing.Y);
        bool march = count > 1;
        for (int k = 0; k < count; k++)
        {
            int s = slots[k];
            var cold = table.Cold(s)!;
            ref var n = ref table.Next[s];
            LeaveFormation(cold);
            cold.FollowOrder = CharacterId.None;
            var spot = spots[k];
            var flat = new Vector2(spot.X, spot.Z);
            var d = flat - centre;
            float from = 0;
            var marchTo = flat;
            if (queued && n.Task == (byte)CharacterTask.GoTo && (n.Flags & (ushort)MoveFlags.AnyPath) != 0)
                cold.OrderQueue.Add(flat);   // after the walk in hand, which now leads into the formation's
            else if (approach is { } block)
            {
                // To its place in the marching block, then onto its spot.
                var via = block.Via[k];
                GoToFloor(world, s, new Vector3(via.X, spot.Y, via.Y), cold.RunSpeed);
                cold.ArriveWithin = SpotArrive;
                cold.ArriveYaw = yaw;
                cold.OrderQueue.Add(flat);
                marchTo = via;
                from = Vector2.Distance(new Vector2(n.Position.X, n.Position.Z), via);
            }
            else
            {
                flat = GoToFloor(world, s, spot, cold.RunSpeed);
                cold.ArriveWithin = SpotArrive;   // onto the spot, not the move's arrival ring short of it
                cold.ArriveYaw = yaw;
                if (march) from = Vector2.Distance(new Vector2(n.Position.X, n.Position.Z), flat);
            }
            if ((cold.Orders & CharacterOrders.Hold) != 0) cold.HoldPoint = flat;
            cold.FormationWalk = true;
            cold.Formation = group;
            cold.FormationSpot = new Vector3(flat.X, spot.Y, flat.Y);
            cold.FormationYaw = yaw;
            group.Members.Add(table.IdOf(s));
            group.Offsets.Add(new Vector2(Vector2.Dot(d, right), Vector2.Dot(d, facing)));
            group.MarchFrom.Add(from);
            group.NextReturn.Add(0);
            group.MarchTo.Add(marchTo);
        }
        formations.Add(group);
    }

    /// <summary>
    /// For exactly the members of one formation sent to a new shape at least three spacings off: each one's place in its current block (its <see cref="FormationGroup.Offsets"/>) turned to face
    /// the way it goes and stopped with its front rank <see cref="Formation.Spacing"/> × 1.5 short of the new centre (<c>Via</c>), and the new spots handed out again so the block spreads without
    /// crossing over (both sorted along the new line). Null otherwise.
    /// </summary>
    static (Vector2[] Via, Vector3[] Spots)? ApproachOf(CharacterTable table, List<int> slots, IReadOnlyList<Vector3> spots, int count, Vector2 centre, Vector2 facing)
    {
        if (count < 2 || table.Cold(slots[0])!.Formation is not { } group) return null;
        int members = 0;
        foreach (var id in group.Members) if (table.TryResolveNext(id, out int m) && table.Cold(m)!.Formation == group) members++;
        if (members != count) return null;
        var offsets = new Vector2[count];
        var here = Vector2.Zero;
        for (int k = 0; k < count; k++)
        {
            int at = table.Cold(slots[k])!.Formation == group ? group.Members.IndexOf(table.IdOf(slots[k])) : -1;
            if (at < 0) return null;
            offsets[k] = group.Offsets[at];
            here += new Vector2(table.Next[slots[k]].Position.X, table.Next[slots[k]].Position.Z);
        }
        here /= count;
        var way = centre - here;
        if (way.LengthSquared() < 9 * Formation.Spacing * Formation.Spacing) return null;
        var ahead = Vector2.Normalize(way);
        var across = RightOf(ahead);
        float front = offsets.Max(o => o.Y);
        var stop = centre - ahead * (front + Formation.Spacing * 1.5f);
        var via = offsets.Select(o => stop + across * o.X + ahead * o.Y).ToArray();
        // Along the new line (and a little by depth), so the left of the block takes the left of the line.
        var right = RightOf(facing);
        float Key(Vector2 p) => Vector2.Dot(p, right) + Vector2.Dot(p, facing) * 1e-3f;
        var byVia = Enumerable.Range(0, count).OrderBy(k => Key(via[k])).ToArray();
        var bySpot = Enumerable.Range(0, count).OrderBy(k => Key(new Vector2(spots[k].X, spots[k].Z))).ToArray();
        var assigned = new Vector3[count];
        for (int r = 0; r < count; r++) assigned[byVia[r]] = spots[bySpot[r]];
        return (via, assigned);
    }

    /// <summary>
    /// The spots of a move order to <paramref name="target"/> given to exactly the members of one formation: its shape kept, centred on the target and turned to face the way
    /// from where it stands (kept as it is for a step shorter than <see cref="Formation.Spacing"/>); null for any other selection.
    /// </summary>
    static (List<Vector3> Spots, Vector2 Facing)? MarchOf(CharacterTable table, List<int> slots, Vector3 target)
    {
        if (slots.Count < 2 || table.Cold(slots[0])!.Formation is not { } group) return null;
        int members = 0;
        foreach (var id in group.Members) if (table.TryResolveNext(id, out int s) && table.Cold(s)!.Formation == group) members++;
        if (members != slots.Count) return null;
        var to = new Vector2(target.X, target.Z);
        var way = to - group.Centre;
        var facing = way.LengthSquared() > Formation.Spacing * Formation.Spacing ? Vector2.Normalize(way) : group.Facing;
        var right = RightOf(facing);
        var spots = new List<Vector3>(slots.Count);
        foreach (int s in slots)
        {
            int k = table.Cold(s)!.Formation == group ? group.Members.IndexOf(table.IdOf(s)) : -1;
            if (k < 0) return null;
            var o = group.Offsets[k];
            var at = to + right * o.X + facing * o.Y;
            spots.Add(new Vector3(at.X, target.Y, at.Y));
        }
        return (spots, facing);
    }

    /// <summary>
    /// Once a tick (serial, before the path requests): drops members that took another order or are gone; sends an idle member that is off its spot back to it (after a fight)
    /// and turns one on it to the formation's facing; keeps a march in step (<see cref="CharacterCold.MarchSpeed"/>).
    /// </summary>
    void KeepFormations(World world)
    {
        if (formations.Count == 0) return;
        var table = world.Characters;
        long tick = world.Tick;
        long returnTicks = Math.Max(1, (long)MathF.Round(FormationReturnSeconds / world.TickSeconds));
        var combat = world.SystemOf<Combat.CombatSystem>();
        for (int g = 0; g < formations.Count; g++)
        {
            var group = formations[g];
            // In a fight while any member fights (and a few seconds after): nobody goes back to his spot until it is over.
            foreach (var member in group.Members)
                if (table.TryResolveNext(member, out int m) && table.Cold(m) is { } mc && (mc.InCombat || table.Next[m].Task == (byte)CharacterTask.Attack))
                {
                    group.FightUntil = tick + (long)MathF.Round(FormationCalmSeconds / world.TickSeconds);
                    break;
                }
            group.Engaged = tick < group.FightUntil;
            var foes = group.Engaged && combat is not null ? Foes(world, combat, group) : null;
            for (int k = group.Members.Count - 1; k >= 0; k--)
            {
                var cold = table.TryResolveNext(group.Members[k], out int s) ? table.Cold(s) : null;
                if (cold is null || cold.Formation != group || cold.Medical is { Dead: true } || cold.IsImprisoned)
                {
                    if (cold?.Formation == group) LeaveFormation(cold);
                    group.RemoveAt(k);
                    continue;
                }
                ref var n = ref table.Next[s];
                var task = (CharacterTask)n.Task;
                // Standing, fighting, or on the formation's walk: still in it. Anything else is another order.
                if (task is not (CharacterTask.Idle or CharacterTask.Attack) && !(task == CharacterTask.GoTo && cold.FormationWalk))
                {
                    LeaveFormation(cold);
                    group.RemoveAt(k);
                    continue;
                }
                if (task != CharacterTask.GoTo && group.MarchFrom[k] > 0)
                {
                    group.MarchFrom[k] = 0;
                    cold.MarchSpeed = 0;
                }
                if (task != CharacterTask.Idle || cold.InCombat || cold.Medical is { Incapacitated: true } || !cold.CarriedBy.IsNone) continue;
                if (group.Engaged)
                {
                    // The fight is on: a free melee member takes the nearest enemy about the formation (a crossbowman shoots from its spot, Fighting.cs); none left near, he waits.
                    if (tick < group.NextReturn[k] || cold.Fighter is not { CanAttack: true } || cold.Fighter.Crossbow is not null || foes is not { Count: > 0 }) continue;
                    group.NextReturn[k] = tick + returnTicks;
                    int best = -1;
                    float bestD = FormationEngageRange * FormationEngageRange;
                    foreach (int f in foes)
                    {
                        float d = Vector2.DistanceSquared(new(n.Position.X, n.Position.Z), new(table.Next[f].Position.X, table.Next[f].Position.Z));
                        if (d < bestD) (best, bestD) = (f, d);
                    }
                    if (best >= 0) world.Defer(new Combat.AttackOrder([group.Members[k]], table.IdOf(best)) { Tick = tick + 1, Defensive = true });
                    continue;
                }
                var spot = new Vector2(cold.FormationSpot.X, cold.FormationSpot.Z);
                if (Vector2.DistanceSquared(new Vector2(n.Position.X, n.Position.Z), spot) > FormationReturnSlack * FormationReturnSlack)
                {
                    // Off its spot (it fought, or was pushed by those walking past): back into the formation.
                    if (tick < group.NextReturn[k]) continue;
                    group.NextReturn[k] = tick + returnTicks;
                    GoToFloor(world, s, cold.FormationSpot, cold.RunSpeed);
                    cold.ArriveWithin = SpotArrive;
                    cold.ArriveYaw = cold.FormationYaw;
                    cold.FormationWalk = true;
                }
                else if (MathF.Abs(MathF.IEEERemainder(n.Yaw - cold.FormationYaw, 2 * MathF.PI)) > 0.5f) n.Yaw = cold.FormationYaw;
            }
            March(table, group);
        }
        formations.RemoveAll(f => f.Members.Count == 0);
    }

    /// <summary>A formation is in a fight until none of its members has fought for this long (engine choice).</summary>
    public const float FormationCalmSeconds = 4;
    /// <summary>How far from a member an enemy may be for it to go after it in a fight (engine choice).</summary>
    public const float FormationEngageRange = 400;

    /// <summary>
    /// The enemies of a formation in a fight: awake characters a member fights, or who fight one of the player's, within <see cref="FormationEngageRange"/> of the formation's centre.
    /// </summary>
    static List<int> Foes(World world, Combat.CombatSystem combat, FormationGroup group)
    {
        var table = world.Characters;
        var foes = new List<int>();
        var targets = new HashSet<int>();
        foreach (var member in group.Members)
            if (table.TryResolveNext(member, out int m) && combat.StateOf(member).TargetSlot is >= 0 and var t) targets.Add(t);
        float range2 = FormationEngageRange * FormationEngageRange;
        for (int i = 0; i < table.HighWater; i++)
        {
            if (!table.Next[i].Alive || table.Cold(i) is not { IsPlayer: false, Medical.Incapacitated: false } c) continue;
            if (Vector2.DistanceSquared(new(table.Next[i].Position.X, table.Next[i].Position.Z), group.Centre) > range2) continue;
            bool fightsOurs = c.InCombat && combat.StateOf(table.IdOf(i)).TargetSlot is >= 0 and var their && table.Cold(their) is { IsPlayer: true };
            if (targets.Contains(i) || fightsOurs) foes.Add(i);
        }
        return foes;
    }

    /// <summary>
    /// Keeps a marching formation a block: each walks at the slowest marcher's speed scaled by its share of the longest way (so all arrive together and the shape goes over
    /// evenly), faster when behind the block's progress and slower when ahead (engine choice).
    /// </summary>
    static void March(CharacterTable table, FormationGroup group)
    {
        float slowest = float.MaxValue, longest = 0, total = 0;
        int walking = 0;
        for (int k = 0; k < group.Members.Count; k++)
        {
            if (group.MarchFrom[k] <= 0 || !table.TryResolveNext(group.Members[k], out int s)) continue;
            ref readonly var n = ref table.Next[s];
            var cold = table.Cold(s)!;
            if (Vector2.DistanceSquared(n.Goal, group.MarchTo[k]) > 1)
            {
                // Past its place in the block: spreading onto its spot at its own pace.
                group.MarchFrom[k] = 0;
                cold.MarchSpeed = 0;
                continue;
            }
            slowest = MathF.Min(slowest, MathF.Min(n.MaxSpeed, SpeedMode.Cap(cold.ModeOverride ?? n.Mode, n.WalkSpeed)));
            longest = MathF.Max(longest, group.MarchFrom[k]);
            total += Progress(n, group.MarchTo[k], group.MarchFrom[k]);
            walking++;
        }
        if (walking == 0) return;
        float block = total / walking;
        for (int k = 0; k < group.Members.Count; k++)
        {
            if (group.MarchFrom[k] <= 0 || !table.TryResolveNext(group.Members[k], out int s)) continue;
            var cold = table.Cold(s)!;
            float share = MathF.Max(MarchLeast, group.MarchFrom[k] / longest);
            float step = Math.Clamp(1 + (block - Progress(table.Next[s], group.MarchTo[k], group.MarchFrom[k])) * MarchCatchUp, 0.5f, 1.5f);
            cold.MarchSpeed = MathF.Max(0.5f, slowest * share * step);
        }

        static float Progress(in CharacterHot n, Vector2 to, float from) =>
            1 - Math.Clamp(Vector2.Distance(new Vector2(n.Position.X, n.Position.Z), to) / from, 0, 1);
    }
}
