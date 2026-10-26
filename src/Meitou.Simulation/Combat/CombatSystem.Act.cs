using System.Numerics;
using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Data.Gameplay.Combat;
using Meitou.Simulation.Bodies;

namespace Meitou.Simulation.Combat;

public sealed partial class CombatSystem
{
    // ------------------------------------------------------------------ act

    /// <summary>The guarding state: the fighter's own, or the Block order (its tooltip's +20 melee defence; docs/game/combat.md).</summary>
    static bool Guarding(CharacterCold c) => c.Fighter!.Guarding || Orders.Has(c, CharacterOrders.Block);

    /// <summary>
    /// A stat as the combat formulas read it: times the injury, hunger and light multiplier and the stats' read-time scale (an animal's age, docs/game/animals.md); crossbows and
    /// assassination also times the worn gear's factor (<see cref="Fighter.Factors"/>; the original's stat getter <c>FUN_140884ac0</c> asks for all of them). Dexterity's and the
    /// weapon skills' gear factors are applied by the formulas themselves (<see cref="Fighter.DexterityMult"/>, <see cref="Fighter.DamageOutput"/>).
    /// </summary>
    static float Eff(CharacterCold c, StatsEnumerated stat) => c.Stats![stat] * c.Stats.Scale * c.Medical!.StatMultiplier(stat) * GearFactor(c, stat);

    /// <summary>The worn gear's factor of a stat that <see cref="Eff"/> applies: <c>ranged skill mult</c> for crossbows, <c>asassination mult</c> for assassination, else 1.</summary>
    internal static float GearFactor(CharacterCold c, StatsEnumerated stat) => stat switch
    {
        StatsEnumerated.Crossbows => c.Fighter?.Factors.Ranged ?? 1,
        StatsEnumerated.Assassination => c.Fighter?.Factors.Assassin ?? 1,
        _ => 1,
    };

    /// <summary>
    /// The ticks a technique's clip takes at <c>anim speed mult</c> x <paramref name="rate"/> clip seconds per second; the clip's length is from the fighter's skeleton (an
    /// animal's own, <see cref="AnimationLengths.For"/>: <c>attack1</c> is 0.77 s on a bonedog and 5 s on a beak thing).
    /// </summary>
    int Ticks(CombatTechnique t, float rate, float dt, CharacterCold cold)
    {
        float clip = lengths.For(cold.Fighter is { IsAnimal: true } ? cold.Race?.StringId : null).Of(t.AnimName);
        if (clip <= 0) clip = CombatTuning.DefaultClipSeconds;
        return Math.Max(2, (int)MathF.Ceiling(clip / (MathF.Max(t.AnimSpeedMult, 0.1f) * MathF.Max(rate, 0.05f)) / dt));
    }

    /// <summary>
    /// The playback rate of a technique (docs/game/combat.md "Animation speed", <b>Verified</b>): attacks lerp(t, 0.8, 1.2) x G capped at 1.2, blocks lerp(t, 0.6, 4) x G
    /// capped at 4, with t = 0.01 (0.6 dexterity + 0.4 skill) (attack, or defence with the bonuses); dodges 1. G = (0.5 + 0.5 min(1, e + 0.5) (1 - f)) x the gear's
    /// <c>combat speed mult</c>, e the encumbrance factor and f the weapon-weight-over-strength factor of the strength XP.
    /// </summary>
    float PlayRate(CharacterCold cold, CombatTechnique tech, bool attack)
    {
        if (!attack && (tech.IsDodge || !tech.IsBlock)) return 1;
        return Rate(cold, attack);
    }

    /// <summary>The attack play rate of a fighter (its <see cref="PlayRate"/> for any attack; CharStats +0x188 in the original), which the chase lead also reads.</summary>
    public float AttackRate(CharacterCold cold) => Rate(cold, true);

    float Rate(CharacterCold cold, bool attack)
    {
        var fighter = cold.Fighter!;
        float weight = fighter.Unarmed ? 0 : fighter.Weapon!.Stats.Weight;
        float f = Math.Clamp(xp.WeaponWeightStrengthFactor(weight, cold.Stats![StatsEnumerated.Strength], cold.Medical!.StatMultiplier(StatsEnumerated.Strength)), 0, 1);
        float g = (0.5f + 0.5f * MathF.Min(1, fighter.EncumbranceFactor + 0.5f) * (1 - f)) * fighter.CombatSpeed;
        float dex = Eff(cold, StatsEnumerated.Dexterity) * fighter.DexterityMult;
        float skill = attack
            ? fighter.Unarmed ? Eff(cold, StatsEnumerated.MartialArts) : Eff(cold, StatsEnumerated.MeleeAttack) + fighter.Weapon!.Stats.AttackMod
            : Eff(cold, StatsEnumerated.MeleeDefence) + fighter.DefenceBonus + (fighter.Weapon?.Stats.DefenceMod ?? 0) + (Guarding(cold) ? 20 : 0);
        float t = 0.01f * (0.6f * dex + 0.4f * skill);
        return attack ? MathF.Min(1.2f, WeaponStats.Lerp(t, 0.8f, 1.2f) * g) : MathF.Min(4f, WeaponStats.Lerp(t, 0.6f, 4f) * g);
    }

    public void Act(World world, Partition part, EffectBuffer effects)
    {
        var table = world.Characters;
        var prev = table.Previous;
        var next = table.Next;
        int now = (int)world.Tick;
        float dt = world.TickSeconds;
        List<MedicalEvent>? events = null;
        for (int i = part.Start; i < part.End; i++)
        {
            if (!prev[i].Alive) continue;
            var cold = table.Cold(i)!;
            if (cold.Fighter is null || cold.Stats is null || cold.Medical is null || cold.Race is null) continue;
            ref var s = ref write[i];
            ulong key = Rng.Key(table.IdOf(i));

            // 1. The body: bleeding, healing, waking.
            if (Settings.TickMedical)
            {
                events ??= [];
                events.Clear();
                cold.Medical.Tick(dt, MakeContext(world, i, cold), events);
            }
            // The weapon in hand follows the arms (docs/game/combat.md "Arms"): losing one puts the weapon away and the fight goes on with what the arms still allow.
            if (cold.Fighter.SetArms(cold.Medical.ArmWorks(left: false), cold.Medical.ArmWorks(left: true)) && cold.InCombat && !s.Ranged) cold.DrawnWeapon = MeleeDrawn(cold.Fighter);
            // A player's character out of a fight keeps its weapon out while it walks or follows on an order; idle or at work it sheathes (Idleness and the jobs need it away).
            if (cold.IsPlayer && !cold.InCombat && cold.DrawnWeapon != WeaponKinds.None && next[i].Task is not ((byte)CharacterTask.GoTo or (byte)CharacterTask.Follow or (byte)CharacterTask.Attack))
                cold.DrawnWeapon = WeaponKinds.None;
            // Waking: it gets up for GetUpSeconds (Task_GetUp), less wanted as a target and left alone by a hit while its fight is off.
            if (s.Down && !cold.Medical.Incapacitated && GetUpSeconds > 0)
            {
                // It neither walks, works nor fights until it stands (Task_GetUp holds the character).
                cold.GettingUpUntil = now + (int)MathF.Ceiling(GetUpSeconds / dt);
                cold.StaggeredUntil = Math.Max(cold.StaggeredUntil, cold.GettingUpUntil);
            }
            s.Down = cold.Medical.Incapacitated;
            if (s.Down)
            {
                s.AttackTech = -1;
                s.NextBlow = 0;
                s.ReactTech = -1;
                s.TargetSlot = -1;
                s.Engaged = false;
                s.Picked = false;
                if (cold.InCombat) EndFight(table, i);
                continue;
            }
            if (now < cold.GettingUpUntil)
            {
                s.AttackTech = -1;
                s.NextBlow = 0;
                s.ReactTech = -1;
                continue;
            }

            // 2. Helpless in a hit reaction until its clip ends; every BreakOutSeconds of it the character may break out into a block against a blow coming at it
            // (the original's +0x124; its 10 % lower bound on the swing's progress is not modelled).
            if (now < s.StunUntil)
            {
                s.AttackTech = -1;
                s.NextBlow = 0;
                int every = Math.Max(1, (int)MathF.Round(CombatTuning.BreakOutSeconds / dt));
                bool chance = s.StumbleClip >= 0 && now > s.StumbleStart && (now - s.StumbleStart) % every == 0;
                if (s.ReactTech >= 0 && now >= s.ReactStart + s.ReactTicks) s.ReactTech = -1;
                if (chance && s.ReactTech < 0 && (s.TargetSlot >= 0 || cold.InCombat))
                {
                    React(world, i, ref s, cold, key, now, dt);
                    if (s.ReactTech >= 0 && !techniques[s.ReactTech].IsDodge) { s.StunUntil = now; s.StumbleClip = -1; }
                    else s.ReactTech = -1;
                }
                if (now < s.StunUntil) continue;
            }

            // 3. The target.
            // A target down or gone: the fighter chooses again at once (state 4, re-decide), and the fight ends only when nobody is left. While deciding, it chooses again
            // every PickSeconds, unless the target is the player's pick (a focused order returns its target without scoring).
            bool fighting = s.TargetSlot >= 0;
            if (fighting && !(s.TargetSlot < prev.Length && prev[s.TargetSlot].Alive && prev[s.TargetSlot].Generation == s.TargetGeneration && !read[s.TargetSlot].Down))
            {
                s.AttackTech = -1;
                s.NextBlow = 0;
                int pick = ChooseTarget(world, i, s, cold, keep: false);
                s.PickTick = now + PickTicks(world, cold);
                if (pick >= 0) Retarget(ref s, pick, prev[pick].Generation, now);
                else
                {
                    fighting = false;
                    s.TargetSlot = -1;
                    s.Picked = false;
                    EndFight(table, i);
                }
            }
            // A player's pick is kept, unless no path leads to it (the focused target is dropped while its path is impossible).
            else if (fighting && (!s.Picked || PursuitSystem.PathImpossible(cold, new CharacterId(s.TargetSlot, s.TargetGeneration), now)) && now >= s.PickTick && s.AttackTech < 0 && s.ReactTech < 0)
            {
                int pick = ChooseTarget(world, i, s, cold, keep: CurrentSensed(world, i, ref s, cold, now));
                s.PickTick = now + PickTicks(world, cold);
                if (pick >= 0 && pick != s.TargetSlot) Retarget(ref s, pick, prev[pick].Generation, now);
                else if (pick < 0)
                {
                    // Nobody qualifies, not even the current target (a holder's that left its area, a player's beyond 400): the chooser has no target and the fight ends.
                    fighting = false;
                    s.TargetSlot = -1;
                    EndFight(table, i);
                }
            }

            // 3b. A crossbowman fights with the crossbow or the melee weapon; with the crossbow the ranged controller runs instead of the melee attack (a blow aimed at it is still met).
            ChooseMode(world, i, ref s, cold, fighting);
            if (s.Ranged)
            {
                if (s.ReactTech >= 0 && now >= s.ReactStart + s.ReactTicks) s.ReactTech = -1;
                if (s.ReactTech < 0 && s.AttackTech < 0) React(world, i, ref s, cold, key, now, dt);
                TickRanged(world, i, ref s, cold, effects, now, dt);
                continue;
            }

            // 4. The reaction and the attack in progress. A block that ends leaves a short wait (a dodge goes straight back to deciding).
            if (s.ReactTech >= 0 && now >= s.ReactStart + s.ReactTicks)
            {
                if (!techniques[s.ReactTech].IsDodge) s.ReadyTick = Math.Max(s.ReadyTick, now + (int)MathF.Ceiling(CombatTuning.AfterBlockPause / dt));
                s.ReactTech = -1;
            }
            float incoming = Incoming(world, i, now, out int yielder);
            // One that lost the initiative roll to it: it turns on that one (FUN_140666a30 sets the winner to attack the asker), unless it fights the player's pick.
            if (yielder >= 0 && yielder != s.TargetSlot && !s.Ranged && !(fighting && s.Picked))
            {
                s.TargetSlot = yielder;
                s.TargetGeneration = prev[yielder].Generation;
                s.Engaged = false;
                s.Picked = false;
                fighting = true;
            }
            if (!fighting) s.Engaged = false;
            // The Block order, standing and let close in: every 1.5-4.5 s it frees the attack slots on itself, so others may start swinging at it (CircleStateAI's timer).
            if (fighting && s.Engaged && Orders.Has(cold, CharacterOrders.Block) && !cold.Medical.Crawling)
            {
                if (s.BlockTimerTick == 0) s.BlockTimerTick = now + BlockTimerTicks(world, key, now);
                else if (now >= s.BlockTimerTick)
                {
                    s.SlotsCleared = now;
                    s.BlockTimerTick = now + BlockTimerTicks(world, key, now);
                }
            }
            else s.BlockTimerTick = 0;
            bool aborted = false;
            if (s.AttackTech >= 0)
            {
                var tech = techniques[s.AttackTech];
                // Given up to defend: still in the first half of the swing while a blow at it is past its half (docs/game/combat.md "Pacing").
                // It goes straight to the defence; one that may not defend yet (after a failed roll) keeps swinging.
                if (incoming > CombatTuning.AbortIncomingProgress && (now - s.AttackStart) < CombatTuning.AbortOwnProgress * s.AttackTicks && now >= s.DefendReadyTick)
                {
                    s.AttackTech = -1;
                    s.NextBlow = 0;
                    aborted = true;
                }
                else if (s.NextBlow > 0 && fighting && now >= s.AttackStart + (int)MathF.Ceiling(tech.StrikeProgress(s.NextBlow) * s.AttackTicks))
                {
                    Strike(world, i, s, cold, tech, s.NextBlow, effects);
                    s.Thrown++;
                    int blow = s.NextBlow;
                    s.NextBlow = s.NextBlow < tech.Blows ? s.NextBlow + 1 : 0;
                    // Someone attacking it: the swing ends at this blow's stop frame and it decides again (FUN_14060a590).
                    if (incoming >= 0) Stop(ref s, tech, blow);
                }
            }
            if (s.AttackTech >= 0 && now >= s.AttackEnd)
            {
                s.AttackTech = -1;
                s.NextBlow = 0;
                float pause = s.LastOutcome switch
                {
                    BlowOutcome.Blocked => CombatTuning.BlockedPause,
                    BlowOutcome.Dodged => CombatTuning.DodgedPause,
                    BlowOutcome.Missed => CombatTuning.MissedPause,
                    _ => CombatTuning.HitPause,
                };
                s.ReadyTick = now + Math.Max(1, (int)MathF.Ceiling(pause / dt));
            }

            // 5. Deciding tries attacking first (asking the target, StartAttack); only when that fails does a fighter defend against the blows coming at it,
            // from anyone (docs/game/combat.md "Pacing"). Being attacked cuts the pause after an attack short; an opponent that lost the initiative to it
            // may be swung at at once.
            if (s.ReactTech < 0 && s.AttackTech < 0)
            {
                // Deciding, it first asks to be one of the fighters its target lets close in (FUN_140666e20); a fighter left out circles (PursuitSystem) and asks again each tick.
                if (fighting && !s.Engaged) s.Engaged = MayJoin(world, i, s.TargetSlot, now);
                bool yielded = fighting && read[s.TargetSlot].YieldTo == i && now < read[s.TargetSlot].YieldUntil;
                if (!aborted && fighting && (now >= s.ReadyTick || incoming >= 0 || yielded))
                {
                    StartAttack(world, i, ref s, cold, key, now, dt);
                    // A failed ask: with a blow coming it defends (below), else an engaged fighter goes to state 5 (FUN_14060c8b0); an attack ends it.
                    s.CircleState = s.Engaged && s.AttackTech < 0 && incoming < 0;
                }
                if (!s.Engaged) s.CircleState = false;
                // Only a fighter defends: a target whose fight is off is simply hit (the ask answers "attack", docs/game/combat.md "Fight lifecycle and orders").
                if (s.AttackTech < 0 && now >= s.DefendReadyTick && (fighting || cold.InCombat)) React(world, i, ref s, cold, key, now, dt);
            }
        }
    }

    void StartAttack(World world, int i, ref CombatSlot s, CharacterCold cold, ulong key, int now, float dt)
    {
        var table = world.Characters;
        var prev = table.Previous;
        int t = s.TargetSlot;
        var fighter = cold.Fighter!;
        if (!fighter.CanAttack || Orders.Has(cold, CharacterOrders.Block)) return;   // the Block order: defends, starts no attack
        if (!s.Engaged) return;   // left out by its target: it circles (PursuitSystem)
        // Dynamic movement: it swings only once it has turned to face the target.
        if (Dynamic && FacingOff(table.Previous, i, s.TargetSlot) > DynamicSwingCone) return;
        // No one but enemies within its body room (x1.2 in state 5): the ask fails (FUN_14060c670, FUN_1402b3180).
        if (NearestFriend(world, i, in s, cold) < BodyRoom(cold) * (s.CircleState ? CircleRoomFactor : 1)) return;
        var tc = table.Cold(t)!;
        var medical = cold.Medical!;
        float length = fighter.WeaponLength;
        // The gap to where the target will be (a target that is not fighting is led by LeadSeconds, FUN_1402b4570).
        var aim = prev[t].Position + (tc.InCombat ? Vector3.Zero : prev[t].Velocity * CombatTuning.AimLeadSeconds);
        float e = fighter.EncumbranceFactor;
        var query = new AttackQuery
        {
            Category = fighter.TechniqueCategory,
            Skill = cold.Stats![fighter.WeaponSkill],
            UnarmedSkill = WeaponStats.Lerp(e, 0.2f, 1) * (cold.Stats[StatsEnumerated.MartialArts] + fighter.UnarmedBonus),
            Animal = fighter.IsAnimal,
            Prone = medical.Crawling,
            Encumbrance = e,
            WeaponLength = length,
            Scale = medical.ScaleK,
            Gap = CombatTuning.Gap(prev[i].Position, aim, cold, tc),
            Previous = s.LastTech,
            TargetStaggered = now < read[t].StunUntil,
            LeftArm = medical.ArmWorks(left: true),
            RightArm = medical.ArmWorks(left: false),
            LegDown = LegDown(medical),
        };
        float roll = CombatRolls.Float(world.Seed, key, CombatRoll.Technique, (ulong)(s.AttackSeq + 1));
        int pick = TechniqueChooser.ChooseAttack(techniques, query, roll, out s.Reach);
        if (pick < 0) return;
        // In reach with a technique: ask the target (and hold an attack slot).
        if (!HasSlot(world, i, t, tc, now) || !HasInitiative(world, i, ref s, t, cold, tc, now)) return;
        var tech = techniques[pick];
        s.AttackSeq++;
        s.AttackTech = pick;
        s.LastTech = pick;
        s.AttackStart = now;
        s.AttackTicks = Ticks(tech, PlayRate(cold, tech, attack: true), dt, cold);
        s.AttackEnd = now + Math.Max(1, (int)MathF.Ceiling(tech.EndProgress * s.AttackTicks));
        s.NextBlow = 1;
        s.LastOutcome = BlowOutcome.Missed;
        s.AttackFrom = prev[i].Position;
        // Less the dust's "Weather penalty" (FUN_140643a30, docs/formats/weather.md "Effects on characters").
        s.AttackSkill = Eff(cold, fighter.Unarmed ? StatsEnumerated.MartialArts : StatsEnumerated.MeleeAttack) + (fighter.Unarmed ? fighter.UnarmedBonus : fighter.AttackBonus + (fighter.Weapon?.Stats.AttackMod ?? 0))
            - cold.Medical!.DustAttackPenalty;
    }

    /// <summary>
    /// Whether <paramref name="i"/> may swing at <paramref name="t"/> now, asking the target first (<b>Verified</b>, FUN_14060c670 / FUN_140666a30, docs/game/combat.md
    /// "Pacing"): yes when the target lost the initiative to it; not while the target is swinging at it (it defends instead); yes while the target is staggered
    /// or not fighting it; otherwise an initiative roll on both attack skills (the skill roll with <c>attack chance factor</c>, 50/50 at equal skill),
    /// drawn from the pair and tick so both sides agree. The loser yields: the winner may swing at it at once (the original sets the winner to attack).
    /// </summary>
    bool HasInitiative(World world, int i, ref CombatSlot s, int t, CharacterCold cold, CharacterCold tc, int now)
    {
        ref readonly var target = ref read[t];
        var prev = world.Characters.Previous;
        bool atMe = target.TargetSlot == i && target.TargetGeneration == prev[i].Generation;
        if (target.YieldTo == i && now < target.YieldUntil) return true;
        if (target.AttackTech >= 0 && atMe) return false;   // it is swinging at me: I defend (FUN_140666a30)
        if (Open(t, tc, now)) return true;
        if (s.YieldTo == t && now < s.YieldUntil) return false;
        // The target is free (deciding or in its pause): one draw for the pair and tick, the lower slot's view.
        int lo = Math.Min(i, t), hi = Math.Max(i, t);
        float skillI = Eff(cold, cold.Fighter!.Unarmed ? StatsEnumerated.MartialArts : StatsEnumerated.MeleeAttack);
        float skillT = Eff(tc, tc.Fighter!.Unarmed ? StatsEnumerated.MartialArts : StatsEnumerated.MeleeAttack);
        float lowSkill = lo == i ? skillI : skillT, highSkill = lo == i ? skillT : skillI;
        float a = 1 + combat.AttackChanceFactor * MathF.Max(0, highSkill - lowSkill), b = 1 + combat.AttackChanceFactor * MathF.Max(0, lowSkill - highSkill);
        float k = b / a, lowWins = k >= 1 ? 1 - 1 / (2 * k) : k / 2;
        ulong pair = ((ulong)(uint)lo << 32) | (uint)hi;
        float roll = CombatRolls.Float(world.Seed, pair, CombatRoll.Initiative, (ulong)(uint)now);
        if ((roll < lowWins) == (i == lo)) return true;
        // A winner still blocking or dodging can turn on it only when that ends, so the yield runs from there (engine choice).
        int from = target.ReactTech >= 0 ? Math.Max(now, target.ReactStart + target.ReactTicks) : now;
        s.YieldTo = t;
        s.YieldUntil = from + Math.Max(1, (int)MathF.Ceiling(CombatTuning.YieldSeconds / world.TickSeconds));
        return false;
    }

    /// <summary>
    /// Whether <paramref name="t"/> would not swing back (staggered, down, not fighting, the Block order; the ask has no Passive case), so it cannot take the initiative. A fighter that is
    /// blocking or dodging is not open: once its defence has started (+0x144) the original's answer rolls the initiative (FUN_140666a30).
    /// </summary>
    bool Open(int t, CharacterCold tc, int now)
    {
        ref readonly var target = ref read[t];
        return now < target.StunUntil || target.Down || target.TargetSlot < 0 || tc.Fighter is not { CanAttack: true }
            || Orders.Has(tc, CharacterOrders.Block);
    }

    /// <summary>
    /// How far through the most advanced attack aimed at <paramref name="i"/> is (0 to 1, from the last tick's state; -1 when nobody is swinging at it), and the
    /// lowest slot that lost the initiative roll to it and still yields (<paramref name="yielder"/>, -1: none).
    /// </summary>
    float Incoming(World world, int i, int now, out int yielder)
    {
        var prev = world.Characters.Previous;
        // Its neighbours within ReactionScanRadius (the grid's cells round it) that yield to it or swing at it: the index's lists, kept to those cells.
        var range = world.Grid.Range(prev[i].Position.X, prev[i].Position.Z, CombatTuning.ReactionScanRadius);
        int generation = prev[i].Generation;
        yielder = -1;
        foreach (var (_, other) in index.YieldingTo(i))
        {
            if (other == i || !range.Holds(index.CellOf(other))) continue;
            ref readonly var a = ref read[other];
            if (now < a.YieldUntil && !a.Down && (yielder < 0 || other < yielder)) yielder = other;
        }
        float most = -1;
        foreach (var (_, x, z, other) in index.AttackersOf(i))
        {
            if (other == i || !range.Holds((x, z))) continue;
            ref readonly var a = ref read[other];
            if (a.AttackTech < 0 || a.NextBlow == 0 || a.TargetGeneration != generation || a.AttackTicks <= 0) continue;
            most = MathF.Max(most, Math.Clamp((now - a.AttackStart) / (float)a.AttackTicks, 0, 1));
        }
        return most;
    }

    /// <summary>Any leg part is down (flesh - stun + rig &lt; 0): the original's two leg parts at medical +0x4d8 / +0x4e0 in the kick factor (<c>FUN_140886030</c>).</summary>
    static bool LegDown(MedicalState medical)
    {
        foreach (var p in medical.Parts) if (p.IsLeg && p.IsDown) return true;
        return false;
    }

    /// <summary>
    /// Whether <paramref name="i"/> gets one of its target's attack slots for this swing (docs/game/combat.md "Attack slots and circling", <c>FUN_140666310</c> / <c>FUN_1406661d0</c>):
    /// <see cref="AttackSlots"/> plus the target race's <c>extra attack slots</c> (1 to 5), only 1 while the target swings itself. A slot is held from the grant to the swing's last
    /// blow (<c>FUN_14060a590</c> gives it back), so the holders are the fighters at the target whose swing has a blow to come. The original grants in its update order; here
    /// the other fighters that may ask on the same tick and have a lower slot go first (engine choice). Their asks are foreseen from the last tick's state only, as Act gates
    /// them (engaged, deciding now, out of the pause or attacked, within their reach), erring towards counting one that then does not ask, so the count is never exceeded.
    /// </summary>
    bool HasSlot(World world, int i, int t, CharacterCold tc, int now)
    {
        int slots = read[t].AttackTech >= 0 ? 1 : Math.Clamp(AttackSlots + (tc.Race?.ExtraAttackSlots ?? 0), 1, MaxAttackSlots);
        var table = world.Characters;
        var prev = table.Previous;
        // The fighters at the target within EngageScanRadius of it (the index's list, kept to the grid's cells round it).
        var range = world.Grid.Range(prev[t].Position.X, prev[t].Position.Z, CombatTuning.EngageScanRadius);
        int generation = prev[t].Generation, holders = 0, ahead = 0;
        var at = prev[t].Position;
        bool open = Open(t, tc, now);
        foreach (var (_, x, z, other) in index.AttackersOf(t))
        {
            if (other == i || !range.Holds((x, z))) continue;
            ref readonly var o = ref read[other];
            if (o.TargetGeneration != generation || o.Down || o.Ranged) continue;
            // A swing holds its slot unless the target (a blocker) freed its slots after the swing started.
            if (o.AttackTech >= 0 && o.NextBlow > 0 && o.AttackStart >= read[t].SlotsCleared)
            {
                if (++holders + ahead >= slots) return false;
                continue;
            }
            if (other > i || now < o.StunUntil || !o.Engaged && !MayJoin(world, other, t, now)) continue;
            // It decides this tick when its swing or reaction ends now (Act's steps 4 and 5): a block's end and a swing's end start a pause (cut short by a blow at it).
            int ready = o.ReadyTick;
            if (o.AttackTech >= 0)
            {
                if (now < o.AttackEnd) continue;
                ready = int.MaxValue;
            }
            else if (o.ReactTech >= 0)
            {
                if (now < o.ReactStart + o.ReactTicks) continue;
                if (!techniques[o.ReactTech].IsDodge) ready = Math.Max(ready, now + (int)MathF.Ceiling(CombatTuning.AfterBlockPause / world.TickSeconds));
            }
            if (now < ready && !(read[t].YieldTo == other && now < read[t].YieldUntil) && Incoming(world, other, now, out _) < 0) continue;
            // A yielder asks in vain unless the target is open (HasInitiative).
            if (o.YieldTo == t && now < o.YieldUntil && !open) continue;
            // Its reach is known from its last ask (StartAttack); one that has never asked counts.
            var c = table.Cold(other);
            if (c is null || o.Reach > 0 && CombatTuning.Gap(prev[other].Position, at, c, tc) > o.Reach) continue;
            if (holders + ++ahead >= slots) return false;
        }
        return holders + ahead < slots;
    }

    /// <summary>The Block order's slot timer: uniform 1.5 to 4.5 s (<b>Verified</b> literals, FUN_1402b3180).</summary>
    static int BlockTimerTicks(World world, ulong key, int now) =>
        Math.Max(1, (int)MathF.Ceiling((1.5f + 3 * CombatRolls.Float(world.Seed, key, CombatRoll.BlockTimer, (ulong)(uint)now)) / world.TickSeconds));

    /// <summary>The most attack slots a target can have (the slot count is clamped to 1 to 5, <c>FUN_1406661d0</c>).</summary>
    public const int MaxAttackSlots = 5;
    /// <summary>The most fighters a target lets close in at once (<c>FUN_140666e20</c> adds an asker while it has at most four).</summary>
    public const int MaxEngaged = 5;

    /// <summary>
    /// Whether <paramref name="i"/> may join the fighters its target lets close in (<see cref="MaxEngaged"/>, <c>FUN_140666e20</c>): it joins while fewer are in. The ones that are
    /// not in yet ask whenever they decide; those with a lower slot ask first (engine choice for the original's update order), counted only when they may decide this tick
    /// (not staggered, blocking or dodging, or swinging past the point where it could still give the swing up), so a fighter is not left out for one that does not ask.
    /// </summary>
    bool MayJoin(World world, int i, int t, int now)
    {
        var prev = world.Characters.Previous;
        var range = world.Grid.Range(prev[t].Position.X, prev[t].Position.Z, CombatTuning.EngageScanRadius);
        int generation = prev[t].Generation, count = 0;
        foreach (var (_, x, z, other) in index.AttackersOf(t))
        {
            if (other == i || !range.Holds((x, z))) continue;
            ref readonly var o = ref read[other];
            if (o.TargetGeneration != generation || o.Down || o.Ranged) continue;
            if (!o.Engaged && (other > i || !MayDecide(in o, now))) continue;
            if (++count >= MaxEngaged) return false;
        }
        return count < MaxEngaged;
    }

    /// <summary>The room a fighter needs round it to ask: its race's <c>pathfind footprint radius</c> + 2.5 (CombatClass +0x280, <b>Observed</b>, constructor <c>FUN_14060a9e0</c>).</summary>
    internal static float BodyRoom(CharacterCold cold) => CombatTuning.Footprint(cold) + 2.5f;
    /// <summary>State 5's stricter room (<c>CircleStateAI</c> <c>FUN_1402b3180</c>).</summary>
    public const float CircleRoomFactor = 1.2f;
    /// <summary>How far above or below a body still counts (the flocking list's height test, <c>FUN_14085a070</c>).</summary>
    const float FlockHeight = 20;

    struct FriendScan
    {
        public CombatSystem Self;
        public World World;
        public int Me, Faction, Target;
        public Vector3 At;
        public float Nearest;
    }

    /// <summary>
    /// The centre distance to the nearest body round fighter <paramref name="i"/> that is not an enemy (<c>FlockingTools::getDistanceToClosestCharacter</c> over the
    /// flocking list, <b>Observed</b>, docs/game/combat.md "Attack slots and circling"): standing characters within <see cref="FlockHeight"/> in height, an enemy being one of a
    /// hostile faction, its target, or one fighting it (engine reading of <c>isEnemy</c>); 99 when none is near. A carried body is not counted (engine choice). Any thread after Schedule.
    /// </summary>
    float NearestFriend(World world, int i, in CombatSlot s, CharacterCold cold)
    {
        var prev = world.Characters.Previous;
        var scan = new FriendScan { Self = this, World = world, Me = i, Faction = cold.Faction, Target = s.TargetSlot, At = prev[i].Position, Nearest = 99 };
        float radius = BodyRoom(cold) * CircleRoomFactor;
        world.Grid.Query(scan.At.X, scan.At.Z, radius, ref scan, static (int c, ref FriendScan q) =>
        {
            if (c == q.Me || c == q.Target) return;
            var table = q.World.Characters;
            ref readonly var p = ref table.Previous[c];
            if (!p.Alive || MathF.Abs(p.Position.Y - q.At.Y) >= FlockHeight) return;
            if (table.Cold(c) is not { } oc || CombatSteering.OutOfFlock(p, oc)) return;
            ref readonly var o = ref q.Self.read[c];
            if (o.TargetSlot == q.Me && o.TargetGeneration == table.Previous[q.Me].Generation) return;
            bool enemy = q.Self.Relations is { } relations ? AggroSystem.IsEnemy(relations, q.Faction, oc.Faction) : oc.Faction != q.Faction;
            if (enemy) return;
            float d = Vector3.Distance(p.Position, q.At);
            if (d < q.Nearest) q.Nearest = d;
        });
        return scan.Nearest;
    }

    /// <summary>Whether a fighter whose last tick's state is <paramref name="o"/> may reach Act's deciding step this tick (its swing or defence ends now, or a blow at it may cut a swing short).</summary>
    static bool MayDecide(in CombatSlot o, int now) =>
        now >= o.StunUntil && (o.ReactTech < 0 || now >= o.ReactStart + o.ReactTicks)
        && (o.AttackTech < 0 || now >= o.AttackEnd || now - o.AttackStart < CombatTuning.AbortOwnProgress * o.AttackTicks);

    readonly AttackerIndex index = new();

    /// <summary>
    /// Lists who is after whom from the combat state as it stands (<see cref="AttackerIndex"/>), for <see cref="FightersAt"/> and the act's checks; serial phases only. Combat's
    /// schedule builds it for the act; a system asking <see cref="FightersAt"/> in its own schedule before combat's builds it first.
    /// </summary>
    public void IndexAttackers(World world)
    {
        index.Build(read, world.Characters, world.Grid);
        // FightersAt's counts, once per target: asked per attacker, a crowd round one target would walk its list once per member.
        int n = world.Characters.HighWater;
        if (fightersAt.Length < n) fightersAt = new int[Math.Max(n, fightersAt.Length * 2)];
        for (int t = 0; t < n; t++) fightersAt[t] = index.AttackersOf(t).IsEmpty ? 0 : CountFightersAt(world, t);
    }

    int[] fightersAt = [];

    /// <summary>The melee fighters (engaged or circling) at <paramref name="t"/>, as of the last tick: the original's engaged count plus the askers it turned away (<c>FUN_140666e20</c>).
    /// After <see cref="IndexAttackers"/>.</summary>
    public int FightersAt(World world, int t) => (uint)t < (uint)world.Characters.HighWater && t < fightersAt.Length ? fightersAt[t] : 0;

    int CountFightersAt(World world, int t)
    {
        var prev = world.Characters.Previous;
        var range = world.Grid.Range(prev[t].Position.X, prev[t].Position.Z, CombatTuning.EngageScanRadius);
        int generation = prev[t].Generation, count = 0;
        foreach (var (_, x, z, other) in index.AttackersOf(t))
        {
            if (!range.Holds((x, z))) continue;
            ref readonly var o = ref read[other];
            if (o.TargetGeneration == generation && !o.Down && !o.Ranged) count++;
        }
        return count;
    }

    void React(World world, int i, ref CombatSlot s, CharacterCold cold, ulong key, int now, float dt)
    {
        var table = world.Characters;
        var prev = table.Previous;
        // The blow at it that lands first, from those swinging at it within ReactionScanRadius (the index's list, kept to the grid's cells round it, in the grid's order:
        // the first of a tie wins); not the one it has reacted to already.
        var range = world.Grid.Range(prev[i].Position.X, prev[i].Position.Z, CombatTuning.ReactionScanRadius);
        int generation = prev[i].Generation, attacker = -1, blow = 0, first = int.MaxValue;
        foreach (var (_, x, z, other) in index.AttackersOf(i))
        {
            if (other == i || !range.Holds((x, z))) continue;
            ref readonly var a = ref read[other];
            if (a.AttackTech < 0 || a.NextBlow == 0 || a.TargetGeneration != generation) continue;
            int lands = a.AttackStart + (int)MathF.Ceiling(techniques[a.AttackTech].StrikeProgress(a.NextBlow) * a.AttackTicks);
            if (lands <= now) continue;
            if (s.ReactedSlot == other && s.ReactedSeq == a.AttackSeq && s.ReactedBlow == a.NextBlow) continue;
            if (lands < first) { first = lands; attacker = other; blow = a.NextBlow; }
        }
        if (attacker < 0) return;

        var attack = read[attacker];
        var atkTech = techniques[attack.AttackTech];
        int direction = atkTech.DirectionOf(blow);
        s.ReactedSlot = attacker;
        s.ReactedSeq = attack.AttackSeq;
        s.ReactedBlow = blow;
        // Dynamic movement: a blow from behind is not seen coming, so it is neither blocked nor dodged.
        if (Dynamic && FacingOff(prev, i, attacker) > DynamicGuardCone) return;

        var fighter = cold.Fighter!;
        float meleeDefence = Eff(cold, StatsEnumerated.MeleeDefence);
        ulong counter = CombatRolls.BlowCounter(attacker, attack.AttackSeq, blow, 0);
        float chanceRoll = CombatRolls.Float(world.Seed, key, CombatRoll.Block, counter);
        float pickRoll = CombatRolls.Float(world.Seed, key, CombatRoll.Reaction, counter);
        float block, dodge = 0;
        if (fighter.CanBlock)
        {
            float d = DefenceFormulas.Defence(meleeDefence, fighter.Weapon!.Stats.DefenceMod, true, fighter.DefenceBonus, Guarding(cold));
            block = DefenceFormulas.BlockChance(d, attack.AttackSkill, combat);
        }
        else
        {
            block = 0;
            float effective = DefenceFormulas.EffectiveDefence(Eff(cold, StatsEnumerated.MartialArts), fighter.EncumbranceFactor, Speed.LegFactor(cold.Medical!), fighter.DefenceBonus, Guarding(cold));
            dodge = DefenceFormulas.BlockChance(effective, attack.AttackSkill, combat, 0, 95);
        }
        int pick = TechniqueChooser.ChooseReaction(techniques, fighter.TechniqueCategory, cold.Medical!.Crawling, fighter.CanBlock, direction, block, dodge, chanceRoll, pickRoll);
        if (pick < 0)
        {
            // No defence (a failed dodge roll, or nothing fits): none is tried again for a while (the original's +0x14c).
            s.DefendReadyTick = now + (int)MathF.Ceiling(CombatTuning.NoDefenceSeconds / dt);
            return;
        }
        var reaction = techniques[pick];
        int ticks = Ticks(reaction, PlayRate(cold, reaction, attack: false), dt, cold);
        // The reaction is timed so that it is at its "blocked frame" when the blow arrives; with less lead time it starts at once and is less far along,
        // With LateDefences (Meitou balance choice, docs/simulation.md "Combat") never too little for the roll it passed to count: a late one joins its clip partway.
        int strike = first;
        int start = Math.Max(now, strike - (int)MathF.Round(reaction.StrikeProgress(1) * ticks));
        if (LateDefences)
        {
            float least = reaction.IsBlock ? HitOutcomes.BlockProgress : HitOutcomes.DodgeFrom;
            start = Math.Min(start, strike - (int)MathF.Ceiling(least * ticks) - 1);
        }
        s.ReactTech = pick;
        s.ReactStart = start;
        s.ReactTicks = ticks;
        s.LastTech = -1;   // the original's current technique (+0x150) is now a defence, which the next attack choice does not count as its last

    }
}
