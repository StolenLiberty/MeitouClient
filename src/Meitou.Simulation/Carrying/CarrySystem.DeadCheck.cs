using System.Numerics;

namespace Meitou.Simulation.Carrying;

// Dead Check (Meitou-only, docs/simulation.md "Carrying, rescue and looting"): the selection spreads over the living bodies on the ground about one and chops at each until it is dead.
public sealed partial class CarrySystem
{
    /// <summary>How far from the clicked body the bodies of a Dead Check order are taken (engine choice).</summary>
    public const float DeadCheckRadius = 400;
    /// <summary>The blow clip (the prisoner killers' chop, <see cref="Kidnap.CaptiveSystem.ChopClip"/>) and its blow point: one blow per clip, at 0.55 of it.</summary>
    const float DeadCheckBlowAt = 0.55f;

    /// <summary>The combat system whose landed-blow path (<see cref="Combat.CombatSystem.Execute"/>) the checks strike with.</summary>
    internal Combat.CombatSystem? Fights { get; set; }

    /// <summary>A body a dead check strikes: alive, on the ground (knocked out, or crawling: playing dead), not carried.</summary>
    public static bool CanBeChecked(CharacterCold c) => c is { Medical: { Dead: false } m, CarriedBy.IsNone: true } && (m.Unconscious || m.Crawling);

    /// <summary>
    /// <see cref="DeadCheckOrder"/>: the bodies (<see cref="CanBeChecked"/>, not the player's) within <see cref="DeadCheckRadius"/> of the target form one group; each checker able to fight joins it and
    /// takes the body with the fewest checkers, the nearest on a tie.
    /// </summary>
    void StartDeadCheck(World world, CharacterTable table, DeadCheckOrder order)
    {
        if (!table.TryResolveNext(order.Target, out int clicked)) return;
        var next = table.Next;
        var at = new Vector2(next[clicked].Position.X, next[clicked].Position.Z);
        var group = new DeadCheckGroup();
        for (int s = 0; s < table.HighWater; s++)
        {
            if (!next[s].Alive || table.Cold(s) is not { } body || !CanBeChecked(body) || PlayerSide(world, body)) continue;
            if (Vector2.DistanceSquared(at, new Vector2(next[s].Position.X, next[s].Position.Z)) <= DeadCheckRadius * DeadCheckRadius) group.Bodies.Add(table.IdOf(s));
        }
        if (group.Bodies.Count == 0)
        {
            world.Post("They're all dead already");
            return;
        }
        foreach (var id in Who(world, order.Checkers))
        {
            if (!table.TryResolveNext(id, out int s) || table.Cold(s) is not { IsPlayer: true, Medical.Incapacitated: false, Fighter: not null } cold || !cold.CarriedBy.IsNone) continue;
            LeaveFight(world, s);
            cold.LootAll = null;
            cold.DeadCheck = group;
            NextDeadCheck(world, table, s);
        }
    }

    /// <summary>Every <see cref="CarrySettings.AutoEveryTicks"/> ticks: a checker whose body is dead goes on to the next; one given another order leaves; the group reports once when all are dead.</summary>
    void KeepDeadCheck(World world, CharacterTable table)
    {
        for (int s = 0; s < table.HighWater; s++)
        {
            if (!table.Next[s].Alive || table.Cold(s) is not { DeadCheck: { } group } cold) continue;
            var task = (CharacterTask)table.Next[s].Task;
            if (task == CharacterTask.Loot) continue;
            if (task != CharacterTask.Idle || cold.Medical is { Incapacitated: true } || !cold.CarriedBy.IsNone)
            {
                (cold.DeadCheck, cold.ActionClip, cold.DeadCheckBlows) = (null, null, 0);
                continue;
            }
            if (cold.InCombat) continue;
            NextDeadCheck(world, table, s);
        }
    }

    /// <summary>Sends the checker to the living body of its group with the fewest checkers on it, the nearest on a tie; none left, it is done (and the group reports once).</summary>
    void NextDeadCheck(World world, CharacterTable table, int slot)
    {
        var cold = table.Cold(slot)!;
        var group = cold.DeadCheck!;
        var next = table.Next;
        var on = new Dictionary<int, int>();
        for (int s = 0; s < table.HighWater; s++)
            if (next[s].Alive && next[s].Task == (byte)CharacterTask.Loot && table.Cold(s) is { DeadCheck: not null } c && table.TryResolveNext(c.CarryTarget, out int b)) on[b] = on.GetValueOrDefault(b) + 1;
        var at = new Vector2(next[slot].Position.X, next[slot].Position.Z);
        int best = -1, bestOn = int.MaxValue;
        float best2 = float.MaxValue;
        for (int k = group.Bodies.Count - 1; k >= 0; k--)
        {
            if (!table.TryResolveNext(group.Bodies[k], out int b) || table.Cold(b) is not { } body || !CanBeChecked(body))
            {
                group.Bodies.RemoveAt(k);
                continue;
            }
            int count = on.GetValueOrDefault(b);
            float d2 = Vector2.DistanceSquared(at, new Vector2(next[b].Position.X, next[b].Position.Z));
            if (count < bestOn || count == bestOn && d2 < best2) (best, bestOn, best2) = (b, count, d2);
        }
        if (best < 0)
        {
            (cold.DeadCheck, cold.ActionClip, cold.DeadCheckBlows) = (null, null, 0);
            if (!group.Reported) { group.Reported = true; world.Post("Dead check done: they're all dead"); }
            return;
        }
        cold.DeadCheckBlows = 0;
        Begin(table, slot, table.IdOf(best), CharacterTask.Loot, CharacterTask.Idle);
    }

    /// <summary>At the body: facing it, the chop clip, one blow each clip (<see cref="Combat.CombatSystem.Execute"/>: no block, no dodge) until it dies; then the next body.</summary>
    void DeadCheckStep(World world, CharacterTable table, int i, int t, float dt)
    {
        var cold = table.Cold(i)!;
        var body = table.Cold(t)!;
        if (Fights is null || !CanBeChecked(body))
        {
            End(table, i);
            (cold.ActionClip, cold.DeadCheckBlows) = (null, 0);
            return;
        }
        var to = table.Next[t].Position - table.Next[i].Position;
        if (to.X * to.X + to.Z * to.Z > 1e-4f) table.Next[i].Yaw = MathF.Atan2(to.X, to.Z);
        if (cold.LiftTime < 0)
        {
            cold.LiftTime = 0;
            cold.ActionClip = Kidnap.CaptiveSystem.ChopClip;
        }
        cold.LiftTime += dt;
        float chop = MathF.Max(world.SystemOf<Kidnap.CaptiveSystem>()?.ChopSeconds ?? 1.5f, 0.1f);
        if (cold.LiftTime < (cold.DeadCheckBlows + DeadCheckBlowAt) * chop) return;
        cold.DeadCheckBlows++;
        bool died = Fights.Execute(world, i, t, cold.DeadCheckBlows);
        if (body.Medical is { Incapacitated: true }) MovementSystem.Stop(table, t);
        if (!died && !body.Medical!.Dead) return;
        End(table, i);
        (cold.ActionClip, cold.DeadCheckBlows) = (null, 0);
    }
}
