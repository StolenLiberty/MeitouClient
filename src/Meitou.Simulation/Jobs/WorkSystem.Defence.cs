using System.Numerics;

namespace Meitou.Simulation.Jobs;

/// <summary>
/// The fights at one outpost this check: <see cref="Attackers"/> fight one of the player's characters (within the outpost's radius plus <see cref="WorkSystem.DefendReach"/>);
/// <see cref="Hostiles"/> are awake characters of a faction hostile to the player's inside the outpost who attack nobody (only soldiers go after them).
/// </summary>
internal sealed record OutpostThreats(List<int> Attackers, List<int> Hostiles)
{
    public bool Any => Attackers.Count > 0 || Hostiles.Count > 0;
}

public sealed partial class WorkSystem
{
    /// <summary>How far outside the outpost's radius a fight with the player's people still calls the squads out (engine choice).</summary>
    public const float DefendReach = 500;
    /// <summary>A worker standing its ground fights an attacker this near (engine choice).</summary>
    public const float LocalReach = 150;

    /// <summary>Members sent inside to shelter; once it is over they stop where they are.</summary>
    readonly HashSet<CharacterId> sheltering = [];

    static OutpostThreats Threats(World world, Combat.CombatSystem? combat, Construction.PlayerTown town)
    {
        var table = world.Characters;
        List<int> attackers = [], hostiles = [];
        if (combat is null) return new(attackers, hostiles);
        var relations = combat.Relations;
        int player = world.Player.Faction;
        var centre = new Vector2(town.Position.X, town.Position.Z);
        float inside = town.Radius * town.Radius, near = (town.Radius + DefendReach) * (town.Radius + DefendReach);
        for (int s = 0; s < table.HighWater; s++)
        {
            ref readonly var n = ref table.Next[s];
            if (!n.Alive || table.Cold(s) is not { IsPlayer: false } c || c.Medical is { Incapacitated: true } || c.IsImprisoned || c.Faction == player) continue;
            float d = Vector2.DistanceSquared(new(n.Position.X, n.Position.Z), centre);
            if (d > near) continue;
            if (c.InCombat && combat.StateOf(table.IdOf(s)) is { TargetSlot: >= 0 and var t } && t < table.HighWater && table.Cold(t) is { IsPlayer: true }) attackers.Add(s);
            else if (d <= inside && relations is not null && c.Faction >= 0 && player >= 0 && relations.Contains(c.Faction) && relations.Contains(player) && relations.IsHostile(c.Faction, player))
                hostiles.Add(s);
        }
        return new(attackers, hostiles);
    }

    /// <summary>
    /// A member's part in a fight at the outpost, before any work: true when it took this pass (fighting, getting out of bed to fight, sheltering), false to go on with its work.
    /// A slave always shelters.
    /// </summary>
    bool Defend(World world, SquadWork work, int slot, CharacterId id, CharacterCold c, CharacterTask task, OutpostThreats threats, Construction.PlayerTown town)
    {
        var table = world.Characters;
        var at = table.Next[slot].Position;
        bool attacked = threats.Attackers.Count > 0;
        var mode = c.SlaveState == 1 ? Stance.Shelter : work.Stance;
        if (mode == Stance.Militia && c.Appearance?.Female == true) mode = Stance.Shelter;
        int? foe = mode switch
        {
            Stance.Soldiers => Nearest(table, at, threats.Attackers) ?? Nearest(table, at, threats.Hostiles),
            Stance.Militia => Nearest(table, at, threats.Attackers),
            Stance.StandGround => threats.Attackers.Where(a => Close(table, at, a) || Targets(world, a, slot)).Select(a => (int?)a).FirstOrDefault(),
            _ => null,
        };
        if (foe is { } f)
        {
            sheltering.Remove(id);
            if (c.InSomething == Locks.HeldState.Bed)
            {
                world.Defer(new Locks.GetOutOrder([id]) { Tick = world.Tick + 1 });
                Log(world, $"{c.Name} out of bed to fight");
            }
            else
            {
                // An attacker is fought back (defence); a soldier going after a hostile who attacks nobody starts it.
                world.Defer(new Combat.AttackOrder([id], table.IdOf(f)) { Tick = world.Tick + 1, Defensive = threats.Attackers.Contains(f) });
                Log(world, $"{c.Name} to fight {table.Cold(f)?.Name ?? "an enemy"}");
            }
            status[id] = "defending";
            return true;
        }
        if (mode == Stance.Shelter && attacked)
        {
            status[id] = "sheltering";
            if (sheltering.Add(id) && Shelter(world, town, at) is { } spot)
            {
                world.Defer(new MoveOrder([id], spot) { Tick = world.Tick + 1 });
                Log(world, $"{c.Name} inside to shelter");
            }
            return true;
        }
        // Nothing for it to do about the fight: back to work (one who sheltered stops where it is).
        if (sheltering.Remove(id) && task == CharacterTask.GoTo) MovementSystem.Stop(table, slot);
        return false;

        static bool Close(CharacterTable table, Vector3 at, int a) => Vector2.DistanceSquared(new(at.X, at.Z), new(table.Next[a].Position.X, table.Next[a].Position.Z)) <= LocalReach * LocalReach;
    }

    /// <summary>Whether the attacker in slot <paramref name="a"/> is fighting the character in slot <paramref name="slot"/>.</summary>
    static bool Targets(World world, int a, int slot) =>
        world.SystemOf<Combat.CombatSystem>() is { } combat && combat.StateOf(world.Characters.IdOf(a)).TargetSlot == slot;

    /// <summary>The nearest complete building of the outpost with a door (one has an inside), its centre; null without one.</summary>
    static Vector3? Shelter(World world, Construction.PlayerTown town, Vector3 at)
    {
        var doors = new HashSet<string>(world.Fixtures.All.Where(f => f.Kind == Locks.FixtureKind.Door).Select(f => f.Building), StringComparer.Ordinal);
        Vector3? best = null;
        float bestD = float.MaxValue;
        foreach (var site in world.Buildings.All)
        {
            if (!site.Complete || site.Destroyed || site.Town != town.Number || site.Parent is not null || !doors.Contains(site.Id)) continue;
            float d = Vector3.DistanceSquared(at, site.Position);
            if (d < bestD) (best, bestD) = (site.Position, d);
        }
        return best;
    }

    static int? Nearest(CharacterTable table, Vector3 at, List<int> slots)
    {
        int? best = null;
        float bestD = float.MaxValue;
        foreach (int s in slots)
        {
            float d = Vector3.DistanceSquared(at, table.Next[s].Position);
            if (d < bestD) (best, bestD) = (s, d);
        }
        return best;
    }
}
