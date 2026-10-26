using System.Numerics;
using Meitou.Data.Characters;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Simulation.Combat;
using Meitou.Simulation.Ragdolls;
using static Meitou.Simulation.Euphoria.DeliriumNative;

namespace Meitou.Simulation.Euphoria;

/// <summary>
/// Physical hit reactions (Meitou-only, docs/euphoria.md): a character that is hit becomes a Delirium (euphoria) body for a few seconds, pushed by the blow, that stumbles and
/// keeps its balance or falls. One that falls is knocked out briefly and handed to its ragdoll (<see cref="IRagdollHandOff"/>), and gets up as from any knockout; one that is
/// knocked out or killed by the hit falls physically before the ragdoll takes over. The body's pelvis moves the character. At most <see cref="Cap"/> bodies at once, the ones
/// nearest the camera. Runs serially in the slow-world step after the animation (it starts from this tick's pose) and before the ragdolls; the bodies step on the world's
/// thread count, each in its own small scene of the ground and buildings around where it started.
/// </summary>
public sealed class EuphoriaSystem : ITickSystem, IHitReactions, IRagdollHandOff, IDisposable
{
    public const int DefaultCap = 24;
    /// <summary>
    /// Seconds (engine choices, docs/euphoria.md "Ending"): a standing body ends once it balances with its centre of mass slower than <see cref="CalmSpeed"/> m/s along the
    /// ground, not before <see cref="CalmEarliest"/>, at the latest after <see cref="Longest"/>, and blends back into the animation over <see cref="Blend"/>. A fallen one lies
    /// once it has been down <see cref="LieEarliest"/> and moves slower than <see cref="LieSpeed"/> m/s, or after <see cref="LieLatest"/>: it goes to its ragdoll, knocked out for
    /// <see cref="FallKnockOut"/> when the hit did not do that; a knocked-out one at the latest after <see cref="DownLongest"/>.
    /// </summary>
    public const float CalmEarliest = 0.5f, CalmSpeed = 0.35f, Longest = 4, LieEarliest = 0.6f, LieSpeed = 0.5f, LieLatest = 2, FallKnockOut = 2.5f, DownLongest = 3, Blend = 0.35f;
    /// <summary>World units: the square of ground each body's scene has round where it started (half its edge), its grid, and how far the body may go before it ends.</summary>
    const float GroundHalf = 70, GridStep = 5, Reach = 55;
    /// <summary>m/s down: a body falling faster is off a ledge (a stumble or a fall to the ground stays under it; about a 1.3 m drop).</summary>
    const float FallingSpeed = 5;
    /// <summary>
    /// The push of a hit on the part struck, in N s: <see cref="PushBase"/> + <see cref="PushPerStrength"/> x strength (the damage over the stumble threshold, at most
    /// <see cref="StrongestHit"/>), times 0.8 for a cut to 1.2 for a blunt blow, times <see cref="RangedPush"/> for a bolt. Strength 1 staggers a man about a metre, 2 nearly two;
    /// from 1.5 the steps he may take to catch himself are limited (<see cref="MaxSteps"/>), and from 2.5 he goes down (measured on Kenshi's man and woman, docs/euphoria.md).
    /// </summary>
    const float PushBase = 15, PushPerStrength = 40, StrongestHit = 4, RangedPush = 0.6f;
    /// <summary>How much more each earlier hit of the same reaction makes the next one count (engine choice).</summary>
    const float StackPerHit = 0.5f;

    /// <summary>The steps a body may take to keep its balance after a hit of <paramref name="strength"/> (0: as many as it needs).</summary>
    static int MaxSteps(float strength) => strength >= 3.5f ? 1 : strength >= 2.5f ? 2 : strength >= 1.5f ? 4 : 0;

    readonly CharacterRigs rigs;
    IRagdollEnvironment? environment;
    readonly Task<EuphoriaData?> loading;
    IAnimationSource? animations;
    CombatSystem? combat;
    PopulationSystem? population;
    IRagdollState? ragdolls;

    readonly Dictionary<CharacterId, Agent> agents = [];
    readonly List<Agent> order = [];
    readonly List<(CharacterId Id, HitPush Push, bool Down)> pending = [];
    readonly HashSet<CharacterId> ending = [];
    readonly Dictionary<CharacterId, BonePose> handed = [];
    readonly Dictionary<CharacterId, Blending> blends = [];

    /// <param name="data">The characters (<see cref="EuphoriaData.Shared"/>); until the task has them, hits play their clips.</param>
    /// <param name="environment">Ground and buildings; null: the world's walkable ground.</param>
    public EuphoriaSystem(CharacterRigs rigs, Task<EuphoriaData?> data, IRagdollEnvironment? environment = null)
    {
        this.rigs = rigs;
        loading = data;
        this.environment = environment;
    }

    /// <summary>New reactions start only while on (the settings' toggle); running ones finish.</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>How many bodies may react at once.</summary>
    public int Cap { get; set; } = DefaultCap;
    /// <summary>Bodies reacting now.</summary>
    public int Count => agents.Count;
    EuphoriaData? Data => loading.IsCompletedSuccessfully ? loading.Result : null;

    sealed class Agent
    {
        public required CharacterId Id;
        public required EuphoriaData.Body Body;
        public required CharacterPoseSetup Setup;
        public required nint Handle, Scene;
        public required Vector3 Origin;
        public required float YawStart;
        public required Quaternion RootStart;
        public bool Down;
        public float Age;
        /// <summary>When it fell (its age then), null while it is up.</summary>
        public float? FellAt;
        /// <summary>Falling from a height (<c>highFall</c> started).</summary>
        public bool Flailing;
        /// <summary>Hits taken so far in this reaction.</summary>
        public int Hits;
        public string? Failed;
        public readonly float[] Parts = new float[EuphoriaData.Parts * 7];
        /// <summary>By part: its bone in world space, last read.</summary>
        public readonly RigidTransform[] Bones = new RigidTransform[EuphoriaData.Parts];
        public RigidTransform Root;
        public BonePose? Published;
    }

    sealed class Blending
    {
        public required CharacterPoseSetup Setup;
        public required EuphoriaData.Body Body;
        public required RigidTransform[] Bones;
        public required RigidTransform Root;
        public float Left = Blend;
        public BonePose? Published;
    }

    // ---- what combat and the ragdolls ask ----

    public bool Holds(CharacterId id) => agents.ContainsKey(id);

    public bool Reacting(CharacterId id) => agents.TryGetValue(id, out var a) && !a.Down && a.FellAt is null;

    public BonePose? Publish(CharacterId id) =>
        agents.TryGetValue(id, out var a) ? a.Published : handed.TryGetValue(id, out var h) ? h : blends.TryGetValue(id, out var b) ? b.Published : null;

    public bool Takes(World world, int slot) => Fits(world, slot, false) is not null && Room(world, slot);

    public bool Hit(World world, int slot, in HitPush push, bool down)
    {
        if (Fits(world, slot, down) is null) return false;
        var id = world.Characters.IdOf(slot);
        if (!agents.ContainsKey(id) && !pending.Exists(p => p.Id == id) && !Room(world, slot)) return false;
        pending.Add((id, push, down));
        return true;
    }

    /// <summary>The body for the character in <paramref name="slot"/>, or null when it cannot react (off, not loaded, not a human skeleton, carried, in a bed or a cage,
    /// getting up, crawling; a hit that downs it needs only the body).</summary>
    EuphoriaData.Body? Fits(World world, int slot, bool down)
    {
        if (!Enabled || Data is not { } data || world.Characters.Cold(slot) is not { Appearance: { } look, Medical: { } medical } cold) return null;
        if (cold.Fighter is { IsAnimal: true } || !cold.CarriedBy.IsNone || cold.InSomething != Locks.HeldState.None) return null;
        if (!down && (medical.Incapacitated || cold.GettingUpUntil > world.Tick || LegsDown(medical))) return null;
        return rigs.For(look) is { } setup ? data.Fit(setup.Rig, look.Female) : null;
    }

    static bool LegsDown(Bodies.MedicalState medical)
    {
        foreach (var p in medical.PartSpan)
            if (p.IsLeg && p.LegDown(medical.LegKoPoint)) return true;
        return false;
    }

    /// <summary>Whether a new body fits under the cap: there is room, or this character is nearer the camera than the farthest body, which then ends.</summary>
    bool Room(World world, int slot)
    {
        var id = world.Characters.IdOf(slot);
        if (agents.ContainsKey(id)) return true;
        int starting = 0;
        foreach (var p in pending) if (!agents.ContainsKey(p.Id)) starting++;
        if (agents.Count - ending.Count + starting < Cap) return true;
        if (population?.Focus is not { } focus) return false;
        var prev = world.Characters.Previous;
        float mine = Vector3.DistanceSquared(prev[slot].Position, focus);
        Agent? farthest = null;
        float worst = mine;
        foreach (var a in order)
        {
            if (ending.Contains(a.Id) || !world.Characters.IsAlive(a.Id)) continue;
            float d = Vector3.DistanceSquared(prev[a.Id.Slot].Position, focus);
            if (d > worst) (farthest, worst) = (a, d);
        }
        if (farthest is null) return false;
        ending.Add(farthest.Id);
        return true;
    }

    // ---- the tick ----

    public void SlowWorld(World world)
    {
        animations ??= world.Systems.OfType<IAnimationSource>().FirstOrDefault();
        combat ??= world.Systems.OfType<CombatSystem>().FirstOrDefault();
        population ??= world.Systems.OfType<PopulationSystem>().FirstOrDefault();
        ragdolls ??= world.Systems.OfType<IRagdollState>().FirstOrDefault();
        handed.Clear();
        var table = world.Characters;
        bool watching = Data is not null && Enabled;
        if (!watching) up.Clear();
        bool downs = watching && Downs(world);
        if (agents.Count == 0 && pending.Count == 0 && blends.Count == 0 && !downs) return;
        var env = environment ??= new WalkabilityEnvironment(world.Walkability);
        float dt = world.TickSeconds;
        int now = (int)world.Tick;

        // 1. Bodies whose character is gone, lifted, laid in something, or pushed out by a nearer one.
        foreach (var a in order.ToArray())
        {
            if (!table.TryResolveNext(a.Id, out int slot) || table.Cold(slot) is not { } cold || !cold.CarriedBy.IsNone || cold.InSomething != Locks.HeldState.None) Destroy(a);
            else if (ending.Contains(a.Id)) End(world, a, ref table.Next[slot], cold, env);
        }
        ending.Clear();

        // 2. The hits of this tick: new bodies, and pushes on the ones reacting.
        foreach (var (id, push, down) in pending)
        {
            if (!table.TryResolveNext(id, out int slot) || table.Cold(slot) is not { } cold) continue;
            if (!agents.TryGetValue(id, out var a) && (a = Start(world, slot, cold, down)) is null) continue;
            if (down && !a.Down) Collapse(a);
            Push(a, cold, push);
        }
        pending.Clear();

        // Characters on their feet last tick who are down now without a hit (bled out, knocked out by a stealth blow or as they got up): they crumple.
        if (downs)
            foreach (int slot in fresh)
                if (table.Cold(slot) is { } cold && !agents.ContainsKey(table.IdOf(slot)) && ragdolls?.WasWhole(table.IdOf(slot)) != true
                    && Fits(world, slot, true) is not null && Room(world, slot)
                    && Start(world, slot, cold, true) is { } a)
                {
                    Collapse(a);
                    float angle = Rng.Float(Rng.Hash(world.Seed, Rng.Key(a.Id), RngPurpose.Euphoria, (ulong)world.Tick ^ 0x436F6C6CUL)) * MathF.Tau;
                    Push(a, cold, new HitPush(new Vector3(MathF.Sin(angle), 0, MathF.Cos(angle)), 0, 0, -1, false));
                }

        // 3. The step, the bodies on the world's threads (each has its own scene).
        if (order.Count > 0)
        {
            var list = order.ToArray();
            Parallel.For(0, list.Length, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, world.Settings.Threads) }, i =>
            {
                if (nm_agent_step(list[i].Handle, list[i].Scene, dt) < 0) list[i].Failed = LastError();
            });
        }
        world.Profile.Part("euphoria");

        // 4. The poses back, the character where its pelvis is, the end of each reaction.
        float[] com = new float[3], comVelocity = new float[3];
        foreach (var a in order.ToArray())
        {
            if (!table.TryResolveNext(a.Id, out int slot) || table.Cold(slot) is not { Medical: { } medical } cold) { Destroy(a); continue; }
            ref var hot = ref table.Next[slot];
            a.Age += dt;
            Read(a);
            var pelvis = a.Bones[EuphoriaData.Pelvis].Position;
            hot.Position = new Vector3(pelvis.X, Ground(env, pelvis), pelvis.Z);
            hot.Velocity = Vector3.Zero;
            a.Published = Pose(a.Setup, a.Body, a.Bones, a.Root, Placement(a.Setup, hot), 1);
            if (nm_agent_has_fallen(a.Handle) != 0) a.FellAt ??= a.Age;
            nm_agent_get_com(a.Handle, com, comVelocity);
            float speed = new Vector3(comVelocity[0], comVelocity[1], comVelocity[2]).Length(), along = new Vector2(comVelocity[0], comVelocity[1]).Length();
            bool far = Vector2.Distance(new Vector2(pelvis.X, pelvis.Z), new Vector2(a.Origin.X, a.Origin.Z)) > Reach;
            bool lying = a.FellAt is { } fell && (a.Age - fell >= LieEarliest && speed < LieSpeed || a.Age - fell >= LieLatest);
            bool balancing = nm_agent_is_balancing(a.Handle) != 0;
            if (medical.Incapacitated) a.Down = true;
            // Off a ledge or a roof: falling faster than a stumble ever does, it flails on the way down (and lands fallen).
            if (!a.Down && !a.Flailing && comVelocity[2] < -FallingSpeed)
            {
                a.Flailing = true;
                nm_msg_begin(a.Handle, "highFall");
                nm_msg_set_bool(a.Handle, "start", 1);
                nm_msg_send(a.Handle, a.Scene);
            }
            if (a.Failed is not null) End(world, a, ref hot, cold, env);
            else if (a.Down)
            {
                if (lying || a.Age >= LieEarliest && speed < LieSpeed || a.Age >= DownLongest || far) HandOff(a);
            }
            else if (lying || a.FellAt is null && a.Age >= Longest && !balancing)
            {
                // Knocked over: down for a moment, then up as from a knockout.
                medical.KnockOut(FallKnockOut);
                HandOff(a);
            }
            else if (a.FellAt is null && (a.Age >= CalmEarliest && balancing && along < CalmSpeed || a.Age >= Longest || far)) End(world, a, ref hot, cold, env);
            else combat?.Hold(world, slot, now + 2);
        }

        // 5. The blends back into the animation.
        foreach (var (id, b) in blends.ToArray())
        {
            b.Left -= dt;
            if (b.Left <= 0 || !table.TryResolveNext(id, out int slot)) { blends.Remove(id); continue; }
            b.Published = Pose(b.Setup, b.Body, b.Bones, b.Root, Placement(b.Setup, table.Next[slot]), b.Left / Blend);
        }
    }

    /// <summary>
    /// Who went down since the last tick: <see cref="fresh"/> gets the slots of characters on their feet then (in <see cref="up"/>) and incapacitated now; <see cref="up"/>
    /// becomes the ones on their feet now. True when there are any (none on the first look: a loaded game's downed stay as they lie).
    /// </summary>
    bool Downs(World world)
    {
        var table = world.Characters;
        fresh.Clear();
        var next = table.Next;
        for (int slot = 0; slot < next.Length; slot++)
        {
            if (!next[slot].Alive || table.Cold(slot) is not { Medical: { } medical }) continue;
            var id = table.IdOf(slot);
            if (!medical.Incapacitated) up.Add(id);
            else if (up.Remove(id)) fresh.Add(slot);
        }
        up.RemoveWhere(id => !table.TryResolveNext(id, out _));
        return fresh.Count > 0;
    }

    readonly HashSet<CharacterId> up = [];
    readonly List<int> fresh = [];

    /// <summary>A new body for the character, in the pose its animation has now and moving as it does; null when the library refuses it.</summary>
    Agent? Start(World world, int slot, CharacterCold cold, bool down)
    {
        if (Data is not { } data || rigs.For(cold.Appearance!) is not { } setup || data.Fit(setup.Rig, cold.Appearance!.Female) is not { } body) return null;
        var id = world.Characters.IdOf(slot);
        ref var hot = ref world.Characters.Next[slot];
        var placement = Placement(setup, hot);
        var turn = Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(placement with { M41 = 0, M42 = 0, M43 = 0 }));
        var origin = hot.Position;
        var states = new float[EuphoriaData.Parts * 13];
        Quaternion rootStart;
        var rig = setup.Rig;
        var scratch = rig.Rent();
        try
        {
            // From the pose on show: the animation's, or the end of a reaction still blending out.
            RagdollSystem.PoseNow(rigs, animations, scratch, setup, cold, blends.TryGetValue(id, out var blend) ? blend.Published : null);
            RigidTransform World(int h) => new(Vector3.Transform(scratch.DerivedPosition[h], placement), Quaternion.Normalize(turn * scratch.DerivedRotation[h]));
            var velocity = Space.DirectionToDelirium(hot.Velocity) * Space.Metres;
            for (int k = 0; k < EuphoriaData.Parts; k++)
            {
                var part = body.BoneInPart[k].Inverse().Then(Space.ToDelirium(World(body.Bone[k]), origin));
                int o = k * 13;
                (states[o], states[o + 1], states[o + 2]) = (part.Position.X, part.Position.Y, part.Position.Z);
                (states[o + 3], states[o + 4], states[o + 5], states[o + 6]) = (part.Rotation.X, part.Rotation.Y, part.Rotation.Z, part.Rotation.W);
                (states[o + 7], states[o + 8], states[o + 9]) = (velocity.X, velocity.Y, velocity.Z);
            }
            rootStart = World(body.Root).Rotation;
        }
        finally { rig.Return(scratch); }
        blends.Remove(id);

        nint scene = nm_world_create();
        if (scene == 0) return null;
        AddSurroundings(scene, environment!, origin);
        float seed = Rng.Float(Rng.Hash(world.Seed, Rng.Key(id), RngPurpose.Euphoria, (ulong)world.Tick));
        nint agent = nm_agent_create(body.Build, [0, 0, 0], hot.Yaw, seed);
        if (agent == 0 || nm_agent_set_part_states(agent, states, EuphoriaData.Parts) < 0)
        {
            if (agent != 0) nm_agent_destroy(agent);
            nm_world_destroy(scene);
            return null;
        }
        var a = new Agent
        {
            Id = id, Body = body, Setup = setup, Handle = agent, Scene = scene, Origin = origin, YawStart = hot.Yaw, RootStart = rootStart,
        };
        if (!down)
        {
            nm_msg_begin(agent, "bodyBalance");
            nm_msg_set_bool(agent, "start", 1);
            nm_msg_send(agent, scene);
        }
        agents[id] = a;
        order.Add(a);
        return a;
    }

    /// <summary>The blow: an impulse on the part struck, along the hit, and the shot reaction on that part (it staggers, steps to keep its balance, and catches its fall).</summary>
    void Push(Agent a, CharacterCold cold, in HitPush push)
    {
        int k = PartOf(cold, push.Part);
        nm_agent_get_part_transforms(a.Handle, a.Parts, EuphoriaData.Parts);
        float[] at = [a.Parts[k * 7], a.Parts[k * 7 + 1], a.Parts[k * 7 + 2]];
        var dir = Space.DirectionToDelirium(push.Direction);
        // Hits on a body still reacting stack: each one before it has its balance back counts half again more, so a character set on by several goes down.
        float strength = Math.Clamp(push.Strength * (1 + StackPerHit * a.Hits), 0, StrongestHit);
        if (!a.Down && push.Strength > 0) a.Hits++;
        float size = (PushBase + PushPerStrength * strength) * (0.8f + 0.4f * push.Blunt) * (push.Ranged ? RangedPush : 1);
        nm_agent_apply_impulse(a.Handle, k, at, [dir.X * size, dir.Y * size, dir.Z * size]);
        if (a.Down) return;
        nm_msg_begin(a.Handle, "shot");
        nm_msg_set_bool(a.Handle, "start", 1);
        nm_msg_set_int(a.Handle, "bodyPart", k);
        nm_msg_set_vec3(a.Handle, "hitPoint", at);
        nm_msg_set_vec3(a.Handle, "normal", [-dir.X, -dir.Y, -dir.Z]);
        nm_msg_set_bool(a.Handle, "melee", push.Ranged ? 0 : 1);
        nm_msg_set_bool(a.Handle, "useCatchFallOnFall", 1);
        if (MaxSteps(strength) is > 0 and var steps) nm_msg_set_int(a.Handle, "maxSteps", steps);
        nm_msg_send(a.Handle, a.Scene);
    }

    /// <summary>Knocked out or killed: the muscles let go and the body falls as it was pushed.</summary>
    static void Collapse(Agent a)
    {
        a.Down = true;
        nm_msg_begin(a.Handle, "bodyBalance");
        nm_msg_set_bool(a.Handle, "start", 0);
        nm_msg_send(a.Handle, a.Scene);
        nm_msg_begin(a.Handle, "bodyRelax");
        nm_msg_set_float(a.Handle, "relaxation", 60);
        nm_msg_send(a.Handle, a.Scene);
    }

    /// <summary>The Delirium part a health part's hit lands on: the part on its bone, else by its kind (the chest for none).</summary>
    static int PartOf(CharacterCold cold, int part)
    {
        if (cold.Medical is not { } medical || (uint)part >= (uint)medical.Parts.Count) return EuphoriaData.Chest;
        var template = medical.Parts[part].Template;
        if (template.BoneName == "Bip01 Spine2") return EuphoriaData.Chest;
        int k = Array.IndexOf(EuphoriaData.PartBones, template.BoneName);
        if (k >= 0) return k;
        bool left = template.Side == BodySide.Left;
        return template.Type switch
        {
            BodyPartType.Head => EuphoriaData.Head,
            BodyPartType.Arm => left ? 14 : 18,
            BodyPartType.Leg => left ? 1 : 4,
            _ => EuphoriaData.Chest,
        };
    }

    /// <summary>Every part's bone (world space) and the root bone from the body's parts now.</summary>
    static void Read(Agent a)
    {
        nm_agent_get_part_transforms(a.Handle, a.Parts, EuphoriaData.Parts);
        for (int k = 0; k < EuphoriaData.Parts; k++)
        {
            int o = k * 7;
            var part = new RigidTransform(new Vector3(a.Parts[o], a.Parts[o + 1], a.Parts[o + 2]),
                Quaternion.Normalize(new Quaternion(a.Parts[o + 3], a.Parts[o + 4], a.Parts[o + 5], a.Parts[o + 6])));
            a.Bones[k] = Space.ToKenshi(a.Body.BoneInPart[k].Then(part), a.Origin);
        }
        a.Root = a.Body.PelvisInRoot.Inverse().Then(a.Bones[EuphoriaData.Pelvis]);
    }

    /// <summary>
    /// The bones the body sets, in the model space of <paramref name="placement"/>: the root bone (rotation and position) and the twenty bones the parts drive (rotations: the
    /// others follow from them); the rest, toes, fingers and the jaw, keep the animation's.
    /// </summary>
    internal static BonePose? Pose(CharacterPoseSetup setup, EuphoriaData.Body body, RigidTransform[] bones, RigidTransform root, Matrix4x4 placement, float weight)
    {
        if (!Matrix4x4.Invert(placement, out var toModel)) return null;
        var unturn = Quaternion.Inverse(Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(placement with { M41 = 0, M42 = 0, M43 = 0 })));
        var pose = new BonePose(setup.Rig.BoneCount) { Weight = weight };
        void Put(int h, in RigidTransform world)
        {
            pose.Set[h] = true;
            pose.Rotation[h] = Quaternion.Normalize(unturn * world.Rotation);
            pose.Position[h] = Vector3.Transform(world.Position, toModel);
        }
        Put(body.Root, root);
        for (int k = 0; k < EuphoriaData.Parts; k++)
            if (k != EuphoriaData.SpineHigh) Put(body.Bone[k], bones[k]);   // Spine2 is the chest's
        return pose;
    }

    static Matrix4x4 Placement(CharacterPoseSetup setup, in CharacterHot hot) => CharacterRigs.Placement(setup, hot.Position, hot.Yaw, 1);

    static float Ground(IRagdollEnvironment env, Vector3 at)
    {
        float y = env.GroundHeight(at.X, at.Z, at.Y);
        return float.IsFinite(y) ? y : at.Y;
    }

    /// <summary>A reaction that ends on its feet: the character faces the way its body turned, stands where it is, and its bones blend back into the animation.</summary>
    void End(World world, Agent a, ref CharacterHot hot, CharacterCold cold, IRagdollEnvironment env)
    {
        if (cold.Medical is { Incapacitated: true }) { HandOff(a); return; }
        var turned = Vector3.Transform(new Vector3(MathF.Sin(a.YawStart), 0, MathF.Cos(a.YawStart)), Quaternion.Normalize(a.Root.Rotation * Quaternion.Inverse(a.RootStart)));
        if (turned.X * turned.X + turned.Z * turned.Z > 0.1f) hot.Yaw = MathF.Atan2(turned.X, turned.Z);
        var pelvis = a.Bones[EuphoriaData.Pelvis].Position;
        hot.Position = new Vector3(pelvis.X, Ground(env, pelvis), pelvis.Z);
        var b = new Blending { Setup = a.Setup, Body = a.Body, Bones = (RigidTransform[])a.Bones.Clone(), Root = a.Root };
        b.Published = Pose(b.Setup, b.Body, b.Bones, b.Root, Placement(b.Setup, hot), 1);
        blends[a.Id] = b;
        Destroy(a);
    }

    /// <summary>A fallen body goes to its ragdoll, which starts from its pose (published this tick).</summary>
    void HandOff(Agent a)
    {
        if (a.Published is { } pose) handed[a.Id] = pose;
        Destroy(a);
    }

    void Destroy(Agent a)
    {
        nm_agent_destroy(a.Handle);
        nm_world_destroy(a.Scene);
        agents.Remove(a.Id);
        order.Remove(a);
    }

    /// <summary>The body's scene: the ground round <paramref name="origin"/> as a grid, and the buildings' collision there.</summary>
    static void AddSurroundings(nint scene, IRagdollEnvironment env, Vector3 origin)
    {
        int n = (int)(2 * GroundHalf / GridStep) + 1;
        var positions = new float[n * n * 3];
        for (int j = 0; j < n; j++)
            for (int i = 0; i < n; i++)
            {
                float x = origin.X - GroundHalf + i * GridStep, z = origin.Z - GroundHalf + j * GridStep;
                float y = env.GroundHeight(x, z, origin.Y + 5);
                var d = Space.ToDelirium(new Vector3(x, float.IsFinite(y) ? y : origin.Y, z), origin);
                int o = (j * n + i) * 3;
                (positions[o], positions[o + 1], positions[o + 2]) = (d.X, d.Y, d.Z);
            }
        var triangles = new uint[(n - 1) * (n - 1) * 6];
        int t = 0;
        for (int j = 0; j < n - 1; j++)
            for (int i = 0; i < n - 1; i++)
            {
                uint a = (uint)(j * n + i), b = a + 1, c = a + 1 + (uint)n, d = a + (uint)n;
                // Facing up in Delirium's space (Kenshi's +Z is its -Y).
                (triangles[t], triangles[t + 1], triangles[t + 2], triangles[t + 3], triangles[t + 4], triangles[t + 5]) = (a, c, b, a, d, c);
                t += 6;
            }
        nm_world_add_mesh(scene, positions, n * n, triangles, triangles.Length / 3, null);

        var walls = new List<Vector3>();
        env.Buildings(origin - new Vector3(GroundHalf, 60, GroundHalf), origin + new Vector3(GroundHalf, 60, GroundHalf), walls);
        if (walls.Count < 3) return;
        int count = walls.Count / 3;
        var points = new float[count * 9];
        var indices = new uint[count * 3];
        for (int v = 0; v < count * 3; v++)
        {
            var d = Space.ToDelirium(walls[v], origin);
            (points[3 * v], points[3 * v + 1], points[3 * v + 2]) = (d.X, d.Y, d.Z);
            indices[v] = (uint)v;
        }
        nm_world_add_mesh(scene, points, count * 3, indices, count, null);
    }

    public void Dispose()
    {
        foreach (var a in order.ToArray()) Destroy(a);
    }
}
