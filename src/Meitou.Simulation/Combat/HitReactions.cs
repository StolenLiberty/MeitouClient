using System.Numerics;

namespace Meitou.Simulation.Combat;

/// <summary>
/// A landed hit as a push (Meitou-only, docs/euphoria.md): <paramref name="Direction"/> the way it goes (world, along the ground), <paramref name="Strength"/> its damage over the
/// defender's stumble threshold plus a soak (<see cref="CombatSystem"/>'s <c>PushSoak</c>; 1 staggers about a metre, from 2.5 it knocks over), <paramref name="Blunt"/> the blunt share of the damage (0 to 1), <paramref name="Part"/> the health part struck (-1: none).
/// </summary>
public readonly record struct HitPush(Vector3 Direction, float Strength, float Blunt, int Part, bool Ranged);

/// <summary>Physical hit reactions in place of the stumble clips (the euphoria bodies, docs/euphoria.md). Called in the serial phases.</summary>
public interface IHitReactions
{
    /// <summary>Whether a hit on the character in <paramref name="slot"/> now would get a physical reaction.</summary>
    bool Takes(World world, int slot);

    /// <summary>
    /// A hit on the character in <paramref name="slot"/>: true when it reacts physically (combat then holds it helpless while it does, <see cref="CombatSystem.Hold"/>). With
    /// <paramref name="down"/> the hit knocked it out or killed it: it falls physically before its ragdoll takes over.
    /// </summary>
    bool Hit(World world, int slot, in HitPush push, bool down);
}

public sealed partial class CombatSystem
{
    /// <summary>The physical hit reactions (null: the stumble clips).</summary>
    public IHitReactions? Reactions { get; set; }

    /// <summary>
    /// Keeps the character in <paramref name="slot"/> helpless until <paramref name="until"/> (a tick): no swing, no block, no walking (<see cref="CharacterCold.StaggeredUntil"/>).
    /// For a system after combat in the slow-world step (its arrays are swapped there: this writes the state the next tick starts from).
    /// </summary>
    public void Hold(World world, int slot, int until)
    {
        if ((uint)slot >= (uint)read.Length) return;
        ref var s = ref read[slot];
        s.StunUntil = Math.Max(s.StunUntil, until);
        s.ReadyTick = Math.Max(s.ReadyTick, until);
        s.AttackTech = -1;
        s.NextBlow = 0;
        s.ReactTech = -1;
        s.StumbleClip = -1;
        if (world.Characters.Cold(slot) is { } cold) cold.StaggeredUntil = Math.Max(cold.StaggeredUntil, until);
    }

    /// <summary>What a push's strength measures damage against, besides the stumble threshold (engine choice): the threshold is toughness x 0.52, under 10 for a new
    /// character, which every hit passes; with this a 30-damage hit staggers a toughness-10 character hard and a 60 one knocks it over.</summary>
    const float PushSoak = 15;

    /// <summary>The push of a landed hit: <paramref name="damage"/> (after armour) against the defender's stumble threshold and <see cref="PushSoak"/>.</summary>
    HitPush Push(Vector3 direction, in Bodies.HitDamage damage, float toughness, int part, bool ranged)
    {
        float total = damage.Cut + damage.Blunt + damage.Pierce + damage.ExtraStun;
        float threshold = DamageFormulas.StumbleThreshold(toughness, combat) + PushSoak;
        var flat = new Vector3(direction.X, 0, direction.Z);
        flat = flat.LengthSquared() > 1e-6f ? Vector3.Normalize(flat) : Vector3.UnitZ;
        return new HitPush(flat, total / threshold, total > 0 ? Math.Clamp((damage.Blunt + damage.ExtraStun) / total, 0, 1) : 0, part, ranged);
    }
}
