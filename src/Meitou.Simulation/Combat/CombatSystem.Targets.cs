using System.Numerics;

namespace Meitou.Simulation.Combat;

public sealed partial class CombatSystem
{
    /// <summary>How long a fighter keeps its target before it chooses again (CombatClassAI +0x2f0, docs/game/combat.md "Attack slots and circling"): 1 s, 0.3 s on Hold.</summary>
    public const float PickSeconds = 1, HoldPickSeconds = 0.3f;

    static int PickTicks(World world, CharacterCold cold) => Math.Max(1, (int)MathF.Round((Orders.Holding(cold) ? HoldPickSeconds : PickSeconds) / world.TickSeconds));

    /// <summary>The chooser's side of a target choice and the best candidate so far.</summary>
    struct Choice
    {
        public int Self, Current;
        public CharacterCold Cold;
        public bool Player, Hold, Prone, Animal;
        public Vector2 HoldPoint;
        public int Best;
        public float BestScore;
    }

    /// <summary>
    /// A fighter's target choice (<c>CombatClassAI::chooseAttackTarget</c>, scored by <see cref="TargetScore"/>): its current target (when <paramref name="keep"/>), the ones
    /// fighting it, and, unless it is Passive, the ones fighting its squad mates within <see cref="RetaliationSystem.JoinRange"/> (engine reading of the protectee's attackers:
    /// every mate counts). Every candidate fights someone of its side, so the leave-alone test holds. Reads the last tick's state only; -1 when none qualifies.
    /// </summary>
    int ChooseTarget(World world, int i, in CombatSlot s, CharacterCold cold, bool keep)
    {
        var table = world.Characters;
        var prev = table.Previous;
        bool hold = Orders.Holding(cold) && !s.Ranged;
        var q = new Choice
        {
            Self = i, Current = s.TargetSlot, Cold = cold, Player = cold.IsPlayer, Hold = hold, Prone = cold.Medical!.Crawling, Animal = cold.Fighter!.IsAnimal,
            HoldPoint = hold ? Orders.HoldPointOf(cold, prev[i].Position) : default, Best = -1, BestScore = float.NegativeInfinity,
        };
        // The current target first, so a tie keeps it (the caller's sense test; the others are sensed: an attacker is put in the senses of whom it attacks).
        if (keep) Consider(world, ref q, s.TargetSlot, false);
        int generation = prev[i].Generation;
        foreach (var (_, _, _, a) in index.AttackersOf(i)) if (read[a].TargetGeneration == generation) Consider(world, ref q, a, false);
        if (!Orders.Has(cold, CharacterOrders.Passive) && cold.SquadId >= 0 && world.Squads.Find(cold.SquadId) is { } squad)
        {
            foreach (var m in squad.Members)
            {
                int ms = m.Slot;
                if (ms == i || !table.IsAlive(m)) continue;
                float dx = prev[ms].Position.X - prev[i].Position.X, dz = prev[ms].Position.Z - prev[i].Position.Z;
                if (dx * dx + dz * dz > RetaliationSystem.JoinRange * RetaliationSystem.JoinRange) continue;
                foreach (var (_, _, _, a) in index.AttackersOf(ms))
                    if (read[a].TargetGeneration == m.Generation) Consider(world, ref q, a, read[ms].TargetSlot != a);
            }
        }
        return q.Best;
    }

    void Consider(World world, ref Choice q, int c, bool protect)
    {
        var table = world.Characters;
        var prev = table.Previous;
        int i = q.Self;
        if (c == i || (uint)c >= (uint)prev.Length || !prev[c].Alive || read[c].Down || table.Cold(c) is not { Medical.Incapacitated: false, IsImprisoned: false } tc) return;
        // One it found no path to lately is left out (AITaskSytem::isPathImpossible).
        if (PursuitSystem.PathImpossible(q.Cold, new CharacterId(c, prev[c].Generation), world.Tick)) return;
        // The current target is whoever the order named, of any faction.
        if (c != q.Current && (tc.Faction == q.Cold.Faction || q.Cold.SquadId >= 0 && tc.SquadId == q.Cold.SquadId)) return;
        ref readonly var o = ref read[c];
        if (q.Hold && !Orders.HoldPost(prev[i].Position, q.HoldPoint, prev[c].Position, prev[c].Velocity, false, o.TargetSlot == i)) return;
        int attackers = AttackerCount(world, c);
        float dx = prev[c].Position.X - prev[i].Position.X, dz = prev[c].Position.Z - prev[i].Position.Z;
        var candidate = new TargetCandidate
        {
            Current = c == q.Current,
            Staggered = (o.StumbleClip >= 0 || Reactions?.Reacting(world.Characters.IdOf(c)) == true) && world.Tick < o.StunUntil,
            Attackers = attackers,
            ChooserAttacks = c == q.Current,
            Taunt = Orders.Has(tc, CharacterOrders.Taunt),
            Passive = Orders.Has(tc, CharacterOrders.Passive),
            Protect = protect && attackers < 2,
            Distance = MathF.Sqrt(dx * dx + dz * dz),
            Fighting = o.TargetSlot >= 0,
            Gunner = tc.Gunning is not null,
            Crippled = tc.Medical.Crawling || tc.Fighter is not { CanAttack: true },
            Peaceful = o.TargetSlot < 0 && Relations is { } relations && !AggroSystem.IsEnemy(relations, tc.Faction, q.Cold.Faction),
            GettingUp = tc.GettingUpUntil > world.Tick,
        };
        if (TargetScore.Score(candidate, q.Player, q.Hold, q.Prone, q.Animal) is { } score && score > q.BestScore)
        {
            q.Best = c;
            q.BestScore = score;
        }
    }

    /// <summary>How many fight the character in <paramref name="slot"/> (standing fighters whose target it is), as of the last tick; any thread after Schedule.</summary>
    public int AttackerCount(World world, int slot)
    {
        int n = 0, generation = world.Characters.Previous[slot].Generation;
        foreach (var (_, _, _, a) in index.AttackersOf(slot)) if (read[a].TargetGeneration == generation && !read[a].Down) n++;
        return n;
    }

    /// <summary>
    /// Whether fighter <paramref name="i"/> senses <paramref name="c"/> (the chooser's sense test, engine reading of the sense entries): one attacking it always; else within its
    /// sight range (<see cref="SenseRange"/>, the race's multiplier and the weather, 500 indoors), and a sneaker only while in view (<see cref="SightMap.Sees"/> with its stealth).
    /// </summary>
    bool Senses(World world, int i, CharacterCold cold, int c)
    {
        var prev = world.Characters.Previous;
        if ((uint)c >= (uint)prev.Length || !prev[c].Alive || world.Characters.Cold(c) is not { } tc) return false;
        if (read[c].TargetSlot == i) return true;
        float outdoor = AggroSystem.RangeOf(cold, SenseRange);
        float range = world.Sight.MaxRange(i, outdoor);
        if (Vector3.DistanceSquared(prev[c].Position, prev[i].Position) > range * range) return false;
        return (tc.Orders & CharacterOrders.Sneak) == 0 || world.Sight.Sees(i, c, prev[i].Position, prev[i].Yaw, prev[c].Position, outdoor, Orders.Stealth(tc)) > 0;
    }

    /// <summary>The original's sense-age unit in seconds (hours x 3600 / 33 x 0.5, <see cref="AggroSystem.GiveUpSenseSeconds"/> = 12 of them).</summary>
    public const float SenseUnitSeconds = AggroSystem.GiveUpSenseSeconds / 12;
    /// <summary>How long a sense entry lasts unsensed, in sense units (<c>SensoryData::decay</c>): 30, 60 for some targets, 120 for an intruder in the home (<see cref="SenseLife"/>).</summary>
    public const int SenseLifeUnits = 30, SenseLifeLongUnits = 60, SenseLifeHomeUnits = 120;
    /// <summary>The long-term squad memory tags that keep an entry 60 units: intruder, stole from me, captor, killed my friend (mask 0x232).</summary>
    const uint LongSenseTags = 0x232;

    /// <summary>
    /// The chooser's sense test on its current target (a sense entry, docs/game/combat.md "Target selection"): it passes while the chooser <see cref="Senses"/> the target and
    /// for <see cref="SenseLife"/> after (an NPC's chase may give it up sooner, <see cref="AggroSystem"/>, 24 s unsensed).
    /// </summary>
    bool CurrentSensed(World world, int i, ref CombatSlot s, CharacterCold cold, int now)
    {
        if (Senses(world, i, cold, s.TargetSlot))
        {
            s.SensedTick = now;
            return true;
        }
        return (now - s.SensedTick) * world.TickSeconds <= SenseLife(world, i, cold, s.TargetSlot) * SenseUnitSeconds;
    }

    /// <summary>
    /// The lifetime of looker <paramref name="i"/>'s sense entry of <paramref name="t"/> in sense units (<c>SensoryData::decay</c> <c>FUN_140859010</c>, <b>Observed</b>): 120 when the
    /// looker's faction is real and <paramref name="t"/> is a player character inside the looker's home building; 60 when it is fighting, its squad remembers it as an intruder, thief,
    /// captor or friend killer, or it is the looker's faction's slave; else 30.
    /// </summary>
    int SenseLife(World world, int i, CharacterCold cold, int t)
    {
        if (world.Characters.Cold(t) is not { } tc) return SenseLifeUnits;
        var squad = cold.SquadId >= 0 ? world.Squads.Find(cold.SquadId) : null;
        bool real = Relations is not { } relations || !relations.Contains(cold.Faction) || !relations.Factions[cold.Faction].NotReal;
        if (real && world.Player.Faction >= 0 && tc.Faction == world.Player.Faction && squad?.HomeBuilding is { } home && world.Sight.BuildingOf(t) == home.Key) return SenseLifeHomeUnits;
        if (tc.InCombat || tc.SlaveState == 1 && tc.SlaveOwner == cold.Faction) return SenseLifeLongUnits;
        if (squad is not null && world.SystemOf<AggroSystem>()?.Memory?.Find(world, squad.Id, world.Characters.IdOf(t)) is { } memory && (memory.Long & LongSenseTags) != 0)
            return SenseLifeLongUnits;
        return SenseLifeUnits;
    }

    /// <summary>How far apart in height two characters inside one building may stand and still be on the same floor (engine reading of the floor test).</summary>
    public const float SameFloorHeight = 20;
    /// <summary>The height of the eyes above the feet for the alert's line test (engine choice, near <see cref="RangedTuning.BarrelHeight"/>).</summary>
    const float EyeHeight = 15;

    /// <summary>
    /// Whether attacker <paramref name="a"/> taking <paramref name="t"/> as its target alerts it (<c>AI::underMeleeAttack</c> from the targeting path, docs/game/combat.md
    /// "Fight lifecycle and orders"): always, unless the target is the player's, or the attacker is and the target is not aware of it (fighting it, or hit by it); then only
    /// when both stand in the same place (<see cref="SightMap.PlaceOf"/>), on the same floor (<see cref="SameFloorHeight"/>) and with a clear line between their eyes (terrain
    /// and buildings, <see cref="SolidHits"/>). A blow landed by the attacker alerts in any case (the hit path). Reads the last tick's state; any thread after Schedule.
    /// </summary>
    public bool Alerts(World world, int a, int t)
    {
        var table = world.Characters;
        if (table.Cold(a) is not { } ac || table.Cold(t) is not { } tc) return false;
        ref readonly var o = ref read[t];
        if (o.HitFrom == a) return true;
        if (!tc.IsPlayer && !(ac.IsPlayer && o.TargetSlot != a)) return true;
        var prev = table.Previous;
        if (world.Sight.PlaceOf(a) != world.Sight.PlaceOf(t)) return false;
        var from = prev[a].Position + new Vector3(0, EyeHeight, 0);
        var seg = prev[t].Position + new Vector3(0, EyeHeight, 0) - from;
        if (world.Sight.Inside(t) && MathF.Abs(seg.Y) > SameFloorHeight) return false;
        return SolidHits.Hit(world.Walkability, Buildings, from, seg, RangedTuning.TerrainStep, out _) < 0;
    }

    /// <summary>
    /// Whether <paramref name="c"/> counts as an enemy of <paramref name="me"/> for the steering's repulsion (its <c>isEnemy</c>, engine reading): of a hostile faction
    /// (another faction when there is no relation table), or fighting <paramref name="me"/>, or fought by it. Last tick's state; any thread after Schedule.
    /// </summary>
    public bool IsEnemyOf(World world, int c, CharacterCold oc, int me, int myFaction)
    {
        var prev = world.Characters.Previous;
        if (read[c].TargetSlot == me && read[c].TargetGeneration == prev[me].Generation) return true;
        if (read[me].TargetSlot == c && read[me].TargetGeneration == prev[c].Generation) return true;
        return Relations is { } relations ? AggroSystem.IsEnemy(relations, oc.Faction, myFaction) : oc.Faction != myFaction;
    }

    /// <summary>The spacing of the points the steering's line test stands on (engine choice: under a footprint, so a wall's gap of a body width is not skipped).</summary>
    const float SteerLineStep = 2;

    /// <summary>
    /// Whether fighter <paramref name="a"/> may steer at <paramref name="t"/> rather than walk a path (<c>CombatClassAI::vfunc_9</c>'s chase test, docs/game/combat.md
    /// "Combat stance"): the same place (<see cref="SightMap.PlaceOf"/>), within <see cref="SameFloorHeight"/> in height, and every <see cref="SteerLineStep"/> of the straight
    /// line between them walkable from the last (engine stand-in for the navmesh line query). Last tick's places; any thread after Sight's update.
    /// </summary>
    public bool SteersAt(World world, int a, int t)
    {
        if (world.Sight.PlaceOf(a) != world.Sight.PlaceOf(t)) return false;
        var prev = world.Characters.Previous;
        Vector3 from = prev[a].Position, to = prev[t].Position;
        if (MathF.Abs(to.Y - from.Y) > SameFloorHeight) return false;
        var leg = new Vector2(to.X - from.X, to.Z - from.Z);
        float length = leg.Length(), y = from.Y;
        for (float d = SteerLineStep; d < length; d += SteerLineStep)
            if (!world.Walkability.TryStand(from.X + leg.X * (d / length), from.Z + leg.Y * (d / length), y, out y)) return false;
        return true;
    }

    /// <summary>Turns a fighter on a new target: it asks its target again for a place and weighs its techniques afresh.</summary>
    static void Retarget(ref CombatSlot s, int target, int generation, int now)
    {
        s.SensedTick = now;
        s.TargetSlot = target;
        s.TargetGeneration = generation;
        s.Engaged = false;
        s.LastTech = -1;
        s.Picked = false;
    }
}
