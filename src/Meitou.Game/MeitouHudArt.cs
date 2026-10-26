using System.Globalization;
using System.Numerics;
using Meitou.Data.Textures;
using Meitou.Rendering.Ui;

namespace Meitou.Game;

/// <summary>
/// The Meitou HUD's pictures, drawn at run time (docs/meitou-ui.md "In-game HUD"): a white rounded plate and rim for nine-slicing under a tint, and line icons on a 24-unit grid
/// (their outlines written as SVG path data) drawn white on clear, also tinted where they are used.
/// </summary>
static class MeitouHudArt
{
    public const string Prefix = "meitou:hud:";
    public const string Plate = Prefix + "plate", Rim = Prefix + "rim", Ring = Prefix + "ring";
    /// <summary>The plate textures' size, corner radius and nine-slice border, in texels: drawn at <see cref="Density"/> texels per pixel.</summary>
    public const int PlateSize = 64, PlateRadius = 20, PlateBorder = 24;
    public const float Density = 2;
    const int IconSize = 48;

    public static string Icon(string name) => Prefix + "icon:" + name;

    /// <summary>The icons' outlines: SVG path data on a 24 x 24 grid (M L H V C S A Z, absolute and relative).</summary>
    public static readonly IReadOnlyDictionary<string, string> Icons = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["block"] = "M12 3l7 3v5c0 4.5-3 8-7 10-4-2-7-5.5-7-10V6z",
        ["hold"] = Circle(12, 5, 2) + "M12 7v14M5 13a7 7 0 0 0 14 0M8 10h8",
        ["passive"] = Circle(12, 12, 9) + "M5.6 18.4L18.4 5.6",
        ["ranged"] = "M5 19L19 5M13 5h6v6M5 19v-4M5 19h4",
        ["taunt"] = "M3 10v4h3l7 5V5l-7 5H3zM17 9a4 4 0 0 1 0 6",
        ["attack"] = "M4 4l16 16M20 4L4 20M4 4h5M4 4v5M20 4h-5M20 4v5",
        ["sneak"] = "M3 3l18 18M10.6 6.1A9.8 9.8 0 0 1 12 6c5 0 9 6 9 6a17 17 0 0 1-2.6 3.2M6.6 6.6C4.3 8.1 3 12 3 12s4 6 9 6a9 9 0 0 0 4.4-1.1",
        ["run"] = "M5 6l6 6-6 6M13 6l6 6-6 6",
        ["medic"] = "M12 7v10M7 12h10M5 3h14a2 2 0 0 1 2 2v14a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2z",
        ["rescue"] = Circle(12, 12, 9) + Circle(12, 12, 4) + "M5.6 5.6l3.6 3.6M14.8 14.8l3.6 3.6M18.4 5.6l-3.6 3.6M9.2 14.8l-3.6 3.6",
        ["prospect"] = "M4 20l9-9M7 6c4-3 9-2 12 1-4 0-7 1-9 3z",
        ["jobs"] = "M9 6h11M9 12h11M9 18h11M4 6h1M4 12h1M4 18h1",
        ["inventory"] = "M5 8h14l-1 12H6zM9 8V6a3 3 0 0 1 6 0v2",
        ["character"] = Circle(12, 8, 4) + "M4 21a8 8 0 0 1 16 0",
        ["map"] = "M9 4L3 6v14l6-2 6 2 6-2V4l-6 2zM9 4v14M15 6v14",
        ["research"] = "M9 3h6M10 3v6L4 19a1 1 0 0 0 1 2h14a1 1 0 0 0 1-2l-6-10V3M7 14h10",
        ["crafting"] = "M14 4l6 6-3 3-6-6zM12 8L3 17l4 4 9-9",
        ["build"] = "M3 11l9-7 9 7M5 10v10h14V10M10 20v-6h4v6",
        ["squad"] = Circle(9, 8, 3.5f) + "M2 20a7 7 0 0 1 14 0M16 4.5a3.5 3.5 0 0 1 0 7M18 13.5a7 7 0 0 1 4 6.5",
        ["faction"] = "M5 21V4M5 4h12l-2 4 2 4H5",
        ["clock"] = Circle(12, 12, 9) + "M12 7v5l3 2",
        ["bolt"] = "M13 3L5 14h6l-1 7 8-11h-6z",
        ["pause"] = "M8 5v14M16 5v14",
        ["up"] = "M6 15l6-6 6 6",
        ["down"] = "M6 9l6 6 6-6",
        ["plus"] = "M12 5v14M5 12h14",
        ["minimise"] = "M5 12h14",
        ["maximise"] = "M5 5h14v14H5z",
        ["close"] = "M6 6l12 12M18 6L6 18",
        ["drop"] = "M12 3s6 7 6 11a6 6 0 0 1-12 0c0-4 6-11 6-11z",
        ["knocked"] = Circle(12, 8, 4) + "M4 21a8 8 0 0 1 16 0M3 3l18 18",
        ["food"] = "M7 3v8M5 3v5a2 2 0 0 0 4 0V3M7 11v10M17 3c-2 0-3 3-3 7h3v11",
        ["lock"] = "M6 11h12v10H6zM8 11V8a4 4 0 0 1 8 0v3",
        ["open"] = "M6 11h12v10H6zM8 11V8a4 4 0 0 1 7.5-2",
        ["alert"] = "M12 4L2 20h20zM12 10v4M12 17h.01",
        ["info"] = Circle(12, 12, 9) + "M12 11v6M12 7.5h.01",
    };

    static string Circle(float x, float y, float r) => string.Create(CultureInfo.InvariantCulture,
        $"M{x - r} {y}a{r} {r} 0 1 0 {2 * r} 0a{r} {r} 0 1 0 {-2 * r} 0");

    /// <summary>Puts the plates and icons on the GPU once.</summary>
    public static void Ensure(UiPainter painter)
    {
        if (painter.HasTexture(Plate)) return;
        painter.SetImage(Plate, RoundedRect(PlateSize, PlateRadius, 0), srgb: false);
        painter.SetImage(Rim, RoundedRect(PlateSize, PlateRadius, 2), srgb: false);
        painter.SetImage(Ring, RoundedRect(PlateSize, PlateRadius, 4), srgb: false);
        foreach (var (name, path) in Icons) painter.SetImage(Icon(name), Stroke(path, IconSize, 1.75f), srgb: false);
    }

    static readonly Vector4 Ink = new(175 / 255f, 166 / 255f, 139 / 255f, 1), Brass = new(196 / 255f, 160 / 255f, 98 / 255f, 1);

    /// <summary>
    /// The Modern style's surfaces for the other windows (the cheat menu, Zeus, squad jobs, towns, settings), as the HUD's: a see-through dark plate with a faint rim for a
    /// window, a brass-tinted one for a header or the primary button ("paper", "primary"), and a button plate (brass while selected, lighter under the pointer).
    /// </summary>
    public static void Surface(List<Meitou.Data.Ui.UiControl> controls, string name, Meitou.Data.Ui.UiRect at, string kind, bool selected, bool hover, Meitou.Data.Ui.UiRect? clip, float alpha, float scale)
    {
        var (fill, rim, radius) = kind switch
        {
            "window" => (new Vector4(18 / 255f, 14 / 255f, 11 / 255f, 0.72f), Ink with { W = 0.18f }, 12f),
            "paper" or "primary" => (Brass with { W = hover ? 0.34f : 0.22f }, Brass with { W = 0.85f }, 7f),
            _ => (selected ? Brass with { W = 0.22f } : hover ? Ink with { W = 0.13f } : new Vector4(1, 1, 1, 0.05f), selected ? Brass with { W = 0.9f } : Ink with { W = 0.18f }, 7f),
        };
        foreach (var (texture, tint) in new[] { (Plate, fill), (Rim, rim) })
        {
            var p = new Dictionary<string, string>
            {
                ["ImageTexture"] = texture, ["Colour"] = string.Create(CultureInfo.InvariantCulture, $"{tint.X} {tint.Y} {tint.Z} {tint.W * alpha}"),
                ["ImageBorder"] = PlateBorder.ToString(CultureInfo.InvariantCulture),
                ["ImageBorderScale"] = (radius * scale / PlateRadius).ToString(CultureInfo.InvariantCulture),
            };
            controls.Add(new(name, "ImageBox", at, "", "", "", true, p, Vector4.Zero, Clip: clip, Pickable: false));
        }
    }

    /// <summary>A white rounded square on clear, anti-aliased: filled, or (with <paramref name="line"/> &gt; 0) only a rim that wide inside its edge.</summary>
    public static RgbaImage RoundedRect(int size, float radius, float line)
    {
        var image = new RgbaImage(size, size);
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                // Signed distance to the rounded square's edge (negative inside).
                float px = MathF.Abs(x + 0.5f - size / 2f) - (size / 2f - radius), py = MathF.Abs(y + 0.5f - size / 2f) - (size / 2f - radius);
                float outside = new Vector2(MathF.Max(px, 0), MathF.Max(py, 0)).Length() + MathF.Min(MathF.Max(px, py), 0) - radius;
                float cover = Math.Clamp(0.5f - outside, 0, 1);
                if (line > 0) cover *= Math.Clamp(outside + line + 0.5f, 0, 1);
                Set(image, x, y, cover);
            }
        return image;
    }

    /// <summary>The path's outline stroked <paramref name="width"/> grid units wide with round ends, white on clear, <paramref name="size"/> texels square.</summary>
    public static RgbaImage Stroke(string path, int size, float width)
    {
        var segments = Flatten(path);
        float k = size / 24f, half = width * k / 2;
        var image = new RgbaImage(size, size);
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                var p = new Vector2(x + 0.5f, y + 0.5f);
                float best = float.MaxValue;
                foreach (var (a, b) in segments) best = MathF.Min(best, Distance(p, a * k, b * k));
                Set(image, x, y, Math.Clamp(half + 0.5f - best, 0, 1));
            }
        return image;
    }

    static void Set(RgbaImage image, int x, int y, float cover)
    {
        int i = (y * image.Width + x) * 4;
        image.Pixels[i] = image.Pixels[i + 1] = image.Pixels[i + 2] = 255;
        image.Pixels[i + 3] = (byte)MathF.Round(cover * 255);
    }

    static float Distance(Vector2 p, Vector2 a, Vector2 b)
    {
        var ab = b - a;
        float t = ab.LengthSquared() < 1e-8f ? 0 : Math.Clamp(Vector2.Dot(p - a, ab) / ab.LengthSquared(), 0, 1);
        return Vector2.Distance(p, a + ab * t);
    }

    /// <summary>SVG path data as straight segments (curves and arcs cut into short pieces).</summary>
    public static List<(Vector2 A, Vector2 B)> Flatten(string path)
    {
        var segments = new List<(Vector2, Vector2)>();
        var tokens = Tokens(path);
        int i = 0;
        char command = 'M';
        Vector2 at = default, start = default, lastControl = default;
        bool hadCubic = false;
        float Next() => float.Parse(tokens[i++], CultureInfo.InvariantCulture);
        void Line(Vector2 to) { segments.Add((at, to)); at = to; }
        while (i < tokens.Count)
        {
            if (char.IsLetter(tokens[i][0])) command = tokens[i++][0];
            bool relative = char.IsLower(command);
            Vector2 origin = relative ? at : Vector2.Zero;
            switch (char.ToUpperInvariant(command))
            {
                case 'M':
                    at = start = origin + new Vector2(Next(), Next());
                    command = relative ? 'l' : 'L';   // further pairs are lines
                    hadCubic = false;
                    break;
                case 'L': Line(origin + new Vector2(Next(), Next())); hadCubic = false; break;
                case 'H': Line(new Vector2((relative ? at.X : 0) + Next(), at.Y)); hadCubic = false; break;
                case 'V': Line(new Vector2(at.X, (relative ? at.Y : 0) + Next())); hadCubic = false; break;
                case 'C':
                case 'S':
                {
                    bool smooth = char.ToUpperInvariant(command) == 'S';
                    var c1 = smooth ? (hadCubic ? 2 * at - lastControl : at) : origin + new Vector2(Next(), Next());
                    var c2 = origin + new Vector2(Next(), Next());
                    var to = origin + new Vector2(Next(), Next());
                    var from = at;
                    for (int s = 1; s <= 12; s++)
                    {
                        float t = s / 12f, u = 1 - t;
                        Line(u * u * u * from + 3 * u * u * t * c1 + 3 * u * t * t * c2 + t * t * t * to);
                    }
                    lastControl = c2;
                    hadCubic = true;
                    break;
                }
                case 'A':
                {
                    float rx = Next(), ry = Next(), rotation = Next();
                    bool large = Next() != 0, sweep = Next() != 0;
                    var to = origin + new Vector2(Next(), Next());
                    foreach (var p in Arc(at, to, rx, ry, rotation, large, sweep)) Line(p);
                    hadCubic = false;
                    break;
                }
                case 'Z':
                    Line(start);
                    hadCubic = false;
                    break;
                default: throw new FormatException($"Unsupported path command '{command}'.");
            }
        }
        return segments;
    }

    static List<string> Tokens(string path)
    {
        var list = new List<string>();
        int i = 0;
        while (i < path.Length)
        {
            char c = path[i];
            if (char.IsWhiteSpace(c) || c == ',') { i++; continue; }
            if (char.IsLetter(c)) { list.Add(c.ToString()); i++; continue; }
            int begin = i;
            if (c is '-' or '+') i++;
            bool dot = false;
            while (i < path.Length && (char.IsDigit(path[i]) || path[i] == '.' && !dot)) { if (path[i] == '.') dot = true; i++; }
            list.Add(path[begin..i]);
        }
        return list;
    }

    /// <summary>The points along an SVG elliptical arc from <paramref name="from"/> to <paramref name="to"/> (the endpoint form turned into centre and angles), <paramref name="to"/> last.</summary>
    static IEnumerable<Vector2> Arc(Vector2 from, Vector2 to, float rx, float ry, float degrees, bool large, bool sweep)
    {
        if (rx == 0 || ry == 0 || from == to) { yield return to; yield break; }
        rx = MathF.Abs(rx); ry = MathF.Abs(ry);
        float phi = degrees * MathF.PI / 180, cos = MathF.Cos(phi), sin = MathF.Sin(phi);
        var d = (from - to) / 2;
        var p = new Vector2(cos * d.X + sin * d.Y, -sin * d.X + cos * d.Y);
        float lambda = p.X * p.X / (rx * rx) + p.Y * p.Y / (ry * ry);
        if (lambda > 1) { rx *= MathF.Sqrt(lambda); ry *= MathF.Sqrt(lambda); }
        float num = rx * rx * ry * ry - rx * rx * p.Y * p.Y - ry * ry * p.X * p.X, den = rx * rx * p.Y * p.Y + ry * ry * p.X * p.X;
        float factor = MathF.Sqrt(MathF.Max(0, num / den)) * (large == sweep ? -1 : 1);
        var cp = factor * new Vector2(rx * p.Y / ry, -ry * p.X / rx);
        var centre = new Vector2(cos * cp.X - sin * cp.Y, sin * cp.X + cos * cp.Y) + (from + to) / 2;
        static float Angle(Vector2 u, Vector2 v) => MathF.Atan2(u.X * v.Y - u.Y * v.X, Vector2.Dot(u, v));
        var u1 = new Vector2((p.X - cp.X) / rx, (p.Y - cp.Y) / ry);
        var u2 = new Vector2((-p.X - cp.X) / rx, (-p.Y - cp.Y) / ry);
        float theta = Angle(Vector2.UnitX, u1), delta = Angle(u1, u2);
        if (!sweep && delta > 0) delta -= MathF.Tau;
        else if (sweep && delta < 0) delta += MathF.Tau;
        int steps = Math.Max(4, (int)MathF.Ceiling(MathF.Abs(delta) / (MathF.PI / 16)));
        for (int s = 1; s < steps; s++)
        {
            float a = theta + delta * s / steps;
            float ex = rx * MathF.Cos(a), ey = ry * MathF.Sin(a);
            yield return new Vector2(cos * ex - sin * ey, sin * ex + cos * ey) + centre;
        }
        yield return to;
    }
}
