using System.Runtime.InteropServices;

namespace Meitou.Simulation.Euphoria;

/// <summary>
/// The C interface of Delirium Physics (native/delirium, GPL-3.0-only; its header <c>crates/delirium-sdk/include/delirium_physics.h</c>), the calls Meitou uses. Z up,
/// metres, kilograms, seconds; quaternions x, y, z, w. Functions return 0 or a count, negative on failure (<see cref="LastError"/>). Native handles are opaque pointers.
/// </summary>
static partial class DeliriumNative
{
    const string Lib = "delirium_physics";

    public const int ModeLead = 0, ModeFollow = 1;

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] public static partial int nm_version();
    [LibraryImport(Lib)] public static partial int nm_last_error(byte[]? buf, int len);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] public static partial int nm_prepare_from_game(string gameDir, string outDir);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] public static partial int nm_init_art(string path, int nm, int littleEndian);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] public static partial int nm_load_task_params(string path);

    [LibraryImport(Lib)] public static partial nint nm_world_create();
    [LibraryImport(Lib)] public static partial void nm_world_destroy(nint world);
    [LibraryImport(Lib)] public static partial int nm_world_add_mesh(nint world, float[] positions, int vertices, uint[] triangles, int count, byte[]? materials);
    [LibraryImport(Lib)] public static partial int nm_world_add_floor(nint world, float x, float y, float z, float half);

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] public static partial nint nm_build_load(string path);
    [LibraryImport(Lib)] public static partial void nm_build_destroy(nint build);
    [LibraryImport(Lib)] public static partial int nm_build_part_count(nint build);
    [LibraryImport(Lib)] public static partial nint nm_build_fit(nint build, float[] joints);
    [LibraryImport(Lib)] public static partial nint nm_build_fit_mass(nint build, float[] joints, float mass);
    [LibraryImport(Lib)] public static partial int nm_build_part_frame(nint build, int part, float[] pos, float[] rot);
    [LibraryImport(Lib)] public static partial int nm_build_part_info(nint build, int part, out float mass, out float radius, out float half, out int parent);

    [LibraryImport(Lib)] public static partial nint nm_agent_create(nint build, float[] pos, float yaw, float seed);
    [LibraryImport(Lib)] public static partial void nm_agent_destroy(nint agent);
    [LibraryImport(Lib)] public static partial int nm_agent_step(nint agent, nint world, float dt);
    [LibraryImport(Lib)] public static partial int nm_agent_get_part_transform(nint agent, int part, float[] pos, float[] rot);
    [LibraryImport(Lib)] public static partial int nm_agent_get_part_transforms(nint agent, float[] output, int parts);
    [LibraryImport(Lib)] public static partial int nm_agent_get_joint(nint agent, int part, out int parent, float[] pivot, float[] frame, float[] limits, float[] armature);
    [LibraryImport(Lib)] public static partial int nm_agent_set_part_states(nint agent, float[] states, int parts);
    [LibraryImport(Lib)] public static partial int nm_agent_apply_impulse(nint agent, int part, float[] at, float[] impulse);
    [LibraryImport(Lib)] public static partial int nm_agent_add_velocity(nint agent, float[] dv);
    [LibraryImport(Lib)] public static partial int nm_agent_is_balancing(nint agent);
    [LibraryImport(Lib)] public static partial int nm_agent_has_fallen(nint agent);
    [LibraryImport(Lib)] public static partial int nm_agent_is_settled(nint agent);
    [LibraryImport(Lib)] public static partial int nm_agent_is_awake(nint agent);
    [LibraryImport(Lib)] public static partial float nm_agent_get_kinetic_energy(nint agent);
    [LibraryImport(Lib)] public static partial float nm_agent_get_mass(nint agent);
    [LibraryImport(Lib)] public static partial int nm_agent_get_com(nint agent, float[] pos, float[]? vel);
    [LibraryImport(Lib)] public static partial int nm_agent_feedback_count(nint agent);
    [LibraryImport(Lib)] public static partial int nm_agent_feedback_get(nint agent, int i, byte[] buf, int len, out int slot, out float value);

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] public static partial int nm_msg_begin(nint agent, string name);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] public static partial int nm_msg_set_float(nint agent, string name, float v);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] public static partial int nm_msg_set_int(nint agent, string name, int v);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] public static partial int nm_msg_set_bool(nint agent, string name, int v);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] public static partial int nm_msg_set_vec3(nint agent, string name, float[] v);
    [LibraryImport(Lib)] public static partial int nm_msg_send(nint agent, nint world);

    /// <summary>The library's message for the calling thread's last failure.</summary>
    public static string LastError()
    {
        int n = nm_last_error(null, 0);
        if (n <= 0) return "";
        var buf = new byte[n + 1];
        nm_last_error(buf, buf.Length);
        return System.Text.Encoding.UTF8.GetString(buf).TrimEnd('\0');
    }

    /// <summary>Whether the library is beside the program (and loads): without it the euphoria bodies are off.</summary>
    public static bool Available
    {
        get
        {
            if (available is { } known) return known;
            try { available = nm_version() >= 0; }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException) { available = false; }
            return available.Value;
        }
    }

    static bool? available;
}
