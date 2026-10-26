using System.Numerics;
using Meitou.Simulation;

namespace Meitou.Game;

/// <summary>
/// Meitou's formation drag (after Total War's right-button drag): Alt + right button pressed on the ground and dragged draws the selected characters' front line, a ring on the ground
/// at each one's spot (the notch the way they will face); let go, each walks to its spot (<see cref="FormationOrder"/>) and the rings go. Shift queues it after the orders in hand.
/// Without a drag (let go within <see cref="FormationDragPixels"/>) it is the plain move the click would have given.
/// </summary>
sealed partial class PlayerInterface
{
    sealed class FormationDrag
    {
        public required Vector2 FromPixel { get; init; }
        public required Vector3 From { get; init; }
        public required IReadOnlyList<CharacterId> Ids { get; init; }
        public bool Queued { get; init; }
        public bool Active { get; set; }
        public Vector3[] Spots { get; set; } = [];
        public Vector2 Facing { get; set; } = Vector2.UnitY;
    }

    FormationDrag? formationDrag;

    /// <summary>How far the pointer moves before a press becomes a drag (pixels).</summary>
    const float FormationDragPixels = 12;

    /// <summary>A press that may start the drag: Alt held, characters selected, the ground under the pointer.</summary>
    bool FormationPress(Vector2 p, bool shift)
    {
        if (SelectedIds() is not { Count: > 0 } ids || Ray(p) is not { } ray || MoveHit(ray.Origin, ray.Direction) is not { } hit) return false;
        formationDrag = new FormationDrag { FromPixel = p, From = hit, Ids = ids, Queued = shift };
        return true;
    }

    /// <summary>Once a frame while the button is held: the line to the point under the pointer and the rings on it (also when the camera moves).</summary>
    void UpdateFormationDrag()
    {
        if (formationDrag is not { } d) return;
        if (!d.Active && Vector2.Distance(d.FromPixel, mouse) < FormationDragPixels) return;
        if (Ray(mouse) is not { } ray || MoveHit(ray.Origin, ray.Direction) is not { } hit) return;
        d.Active = true;
        var snapshot = session.CurrentSnapshot;
        var positions = d.Ids.Select(id => snapshot.Characters.FirstOrDefault(c => c.Id == id)?.Position ?? d.From).Select(q => new Vector2(q.X, q.Z)).ToArray();
        // The way they face, as Total War's: the drag turned a quarter on the screen (left to right faces up the screen, right to left down it), taken onto the ground.
        var drag = mouse - d.FromPixel;
        var up = Vector2.Normalize(new Vector2(drag.Y, -drag.X));
        var mid = (mouse + d.FromPixel) / 2;
        var away = new Vector2(ray.Direction.X, ray.Direction.Z);
        if (Ray(mid) is { } r0 && MoveHit(r0.Origin, r0.Direction) is { } g0 && Ray(mid + up * 40) is { } r1 && MoveHit(r1.Origin, r1.Direction) is { } g1
            && new Vector2(g1.X - g0.X, g1.Z - g0.Z) is { } ahead && ahead.LengthSquared() > 1e-4f) away = ahead;
        var (spots, facing) = Formation.Line(positions, new Vector2(d.From.X, d.From.Z), new Vector2(hit.X, hit.Z), away);
        var walk = session.World.Walkability;
        d.Spots = [.. spots.Select(s => new Vector3(s.X, walk.GroundHeight(s.X, s.Y, d.From.Y), s.Y))];
        d.Facing = facing;
        if (Markers is { } markers)
        {
            markers.Rings = [.. d.Spots.Select(s => s + new Vector3(0, 0.6f, 0))];
            markers.RingFacing = new Vector3(facing.X, 0, facing.Y);
        }
    }

    /// <summary>The button let go: the formation order (or the plain move); the rings go. False when no drag was on.</summary>
    bool FormationRelease()
    {
        if (formationDrag is not { } d) return false;
        UpdateFormationDrag();
        formationDrag = null;
        if (Markers is { } markers) markers.Rings = [];
        if (!d.Active || d.Spots.Length != d.Ids.Count)
        {
            Move(d.From, d.Queued);
            PlaceMarker(d.From);
            return true;
        }
        Say(new FormationOrder(d.Ids, d.Spots, d.Facing) { Tick = session.World.Tick, Queued = d.Queued });
        return true;
    }

    /// <summary>The drag is dropped without an order (the world went away, Escape).</summary>
    void CancelFormationDrag()
    {
        formationDrag = null;
        if (Markers is { } markers) markers.Rings = [];
    }
}
