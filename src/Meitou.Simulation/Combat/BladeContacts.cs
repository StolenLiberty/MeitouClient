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

    /// <summary>A blow of <paramref name="attacker"/> landed on <paramref name="defender"/>: its swing stops on the body for a moment.</summary>
    void Struck(World world, int attacker, int defender);
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
    /// <summary>Seconds a landed blow holds the swing where it touched, and the release back into the animation after (engine choices).</summary>
    public const float HitStop = 0.12f, Release = 0.1f;
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
        public long StrikeUntil = long.MinValue, FreezeUntil = long.MinValue, ReleaseUntil = long.MinValue;
        public Quaternion[]? Frozen;
        public Vector3 FrozenRoot;
        public BonePose? Published;
        /// <summary>This tick: how deep the animated blade went into the body, and how deep it is once held off (negative: the gap); for tests.</summary>
        public float RawDepth = float.NegativeInfinity, HeldDepth = float.NegativeInfinity;
        public Vector3 RawTip, HeldTip, RawBase, HeldBase;
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

    public void Struck(World world, int attacker, int defender)
    {
        if (!Enabled) return;
        var id = world.Characters.IdOf(attacker);
        if (!swings.TryGetValue(id, out var s)) swings[id] = s = new Swing();
        if (world.Tick < s.ReleaseUntil) return;
        s.Defender = defender;
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
        int freezeTicks = Math.Max(1, (int)MathF.Round(HitStop / world.TickSeconds)), releaseTicks = Math.Max(1, (int)MathF.Round(Release / world.TickSeconds));
        var focus = population?.Focus;

        foreach (var id in swings.Keys.ToArray())
            if (!table.TryResolveNext(id, out _)) swings.Remove(id);

        var next = table.Next;
        for (int slot = 0; slot < next.Length; slot++)
        {
            if (!next[slot].Alive || table.Cold(slot) is not { Appearance: { } look, Fighter: { IsAnimal: false }, Medical: { Incapacitated: false } } cold) continue;
            var id = table.IdOf(slot);
            swings.TryGetValue(id, out var s);
            var playing = combat.Playing(id, tick);
            int target = combat.StateOf(id).TargetSlot;
            bool swinging = playing is { IsAttack: true } && target >= 0 && target != slot;
            bool holding = s is not null && tick < s.ReleaseUntil;
            if (!swinging && !holding) { if (s is not null) swings.Remove(id); continue; }
            if (focus is { } f && Vector3.DistanceSquared(next[slot].Position, f) > Near * Near) { if (s is not null) swings.Remove(id); continue; }
            // A body the euphoria or the ragdoll moves is theirs.
            if (ragdolls?.WasWhole(id) == true || world.RagdollPose(id) is { } other && other != s?.Published) { if (s is not null) swings.Remove(id); continue; }
            if (rigs.For(look) is not { } setup) continue;
            s ??= swings[id] = new Swing();
            if (swinging) s.Defender = target;
            Trace(world, slot, cold, setup, s, freezeTicks, releaseTicks);
        }
    }

    /// <summary>One swing's tick: the arm held off the body, the touch, the freeze, and the published pose.</summary>
    void Trace(World world, int slot, CharacterCold cold, CharacterPoseSetup setup, Swing s, int freezeTicks, int releaseTicks)
    {
        var table = world.Characters;
        long tick = world.Tick;
        var rig = setup.Rig;
        ref var hot = ref table.Next[slot];
        var placement = CharacterRigs.Placement(setup, hot.Position, hot.Yaw, 1);
        var turn = Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(placement with { M41 = 0, M42 = 0, M43 = 0 }));
        var scratch = rig.Rent();
        try
        {
            RagdollSystem.PoseNow(rigs, animations, scratch, setup, cold);
            int? upper = rig.Handle("Bip01 R UpperArm"), prop = rig.Handle("Bip01 Prop2");
            float tip = cold.Appearance is { } look ? BladeTip(look) : 0;
            Quaternion? arm = null;
            var body = (uint)s.Defender < (uint)table.HighWater && table.Next[s.Defender].Alive ? Body(world, s.Defender) : null;
            if (upper is int u && prop is int p && tip > BladeStart && body is not null)
            {
                Vector3 World(int h) => Vector3.Transform(scratch.DerivedPosition[h], placement);
                var shoulder = World(u);
                var upperWorld = Quaternion.Normalize(turn * scratch.DerivedRotation[u]);
                // The blade in the shoulder's frame, so a turn of the arm turns it about the shoulder.
                var propWorld = Quaternion.Normalize(turn * scratch.DerivedRotation[p]);
                var propAt = World(p);
                Vector3 baseAt = propAt + Vector3.Transform(new Vector3(0, BladeStart, 0), propWorld), tipAt = propAt + Vector3.Transform(new Vector3(0, tip, 0), propWorld);
                var correction = Quaternion.Identity;
                bool touching = false;
                for (int pass = 0; pass < 8; pass++)
                {
                    Vector3 b0 = shoulder + Vector3.Transform(baseAt - shoulder, correction), b1 = shoulder + Vector3.Transform(tipAt - shoulder, correction);
                    var (depth, point, normal, part, axisPoint) = Deepest(body, b0, b1, s.Side);
                    if (part < 0) break;
                    if (depth > -TouchGap)
                    {
                        touching = true;
                        s.Side ??= normal;
                        var surface = axisPoint + Vector3.Normalize(point - axisPoint == Vector3.Zero ? normal : point - axisPoint) * (body.First(c => c.Part == part).Radius);
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
                {
                    var (raw, _, _, _, _) = Deepest(body, baseAt, tipAt, null);
                    Vector3 h0 = shoulder + Vector3.Transform(baseAt - shoulder, correction), h1 = shoulder + Vector3.Transform(tipAt - shoulder, correction);
                    var (held, _, _, _, _) = Deepest(body, h0, h1, null);
                    (s.RawDepth, s.HeldDepth, s.RawTip, s.HeldTip, s.RawBase, s.HeldBase, s.LastBody) = (raw, held, tipAt, h1, baseAt, h0, body);
                }
                if (!touching) { s.Before = null; if (++s.Untouched > 3) s.Side = null; }
                else s.Untouched = 0;
                if (correction != Quaternion.Identity) arm = Quaternion.Normalize(Quaternion.Inverse(turn) * (correction * upperWorld));
                // A landed blow freezes the swing as soon as its blade is on the body.
                if (touching && tick <= s.StrikeUntil && s.Frozen is null)
                {
                    s.Frozen = new Quaternion[rig.BoneCount];
                    for (int h = 0; h < rig.BoneCount; h++) s.Frozen[h] = scratch.DerivedRotation[h];
                    if (arm is { } a && upper is int uu) s.Frozen[uu] = a;
                    var root = rig.Skeleton.Bones.First(b => b.Parent is null).Handle;
                    s.FrozenRoot = scratch.DerivedPosition[root];
                    s.FreezeUntil = tick + freezeTicks;
                    s.ReleaseUntil = s.FreezeUntil + releaseTicks;
                    s.StrikeUntil = long.MinValue;
                }
            }
            s.Published = Pose(rig, scratch, s, arm, upper, tick, releaseTicks);
            if (tick >= s.ReleaseUntil) s.Frozen = null;
        }
        finally { rig.Return(scratch); }
    }

    /// <summary>The overrides: the frozen swing (all bones) while it holds, blending into the animation (with the arm held off the body) as it lets go; else the arm alone.</summary>
    static BonePose? Pose(SkeletonRig rig, PoseScratch scratch, Swing s, Quaternion? arm, int? upper, long tick, int releaseTicks)
    {
        if (s.Frozen is { } frozen && tick < s.ReleaseUntil)
        {
            float f = tick < s.FreezeUntil ? 0 : (tick - s.FreezeUntil) / (float)releaseTicks;
            var pose = new BonePose(rig.BoneCount);
            int root = rig.Skeleton.Bones.First(b => b.Parent is null).Handle;
            for (int h = 0; h < rig.BoneCount; h++)
            {
                var now = arm is { } a && upper == h ? a : scratch.DerivedRotation[h];
                if (Quaternion.Dot(frozen[h], now) < 0) now = -now;
                pose.Set[h] = true;
                pose.Rotation[h] = Quaternion.Normalize(Quaternion.Lerp(frozen[h], now, f));
            }
            pose.Position[root] = Vector3.Lerp(s.FrozenRoot, scratch.DerivedPosition[root], f);
            return pose;
        }
        if (arm is not { } held || upper is not int u) return null;
        var only = new BonePose(rig.BoneCount);
        only.Set[u] = true;
        only.Rotation[u] = held;
        return only;
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
