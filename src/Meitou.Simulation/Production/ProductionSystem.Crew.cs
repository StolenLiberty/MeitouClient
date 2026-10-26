using System.Numerics;
using Meitou.Data.Gameplay.Construction;
using Meitou.Simulation.Construction;
using Meitou.Simulation.Items;

namespace Meitou.Simulation.Production;

/// <summary>A haul a crew's hauler makes (Meitou): pick up <see cref="Item"/> at <see cref="From"/> (a machine's output, a storage; the same as <see cref="To"/>: what it carries), take it to <see cref="To"/>.</summary>
internal readonly record struct CrewTrip(string From, string To, string Item);

// Crews and haulers (docs/simulation.md "Squad jobs", engine choices throughout): a material crew keeps some members at the material's mines, some at its processors and the rest hauling
// between them and the storages, so the station workers never leave their machine (CharacterCold.StationUntil stops their own hauling); the Haulers job hauls for every machine.
public sealed partial class ProductionSystem
{
    /// <summary>The material whose chain a machine is in (<see cref="InChain"/>: iron, stone, copper), 0 none.</summary>
    internal int MaterialOf(MachineData m)
    {
        foreach (int r in (ReadOnlySpan<int>)[ResourceField.StoneResource, ResourceField.IronResource, ResourceField.CopperResource])
            if (InChain(m, r)) return r;
        return 0;
    }

    public static string MaterialName(int material) => material switch
    {
        ResourceField.StoneResource => "stone", ResourceField.IronResource => "iron", ResourceField.CopperResource => "copper", _ => "any",
    };

    /// <summary>The machines of one material's chain in a player outpost (<see cref="InChain"/>): its mines (natural rocks included) and its processors, by site id order.</summary>
    internal (List<Machine> Mines, List<Machine> Processors) CrewMachines(World world, PlayerTown town, int material, IReadOnlyCollection<string>? only)
    {
        List<Machine> mines = [], processors = [];
        foreach (var m in world.Machines.All)
        {
            if (!InChain(m.Data, material) || m.Data.IsAutomatic || !Workable(world, town, m) || only is { Count: > 0 } && !only.Contains(m.Site)) continue;
            (m.Data.Function == MachineData.RefineryFunction ? processors : mines).Add(m);
        }
        mines.Sort((a, b) => string.CompareOrdinal(a.Site, b.Site));
        processors.Sort((a, b) => string.CompareOrdinal(a.Site, b.Site));
        return (mines, processors);
    }

    /// <summary>
    /// What the Haulers job carries between, in the outpost (or <paramref name="only"/>): producers (mines, farms, wells: an output and no inputs) and processors (a machine with inputs
    /// it uses and an output), by site id order.
    /// </summary>
    internal (List<Machine> Producers, List<Machine> Processors) HaulMachines(World world, PlayerTown town, IReadOnlyCollection<string>? only)
    {
        List<Machine> producers = [], processors = [];
        foreach (var m in world.Machines.All)
        {
            if (m.Data.IsStorage || m.Output is null || !Workable(world, town, m) || only is { Count: > 0 } && !only.Contains(m.Site)) continue;
            (m.Inputs.Any(i => i.Rate > 0) ? processors : producers).Add(m);
        }
        producers.Sort((a, b) => string.CompareOrdinal(a.Site, b.Site));
        processors.Sort((a, b) => string.CompareOrdinal(a.Site, b.Site));
        return (producers, processors);
    }

    /// <summary>A complete machine in the outpost's radius: the player's own, or a natural rock.</summary>
    bool Workable(World world, PlayerTown town, Machine m)
    {
        if (Site(world, m.Site) is not { Complete: true } site || !IsNatural(m.Site) && site.Owner != world.Player.Faction) return false;
        return Vector2.DistanceSquared(new(site.Position.X, site.Position.Z), new(town.Position.X, town.Position.Z)) <= town.Radius * town.Radius;
    }

    /// <summary>The machine of the material's chain the character is on now, or the one of <paramref name="stations"/> with the most free room (then the nearest); null when all are full.</summary>
    internal string? CrewStation(World world, int slot, List<Machine> stations)
    {
        var table = world.Characters;
        if (table.Cold(slot) is not { } cold) return null;
        if (cold.OperateSite is { } now && stations.Any(m => m.Site == now)) return now;
        var at = table.Next[slot].Position;
        string? best = null;
        (int Free, float D) top = (0, 0);
        foreach (var m in stations)
        {
            int free = m.Data.MaxOperators - OperatorsOf(world, m.Site);
            if (free <= 0 || Blocked(world, cold, new PermaJob(Meitou.Data.Gameplay.Ai.TaskType.OPERATE_MACHINERY, m.Site))) continue;
            float d = Site(world, m.Site) is { } s ? Vector3.DistanceSquared(at, s.Position) : float.MaxValue;
            if (best is null || free > top.Free || free == top.Free && d < top.D) (best, top) = (m.Site, (free, d));
        }
        return best;
    }

    /// <summary>
    /// The next haul for a hauler, or null, the end of the chain first: what it carries (of <paramref name="chain"/>; null: anything a machine makes) to a processor that takes it (else a
    /// storage); a processor's full output to storage; a processor input below half from a producer (else a storage); a producer's full output; then any output a quarter full. A source
    /// another hauler is making for is left to it.
    /// </summary>
    internal CrewTrip? CrewHaul(World world, int slot, PlayerTown town, HashSet<string>? chain, List<Machine> mines, List<Machine> processors)
    {
        var table = world.Characters;
        if (table.Cold(slot) is not { Inventory: { } inv }) return null;
        bool Ours(string record) => chain?.Contains(record) ?? world.Machines.All.Any(m => !m.Data.IsStorage && m.Output?.Recipe.Item == record);
        var storages = world.Machines.All.Where(s => s.Data.IsStorage && s.Storage is not null && Workable(world, town, s)).OrderBy(s => s.Site, StringComparer.Ordinal).ToList();
        string? Into(string item, int carry)
        {
            // A processor somebody works first, the emptiest.
            foreach (var p in processors.OrderByDescending(p => OperatorsOf(world, p.Site) > 0).ThenBy(p => p.Inputs.Where(i => i.Item == item).Sum(i => i.Count)))
                if (p.Inputs.Any(i => i.Item == item && i.Rate > 0 && i.Count < i.Recipe.Capacity)) return p.Site;
            if (items.Create(item, Math.Max(1, carry)) is not { } one) return null;
            return storages.FirstOrDefault(s => s.Data.Accepts(one.Record, one.Type) && Fits(s.Storage!, one))?.Site;
        }
        string? Store(string item) => items.Create(item, 1) is { } one ? storages.FirstOrDefault(s => s.Data.Accepts(one.Record, one.Type) && Fits(s.Storage!, one))?.Site : null;
        bool Claimed(string source, string item)
        {
            for (int i = 0; i < table.HighWater; i++)
                if (i != slot && table.Next[i].Alive && table.Next[i].Task == (byte)CharacterTask.Haul && table.Cold(i) is { HaulSite: { } at, HaulFetch: { } what } && at == source && what == item) return true;
            return false;
        }
        // What it carries.
        foreach (var item in inv.All().Where(i => i.Section is "main" or "backpack_content" && !i.LockedOn && Ours(i.Record)).Select(i => i.Record).Distinct().ToList())
            if (Into(item, 1) is { } to) return new(to, to, item);
        // The end of the chain first, so it keeps flowing: a processor's full output to storage.
        foreach (var p in processors.Where(m => m.Output is { Full: true }))
            if (!Claimed(p.Site, p.Output!.Recipe.Item) && Store(p.Output.Recipe.Item) is { } to) return new(p.Site, to, p.Output.Recipe.Item);
        // A processor running low.
        foreach (var p in processors.OrderByDescending(p => OperatorsOf(world, p.Site) > 0))
            foreach (var input in p.Inputs)
            {
                if (input.Rate <= 0 || input.Count * 2 >= input.Recipe.Capacity) continue;
                var mine = mines.Where(m => m.Output is { Count: > 0 } o && o.Recipe.Item == input.Item && !Claimed(m.Site, input.Item)).OrderByDescending(m => m.Output!.Count).FirstOrDefault();
                if (mine is not null) return new(mine.Site, p.Site, input.Item);
                var store = storages.FirstOrDefault(s => s.Storage!.Items.Any(i => i.Record == input.Item && i.Section == "main") && !Claimed(s.Site, input.Item));
                if (store is not null) return new(store.Site, p.Site, input.Item);
            }
        // A mine's full output, then any output worth a trip (processors first).
        foreach (var m in mines.Where(m => m.Output is { Full: true }))
            if (!Claimed(m.Site, m.Output!.Recipe.Item) && Into(m.Output.Recipe.Item, m.Output.Count) is { } to) return new(m.Site, to, m.Output.Recipe.Item);
        foreach (var m in processors.Concat(mines).Where(m => m.Output is { Count: > 0 } o && o.Count * 4 >= o.Recipe.Capacity))
        {
            string item = m.Output!.Recipe.Item;
            if (Claimed(m.Site, item)) continue;
            if ((processors.Contains(m) ? Store(item) : Into(item, m.Output.Count)) is { } to) return new(m.Site, to, item);
        }
        return null;
    }

    /// <summary>Sets the hauler off on a trip (task <see cref="CharacterTask.Haul"/>, <see cref="CharacterCold.HaulFetch"/> naming the item); false when a site is gone.</summary>
    internal bool CrewHaulBegin(World world, int slot, CrewTrip trip)
    {
        if (Site(world, trip.From) is not { Complete: true } || Site(world, trip.To) is not { Complete: true } || world.Machines.Find(trip.From) is null || world.Machines.Find(trip.To) is null) return false;
        var table = world.Characters;
        var cold = table.Cold(slot)!;
        End(table, slot);
        ref var n = ref table.Next[slot];
        MovementSystem.Stop(table, slot);
        n.Task = (byte)CharacterTask.Haul;
        n.Mode = SpeedMode.Free;
        n.TaskTime = 0;
        cold.HaulSite = trip.From;
        cold.HaulTo = trip.To;
        cold.HaulFetch = trip.Item;
        sweep = true;
        return true;
    }

    /// <summary>A crew hauler at the end of a leg (serial): at the source it takes the item (a machine's output, a storage's stacks up to what the processor holds room for), then makes for
    /// the destination; there it fills the processor's input or puts it in the storage. What is left it keeps (its next trip takes it to a storage).</summary>
    void CrewHaulStep(World world, int slot)
    {
        var table = world.Characters;
        var cold = table.Cold(slot)!;
        ref var n = ref table.Next[slot];
        if (cold.HaulTo is not { } toId || cold.HaulSite is not { } atId || cold.HaulFetch is not { } item
            || Site(world, toId) is not { Complete: true } || world.Machines.Find(toId) is not { } to
            || Site(world, atId) is not { Complete: true } atSite || world.Machines.Find(atId) is not { } at || !Able(cold) || cold.Inventory is not { } inv)
        {
            End(table, slot);
            return;
        }
        if (!AtSite(n, atSite, at)) return;
        if (atId != toId)
        {
            MakeRoom(world, slot, items.Create(item, 1), at.Data.IsStorage ? at : null);
            int room = to.Storage is null ? to.Inputs.Where(i => i.Item == item).Sum(i => Math.Max(0, i.Recipe.Capacity - i.Count)) : int.MaxValue;
            if (at.Output is { Count: > 0 } output && output.Recipe.Item == item && items.Create(item, Math.Min(output.Count, room)) is { } made)
                output.Count -= Placed(inv, made);
            else if (at.Storage is { } storage)
                foreach (var stack in storage.Items.Where(i => i.Record == item && i.Section == "main").ToList())
                {
                    if (room <= 0) break;
                    int k = Math.Min(room, stack.Quantity);
                    var moving = k == stack.Quantity ? stack : stack.WithQuantity(k);
                    if (ReferenceEquals(moving, stack)) storage.Items.Remove(stack);
                    else stack.Quantity -= k;
                    int placed = Placed(inv, moving);
                    if (placed < k) storage.Add(moving);
                    room -= placed;
                    if (placed == 0) break;
                }
            world.Machines.Touch();
            if (Carried(inv, item).Any()) { cold.HaulSite = toId; n.TaskTime = 0; }
            else End(table, slot);
            return;
        }
        foreach (var input in to.Inputs.Where(i => i.Item == item && i.Rate > 0))
            foreach (var stack in Carried(inv, item).ToList())
            {
                int k = Math.Min(input.Recipe.Capacity - input.Count, stack.Quantity);
                if (k <= 0) break;
                input.Count += k;
                stack.Quantity -= k;
                if (stack.Quantity == 0) inv.Remove(stack);
            }
        if (to.Storage is { } into && to.Data.IsStorage)
            foreach (var stack in Carried(inv, item).ToList())
            {
                if (!to.Data.Accepts(stack.Record, stack.Type)) break;
                inv.Remove(stack);
                if (into.Add(stack)) continue;
                if (!inv.Add(stack)) world.GroundItems.Drop(stack, to.Position, 0);
                break;
            }
        world.Machines.Touch();
        End(table, slot);
    }
}
