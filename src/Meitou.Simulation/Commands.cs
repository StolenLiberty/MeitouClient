using System.Numerics;
using Meitou.Data.World;

namespace Meitou.Simulation;

/// <summary>
/// Something the player (or a test) tells the world, applied at the start of simulation tick <see cref="Tick"/>
/// (docs/simulation.md, "Threading": inputs are step 1 of a tick). Commands never touch the world directly, so the same commands
/// at the same ticks give the same world.
/// </summary>
public abstract record SimCommand
{
    /// <summary>The simulation tick the command applies at; a command for a tick already run applies at the next one.</summary>
    public required long Tick { get; init; }
}

/// <summary>Move order: the characters walk to <see cref="Target"/> (world units; the height is taken from the ground).</summary>
public sealed record MoveOrder(IReadOnlyList<CharacterId> Characters, Vector3 Target) : SimCommand
{
    /// <summary>Add to the characters' order lists (shift in the game) instead of replacing them.</summary>
    public bool Queued { get; init; }
}

/// <summary>
/// Meitou's formation move (a right-button drag on the ground, as Total War's): each character walks to its own spot and, arrived, turns to <see cref="Facing"/> (X/Z, unit).
/// <see cref="Targets"/> pairs with <see cref="Characters"/>.
/// </summary>
public sealed record FormationOrder(IReadOnlyList<CharacterId> Characters, IReadOnlyList<Vector3> Targets, Vector2 Facing) : SimCommand
{
    /// <summary>Added to the characters' order lists (Shift) instead of replacing them; a queued spot keeps no facing.</summary>
    public bool Queued { get; init; }
}

/// <summary>
/// The context menu's Follow (order 31, <c>Task_Follow</c>; docs/game/ui-input.md "Context menu verbs"): the characters (the selection when empty) keep a formation place behind
/// <see cref="Target"/> until another order replaces it or the target is gone (<see cref="CharacterCold.FollowOrder"/>). <b>Engine</b>: the squad follower's slot rule, not saved.
/// </summary>
public sealed record FollowOrder(IReadOnlyList<CharacterId> Characters, CharacterId Target) : SimCommand
{
    /// <summary>Bodyguard (order 45) rather than Follow (31): the follower also fights whoever attacks the target (<see cref="CharacterCold.Guarding"/>). <b>Engine</b> reading of the order: the original shares the follow order giver and the task type's data names no more.</summary>
    public bool Guard { get; init; }
}

/// <summary>Sandbox only: fixes a character's speed mode (null: leave) and/or its walking speed in units per second (null: leave; 0: the speed chain's again). The animation sandbox sends it, the game never does.</summary>
public sealed record SandboxMovement(CharacterId Character, byte? Mode, float? Speed) : SimCommand;

/// <summary>
/// Commands waiting for their tick. Any thread may <see cref="Enqueue"/> (the UI runs on the host's thread); the simulation takes
/// the due ones with <see cref="TakeDue"/> in a fixed order: by tick, then in the order they were queued.
/// </summary>
public sealed class CommandQueue
{
    readonly Lock gate = new();
    readonly List<(SimCommand Command, long Sequence)> pending = [];
    long sequence;

    public int Count
    {
        get { lock (gate) return pending.Count; }
    }

    public void Enqueue(SimCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        lock (gate) pending.Add((command, sequence++));
    }

    /// <summary>Removes and returns the commands whose tick is at or before <paramref name="tick"/>, oldest tick first, then queue order.</summary>
    public IReadOnlyList<SimCommand> TakeDue(long tick, Func<SimCommand, bool>? include = null)
    {
        List<(SimCommand Command, long Sequence)>? due = null;
        lock (gate)
        {
            if (pending.Count == 0) return [];
            int kept = 0;
            for (int k = 0; k < pending.Count; k++)
            {
                var p = pending[k];
                if (p.Command.Tick <= tick && (include is null || include(p.Command))) (due ??= []).Add(p);
                else pending[kept++] = p;
            }
            if (due is null) return [];
            pending.RemoveRange(kept, pending.Count - kept);
        }
        due.Sort(static (a, b) => a.Command.Tick != b.Command.Tick ? a.Command.Tick.CompareTo(b.Command.Tick) : a.Sequence.CompareTo(b.Sequence));
        var commands = new SimCommand[due.Count];
        for (int k = 0; k < commands.Length; k++) commands[k] = due[k].Command;
        return commands;
    }
}

/// <summary>
/// Tells the world where the player is looking (the camera focus), which zones to keep active and whose town residents to load
/// (docs/game/game-loop.md "Zones": the game activates zones around the player's characters; until there is a player, the camera).
/// </summary>
public sealed record FocusCommand(Vector3 Position) : SimCommand;

/// <summary>Selects the player's own characters. No valid targets retains the selection; otherwise non-additive replaces it and additive (shift) adds to it.</summary>
public sealed record SelectCommand(IReadOnlyList<CharacterId> Characters, bool Additive = false) : SimCommand;

/// <summary>Clears order selection and shows another faction's character in the HUD panels. <see cref="CharacterId.None"/> clears inspection only.</summary>
public sealed record InspectCommand(CharacterId Target) : SimCommand;

/// <summary>The player's characters the pinned inventory windows show: the snapshot carries their item grids while they are not selected. Display only.</summary>
public sealed record ShowInventoryCommand(IReadOnlyList<CharacterId> Targets) : SimCommand;

/// <summary>The building the HUD panels show (docs/game/ui-screens.md "Selected building"): the snapshot then carries its doors' state. A null instance clears it. Display only.</summary>
public sealed record InspectBuildingCommand(string? Instance) : SimCommand;

/// <summary>The map was closed: towns lose their recently-discovered mark (<see cref="TownKnowledge.Recent"/>).</summary>
public sealed record SeenDiscoveriesCommand : SimCommand;

/// <summary>Stop: the characters (the selection when the list is empty) drop their orders and stand.</summary>
public sealed record StopCommand(IReadOnlyList<CharacterId> Characters) : SimCommand;

/// <summary>
/// The navmesh changed (a zone became ready, a door moved): characters on the way ask for their path again (those with a go-to in hand).
/// Paths made while a zone was still building came from the open-ground stand-in and may cross buildings.
/// With <see cref="Zones"/>, only those whose path still to walk crosses one of them, and those whose answer is on its way; null: all.
/// </summary>
public sealed record RepathCommand : SimCommand
{
    public IReadOnlySet<ZoneCoordinate>? Zones { get; init; }
}
