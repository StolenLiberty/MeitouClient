using System.Numerics;

namespace Meitou.Simulation;

/// <summary>The FACTION <c>squad formation</c> values (<c>fcs_enums.def</c> SquadFormation).</summary>
public enum SquadFormation { Random = 0, Caravan = 1, Military = 2 }

/// <summary>One character in a squad's formation: its role (<see cref="SquadRole"/>), whether it is an animal, and its radius (1.5 x the footprint radius, as the original).</summary>
public readonly record struct FormationMember(CharacterId Id, int Role, bool Animal, float Radius);

/// <summary>
/// Where a squad's followers keep station (<see cref="Layout"/>, the original's <c>Blackboard::buildFormation</c>, docs/game/pathfinding.md "Squads travelling together")
/// and where the characters of a move order end up (<see cref="Place"/>, engine choice: the original's move-order spread is not researched).
/// </summary>
public static class Formation
{
    /// <summary>Distance between neighbouring <see cref="Place"/> targets: twice the human footprint radius plus a margin.</summary>
    public const float Spacing = 9;

    /// <summary>The original's formation radius per footprint radius (<c>getRadius() * 1.5</c>).</summary>
    public const float RadiusScale = 1.5f;

    /// <summary>
    /// The slot of each character relative to the <paramref name="head"/>, in the original's frame: X behind the head, Y (the world's Z after <see cref="ToWorld"/>) across.
    /// <paramref name="others"/> are the squad's other members in squad order, <paramref name="slaves"/> those walking with the head (the original attaches each slave to its
    /// owner; Meitou's slave owner is a faction, so a squad's slaves go with its head, an engine choice). <paramref name="draw"/> gives the uniform draws in [0, 1).
    /// </summary>
    public static Dictionary<CharacterId, Vector2> Layout(SquadFormation type, FormationMember head, IReadOnlyList<FormationMember> others, IReadOnlyList<FormationMember> slaves, Func<float> draw)
    {
        var slots = new Dictionary<CharacterId, Vector2> { [head.Id] = Vector2.Zero };
        if (others.Count == 0 && slaves.Count == 0) return slots;
        float Range(float a, float b) => a + draw() * (b - a);
        switch (type)
        {
            case SquadFormation.Military: Military(slots, others, slaves); break;
            case SquadFormation.Caravan: Caravan(slots, head, others, slaves, Range, draw); break;
            default: Scatter(slots, head, others, slaves, draw); break;
        }
        return slots;
    }

    /// <summary>A slot of <see cref="Layout"/> in the world, for a head heading along <paramref name="heading"/> (unit, X/Z): the original turns −X onto the heading.</summary>
    public static Vector2 ToWorld(Vector2 slot, Vector2 heading) => -slot.X * heading + slot.Y * new Vector2(heading.Y, -heading.X);

    /// <summary>MILITARY: blocks of rows behind the head, rows across, c to a row (c from the number of humans); slaves, then small and big animals, then the humans.</summary>
    static void Military(Dictionary<CharacterId, Vector2> slots, IReadOnlyList<FormationMember> others, IReadOnlyList<FormationMember> slaves)
    {
        var humans = others.Where(m => !m.Animal).ToList();
        int n = Math.Max(2, humans.Count);
        int perRow = (int)MathF.Ceiling(n / MathF.Round(MathF.Sqrt(n), MidpointRounding.AwayFromZero));
        float start = 25;
        foreach (var block in new[] { slaves.ToList(), others.Where(m => m.Animal && m.Radius < 10).ToList(), others.Where(m => m.Animal && m.Radius >= 10).ToList(), humans })
        {
            if (block.Count == 0) continue;
            float r = block.Max(m => m.Radius), step = 2 * r + 10, half = step / 2;
            float x = start + r, across = (Math.Min(perRow, block.Count) - 1) * half, z = -across;
            int inRow = 0;
            for (int k = 0; k < block.Count; k++)
            {
                slots[block[k].Id] = new Vector2(x, z);
                z += step;
                if (++inRow == perRow && k < block.Count - 1)
                {
                    inRow = 0;
                    int left = block.Count - k - 1;
                    if (left < perRow) across = (left - 1) * half;   // a short last row is centred
                    x += step;
                    z = -across;
                }
            }
            start = r + x + 20;
        }
    }

    /// <summary>CARAVAN: SQUAD_1 members in single file behind the head, slaves behind it; everyone else out to the sides near the front.</summary>
    static void Caravan(Dictionary<CharacterId, Vector2> slots, FormationMember head, IReadOnlyList<FormationMember> others, IReadOnlyList<FormationMember> slaves,
        Func<float, float, float> range, Func<float> draw)
    {
        var circles = new List<(Vector2 At, float R)> { (Vector2.Zero, head.Radius) };
        float edge = range(10, 20) + head.Radius;
        void InFile(FormationMember m, float gapMin, float gapMax)
        {
            float x = range(gapMin, gapMax) + edge + m.Radius;
            edge = x + m.Radius;
            slots[m.Id] = new Vector2(x, 0);
            circles.Add((new Vector2(x, 0), m.Radius));
        }
        foreach (var s in slaves) InFile(s, 5, 10);
        var side = new List<FormationMember>();
        var sideAnimals = new List<FormationMember>();
        foreach (var m in others)
            if (m.Role == (int)SquadRole.Squad1) InFile(m, 10, 20);
            else (m.Animal ? sideAnimals : side).Add(m);
        // The original places the side members only when one of them is SQUAD_2 (the rest keep no slot); Meitou always does (engine choice).
        bool Free(Vector2 at, float r) => circles.All(c => (c.R + r) * (c.R + r) <= Vector2.DistanceSquared(at, c.At));
        float frontMin = range(5, 10) + head.Radius, frontMax = range(20, 40);
        Vector2 Beside(FormationMember m)
        {
            var at = new Vector2(range(frontMin, frontMax), 0);
            float sign = draw() > 0.5f ? 1 : -1;
            for (int tries = 0; tries < 256; tries++)
            {
                at.Y += range(10, 50) * sign;
                if (Free(at, m.Radius)) break;
            }
            return at;
        }
        int file = circles.Count;
        foreach (var m in side)
        {
            var at = Beside(m);
            slots[m.Id] = at;
            circles.Add((at, m.Radius));
        }
        // Animals stand by the side members in turn (diagonally off one, then outward), the rest like them.
        int sides = circles.Count;
        for (int k = 0; k < sideAnimals.Count; k++)
        {
            var m = sideAnimals[k];
            Vector2 at;
            if (file + k < sides)
            {
                var (by, r) = circles[file + k];
                var sign = new Vector2(draw() > 0.5f ? 1 : -1, draw() > 0.5f ? 1 : -1);
                at = by + sign * (r + m.Radius);
                for (int tries = 0; tries < 256 && !Free(at, m.Radius); tries++)
                    at += sign * new Vector2(range(2, 5), range(2, 5));
            }
            else at = Beside(m);
            slots[m.Id] = at;
            circles.Add((at, m.Radius));
        }
    }

    /// <summary>The gap the RANDOM layout keeps between circles.</summary>
    const float ScatterGap = 5;

    /// <summary>RANDOM: everyone scattered round the head, pushed apart; slaves in a cluster behind, and then a fifth of the members as a rear guard.</summary>
    static void Scatter(Dictionary<CharacterId, Vector2> slots, FormationMember head, IReadOnlyList<FormationMember> others, IReadOnlyList<FormationMember> slaves, Func<float> draw)
    {
        var all = new List<FormationMember>(others.Count + 1) { head };
        all.AddRange(others);
        var circles = new List<(Vector2 At, float R)>();
        Vector2 Polar(float radius)
        {
            float u = draw(), angle = draw() * MathF.Tau;
            return new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * (u * radius);
        }
        float front = 0;
        float spread = MathF.Sqrt(all.Count * 100f);
        foreach (var m in all)
        {
            var at = Polar(spread);
            if (at.X > front) front = at.X;
            at = PushApart(circles, at, m.Radius, draw);
            if (m.Id != head.Id) slots[m.Id] = at;
            circles.Add((at, m.Radius));
        }
        if (slaves.Count == 0) return;   // the original forms the rear guard only after placing slaves (Observed)
        float cluster = MathF.Sqrt(slaves.Count * 50f);
        var centre = new Vector2(cluster + front, 0);
        foreach (var s in slaves)
        {
            var at = centre + Polar(cluster);
            front = MathF.Max(front, at.X);
            at = PushApart(circles, at, s.Radius, draw);
            slots[s.Id] = at;
            circles.Add((at, s.Radius));
        }
        int last = all.Count - 1;
        for (int guard = (int)MathF.Ceiling(all.Count / 5f); guard > 0; guard--)
        {
            while (last > 0 && all[last].Animal) last--;
            if (last == 0) break;
            var at = PushApart(circles, new Vector2(front + ScatterGap + 20 + circles[last].R, 0), circles[last].R, draw, last);
            circles[last] = (at, circles[last].R);
            slots[all[last].Id] = at;
            last--;
        }
    }

    /// <summary>
    /// <paramref name="at"/> moved out of every circle it overlaps (with <see cref="ScatterGap"/>), one at a time, starting over after each move; a later move keeps the
    /// first one's side (it reverses onto it rather than back into an earlier circle). <paramref name="self"/> is its own circle, skipped.
    /// </summary>
    static Vector2 PushApart(List<(Vector2 At, float R)> circles, Vector2 at, float radius, Func<float> draw, int self = -1)
    {
        var first = Vector2.Zero;
        int pushes = 0;
        for (int j = 0; j < circles.Count && pushes < 64; j++)
        {
            if (j == self) continue;
            var (c, r) = circles[j];
            float reach = r + radius + ScatterGap;
            var v = at - c;
            if (v.LengthSquared() >= reach * reach) continue;
            var d = v == Vector2.Zero ? new Vector2(draw() - 0.5f, draw() - 0.5f) : v;
            if (pushes == 0) first = d;
            else if (Vector2.Dot(d, first) < 0) d = first;
            d = Vector2.Normalize(d);
            float b = Vector2.Dot(v, d);
            at += d * (-b + MathF.Sqrt(MathF.Max(0, b * b - v.LengthSquared() + reach * reach)));
            pushes++;
            j = -1;
        }
        return at;
    }

    /// <summary>
    /// One target per character, in the order of <paramref name="positions"/> (X/Z where they stand now). One character goes to the
    /// point itself. Ties (equal positions) break by the order given, so the result is a pure function of the input.
    /// </summary>
    /// <summary>
    /// Total War's drag formation (engine choice, after its right-button drag): the drag from <paramref name="from"/> to <paramref name="to"/> is the front line (with
    /// <paramref name="fromCentre"/> the drag starts at the line's middle and reaches one end); the characters face across it, to the side <paramref name="away"/> points to (the
    /// camera's look, so a drag left to right faces away from the camera). As many stand in a row as the line holds at <paramref name="spacing"/>, spread over its whole length;
    /// the other rows stand behind, a short last row centred. The front-most characters take the front row, and each row keeps their order from left to right, so nobody crosses.
    /// </summary>
    public static (Vector2[] Spots, Vector2 Facing) Line(IReadOnlyList<Vector2> positions, Vector2 from, Vector2 to, Vector2 away, bool fromCentre = false, float spacing = Spacing)
    {
        int n = positions.Count;
        if (fromCentre) from -= to - from;
        var axis = to - from;
        float length = axis.Length();
        axis = length > 1e-3f ? axis / length : away.LengthSquared() > 1e-6f ? Vector2.Normalize(new Vector2(away.Y, -away.X)) : Vector2.UnitX;
        var facing = new Vector2(-axis.Y, axis.X);
        if (Vector2.Dot(facing, away) < 0) facing = -facing;
        var spots = new Vector2[n];
        if (n == 0) return (spots, facing);
        int perRow = Math.Clamp((int)MathF.Floor(length / spacing) + 1, 1, n);
        int rows = (n + perRow - 1) / perRow;
        float step = perRow > 1 ? length / (perRow - 1) : 0;
        var centre = Vector2.Zero;
        foreach (var p in positions) centre += p;
        centre /= n;
        var order = Enumerable.Range(0, n).OrderByDescending(i => Vector2.Dot(positions[i] - centre, facing)).ThenBy(i => i).ToArray();
        int next = 0;
        for (int row = 0; row < rows; row++)
        {
            int inRow = Math.Min(perRow, n - next);
            var members = order.Skip(next).Take(inRow).OrderBy(i => Vector2.Dot(positions[i] - centre, axis)).ThenBy(i => i).ToArray();
            next += inRow;
            var start = from - facing * (row * spacing) + axis * ((perRow - inRow) * step / 2);
            for (int c = 0; c < inRow; c++) spots[members[c]] = start + axis * (c * step);
        }
        return (spots, facing);
    }

    public static Vector2[] Place(IReadOnlyList<Vector2> positions, Vector2 target, float spacing = Spacing)
    {
        int n = positions.Count;
        var result = new Vector2[n];
        if (n == 0) return result;
        if (n == 1)
        {
            result[0] = target;
            return result;
        }
        var centre = Vector2.Zero;
        foreach (var p in positions) centre += p;
        centre /= n;
        var forward = target - centre;
        forward = forward.LengthSquared() < 1e-4f ? new Vector2(0, 1) : Vector2.Normalize(forward);
        var right = new Vector2(forward.Y, -forward.X);   // 90 degrees clockwise seen from above (X right, Z up on the map)

        int columns = (int)MathF.Ceiling(MathF.Sqrt(n));
        int rows = (n + columns - 1) / columns;
        // Characters by how far ahead they are (the front row takes the leaders), then each row left to right.
        var order = Enumerable.Range(0, n).OrderByDescending(i => Vector2.Dot(positions[i] - centre, forward)).ThenBy(i => i).ToArray();
        int next = 0;
        for (int row = 0; row < rows; row++)
        {
            int inRow = Math.Min(columns, n - next);
            var members = order.Skip(next).Take(inRow).OrderBy(i => Vector2.Dot(positions[i] - centre, right)).ThenBy(i => i).ToArray();
            next += inRow;
            // The block is centred on the click: rows go back from the front, a short last row stays centred.
            float depth = ((rows - 1) / 2f - row) * spacing;
            for (int c = 0; c < inRow; c++)
            {
                float lateral = (c - (inRow - 1) / 2f) * spacing;
                result[members[c]] = target + forward * depth + right * lateral;
            }
        }
        return result;
    }
}
