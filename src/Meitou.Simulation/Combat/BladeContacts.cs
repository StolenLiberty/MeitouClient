using System.Numerics;
using Meitou.Data.Characters;
using Meitou.Data.Ogre;
using Meitou.Simulation.Ragdolls;

namespace Meitou.Simulation.Combat;

/// <summary>Where a fighter's blade last touched the body it swings at (Meitou, docs/euphoria.md "Blade contact"). Asked in the serial phases.</summary>
public interface IBladeContacts
{
    /// <summary>The last touch of <paramref name="attacker"/>'s blade on <paramref name="defender"/> in the last few ticks: the point (world), the way the blade moved, and the
    /// euphoria part touched; false when it did not touch.</summary>
    bool Touch(World world, int attacker, int defender, out Vector3 at, out Vector3 direction, out int body);

    /// <summary>Blow <paramref name="blow"/> of <paramref name="attacker"/>'s swing landed on <paramref name="defender"/>: the swing stops on the body, and the fighter
    /// comes back to its stance instead of following through.</summary>
    void Struck(World world, int attacker, int defender, int blow);
}

/// <summary>
/// Blades that meet bodies (Meitou-only, docs/euphoria.md "Blade contact"). Hits are still decided by combat's rolls; this makes them look it. Every tick, for each character
/// swinging a held weapon at a target near the camera: the blade (along its mesh from the hand) is traced against the target's body (capsules over its bones, in the pose on
/// show, a euphoria body's included). A blade that would go into the body is held on its surface, the side it first touched, by turning the arm at the shoulder; a blow that
/// lands freezes the swing where the blade touches for <see cref="HitStop"/> and lets go over <see cref="Release"/>; and the touch (point, the blade's motion, the part)
/// is where the hit pushes the body. The arm and the frozen swing are published as bone overrides. Runs serially in the slow-world step after the euphoria bodies.
/// </summary>
public sealed class BladeContactSystem : ITickSystem, IRagdollSource, IBladeContacts
{
    /// <summary>
    /// Seconds (engine choices): a landed blow holds the swing where the blade touched for <see cref="HitStop"/>, then the fighter eases back over <see cref="Recover"/> into
    /// the stance it swung from and holds it; it eases back into the animation over <see cref="Release"/> when the swing ends, or half way to a combo's next blow.
    /// </summary>
    public const float HitStop = 0.12f, Recover = 0.35f, Release = 0.2f;
    /// <summary>World units: swings are traced only this near the camera focus (they are a look), the blade's thickness, and a touch's reach (a blade this close counts).</summary>
    const float Near = 900, BladeRadius = 0.25f, TouchGap = 0.6f;
    /// <summary>Ticks a touch counts for a blow; a landed blow waits this long for its blade to reach the body before it freezes nothing.</summary>
    const int TouchTicks = 8, StrikeWindow = 6;
    /// <summary>Where a blade starts along its mesh's +Y from the hand, in world units (the grip is below it).</summary>
    const float BladeStart = 1.5f;

    readonly CharacterRigs rigs;
    IAnimationSource? animations;
    CombatSystem? combat;
    PopulationSystem? population;
    IRagdollState? ragdolls;
    readonly Dictionary<CharacterId, Swing> swings = [];
    readonly Dictionary<string, float> bladeTips = new(StringComparer.OrdinalIgnoreCase);

    public BladeContactSystem(CharacterRigs rigs) => this.rigs = rigs;

    /// <summary>Tests: also work out where the shown blade's tip is (<c>Swing.ShownTip</c>).</summary>
    internal static bool Debug;

    /// <summary>Off: no tracing, no freezing (the settings' toggle with the euphoria bodies).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>The body capsules: from bone, to bone, radius (world units), the euphoria part it is (docs/euphoria.md).</summary>
    static readonly (string From, string To, float Radius, int Part)[] Capsules =
    [
        ("Bip01 Pelvis", "Bip01 Spine1", 1.7f, 7), ("Bip01 Spine1", "Bip01 Neck", 1.8f, 10), ("Bip01 Neck", "Bip01 HeadNub", 1.1f, 12),
        ("Bip01 L UpperArm", "Bip01 L Forearm", 0.6f, 14), ("Bip01 L Forearm", "Bip01 L Hand", 0.5f, 15),
        ("Bip01 R UpperArm", "Bip01 R Forearm", 0.6f, 18), ("Bip01 R Forearm", "Bip01 R Hand", 0.5f, 19),
        ("Bip01 L Thigh", "Bip01 L Calf", 0.85f, 1), ("Bip01 L Calf", "Bip01 L Foot", 0.6f, 2),
        ("Bip01 R Thigh", "Bip01 R Calf", 0.85f, 4), ("Bip01 R Calf", "Bip01 R Foot", 0.6f, 5),
    ];

    sealed class Swing
    {
        public int Defender = -1;
        /// <summary>The side the blade first touched (from the body's axis to the blade), held while it stays in touch.</summary>
        public Vector3? Side;
        public int Untouched;
        public Vector3 TouchAt, TouchDirection;
        public int TouchBody = -1;
        public long TouchTick = long.MinValue;
        /// <summary>The blade's point nearest the body last tick (world), for its motion.</summary>
        public Vector3? Before;
        public long StrikeUntil = long.MinValue, FreezeUntil = long.MinValue, RecoverUntil = long.MinValue, ResumeAt = long.MaxValue, ReleaseUntil = long.MinValue;
        public int Blow, AttackStart = int.MinValue;
        /// <summary>The swing where the blade touched, the stance it started from (its first tick), and the pose last shown (released from), model space.</summary>
        public Quaternion[]? Frozen, Stance, Shown;
        public Vector3 FrozenRoot, StanceRoot, ShownRoot;
        public bool Holding(long tick) => Frozen is not null && tick < ReleaseUntil;
        public BonePose? Published;
        /// <summary>This tick: how deep the animated blade went into the body, and how deep it is once held off (negative: the gap); for tests.</summary>
        public float RawDepth = float.NegativeInfinity, HeldDepth = float.NegativeInfinity;
        public Vector3 RawTip, HeldTip, RawBase, HeldBase, ShownTip;
        public List<(Vector3 A, Vector3 B, float Radius, int Part)>? LastBody;
    }

    // ---- what combat and the world ask ----

    public BonePose? Publish(CharacterId id) => swings.TryGetValue(id, out var s) ? s.Published : null;

    public bool Touch(World world, int attacker, int defender, out Vector3 at, out Vector3 direction, out int body)
    {
        (at, direction, body) = (default, default, -1);
        if (!swings.TryGetValue(world.Characters.IdOf(attacker), out var s) || s.Defender != defender || world.Tick - s.TouchTick > TouchTicks) return false;
        (at, direction, body) = (s.TouchAt, s.TouchDirection, s.TouchBody);
        return true;
    }

    public void Struck(World world, int attacker, int defender, int blow)
    {
        if (!Enabled) return;
        var id = world.Characters.IdOf(attacker);
        if (!swings.TryGetValue(id, out var s)) swings[id] = s = new Swing();
        if (s.Holding(world.Tick)) return;
        s.Defender = defender;
        s.Blow = blow;
        s.StrikeUntil = world.Tick + StrikeWindow;
    }

    // ---- the tick ----

    public void SlowWorld(World world)
    {
        animations ??= world.Systems.OfType<IAnimationSource>().FirstOrDefault();
        combat ??= world.Systems.OfType<CombatSystem>().FirstOrDefault();
        population ??= world.Systems.OfType<PopulationSystem>().FirstOrDefault();
        ragdolls ??= world.Systems.OfType<IRagdollState>().FirstOrDefault();
        if (combat is null || !Enabled) { swings.Clear(); return; }
        var table = world.Characters;
        long tick = world.Tick;
        int freezeTicks = Math.Max(1, (int)MathF.Round(HitStop / world.TickSeconds)), recoverTicks = Math.Max(1, (int)MathF.Round(Recover / world.TickSeconds)),
            releaseTicks = Math.Max(1, (int)MathF.Round(Release / world.TickSeconds));
        var focus = population?.Focus;

        // A swing ends with its fighter: gone, down (knocked out or dead: its ragdoll is its pose now), carried or laid in something.
        foreach (var id in swings.Keys.ToArray())
            if (!table.TryResolveNext(id, out int at) || table.Cold(at) is not { Medical: { Incapacitated: false }, InSomething: Locks.HeldState.None } held || !held.CarriedBy.IsNone)
                swings.Remove(id);

        var next = table.Next;
        for (int slot = 0; slot < next.Length; slot++)
        {
            if (!next[slot].Alive || table.Cold(slot) is not { Appearance: { } look, Fighter: { IsAnimal: false }, Medical: { Incapacitated: false } } cold) continue;
            var id = table.IdOf(slot);
            swings.TryGetValue(id, out var s);
            var playing = combat.Playing(id, tick);
            int target = combat.StateOf(id).TargetSlot;
            bool swinging = playing is { IsAttack: true } && target >= 0 && target != slot;
            bool holding = s is not null && s.Holding(tick);
            if (!swinging && !holding) { if (s is not null) swings.Remove(id); continue; }
            if (focus is { } f && Vector3.DistanceSquared(next[slot].Position, f) > Near * Near) { if (s is not null) swings.Remove(id); continue; }
            // A body the euphoria or the ragdoll moves is theirs.
            if (ragdolls?.WasWhole(id) == true || world.RagdollPose(id) is { } other && other != s?.Published) { if (s is not null) swings.Remove(id); continue; }
            if (rigs.For(look) is not { } setup) continue;
            s ??= swings[id] = new Swing();
            if (swinging) s.Defender = target;
            Trace(world, slot, cold, setup, s, freezeTicks, recoverTicks, releaseTicks);
        }
    }

    /// <summary>
    /// One swing's tick: the pose to show (the animation, or after a landed blow the frozen swing easing into the stance, holding it, then easing back into the animation),
    /// the blade in that pose held off the body by the arm, the touch, and the published overrides.
    /// </summary>
    void Trace(World world, int slot, CharacterCold cold, CharacterPoseSetup setup, Swing s, int freezeTicks, int recoverTicks, int releaseTicks)
    {
        var table = world.Characters;
        long tick = world.Tick;
        var rig = setup.Rig;
        ref var hot = ref table.Next[slot];
        var placement = CharacterRigs.Placement(setup, hot.Position, hot.Yaw, 1);
        var turn = Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(placement with { M41 = 0, M42 = 0, M43 = 0 }));
        var animated = rig.Rent();
        var shown = rig.Rent();
        try
        {
            RagdollSystem.PoseNow(rigs, animations, animated, setup, cold);
            int root = rig.Skeleton.Bones.First(b => b.Parent is null).Handle;
            var fight = combat!.StateOf(table.IdOf(slot));
            bool newSwing = fight.AttackTech >= 0 && fight.AttackStart != s.AttackStart;
            // A new swing: its first pose is the stance it swings from (the clip has barely blended in).
            if (newSwing && s.Frozen is null)
            {
                s.AttackStart = fight.AttackStart;
                s.Stance = Capture(rig, animated, out s.StanceRoot);
            }
            // After a landed blow: back into the animation once the stance is reached and the swing is over, when a combo's next blow comes, or when a new swing starts.
            if (s.Frozen is not null && s.ReleaseUntil == long.MaxValue
                && (tick >= s.ResumeAt || newSwing || tick >= s.RecoverUntil && fight.AttackTech < 0 || tick >= s.FreezeUntil + 3 * recoverTicks))
            {
                s.ResumeAt = tick;
                s.ReleaseUntil = tick + releaseTicks;
                if (newSwing) { s.AttackStart = fight.AttackStart; s.Stance = Capture(rig, animated, out s.StanceRoot); }
            }

            // The pose to show, before the arm is held off the body.
            var want = Wanted(rig, animated, s, root, tick, recoverTicks, releaseTicks, out var wantRoot);
            var pose = animated;
            if (want is not null)
            {
                RagdollSystem.PoseNow(rigs, animations, shown, setup, cold, Full(rig, want, root, wantRoot));
                pose = shown;
            }

            int? upper = rig.Handle("Bip01 R UpperArm"), prop = rig.Handle("Bip01 Prop2");
            float tip = cold.Appearance is { } look ? BladeTip(look) : 0;
            Quaternion? arm = null;
            bool touching = false;
            var body = (uint)s.Defender < (uint)table.HighWater && table.Next[s.Defender].Alive ? Body(world, s.Defender) : null;
            if (upper is int u && prop is int p && tip > BladeStart && body is not null)
            {
                Vector3 At(int h) => Vector3.Transform(pose.DerivedPosition[h], placement);
                var shoulder = At(u);
                var propWorld = Quaternion.Normalize(turn * pose.DerivedRotation[p]);
                Vector3 baseAt = At(p) + Vector3.Transform(new Vector3(0, BladeStart, 0), propWorld), tipAt = At(p) + Vector3.Transform(new Vector3(0, tip, 0), propWorld);
                var correction = HoldOff(body, shoulder, baseAt, tipAt, s, tick, out touching);
                if (correction != Quaternion.Identity) arm = Quaternion.Normalize(Quaternion.Inverse(turn) * correction * turn);
                if (Debug)
                {
                    var (raw, _, _, _, _) = Deepest(body, baseAt, tipAt, null);
                    Vector3 h0 = shoulder + Vector3.Transform(baseAt - shoulder, correction), h1 = shoulder + Vector3.Transform(tipAt - shoulder, correction);
                    var (held, _, _, _, _) = Deepest(body, h0, h1, null);
                    (s.RawDepth, s.HeldDepth, s.RawTip, s.HeldTip, s.RawBase, s.HeldBase, s.LastBody) = (raw, held, tipAt, h1, baseAt, h0, body);
                }
            }
            if (!touching) { s.Before = null; if (++s.Untouched > 3) s.Side = null; }
            else s.Untouched = 0;
            // The arm's turn (model space) on every bone from the upper arm down: a full override sets each bone's rotation itself.
            if (want is not null && arm is { } wantArm && upper is int wu) TurnArm(rig, want, wu, wantArm);

            // A landed blow freezes the swing as soon as its blade is on the body; then the fighter comes back to its stance.
            if (touching && tick <= s.StrikeUntil && s.Frozen is null)
            {
                s.Frozen = Capture(rig, animated, out s.FrozenRoot);
                if (arm is { } a && upper is int uu) TurnArm(rig, s.Frozen, uu, a);
                s.Stance ??= s.Frozen;
                s.FreezeUntil = tick + freezeTicks;
                s.RecoverUntil = s.FreezeUntil + recoverTicks;
                s.ResumeAt = ComboResume(fight, s.Blow);
                s.ReleaseUntil = long.MaxValue;
                s.StrikeUntil = long.MinValue;
                want = (Quaternion[])s.Frozen.Clone();
                wantRoot = s.FrozenRoot;
            }

            // Published: every bone while a landed blow holds the swing, else the arm alone (when it is held off the body).
            if (want is not null)
            {
                s.Published = Full(rig, want, root, wantRoot);
                if (s.ReleaseUntil == long.MaxValue) (s.Shown, s.ShownRoot) = (want, wantRoot);
            }
            else if (arm is { } held && upper is int u3)
            {
                // The upper arm alone: the bones below it follow it.
                var only = new BonePose(rig.BoneCount);
                only.Set[u3] = true;
                only.Rotation[u3] = Quaternion.Normalize(held * animated.DerivedRotation[u3]);
                s.Published = only;
            }
            else s.Published = null;
            if (s.Frozen is not null && tick >= s.ReleaseUntil) (s.Frozen, s.Shown) = (null, null);

            if (Debug && prop is int shownProp)
            {
                RagdollSystem.PoseNow(rigs, animations, shown, setup, cold, s.Published);
                s.ShownTip = Vector3.Transform(shown.DerivedPosition[shownProp] + Vector3.Transform(new Vector3(0, 8, 0), shown.DerivedRotation[shownProp]), placement);
            }
        }
        finally { rig.Return(animated); rig.Return(shown); }
    }

    /// <summary>Turns <paramref name="bone"/> and every bone under it by <paramref name="turn"/> (model space) in a set of model-space rotations.</summary>
    static void TurnArm(SkeletonRig rig, Quaternion[] rotation, int bone, Quaternion turn)
    {
        foreach (int h in rig.Order)
            if (Under(rig, h, bone)) rotation[h] = Quaternion.Normalize(turn * rotation[h]);
    }

    static bool Under(SkeletonRig rig, int h, int ancestor)
    {
        for (int b = h; b >= 0; b = rig.Parent[b])
            if (b == ancestor) return true;
        return false;
    }

    static BonePose Full(SkeletonRig rig, Quaternion[] rotation, int root, Vector3 rootAt)
    {
        var pose = new BonePose(rig.BoneCount);
        for (int h = 0; h < rig.BoneCount; h++) (pose.Set[h], pose.Rotation[h]) = (true, rotation[h]);
        pose.Position[root] = rootAt;
        return pose;
    }

    /// <summary>
    /// After a landed blow, every bone's rotation to show (null otherwise): the frozen swing, then easing into the stance and holding it, then easing from the pose last shown
    /// into the animation.
    /// </summary>
    static Quaternion[]? Wanted(SkeletonRig rig, PoseScratch animated, Swing s, int root, long tick, int recoverTicks, int releaseTicks, out Vector3 rootAt)
    {
        rootAt = default;
        if (s.Frozen is not { } frozen || s.Stance is not { } stance) return null;
        var rotation = new Quaternion[rig.BoneCount];
        if (s.ReleaseUntil != long.MaxValue)
        {
            float f = Ease((tick - s.ResumeAt) / (float)releaseTicks);
            var from = s.Shown ?? stance;
            for (int h = 0; h < rig.BoneCount; h++) rotation[h] = Mix(from[h], animated.DerivedRotation[h], f);
            rootAt = Vector3.Lerp(s.Shown is null ? s.StanceRoot : s.ShownRoot, animated.DerivedPosition[root], f);
            return rotation;
        }
        float g = tick < s.FreezeUntil ? 0 : Ease((tick - s.FreezeUntil) / (float)recoverTicks);
        for (int h = 0; h < rig.BoneCount; h++) rotation[h] = Mix(frozen[h], stance[h], g);
        rootAt = Vector3.Lerp(s.FrozenRoot, s.StanceRoot, g);
        return rotation;
    }

    /// <summary>
    /// The turn (world, about the shoulder) that holds the blade <paramref name="b0"/>-<paramref name="b1"/> off the body, on the side it first touched; records the touch.
    /// </summary>
    static Quaternion HoldOff(List<(Vector3 A, Vector3 B, float Radius, int Part)> body, Vector3 shoulder, Vector3 b0, Vector3 b1, Swing s, long tick, out bool touching)
    {
        var correction = Quaternion.Identity;
        touching = false;
        for (int pass = 0; pass < 8; pass++)
        {
            Vector3 c0 = shoulder + Vector3.Transform(b0 - shoulder, correction), c1 = shoulder + Vector3.Transform(b1 - shoulder, correction);
            var (depth, point, normal, part, axisPoint) = Deepest(body, c0, c1, s.Side);
            if (part < 0) break;
            if (depth > -TouchGap && !touching)
            {
                touching = true;
                s.Side ??= normal;
                var outward = point - axisPoint;
                var surface = axisPoint + (outward.LengthSquared() > 1e-8f ? Vector3.Normalize(outward) : normal) * body.First(c => c.Part == part).Radius;
                var moved = s.Before is { } before ? point - before : Vector3.Zero;
                s.TouchAt = surface;
                s.TouchDirection = moved.LengthSquared() > 1e-4f ? Vector3.Normalize(moved) : -normal;
                s.TouchBody = part;
                s.TouchTick = tick;
                s.Before = point;
            }
            if (depth <= 0) break;
            // Turn the arm about the shoulder so the touching point moves out by the depth, along the side it touched.
            var lever = point - shoulder;
            var push = (s.Side ?? normal) * depth;
            var axis = Vector3.Cross(lever, push);
            float reach = axis.Length() / MathF.Max(push.Length(), 1e-4f);
            if (axis.LengthSquared() < 1e-8f || reach < 0.5f) break;
            correction = Quaternion.Normalize(Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), MathF.Min(depth / reach, 0.6f)) * correction);
        }
        return correction;
    }

    /// <summary>When a combo's next blow needs the animation back: half way from the blow that landed to the next; none after the last blow.</summary>
    long ComboResume(in CombatSlot fight, int blow)
    {
        if (fight.AttackTech < 0 || fight.AttackTicks <= 0) return long.MaxValue;
        var tech = combat!.Techniques[fight.AttackTech];
        if (blow >= tech.Blows) return long.MaxValue;
        return fight.AttackStart + (long)MathF.Ceiling((tech.StrikeProgress(blow) + tech.StrikeProgress(blow + 1)) * 0.5f * fight.AttackTicks);
    }

    static Quaternion[] Capture(SkeletonRig rig, PoseScratch scratch, out Vector3 root)
    {
        var rotation = new Quaternion[rig.BoneCount];
        for (int h = 0; h < rig.BoneCount; h++) rotation[h] = scratch.DerivedRotation[h];
        root = scratch.DerivedPosition[rig.Skeleton.Bones.First(b => b.Parent is null).Handle];
        return rotation;
    }

    static Quaternion Mix(Quaternion a, Quaternion b, float f)
    {
        if (Quaternion.Dot(a, b) < 0) b = -b;
        return Quaternion.Normalize(Quaternion.Lerp(a, b, f));
    }

    /// <summary>Smooth in and out (no jolt where a blend starts or ends).</summary>
    static float Ease(float t)
    {
        t = Math.Clamp(t, 0, 1);
        return t * t * (3 - 2 * t);
    }

    /// <summary>The defender's body as capsules (world) in the pose on show: a euphoria body's or a ragdoll's bones over its animation.</summary>
    List<(Vector3 A, Vector3 B, float Radius, int Part)>? Body(World world, int slot)
    {
        var table = world.Characters;
        if (table.Cold(slot) is not { Appearance: { } look } cold || rigs.For(look) is not { } setup) return null;
        var rig = setup.Rig;
        ref var hot = ref table.Next[slot];
        var placement = CharacterRigs.Placement(setup, hot.Position, hot.Yaw, cold.Fighter is { IsAnimal: true } && cold.Medical is { } m ? m.ScaleK : 1);
        var scratch = rig.Rent();
        try
        {
            var id = table.IdOf(slot);
            RagdollSystem.PoseNow(rigs, animations, scratch, setup, cold, world.RagdollPose(id));
            var list = new List<(Vector3, Vector3, float, int)>(Capsules.Length);
            foreach (var (from, to, radius, part) in Capsules)
                if (rig.Handle(from) is int a && rig.Handle(to) is int b)
                    list.Add((Vector3.Transform(scratch.DerivedPosition[a], placement), Vector3.Transform(scratch.DerivedPosition[b], placement), radius, part));
            return list.Count > 0 ? list : null;
        }
        finally { rig.Return(scratch); }
    }

    /// <summary>
    /// The capsule the blade goes deepest into: how far its nearest point must move to clear it (along <paramref name="side"/> when given, else straight out; negative: the gap),
    /// that point, the way out, the part, and the nearest point on the capsule's axis.
    /// </summary>
    static (float Depth, Vector3 Point, Vector3 Normal, int Part, Vector3 Axis) Deepest(List<(Vector3 A, Vector3 B, float Radius, int Part)> body, Vector3 b0, Vector3 b1, Vector3? side)
    {
        (float Depth, Vector3 Point, Vector3 Normal, int Part, Vector3 Axis) best = (float.NegativeInfinity, default, default, -1, default);
        foreach (var (a, b, radius, part) in body)
        {
            var (p, q) = Closest(b0, b1, a, b);
            var v = p - q;
            float r = radius + BladeRadius, d = v.Length();
            var normal = d > 1e-4f ? v / d : side ?? Vector3.UnitY;
            float depth;
            if (side is { } n)
            {
                float along = Vector3.Dot(v, n);
                var across = v - along * n;
                float w2 = across.LengthSquared();
                depth = w2 >= r * r ? r - MathF.Sqrt(w2) - MathF.Max(0, along) : MathF.Sqrt(r * r - w2) - along;
            }
            else depth = r - d;
            if (depth > best.Depth) best = (depth, p, normal, part, q);
        }
        return best;
    }

    /// <summary>The nearest points of segments p0-p1 and q0-q1.</summary>
    static (Vector3, Vector3) Closest(Vector3 p0, Vector3 p1, Vector3 q0, Vector3 q1)
    {
        Vector3 d1 = p1 - p0, d2 = q1 - q0, r = p0 - q0;
        float a = Vector3.Dot(d1, d1), e = Vector3.Dot(d2, d2), f = Vector3.Dot(d2, r);
        float s, t;
        if (a <= 1e-8f && e <= 1e-8f) return (p0, q0);
        if (a <= 1e-8f) { s = 0; t = Math.Clamp(f / e, 0, 1); }
        else
        {
            float c = Vector3.Dot(d1, r);
            if (e <= 1e-8f) { t = 0; s = Math.Clamp(-c / a, 0, 1); }
            else
            {
                float b = Vector3.Dot(d1, d2), denom = a * e - b * b;
                s = denom > 1e-8f ? Math.Clamp((b * f - c * e) / denom, 0, 1) : 0;
                t = (b * s + f) / e;
                if (t < 0) { t = 0; s = Math.Clamp(-c / a, 0, 1); }
                else if (t > 1) { t = 1; s = Math.Clamp((b - c) / a, 0, 1); }
            }
        }
        return (p0 + d1 * s, q0 + d2 * t);
    }

    /// <summary>How far along +Y from the hand the held weapon's mesh reaches (its tip, world units); 0 without one in the hands.</summary>
    float BladeTip(CharacterAppearance look)
    {
        foreach (var part in look.Parts)
        {
            if (part.Slot != AttachSlot.Weapon || part.Point != "hands" || part.Shown is WeaponShown.Sheathed or WeaponShown.BackSheathed or WeaponShown.RangedSheathed or WeaponShown.RangedDrawn) continue;
            lock (bladeTips)
            {
                if (bladeTips.TryGetValue(part.Mesh, out float known)) return known;
                float tip = 0;
                try
                {
                    if (rigs.FindMesh(part.Mesh) is { } path)
                    {
                        var mesh = OgreMeshReader.ReadFile(path);
                        foreach (var sub in mesh.SubMeshes)
                            foreach (var v in (sub.UseSharedVertices ? mesh.SharedVertexData : sub.VertexData)?.ReadPositions() ?? [])
                                tip = MathF.Max(tip, v.Y);
                    }
                }
                catch (Exception e) when (e is IOException or OgreFormatException or EndOfStreamException or InvalidDataException) { tip = 0; }
                return bladeTips[part.Mesh] = tip;
            }
        }
        return 0;
    }
}
