using Meitou.Simulation.Items;

namespace Meitou.Simulation.Production;

// Making room for the job (Meitou, engine choices; docs/simulation.md "Squad jobs"): a worker whose inventory has no room for what its job carries (building materials, a machine's output,
// a hauler's load) puts what it does not need where it stands (into the storage it is at when that takes it, else on the ground) until the job's item fits. Kept: what is worn, food,
// first aid, splint and robot repair kits, containers, locked items, and the job's own item.
public sealed partial class ProductionSystem
{
    /// <summary>Whether one unit of <paramref name="unit"/> (null: any 1 x 1 item) fits: a stack of it with room, or a free cell, in the main grid or the backpack.</summary>
    internal static bool HasRoom(Inventory inv, ItemInstance? unit)
    {
        int w = unit?.Width ?? 1, h = unit?.Height ?? 1;
        if (unit is not null)
        {
            int limit = inv.LimitOf(unit);
            if (inv.Items.Any(i => i.Record == unit.Record && i.Section == "main" && i.Quantity < limit && i.Quality == unit.Quality)) return true;
        }
        if (inv.FindCell("main", w, h, out _, out _)) return true;
        return inv.Backpack?.Contents is { } bag && (bag.FindCell("backpack_content", w, h, out _, out _)
            || unit is not null && bag.Items.Any(i => i.Record == unit.Record && i.Quantity < bag.LimitOf(unit)));
    }

    static bool Keeps(ItemInstance i, string? job) =>
        i.LockedOn || i.Contents is not null || i.IsFood || i.ItemFunction is ItemInfo.FirstAidKitFunction or ItemInfo.SplintKitFunction or ItemInfo.RobotKitFunction
        || job is not null && i.Record == job;

    /// <summary>
    /// Clears room for one unit of <paramref name="unit"/> (null: a 1 x 1 cell) in the character's inventory, biggest things first: each goes into <paramref name="store"/> (the storage it
    /// stands at) when that takes it, else on the ground at its feet. The number of stacks put aside; it says "Making room." once when it puts any aside.
    /// </summary>
    internal static int MakeRoom(World world, int slot, ItemInstance? unit, Machine? store = null)
    {
        var table = world.Characters;
        if (table.Cold(slot) is not { Inventory: { } inv } cold || HasRoom(inv, unit)) return 0;
        var bag = inv.Backpack?.Contents;
        var junk = inv.Items.Where(i => i.Section == "main" && !Keeps(i, unit?.Record)).Select(i => (Item: i, From: inv))
            .Concat(bag?.Items.Where(i => !Keeps(i, unit?.Record)).Select(i => (Item: i, From: bag)) ?? [])
            .OrderByDescending(j => j.Item.Width * j.Item.Height).ThenByDescending(j => j.Item.Weight).ToList();
        int moved = 0;
        var at = table.Next[slot].Position;
        foreach (var (item, from) in junk)
        {
            if (HasRoom(inv, unit)) break;
            from.Remove(item);
            item.Section = "main";
            // What the storage takes goes in; the rest (or what it has no room for) on the ground.
            if (!(store?.Storage is { } box && store.Data.IsStorage && store.Data.Accepts(item.Record, item.Type) && box.Add(item))) world.GroundItems.Drop(item, at, table.Next[slot].Yaw);
            moved++;
        }
        if (moved > 0)
        {
            world.Machines.Touch();
            world.Bark(table.IdOf(slot), "Making room.");
        }
        return moved;
    }
}
