using System.Numerics;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Ai;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Items;
using Meitou.Simulation.Locks;

namespace Meitou.Simulation.FirstAid;

/// <summary>
/// First aid and resting in play (docs/simulation.md "Bodies", docs/game/character-stats.md "First aid, medkits and robot repair" and "Sleep, beds and resting").
/// <list type="bullet">
/// <item><b>Treat</b> (<see cref="CharacterTask.Treat"/>): <see cref="TreatOrder"/> (the player), the medic job and the NPC rule start it. <c>Schedule</c> walks the medic to the patient
/// (the pursuit system's pattern: a path at free speed, asked again when the patient has moved); <c>Act</c> (own slot, read-only on the patient) emits one effect per tick while the medic
/// is in reach, awake, not fighting and still, and the patient is still or down; <c>Apply</c> (serial commit, in target / source order) picks the kit, bandages the patient, drains the kit
/// and trains the medic. So nothing on another character is written in a parallel phase.</item>
/// <item><b>Rate</b> (<b>Observed</b>, character-stats.md): <c>lerp(1, 15, min(skill, kit quality) x 0.01) x medic speed mult</c> (x <c>robot medic speed mult</c> for a robot kit) per unit of <c>dt</c> on the first part with room (the next only once it fills), after any open wound is closed;
/// the kit's <c>charges</c> fall by <c>lerp(medkit drain 99, medkit drain 1, (100 - skill) / 100) x dt</c> per wound step or part bandaged (bandaging drains only the player's kits; character-stats.md "First aid update"). The skill is the medic's <c>medic</c> (first aid kit) or <c>robotics</c> (robot kit) times
/// the stat multiplier of injuries and hunger. <c>dt</c> is <b>game seconds</b> (<b>engine choice</b>, the unit is <b>Unknown</b>).</item>
/// <item><b>Auto</b> (<c>SlowWorld</c>, every <see cref="FirstAidSettings.AutoEveryTicks"/> ticks, serial in slot order): an idle, wandering or following character with a kit treats the most
/// hurt squad mate within reach (knocked-out ones first), when no one in its squad is fighting: NPCs (engine stand-in until stage 9). The player's characters run their medic, robotics and
/// splint jobs instead, with the original's finders (<see cref="RunJobs"/>).</item>
/// <item><b>Rest</b> (<see cref="CharacterTask.Rest"/>): <see cref="RestOrder"/>; <see cref="BodySystem"/> reads the task as <see cref="MedicalContext.Resting"/>. Lying in a bed is
/// <see cref="Locks.BedSystem"/>'s (<see cref="CharacterTask.Sleep"/>); <see cref="BodySystem"/> gives it the bed's healing.</item>
/// </list>
/// Placed after the movement system (a later move order replaces a treatment, since it sets the task) and before the body system.
/// </summary>
public sealed class FirstAidSystem(GameConstants constants, BodyOptions options, ItemFactory items, FirstAidSettings? settings = null) : ITickSystem
{
    /// <summary>The effect kind: one tick of treatment from the source (medic) on the target (patient).</summary>
    public const int EffectTreat = 1;
    /// <summary>Untreated damage (HP) under which a treatment in progress counts as finished.</summary>
    const float Done = 1e-3f;
    /// <summary>The kit quality closing a wound without a kit (<c>FUN_14064d800</c>).</summary>
    const float KitlessQuality = 5;

    public FirstAidSettings Settings { get; } = settings ?? new FirstAidSettings();
    public GameConstants Constants { get; } = constants;
    public ItemFactory Items { get; } = items;
    /// <summary>The relation event of bandaging an unconscious patient of another faction (<see cref="FactionDeeds.FirstAid"/>); null: none.</summary>
    public FactionDeeds? Deeds { get; set; }
    readonly XpService xp = new(constants, options);
    readonly BodyOptions bodyOptions = options;

    // ------------------------------------------------------------------ orders

    public void Inputs(World world, IReadOnlyList<SimCommand> commands)
    {
        var table = world.Characters;
        foreach (var command in commands)
        {
            switch (command)
            {
                case TreatOrder order:
                    if (!table.TryResolveNext(order.Patient, out int patient) || table.Cold(patient)!.Medical is not { Dead: false }) break;
                    foreach (var id in Who(world, order.Medics))
                        if (table.TryResolveNext(id, out int slot) && table.Cold(slot) is { Race: not null, Stats: not null } cold && CanAct(cold))
                        {
                            if (FindKit(world, slot, cold, ItemInfo.FirstAidKitFunction) is null && FindKit(world, slot, cold, ItemInfo.RobotKitFunction) is null)
                                world.Bark(id, "I don't have a medkit.");   // refused before the medic moves
                            else Begin(world, slot, order.Patient, CharacterTask.Idle);
                        }
                    break;
                case SplintOrder splint:
                    if (!table.TryResolveNext(splint.Patient, out int splinted) || table.Cold(splinted)!.Medical is not { Dead: false }) break;
                    foreach (var id in Who(world, splint.Medics))
                        if (table.TryResolveNext(id, out int slot) && table.Cold(slot) is { Race: not null, Stats: not null } cold && CanAct(cold))
                        {
                            if (FindKit(world, slot, cold, ItemInfo.SplintKitFunction) is null) world.Bark(id, "I don't have a splint kit.");
                            else Begin(world, slot, splint.Patient, CharacterTask.Idle, splint: true);
                        }
                    break;
                case RestOrder rest:
                    foreach (var id in Who(world, rest.Characters))
                    {
                        if (!table.TryResolveNext(id, out int slot) || table.Cold(slot) is not { } cold || !CanAct(cold)) continue;
                        MovementSystem.Stop(table, slot);
                        table.Next[slot].Task = (byte)CharacterTask.Rest;
                        cold.TreatTarget = CharacterId.None;
                        cold.TreatSplint = false;
                        cold.ResumeTask = CharacterTask.Idle;
                    }
                    break;
                case MedicJobCommand job:
                {
                    var taken = job.Job ? null : Treated(table);
                    foreach (var id in Who(world, job.Characters))
                    {
                        if (!table.TryResolveNext(id, out int slot) || table.Cold(slot) is not { IsPlayer: true } cold) continue;
                        if (taken is null) Production.PermaJobs.Add(cold, Meitou.Data.Gameplay.Ai.TaskType.JOB_MEDIC, Production.PermaJob.Untargeted(Meitou.Data.Gameplay.Ai.TaskType.JOB_MEDIC));
                        else HealSomeone(world, slot, taken, robotics: false);
                    }
                    break;
                }
            }
        }
    }

    static IReadOnlyList<CharacterId> Who(World world, IReadOnlyList<CharacterId> ids) => ids.Count > 0 ? ids : world.Player.Selection.ToArray();

    /// <summary>A character that can be ordered to treat or rest: it has a medical state, is awake and is not in a fight.</summary>
    static bool CanAct(CharacterCold cold) => cold.Medical is { Incapacitated: false } && !cold.InCombat && cold.CarriedBy.IsNone;

    /// <summary>Starts a treatment (serial phases): the medic stands, then walks to the patient. <paramref name="resume"/> is the task to go back to afterwards (kept if already treating).</summary>
    static void Begin(World world, int slot, CharacterId patient, CharacterTask resume, bool splint = false)
    {
        var table = world.Characters;
        var cold = table.Cold(slot)!;
        if (cold.TreatTarget != patient) Say(world, slot, patient, HealingOtherStart, BeingHealedStart);
        ref var n = ref table.Next[slot];
        var keep = n.Task == (byte)CharacterTask.Treat ? cold.ResumeTask : resume;
        MovementSystem.Stop(table, slot);
        n.Task = (byte)CharacterTask.Treat;
        n.Mode = SpeedMode.Free;
        n.TaskTime = 0;
        cold.TreatTarget = patient;
        cold.TreatSplint = splint;
        cold.ResumeTask = keep;
    }

    /// <summary>Dialogue events 18-22 (EV_HEALING_OTHER_START ... EV_FIRSTAID_KIT_EMPTY).</summary>
    const int HealingOtherStart = 18, BeingHealedStart = 19, HealingOtherFinished = 20, BeingHealedFinished = 21, KitEmpty = 22;

    /// <summary>
    /// The treatment's dialogue events (<c>Task_FirstAid</c> start and finish, <c>FUN_14034ecf0</c> / <c>FUN_1403563a0</c>): the medic fires <paramref name="medicEvent"/> at the
    /// patient and a patient that is awake fires <paramref name="patientEvent"/> back (-1: none); nothing for a medic treating itself. Said from the next tick.
    /// </summary>
    static void Say(World world, int medic, CharacterId patient, int medicEvent, int patientEvent = -1)
    {
        var table = world.Characters;
        var id = table.IdOf(medic);
        if (patient == id || patient.IsNone) return;
        world.Defer(new Dialogue.DialogueEvent(id, patient, medicEvent) { Tick = world.Tick + 1 });
        if (patientEvent >= 0 && table.TryResolveNext(patient, out int p) && table.Cold(p)?.Medical is { Incapacitated: false })
            world.Defer(new Dialogue.DialogueEvent(patient, id, patientEvent) { Tick = world.Tick + 1 });
    }

    /// <summary>Ends a treatment (serial phases): back to the task before it, path dropped.</summary>
    static void End(CharacterTable table, int slot)
    {
        var cold = table.Cold(slot)!;
        ref var n = ref table.Next[slot];
        if (n.Task == (byte)CharacterTask.Treat)
        {
            MovementSystem.Stop(table, slot);
            n.Task = (byte)cold.ResumeTask;
            n.TaskTime = 0;
        }
        cold.TreatTarget = CharacterId.None;
        cold.TreatSplint = false;
        cold.ResumeTask = CharacterTask.Idle;
    }

    static float Gap(in CharacterHot a, in CharacterHot b)
    {
        float dx = a.Position.X - b.Position.X, dz = a.Position.Z - b.Position.Z;
        return MathF.Sqrt(dx * dx + dz * dz);
    }

    // ------------------------------------------------------------------ walking to the patient

    public void Schedule(World world)
    {
        var table = world.Characters;
        var next = table.Next;
        float dt = world.TickSeconds;
        for (int i = 0; i < table.HighWater; i++)
        {
            ref var n = ref next[i];
            if (!n.Alive || n.Task != (byte)CharacterTask.Treat) continue;
            var cold = table.Cold(i)!;
            if (cold.Medical is not { Incapacitated: false } || cold.InCombat
                || !table.TryResolveNext(cold.TreatTarget, out int t) || table.Cold(t)!.Medical is not { Dead: false })
            {
                End(table, i);
                continue;
            }
            const ushort Path = (ushort)MoveFlags.AnyPath;
            if (t == i || Gap(next[i], next[t]) <= Settings.Reach)
            {
                // In reach (or treating itself): stand still and let the Act step work.
                if ((n.Flags & Path) != 0)
                {
                    n.Flags = (ushort)(n.Flags & ~Path);
                    n.PathCursor = 0;
                    cold.PathRequest = 0;
                }
                n.TaskTime = 0;
                continue;
            }
            n.TaskTime -= dt;
            var goal = new Vector2(next[t].Position.X, next[t].Position.Z);
            bool asking = (n.Flags & (ushort)(MoveFlags.NeedPath | MoveFlags.Pending)) != 0;
            bool stale = (n.Flags & (ushort)MoveFlags.HasPath) == 0 || Vector2.DistanceSquared(goal, n.Goal) > PursuitSystem.RepathDistance * PursuitSystem.RepathDistance;
            if (asking || !stale || n.TaskTime > 0) continue;
            n.Goal = goal;
            n.Mode = SpeedMode.Free;
            n.Flags |= (ushort)MoveFlags.NeedPath;
            n.TaskTime = PursuitSystem.RepathSeconds;
        }
    }

    // ------------------------------------------------------------------ the treatment

    public void Act(World world, Partition part, EffectBuffer effects)
    {
        var table = world.Characters;
        var prev = table.Previous;
        var next = table.Next;
        float still2 = Settings.StillSpeed * Settings.StillSpeed;
        float reach = Settings.Reach;
        for (int i = part.Start; i < part.End; i++)
        {
            if (!prev[i].Alive || next[i].Task != (byte)CharacterTask.Treat) continue;
            var cold = table.Cold(i)!;
            if (cold.Medical is not { Incapacitated: false } medical || cold.InCombat || medical.Aggregates().BothArmsDisabled || cold.StaggeredUntil > world.Tick) continue;
            var target = cold.TreatTarget;
            if (!table.IsAlive(target)) continue;
            int t = target.Slot;
            if (table.Cold(t) is not { Medical: { Dead: false } patient }) continue;
            if (Speed2(prev[i].Velocity) > still2) continue;
            if (t != i)
            {
                if (Gap(prev[i], prev[t]) > reach) continue;
                if (!patient.Incapacitated && Speed2(prev[t].Velocity) > still2) continue;
            }
            effects.Emit(i, t, EffectTreat);
        }
    }

    static float Speed2(Vector3 v) => v.X * v.X + v.Z * v.Z;

    /// <summary>Serial, in (patient, medic) order: the kit, the bandaging, the drain and the medic's XP. Ends the treatment when nothing is left to bandage or no kit fits.</summary>
    public void Apply(World world, in Effect effect)
    {
        if (effect.Kind != EffectTreat) return;
        var table = world.Characters;
        int m = effect.Source, p = effect.Target;
        if (!table.Next[m].Alive || table.Next[m].Task != (byte)CharacterTask.Treat) return;
        var medicCold = table.Cold(m)!;
        if (!table.TryResolveNext(medicCold.TreatTarget, out int t) || t != p) return;
        var patientCold = table.Cold(p)!;
        if (medicCold is not { Medical: { Incapacitated: false } medical, Stats: { } stats, Race: { } medicRace } || patientCold is not { Medical: { Dead: false } body, Race: { } race })
        {
            End(table, m);
            return;
        }
        if (medicCold.TreatSplint)
        {
            Splint(world, m, medicCold, medical, stats, medicRace, body, race);
            return;
        }
        float dt = world.TickSeconds;
        // The skill and trained stat: medic with a first aid kit, robotics with a robot repair kit; treating oneself x0.8 (FUN_14033c5a0, Task_FirstAid's kit phase).
        StatsEnumerated StatOf(bool robotKit) => robotKit ? StatsEnumerated.Robotics : StatsEnumerated.Medic;
        float SkillOf(bool robotKit) => stats[StatOf(robotKit)] * medical.StatMultiplier(StatOf(robotKit)) * (m == p ? 0.8f : 1);
        // A medic not of the player's treats an imprisoned patient without a kit (Task_FirstAid FUN_1403563a0 skips the kit search; the chained-slave case is not modelled).
        bool kitless = !medicCold.IsPlayer && patientCold.IsImprisoned;
        var ctx = new MedicalContext(Constants, bodyOptions, race);
        // Open wounds are closed first, newest first, with a kit matching the patient's race (none: quality 5); nothing is bandaged meanwhile (FUN_14064d800). This drain is not gated.
        if (body.Wounds.Count > 0)
        {
            var woundKit = kitless ? null : FindKit(world, m, medicCold, FunctionOf(race.IsRobot));
            if (woundKit is null && !kitless)
            {
                Say(world, m, medicCold.TreatTarget, KitEmpty);
                End(table, m);
                return;
            }
            float woundSkill = SkillOf(race.IsRobot && !kitless);
            body.CloseWound(dt, woundSkill, woundKit is { } wk ? Items.Info(wk.Item.Record)?.Quality ?? 0 : KitlessQuality, Constants);
            if (woundKit is { } used) Drain(used, woundSkill, dt, race.IsRobot, 1);
            xp.Medic(stats, race, medicRace, dt, StatOf(race.IsRobot && !kitless));
            return;
        }
        if (kitless)
        {
            // Without a kit every part takes 1 HP a second, organic or robotic alike, and nothing drains (FUN_140649a00).
            if (body.Bandage(dt, null, race, out _) <= 0)
            {
                Say(world, m, medicCold.TreatTarget, HealingOtherFinished, BeingHealedFinished);
                patientCold.StuckBolts = null;
                End(table, m);
                return;
            }
            xp.Medic(stats, race, medicRace, dt, StatsEnumerated.Medic);
            Deeds?.FirstAid(world, m, p, dt);
            return;
        }
        // Ordinary kits first; a robotic part is treated once the organic ones are done. Treatment runs until the last bit is bandaged (the start threshold is MedicalState.FirstAidThreshold).
        bool robot = body.BandageRoom(race, false) <= Done;
        if (robot && body.BandageRoom(race, true) <= Done)
        {
            Say(world, m, medicCold.TreatTarget, HealingOtherFinished, BeingHealedFinished);
            patientCold.StuckBolts = null;   // the bolts go when first aid has nothing left to do (FUN_14064d800 -> FUN_14052bc10)
            End(table, m);   // nothing left to bandage
            return;
        }
        if (FindKit(world, m, medicCold, FunctionOf(robot)) is not { } kit)
        {
            Say(world, m, medicCold.TreatTarget, KitEmpty);   // Task_FirstAidRig's empty kit (FUN_14034ef20)
            End(table, m);   // no kit of the right kind within reach
            return;
        }
        float skill = SkillOf(robot);
        float quality = Items.Info(kit.Item.Record)?.Quality ?? 0;
        float bandaged = body.Treat(dt, skill, quality, robot, ctx, out int touched);
        if (bandaged <= 0)
        {
            End(table, m);
            return;
        }
        if (medicCold.IsPlayer) Drain(kit, skill, dt, robot, touched);   // once per part bandaged; only the player's medics use up kits here (FUN_140649a00: faction +0x250)
        xp.Medic(stats, race, medicRace, dt, StatOf(robot));
        // An unconscious patient's faction is grateful to the medic's (engine choice: every update that bandages; the original's update fires it only when its loop got past the wounds).
        Deeds?.FirstAid(world, m, p, dt);
    }

    /// <summary>
    /// One update of a splint (task "Splinting", <c>FUN_140649d50</c>, docs/game/character-stats.md "Splints"): the medic's <c>medic</c> skill and the kit's quality set the rate and the cap;
    /// the kit drains as first aid's only when a part took rig; medic XP every update. Ends when no part is left to splint or no splint kit is in reach.
    /// </summary>
    void Splint(World world, int m, CharacterCold medicCold, MedicalState medical, CharacterStats stats, RaceData medicRace, MedicalState body, RaceData race)
    {
        var table = world.Characters;
        if (FindKit(world, m, medicCold, ItemInfo.SplintKitFunction) is not { } kit)
        {
            End(table, m);
            return;
        }
        var item = kit.Item;
        float dt = world.TickSeconds;
        float skill = stats[StatsEnumerated.Medic] * medical.StatMultiplier(StatsEnumerated.Medic);
        float quality = Items.Info(item.Record)?.Quality ?? 0;
        float added = body.Splint(dt, skill, quality, new MedicalContext(Constants, bodyOptions, race));
        if (added <= 0)
        {
            End(table, m);   // nothing left to splint
            return;
        }
        Drain(kit, skill, dt, false, 1);
        xp.Medic(stats, race, medicRace, dt, StatsEnumerated.Medic);
    }

    // ------------------------------------------------------------------ kits

    /// <summary>Uses <paramref name="times"/> drains of a kit (<see cref="MedicalState.KitDrain"/>); a used-up kit is gone.</summary>
    void Drain((ItemInstance Item, Inventory Owner) kit, float skill, float dt, bool robotKit, int times)
    {
        kit.Item.Charges -= MedicalState.KitDrain(Constants, skill, dt, robotKit) * times;
        if (kit.Item.Charges <= 0) kit.Owner.RemoveOne(kit.Item);
    }

    /// <summary>The best kit of a function in an inventory (and its backpack): the highest record quality with charges left; the first of equals.</summary>
    ItemInstance? Best(Inventory? inventory, int function)
    {
        if (inventory is null) return null;
        ItemInstance? best = null;
        float bestQuality = float.MinValue;
        foreach (var item in inventory.All())
        {
            if (item.Type != FcsRecordType.ITEM || item.ItemFunction != function || item.Charges <= 0 || item.Quantity <= 0) continue;
            float q = Items.Info(item.Record)?.Quality ?? 0;
            if (q > bestQuality)
            {
                best = item;
                bestQuality = q;
            }
        }
        return best;
    }

    static int FunctionOf(bool robot) => robot ? ItemInfo.RobotKitFunction : ItemInfo.FirstAidKitFunction;

    /// <summary>The medic's own best kit of an <c>item function</c>, else a squad mate's within <see cref="FirstAidSettings.KitReach"/> (an engine choice).</summary>
    (ItemInstance Item, Inventory Owner)? FindKit(World world, int slot, CharacterCold cold, int function)
    {
        if (cold.Inventory is { } own && Best(own, function) is { } mine) return (mine, own);
        if (cold.SquadId < 0 || world.Squads.Find(cold.SquadId) is not { } squad) return null;
        var table = world.Characters;
        var here = table.Next[slot].Position;
        float reach2 = Settings.KitReach * Settings.KitReach;
        foreach (var m in squad.Members)
        {
            if (m.Slot == slot || !table.TryResolveNext(m, out int s) || table.Cold(s) is not { Inventory: { } mates, Medical: { Dead: false } }) continue;
            var d = table.Next[s].Position - here;
            if (d.X * d.X + d.Z * d.Z > reach2) continue;
            if (Best(mates, function) is { } found) return (found, mates);
        }
        return null;
    }

    // ------------------------------------------------------------------ treating by themselves

    public void SlowWorld(World world)
    {
        if (Settings.AutoEveryTicks <= 0 || world.Tick % Settings.AutoEveryTicks != 0) return;
        var table = world.Characters;
        var next = table.Next;
        var taken = Treated(table);
        for (int i = 0; i < table.HighWater; i++)
        {
            if (!next[i].Alive) continue;
            var cold = table.Cold(i)!;
            if (cold.IsPlayer)
            {
                if (cold.Jobs.Count > 0) RunJobs(world, i, cold, taken);
                // Squad jobs: a Medics squad's member, free (idle or at a machine its job gave it), treats the wounded first.
                if (next[i].Task is (byte)CharacterTask.Idle or (byte)CharacterTask.Operate && Jobs.WorkRules.JobOf(world, cold) == Jobs.SquadJob.Medics)
                    HealSomeone(world, i, taken, robotics: false);
                continue;
            }
            if (!Settings.AutoNpcs) continue;
            // Idle: no task, following, wandering or an implicit job.
            bool idle = next[i].Task is (byte)CharacterTask.Idle or (byte)CharacterTask.Wander or (byte)CharacterTask.Follow || cold.Implicit is not null;
            if (idle && !TreatSomeone(world, i, taken)) TreatPrisoner(world, i, taken);
        }
    }

    /// <summary>
    /// A player character's first aid jobs, in list order (docs/game/ai-tasks.md "Perma-jobs"): the first of Medic 58, Robotics 57 and Splint Rigging 250 that may start
    /// (<see cref="Production.PermaJobs.FreeFor"/>; all three run with the jobs switch off) and whose finder finds a patient starts the treatment.
    /// </summary>
    void RunJobs(World world, int i, CharacterCold cold, HashSet<int> taken)
    {
        var task = (CharacterTask)world.Characters.Next[i].Task;
        for (int k = 0; k < cold.Jobs.Count; k++)
        {
            var type = cold.Jobs[k].Type;
            if (type is not (TaskType.JOB_MEDIC or TaskType.JOB_REPAIR_ROBOT or TaskType.SPLINT_JOB) || !Production.PermaJobs.FreeFor(cold, task, k)) continue;
            if (type == TaskType.SPLINT_JOB ? SplintSomeone(world, i, taken) : HealSomeone(world, i, taken, robotics: type == TaskType.JOB_REPAIR_ROBOT)) return;
        }
    }

    /// <summary>The live relation table (allied factions for <see cref="SquadAi.Heal"/>); null: other factions are never treated by a job.</summary>
    public FactionRelations? Relations { get; set; }

    /// <summary>
    /// The square of the distance at which a job's patient weighs nothing (<c>FUN_140998b90</c>: the weight is 1 - d^2 / 3397386.5, about 1843 units, clamped to [0, 1];
    /// <b>Verified</b> constant). The original raises it to at least 0.4 for a patient in the medic's building; Meitou does not (engine reduction).
    /// </summary>
    public const float PatientFalloff2 = 3397386.5f;

    static float Nearness(float d2) => Math.Clamp(1 - d2 / PatientFalloff2, 0, 1);

    /// <summary>
    /// What a patient needs of a medic with these kits (<c>FUN_140998200</c>): the untreated damage an organic kit can bandage (with one) plus what a robot kit can (with one). The original
    /// reads two cached floats of the medical state there; Meitou takes <see cref="MedicalState.BandageRoom"/> above <see cref="MedicalState.FirstAidThreshold"/> (engine reading).
    /// </summary>
    static float Need(MedicalState med, RaceData race, bool organic, bool robotic) =>
        (organic && med.NeedsAid(race, false) ? med.BandageRoom(race, false) : 0) + (robotic && med.NeedsAid(race, true) ? med.BandageRoom(race, true) : 0);

    /// <summary>
    /// The medic and robotics jobs' finder for a player character (<c>FUN_14099f690</c>; 58 passes 0, 57 passes 1; docs/game/ai-tasks.md "Medic jobs"), and the Medic button's one
    /// look. Candidates within the medic's sight (the stand-in for its sensed list): not carried, not fighting, still or down, not treated by another, not of a robot race unless
    /// <paramref name="robotics"/>; of its faction, or with <see cref="SquadAi.Heal"/> of an allied one. Score = need x relation factor x nearness (<see cref="PatientFalloff2"/>); the
    /// relation factor is 1 for its own faction, else the relation / 100, at least 0.05; another faction's patient needs a score of 0.1. The medic itself scores its plain need. The
    /// highest score is treated; none: false. No fight may be on in its squad (engine rule, as <see cref="TreatSomeone"/>).
    /// </summary>
    bool HealSomeone(World world, int i, HashSet<int> taken, bool robotics)
    {
        var table = world.Characters;
        var next = table.Next;
        var cold = table.Cold(i)!;
        if (cold.Medical is not { Incapacitated: false } own || cold.Race is not { } ownRace || cold.Stats is null || cold.Inventory is null || cold.InCombat) return false;
        bool organic = Best(cold.Inventory, ItemInfo.FirstAidKitFunction) is not null, robotic = Best(cold.Inventory, ItemInfo.RobotKitFunction) is not null;
        if (!organic && !robotic) return false;
        if (cold.SquadId >= 0 && world.Squads.Find(cold.SquadId) is { } squad && Fighting(table, squad)) return false;
        bool allies = (world.Player.SquadAi & SquadAi.Heal) != 0 && Relations is not null && Relations.Contains(cold.Faction);
        float range = AggroSystem.RangeOf(cold, AggroSystem.OutdoorSight), still2 = Settings.StillSpeed * Settings.StillSpeed;
        int best = -1;
        float bestScore = 0;
        for (int s = 0; s < table.HighWater; s++)
        {
            if (s == i || !next[s].Alive || taken.Contains(s) || table.Cold(s) is not { Medical: { Dead: false } med, Race: { } race } other) continue;
            if (other.InCombat || !other.CarriedBy.IsNone || !robotics && race.IsRobot) continue;
            float dx = next[s].Position.X - next[i].Position.X, dz = next[s].Position.Z - next[i].Position.Z, d2 = dx * dx + dz * dz;
            if (d2 > range * range || !med.Incapacitated && Speed2(next[s].Velocity) > still2) continue;
            bool same = other.Faction == cold.Faction;
            if (!same && !(allies && Relations!.Contains(other.Faction) && Relations.IsAlly(cold.Faction, other.Faction))) continue;
            float score = Need(med, race, organic, robotic) * (same ? 1 : MathF.Max(Relations!.Get(cold.Faction, other.Faction) * 0.01f, 0.05f)) * Nearness(d2);
            if (!same && score < 0.1f || score <= bestScore) continue;
            best = s;
            bestScore = score;
        }
        if (!taken.Contains(i) && Need(own, ownRace, organic, robotic) > bestScore) best = i;
        return best >= 0 && StartJob(world, i, best, taken, splint: false);
    }

    /// <summary>
    /// The splint job's finder (<c>FUN_14099e240</c>): with skill X = min(medic skill, the best splint kit's quality) (no kit of its own: nothing), the nearest-weighted patient of its own
    /// faction that <see cref="MedicalState.NeedsSplint"/> at X (robot races never do), not carried, not treated by another, scored by <see cref="Nearness"/>; the medic itself scores 1.
    /// The original's need weight per patient is <b>Unknown</b>; Meitou uses 1.
    /// </summary>
    bool SplintSomeone(World world, int i, HashSet<int> taken)
    {
        var table = world.Characters;
        var next = table.Next;
        var cold = table.Cold(i)!;
        if (cold.Medical is not { Incapacitated: false } own || cold.Race is not { } ownRace || cold.Stats is not { } stats || cold.InCombat) return false;
        if (Best(cold.Inventory, ItemInfo.SplintKitFunction) is not { } kit) return false;
        if (cold.SquadId >= 0 && world.Squads.Find(cold.SquadId) is { } squad && Fighting(table, squad)) return false;
        float skill = MathF.Min(stats[StatsEnumerated.Medic] * own.StatMultiplier(StatsEnumerated.Medic), Items.Info(kit.Record)?.Quality ?? 0);
        float range = AggroSystem.RangeOf(cold, AggroSystem.OutdoorSight), still2 = Settings.StillSpeed * Settings.StillSpeed;
        int best = -1;
        float bestScore = 0;
        for (int s = 0; s < table.HighWater; s++)
        {
            if (s == i || !next[s].Alive || taken.Contains(s) || table.Cold(s) is not { Medical: { Dead: false } med, Race: { } race } other) continue;
            if (other.Faction != cold.Faction || other.InCombat || !other.CarriedBy.IsNone || !med.NeedsSplint(race, skill)) continue;
            float dx = next[s].Position.X - next[i].Position.X, dz = next[s].Position.Z - next[i].Position.Z, d2 = dx * dx + dz * dz;
            if (d2 > range * range || !med.Incapacitated && Speed2(next[s].Velocity) > still2) continue;
            float score = Nearness(d2);
            if (score <= bestScore) continue;
            best = s;
            bestScore = score;
        }
        if (!taken.Contains(i) && own.NeedsSplint(ownRace, skill)) best = i;
        return best >= 0 && StartJob(world, i, best, taken, splint: true);
    }

    /// <summary>Starts a job's treatment of <paramref name="patient"/>, going back afterwards to what the medic did before its job.</summary>
    static bool StartJob(World world, int i, int patient, HashSet<int> taken, bool splint)
    {
        var next = world.Characters.Next;
        var cold = world.Characters.Cold(i)!;
        taken.Add(patient);
        var resume = cold.Implicit is not null || cold.JobIndex >= 0 ? cold.ResumeTask : next[i].Task is (byte)CharacterTask.Wander or (byte)CharacterTask.Follow ? (CharacterTask)next[i].Task : CharacterTask.Idle;
        Begin(world, i, new CharacterId(patient, next[patient].Generation), resume, splint);
        return true;
    }

    /// <summary>The patients being treated now (by slot).</summary>
    static HashSet<int> Treated(CharacterTable table)
    {
        var taken = new HashSet<int>();
        for (int i = 0; i < table.HighWater; i++)
            if (table.Next[i].Alive && table.Next[i].Task == (byte)CharacterTask.Treat && table.TryResolveNext(table.Cold(i)!.TreatTarget, out int busy)) taken.Add(busy);
        return taken;
    }

    /// <summary>
    /// One look for a patient (the medic job's pass, and the Medic button without Shift once): a medic with a kit and no fight in its squad treats the squad mate (or itself) within
    /// <see cref="FirstAidSettings.AutoReach"/> that most needs it, downed first, then the least blood; true when it started.
    /// </summary>
    bool TreatSomeone(World world, int i, HashSet<int> taken)
    {
        var table = world.Characters;
        var next = table.Next;
        var cold = table.Cold(i)!;
        if (cold.Medical is not { Incapacitated: false } || cold.Race is null || cold.Stats is null || cold.Inventory is null || cold.InCombat) return false;
        bool organic = Best(cold.Inventory, ItemInfo.FirstAidKitFunction) is not null, robotic = Best(cold.Inventory, ItemInfo.RobotKitFunction) is not null;
        if (!organic && !robotic) return false;
        var squad = cold.SquadId >= 0 ? world.Squads.Find(cold.SquadId) : null;
        if (squad is not null && Fighting(table, squad)) return false;
        int best = -1;
        (int Down, float Blood, float Distance) bestKey = default;
        IEnumerable<CharacterId> candidates = squad is not null ? squad.Members : [table.IdOf(i)];
        foreach (var id in candidates)
        {
            if (!table.TryResolveNext(id, out int s) || taken.Contains(s) || table.Cold(s) is not { Medical: { Dead: false } med, Race: { } race } other || other.InCombat) continue;
            float distance = Gap(next[i], next[s]);
            if (distance > Settings.AutoReach || (s != i && !med.Incapacitated && Speed2(next[s].Velocity) > Settings.StillSpeed * Settings.StillSpeed)) continue;   // a moving patient cannot be treated (targetStandingStill)
            if (!(organic && med.NeedsAid(race, false)) && !(robotic && med.NeedsAid(race, true))) continue;
            float capacity = MathF.Max(MedicalState.BloodCapacity(race, other.Stats?.Strength ?? 50), 1);
            var key = (med.Incapacitated ? 0 : 1, med.Blood / capacity, distance);
            if (best < 0 || key.CompareTo(bestKey) < 0)
            {
                best = s;
                bestKey = key;
            }
        }
        if (best < 0) return false;
        taken.Add(best);
        var resume = cold.Implicit is not null || cold.JobIndex >= 0 ? cold.ResumeTask : next[i].Task is (byte)CharacterTask.Wander or (byte)CharacterTask.Follow ? (CharacterTask)next[i].Task : CharacterTask.Idle;
        Begin(world, i, new CharacterId(best, next[best].Generation), resume);
        return true;
    }

    /// <summary>
    /// The Medic goal's second pass for an NPC that found no patient (<c>FUN_14099f690</c>, docs/game/ai-tasks.md "Medic jobs"): a prisoner in a cage its faction owns, within its sight,
    /// not of a robot race, not an obedient slave, not of its squad and not treated by another, scored by need x relation factor x nearness; the highest is treated, without a kit
    /// (<see cref="Apply"/>). Need counts both kinds of damage, kit or not (<c>FUN_140998200</c>). True when it started.
    /// </summary>
    bool TreatPrisoner(World world, int i, HashSet<int> taken)
    {
        var table = world.Characters;
        var next = table.Next;
        var cold = table.Cold(i)!;
        if (cold.Medical is not { Incapacitated: false } || cold.Stats is null || cold.InCombat) return false;
        if (cold.SquadId >= 0 && world.Squads.Find(cold.SquadId) is { } squad && Fighting(table, squad)) return false;
        float range = AggroSystem.RangeOf(cold, AggroSystem.OutdoorSight);
        int best = -1;
        float bestScore = 0;
        foreach (var f in world.Fixtures.All)
        {
            if (f.Kind != FixtureKind.Cage || f.Owner != cold.Faction || FixtureRegistry.IsFree(table, f) || !table.TryResolveNext(f.Occupant, out int s)) continue;
            if (s == i || taken.Contains(s) || table.Cold(s) is not { IsImprisoned: true, Medical: { Dead: false } med, Race: { IsRobot: false } race } other) continue;
            if (other.SlaveState == 1 || other.SquadId >= 0 && other.SquadId == cold.SquadId) continue;
            float dx = next[s].Position.X - next[i].Position.X, dz = next[s].Position.Z - next[i].Position.Z, d2 = dx * dx + dz * dz;
            if (d2 > range * range) continue;
            float relation = other.Faction == cold.Faction || Relations is null || !Relations.Contains(cold.Faction) || !Relations.Contains(other.Faction) ? 1 : MathF.Max(Relations.Get(cold.Faction, other.Faction) * 0.01f, 0.05f);
            float score = Need(med, race, true, true) * relation * Nearness(d2);
            if (score <= bestScore) continue;
            best = s;
            bestScore = score;
        }
        return best >= 0 && StartJob(world, i, best, taken, splint: false);
    }

    /// <summary>A fight is on in the squad: a member in combat or with an attack order.</summary>
    static bool Fighting(CharacterTable table, Squad squad)
    {
        foreach (var m in squad.Members)
            if (table.TryResolveNext(m, out int s) && (table.Cold(s)!.InCombat || table.Next[s].Task == (byte)CharacterTask.Attack)) return true;
        return false;
    }
}
