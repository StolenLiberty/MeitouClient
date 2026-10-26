using System.Numerics;
using Meitou.Data.Characters;
using Meitou.Simulation.Ragdolls;
using static Meitou.Simulation.Euphoria.DeliriumNative;

namespace Meitou.Simulation.Euphoria;

/// <summary>
/// The euphoria characters (docs/euphoria.md): Delirium's male and female bodies from the player's GTA IV, imported once (<c>nm_prepare_from_game</c>) into
/// <see cref="DefaultFolder"/>, loaded into the library, and fitted to each Kenshi skeleton they drive (<see cref="Fit"/>). One per process: the library's
/// characters are process-wide.
/// </summary>
public sealed class EuphoriaData
{
    /// <summary>Where the import goes: <c>%LOCALAPPDATA%\Meitou\euphoria</c>.</summary>
    public static string DefaultFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Meitou", "euphoria");

    static readonly string[] Files = ["fred.art", "wilma.art", "build-male.txt", "build-female.txt"];

    /// <summary>Delirium's 21 parts by index (its <c>nm_part_name</c> order) and the Kenshi bone each one fits and drives (Kenshi has no fourth spine bone: the chest's joint is
    /// half way between <c>Bip01 Spine2</c> and <c>Bip01 Neck</c>, and Spine2 is driven by the chest, not by part 9).</summary>
    public static readonly string[] PartBones =
    [
        "Bip01 Pelvis", "Bip01 L Thigh", "Bip01 L Calf", "Bip01 L Foot", "Bip01 R Thigh", "Bip01 R Calf", "Bip01 R Foot",
        "Bip01 Spine", "Bip01 Spine1", "Bip01 Spine2", "Bip01 Spine2", "Bip01 Neck", "Bip01 Head",
        "Bip01 L Clavicle", "Bip01 L UpperArm", "Bip01 L Forearm", "Bip01 L Hand", "Bip01 R Clavicle", "Bip01 R UpperArm", "Bip01 R Forearm", "Bip01 R Hand",
    ];
    /// <summary>What the fitted bodies weigh, in kg (engine choice: GTA IV's man is 78 kg).</summary>
    public const float MaleMass = 80, FemaleMass = 65;
    public const int Parts = 21, Pelvis = 0, LeftThigh = 1, RightThigh = 4, Chest = 10, Neck = 11, Head = 12, SpineLow = 7, SpineMid = 8, SpineHigh = 9;

    readonly nint male, female;
    readonly Dictionary<SkeletonRig, Body?> bodies = [];

    EuphoriaData(nint male, nint female) => (this.male, this.female) = (male, female);

    /// <summary>
    /// The GTA IV PC install (the folder with <c>GTAIV.exe</c>): <paramref name="configured"/>, else the <c>MEITOU_GTA4</c> environment variable, else
    /// <c>steamapps/common/Grand Theft Auto IV/GTAIV</c> in the Steam library Kenshi is in. Null when none has it.
    /// </summary>
    public static string? FindGame(string? kenshiRoot, string? configured = null)
    {
        foreach (var candidate in new[] { configured, Environment.GetEnvironmentVariable("MEITOU_GTA4") })
            if (Game(candidate) is { } found) return found;
        if (kenshiRoot is null) return null;
        var common = Path.GetDirectoryName(Path.GetFullPath(kenshiRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return common is null ? null : Game(Path.Combine(common, "Grand Theft Auto IV"));
    }

    static string? Game(string? dir)
    {
        if (string.IsNullOrWhiteSpace(dir)) return null;
        if (File.Exists(Path.Combine(dir, "GTAIV.exe"))) return dir;
        var inner = Path.Combine(dir, "GTAIV");
        return File.Exists(Path.Combine(inner, "GTAIV.exe")) ? inner : null;
    }

    static Task<EuphoriaData?>? shared;
    static readonly Lock sharing = new();

    /// <summary>Why the shared load (<see cref="Shared"/>) has no characters ("" while loading or when it has them).</summary>
    public static string Problem { get; private set; } = "";

    /// <summary>
    /// The characters for the whole process, loaded (and imported the first time) on a worker thread: the library's characters are process-wide, so the first call's folder and
    /// game decide. The import reads GTA IV's archives and takes some seconds; until it finishes hits play their clips.
    /// </summary>
    public static Task<EuphoriaData?> Shared(string folder, string? game)
    {
        lock (sharing)
            return shared ??= Task.Run(() =>
            {
                try
                {
                    var data = Load(folder, game, out string why);
                    Problem = why;
                    return data;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
                {
                    Problem = e.Message;
                    return null;
                }
            });
    }

    /// <summary>
    /// Loads the characters, importing them from <paramref name="game"/> first when <paramref name="folder"/> lacks them. Null (and <paramref name="why"/>) without the
    /// library, without the files and a game to make them from, or when the import fails.
    /// </summary>
    public static EuphoriaData? Load(string folder, string? game, out string why)
    {
        why = "";
        if (!Available) { why = "the Delirium library (delirium_physics) is not beside the program"; return null; }
        if (Files.Any(f => !File.Exists(Path.Combine(folder, f))))
        {
            if (game is null) { why = "no GTA IV install found (MEITOU_GTA4, or GTA IV in Kenshi's Steam library) and no imported files in " + folder; return null; }
            if (nm_prepare_from_game(game, folder) < 0) { why = LastError(); return null; }
        }
        if (nm_init_art(Path.Combine(folder, "fred.art"), 0, 1) < 0 || nm_init_art(Path.Combine(folder, "wilma.art"), 1, 1) < 0) { why = LastError(); return null; }
        nint m = nm_build_load(Path.Combine(folder, "build-male.txt")), f = nm_build_load(Path.Combine(folder, "build-female.txt"));
        if (m == 0 || f == 0) { why = LastError(); return null; }
        return new EuphoriaData(m, f);
    }

    /// <summary>
    /// A Delirium body fitted to one Kenshi skeleton: the fitted build, the bone handle of each part, and each driven bone's transform in its part's frame (Delirium space,
    /// from the binding pose: bone = <c>BoneInPart[k].Then(part)</c>).
    /// </summary>
    public sealed class Body
    {
        public required nint Build { get; init; }
        public required int[] Bone { get; init; }
        public required RigidTransform[] BoneInPart { get; init; }
        /// <summary><c>Bip01 Pelvis</c> in its parent <c>Bip01</c>'s frame (Kenshi space, the binding pose): the root bone follows the pelvis part.</summary>
        public required RigidTransform PelvisInRoot { get; init; }
        public required int Root { get; init; }
    }

    /// <summary>The body for <paramref name="rig"/> (cached; the female character for <paramref name="female"/>); null for a skeleton without the 20 bones (animals, other races' rigs).</summary>
    public Body? Fit(SkeletonRig rig, bool female)
    {
        lock (bodies)
        {
            if (bodies.TryGetValue(rig, out var known)) return known;
            return bodies[rig] = Make(rig, female ? this.female : male, female ? FemaleMass : MaleMass);
        }
    }

    /// <summary>A build's joints in its own pose (the root's: its centre), from a new body of it.</summary>
    static Vector3[] Joints(nint build)
    {
        var joints = new Vector3[Parts];
        nint agent = nm_agent_create(build, [0, 0, 0], 0, 0);
        float[] p = new float[3], q = new float[4], limits = new float[3], armature = new float[1];
        nm_agent_get_part_transform(agent, Pelvis, p, q);
        joints[Pelvis] = new Vector3(p[0], p[1], p[2]);
        for (int k = 1; k < Parts; k++)
        {
            nm_agent_get_joint(agent, k, out _, p, q, limits, armature);
            joints[k] = new Vector3(p[0], p[1], p[2]);
        }
        nm_agent_destroy(agent);
        return joints;
    }

    /// <summary>The turn whose X is <paramref name="side"/> and whose Z is <paramref name="up"/> made square to it.</summary>
    static Quaternion Basis(Vector3 side, Vector3 up)
    {
        var x = Vector3.Normalize(side);
        var z = Vector3.Normalize(up - x * Vector3.Dot(up, x));
        var y = Vector3.Cross(z, x);
        return Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(new Matrix4x4(x.X, x.Y, x.Z, 0, y.X, y.Y, y.Z, 0, z.X, z.Y, z.Z, 0, 0, 0, 0, 1)));
    }

    static Body? Make(SkeletonRig rig, nint source, float mass)
    {
        var bone = new int[Parts];
        for (int k = 0; k < Parts; k++)
        {
            if (rig.Handle(PartBones[k]) is not int h) return null;
            bone[k] = h;
        }
        int root = rig.Parent[bone[Pelvis]];
        if (rig.Handle("Bip01 Neck") is not int neck || root < 0) return null;
        // The binding pose in model space, then Delirium space (model origin at Delirium's origin).
        RigidTransform Bind(int h) => new(rig.BindPosition[h], Quaternion.Normalize(Quaternion.Inverse(rig.BindRotationInverse[h])));
        var joints = new float[Parts * 3];
        for (int k = 0; k < Parts; k++)
        {
            var at = k == Chest ? (rig.BindPosition[bone[SpineHigh]] + rig.BindPosition[neck]) * 0.5f : rig.BindPosition[bone[k]];
            var d = Space.ToDelirium(at, Vector3.Zero);
            (joints[3 * k], joints[3 * k + 1], joints[3 * k + 2]) = (d.X, d.Y, d.Z);
        }
        // The trunk keeps the euphoria body's proportions: Kenshi has three spine bones to its four, spaced otherwise, and fitting the spine and neck parts to Kenshi's bones
        // makes barrels of some and slivers of others (a 190 kg man). Their joints are the source's, turned and scaled onto Kenshi's pelvis, hips and head.
        var source0 = Joints(source);
        Vector3 J(int k) => new(joints[3 * k], joints[3 * k + 1], joints[3 * k + 2]);
        var (pelvisAt, headAt) = (J(Pelvis), J(Head));
        float scale = Vector3.Distance(pelvisAt, headAt) / MathF.Max(1e-4f, Vector3.Distance(source0[Pelvis], source0[Head]));
        var turn = Quaternion.Normalize(Basis(J(LeftThigh) - J(RightThigh), headAt - pelvisAt) * Quaternion.Inverse(Basis(source0[LeftThigh] - source0[RightThigh], source0[Head] - source0[Pelvis])));
        foreach (int k in (int[])[SpineLow, SpineMid, SpineHigh, Chest, Neck])
        {
            var at = pelvisAt + Vector3.Transform(source0[k] - source0[Pelvis], turn) * scale;
            (joints[3 * k], joints[3 * k + 1], joints[3 * k + 2]) = (at.X, at.Y, at.Z);
        }
        nint fitted = nm_build_fit_mass(source, joints, mass);
        if (fitted == 0) return null;
        var boneInPart = new RigidTransform[Parts];
        float[] pos = new float[3], rot = new float[4];
        for (int k = 0; k < Parts; k++)
        {
            nm_build_part_frame(fitted, k, pos, rot);
            var part = new RigidTransform(new Vector3(pos[0], pos[1], pos[2]), Quaternion.Normalize(new Quaternion(rot[0], rot[1], rot[2], rot[3])));
            var b = Space.ToDelirium(Bind(bone[k]), Vector3.Zero);
            boneInPart[k] = b.Then(part.Inverse());
        }
        var pelvisBone = rig.Skeleton.Bones.First(b => b.Handle == bone[Pelvis]);
        return new Body
        {
            Build = fitted, Bone = bone, BoneInPart = boneInPart, Root = root,
            PelvisInRoot = new RigidTransform(pelvisBone.Position, Quaternion.Normalize(pelvisBone.Orientation)),
        };
    }
}

/// <summary>
/// Kenshi space (Y up, right-handed, 1 unit = 0.1 m, <b>Observed</b> docs/formats/terrain.md) and Delirium's (Z up, right-handed, metres): a turn of +90 degrees about X, so
/// Kenshi's (x, y, z) is Delirium's (x, -z, y), and a scale, about an origin (Delirium's coordinates stay small: an agent's origin is where its reaction started).
/// </summary>
static class Space
{
    public const float Metres = 0.1f;
    static readonly Quaternion Turn = Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 2), Unturn = Quaternion.Inverse(Turn);

    public static Vector3 ToDelirium(Vector3 k, Vector3 origin) => new((k.X - origin.X) * Metres, -(k.Z - origin.Z) * Metres, (k.Y - origin.Y) * Metres);
    public static Vector3 ToKenshi(Vector3 d, Vector3 origin) => new(d.X / Metres + origin.X, d.Z / Metres + origin.Y, -d.Y / Metres + origin.Z);
    public static Vector3 DirectionToDelirium(Vector3 k) => new(k.X, -k.Z, k.Y);
    public static Quaternion ToDelirium(Quaternion k) => Quaternion.Normalize(Turn * k * Unturn);
    public static Quaternion ToKenshi(Quaternion d) => Quaternion.Normalize(Unturn * d * Turn);
    public static RigidTransform ToDelirium(RigidTransform k, Vector3 origin) => new(ToDelirium(k.Position, origin), ToDelirium(k.Rotation));
    public static RigidTransform ToKenshi(RigidTransform d, Vector3 origin) => new(ToKenshi(d.Position, origin), ToKenshi(d.Rotation));
}
