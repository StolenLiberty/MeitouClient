using System.Numerics;
using Meitou.Rendering.Gpu;
using Silk.NET.Vulkan;

namespace Meitou.Rendering.Characters;

/// <summary>One hover-outline mask: the characters outlined in one colour, drawn alone (<see cref="CharacterRenderer.DrawHoverMasks"/>).</summary>
/// <param name="Colour">Its colour target, alpha 1 where a character is.</param>
/// <param name="Depth">Its depth, from the projection <paramref name="Near"/>..<paramref name="Far"/>.</param>
/// <param name="Tint">The outline's and tint's colour (alpha: their strength).</param>
/// <param name="Spheres">The bounding spheres of the characters in it.</param>
public readonly record struct HoverMask(GpuTexture Colour, GpuTexture Depth, float Near, float Far, Vector4 Tint, IReadOnlyList<(Vector3 Centre, float Radius)> Spheres);

/// <summary>The outlined characters drawn alone into masks of their own, one per colour, for the hover outline (<see cref="HoverOutlineRenderer"/>, docs/game/ui-input.md "Hover outline").</summary>
internal sealed unsafe partial class CharacterRenderer
{
    readonly List<(GpuTexture Colour, GpuTexture Depth)> hoverTargets = [];
    readonly List<HoverMask> hoverMasks = [];

    /// <summary>This frame's masks (empty when nothing is outlined).</summary>
    public IReadOnlyList<HoverMask> HoverMasks => hoverMasks;

    /// <summary>
    /// Draws the characters <paramref name="wanted"/> (by draw-list key) alone, in their live poses and the picture's levels, through the colour pass (it writes alpha 1),
    /// one <paramref name="width"/> × <paramref name="height"/> mask per colour, seen from <paramref name="eye"/> along <paramref name="view"/> (unjittered). Each mask's
    /// planes hug its characters, so their depth stays precise. Nothing for a character not drawn in the world (beyond the draw distance, or hidden by the interior
    /// view) or off screen. Called after <see cref="Update"/> and before the world's colour pass, whose statistics then stand.
    /// </summary>
    public void DrawHoverMasks(IReadOnlyList<(long Key, Vector4 Colour)> wanted, int width, int height, Matrix4x4 view, float fieldOfView, Vector3 eye)
    {
        hoverMasks.Clear();
        if (wanted.Count == 0 || width <= 0 || height <= 0) return;
        var groups = new List<(Vector4 Colour, List<int> Live)>();
        var onScreen = view * Matrix4x4.CreatePerspectiveFieldOfView(fieldOfView, width / (float)height, 0.25f, 1e5f);
        foreach (var (key, colour) in wanted)
        {
            int index = -1;
            for (int i = 0; i < liveCount; i++)
                if (live[i].Key == key) { index = i; break; }
            if (index < 0 || live[index].PortraitOnly && !live[index].MaskOnly) continue;
            ref var l = ref live[index];
            if (HoverOutlineRenderer.Bounds(l.Centre, l.Radius, onScreen, width, height, 0) is null) continue;
            int g = groups.FindIndex(x => x.Colour == colour);
            if (g < 0) { g = groups.Count; groups.Add((colour, [])); }
            groups[g].Live.Add(index);
        }
        for (int g = 0; g < groups.Count; g++)
        {
            if (hoverTargets.Count <= g || hoverTargets[g].Colour.Desc.Width != width || hoverTargets[g].Colour.Desc.Height != height)
            {
                if (hoverTargets.Count > g) { hoverTargets[g].Colour.Dispose(); hoverTargets[g].Depth.Dispose(); }
                // The portraits' formats, so the masks share their pipelines.
                var pair = (Gpu.CreateTexture(new TextureDesc(InternalFormat.Srgb8Alpha8, width, height, Use: TextureUse.Sampled | TextureUse.ColourTarget, Name: "hover mask")),
                    Gpu.CreateTexture(new TextureDesc(InternalFormat.DepthComponent24, width, height, Use: TextureUse.Sampled | TextureUse.DepthTarget, Name: "hover mask depth")));
                if (hoverTargets.Count > g) hoverTargets[g] = pair; else hoverTargets.Add(pair);
            }
            var (colourTarget, depthTarget) = hoverTargets[g];
            float near = float.MaxValue, far = 0;
            var spheres = new List<(Vector3, float)>();
            foreach (int i in groups[g].Live)
            {
                float distance = Vector3.Distance(eye, live[i].Centre);
                (near, far) = (Math.Min(near, distance - live[i].Radius * 2), Math.Max(far, distance + live[i].Radius * 2));
                spheres.Add((live[i].Centre, live[i].Radius));
            }
            near = Math.Max(0.25f, near);
            var projection = Matrix4x4.CreatePerspectiveFieldOfView(fieldOfView, width / (float)height, near, far);
            var target = PassTargets.Of(colourTarget, depthTarget);
            bool first = true;
            foreach (int i in groups[g].Live)
            {
                var cmd = Gpu.BeginNative("hover mask");
                var load = first ? LoadOp.Clear : LoadOp.Load;
                cmd.BeginRendering(new RenderingDesc(
                    target.Colour with { Load = load, Clear = new ClearColour(0f, 0f, 0f, 0f) },
                    target.Depth with { Load = load, DepthClear = 1f }, target.Width, target.Height));
                Gpu.BeginHostPass(cmd, target, DrawState.Scene(target.Formats));
                portraitLive = i;
                try { DrawView(new ViewConstants { ViewProjection = view * projection, Eye = eye, LightDir = Vector3.UnitY }, eye, []); }
                finally { portraitLive = -1; }
                Gpu.EndHostPass(cmd);
                cmd.EndRendering();
                Gpu.EndNative(cmd);
                first = false;
            }
            hoverMasks.Add(new HoverMask(colourTarget, depthTarget, near, far, groups[g].Colour, spheres));
        }
    }

    void DisposeHover()
    {
        foreach (var (colour, depth) in hoverTargets) { colour.Dispose(); depth.Dispose(); }
        hoverTargets.Clear();
        hoverMasks.Clear();
    }
}
