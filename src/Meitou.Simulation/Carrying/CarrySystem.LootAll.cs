using System.Numerics;
using Meitou.Data.Fcs;
using Meitou.Simulation.Items;

namespace Meitou.Simulation.Carrying;

// Loot All (Meitou-only, docs/simulation.md "Carrying, rescue and looting"): the selection goes over the bodies about one, each taking only what is better for it.
public sealed partial class CarrySystem
{
    /// <summary>How far from the clicked body the bodies of a Loot All order are taken (engine choice).</summary>
    public const float LootAllRadius = 400;
    /// <summary>How much better (factor) a piece must be than the one it replaces (engine choice: no swapping for a hair).</summary>
    const float LootAllMargin = 1.05f;

    /// <summary>One thing a looter takes from a body: the item and where it goes (an equipment section, or "main" for ammunition).</summary>
    readonly record struct LootPick(ItemInstance Item, string Section);

    /// <summary>
    /// <see cref="LootAllOrder"/>: the bodies (knocked out or dead, with an inventory, not carried or imprisoned, not the player's) within <see cref="LootAllRadius"/> of the target become
    /// one group; each looter able to loot joins it and starts on the nearest body with something better for it (<see cref="NextLootAll"/>).
    /// </summary>
    void StartLootAll(World world, CharacterTable table, LootAllOrder order)
    {
        if (!table.TryResolveNext(order.Target, out int clicked)) return;
        var next = table.Next;
        var at = new Vector2(next[clicked].Position.X, next[clicked].Position.Z);
        var group = new LootAllGroup();
        for (int s = 0; s < table.HighWater; s++)
        {
            if (!next[s].Alive || table.Cold(s) is not { } body || !CanBeLooted(body) || !body.CarriedBy.IsNone || body.IsImprisoned || PlayerSide(world, body)) continue;
            if (Vector2.DistanceSquared(at, new Vector2(next[s].Position.X, next[s].Position.Z)) <= LootAllRadius * LootAllRadius) group.Bodies.Add(table.IdOf(s));
        }
        if (group.Bodies.Count == 0) return;
        var looting = Looting(table);
        foreach (var id in Who(world, order.Looters))
        {
            if (!table.TryResolveNext(id, out int s) || table.Cold(s) is not { IsPlayer: true, Medical.Incapacitated: false, Inventory: not null } cold || !cold.CarriedBy.IsNone) continue;
            LeaveFight(world, s);
            MovementSystem.Stop(table, s);
            cold.LootAll = group;
            if (!NextLootAll(world, table, s, looting)) world.Post($"{cold.Name} found nothing better to take");
        }
    }

    /// <summary>
    /// Every <see cref="CarrySettings.AutoEveryTicks"/> ticks: a Loot All looter that finished a body (idle again, out of combat) goes on to the next; one given another order, or down, leaves
    /// the group.
    /// </summary>
    void KeepLootAll(World world, CharacterTable table)
    {
        HashSet<int>? looting = null;
        for (int s = 0; s < table.HighWater; s++)
        {
            if (!table.Next[s].Alive || table.Cold(s) is not { LootAll: not null } cold) continue;
            var task = (CharacterTask)table.Next[s].Task;
            if (task == CharacterTask.Loot) continue;
            if (task != CharacterTask.Idle || cold.Medical is { Incapacitated: true } || !cold.CarriedBy.IsNone)
            {
                cold.LootAll = null;
                continue;
            }
            if (cold.InCombat) continue;
            looting ??= Looting(table);
            if (!NextLootAll(world, table, s, looting)) world.Post($"{cold.Name} is done looting");
        }
    }

    /// <summary>Sends the looter to the nearest body of its group that nobody else loots now and that holds something better for it; false (and out of the group) when none is left.</summary>
    bool NextLootAll(World world, CharacterTable table, int slot, HashSet<int> looting)
    {
        var cold = table.Cold(slot)!;
        var group = cold.LootAll!;
        var next = table.Next;
        var at = new Vector2(next[slot].Position.X, next[slot].Position.Z);
        int best = -1;
        float best2 = float.MaxValue;
        bool waiting = false;
        for (int k = group.Bodies.Count - 1; k >= 0; k--)
        {
            if (!table.TryResolveNext(group.Bodies[k], out int b) || table.Cold(b) is not { } body || !CanBeLooted(body) || !body.CarriedBy.IsNone)
            {
                group.Bodies.RemoveAt(k);
                continue;
            }
            if (group.Visited.Contains((table.IdOf(slot), group.Bodies[k])) || Picks(cold, body).Count == 0) continue;
            if (looting.Contains(b))
            {
                waiting = true;   // someone is at it: it may still hold something for this one after
                continue;
            }
            float d2 = Vector2.DistanceSquared(at, new Vector2(next[b].Position.X, next[b].Position.Z));
            if (d2 < best2) (best, best2) = (b, d2);
        }
        if (best < 0)
        {
            if (!waiting) cold.LootAll = null;
            return waiting;
        }
        looting.Add(best);
        Begin(table, slot, table.IdOf(best), CharacterTask.Loot, CharacterTask.Idle);
        return true;
    }

    /// <summary>At the body: takes the picks (<see cref="Picks"/>), what they replace going to the looter's main grid, else onto the body; reports what it took.</summary>
    void LootAllTake(World world, CharacterTable table, int i, int t)
    {
        var cold = table.Cold(i)!;
        var victim = table.Cold(t)!;
        cold.LootAll?.Visited.Add((table.IdOf(i), table.IdOf(t)));
        var into = cold.Inventory!;
        var from = victim.Inventory!;
        int stolen = Crime.CrimeSystem.IsLootTheft(cold, victim) ? victim.Faction : -1;
        var names = new List<string>();
        bool wear = false, worn = false;
        foreach (var pick in Picks(cold, victim))
        {
            var owner = from.Items.Contains(pick.Item) ? from : from.Backpack?.Contents is { } bag && bag.Items.Contains(pick.Item) ? bag : null;
            if (owner is null) continue;
            bool gear = InventoryCommands.IsEquipmentSection(pick.Item.Section);
            if (stolen >= 0 && pick.Item.StolenFrom < 0) pick.Item.StolenFrom = stolen;
            if (pick.Section == "main")
            {
                if (InventoryCommands.MoveUnits(owner, pick.Item, 0, into) > 0) names.Add(pick.Item.Name);
                continue;
            }
            // Make room: what the section holds goes to the main grid, else onto the body; if neither has room the pick is left.
            var displaced = into.Items.Where(x => x.Section == pick.Section).ToList();
            if (displaced.Any(x => x.LockedOn)) continue;
            var put = new List<(ItemInstance Item, Inventory Where)>();
            bool room = true;
            foreach (var d in displaced)
            {
                var (section, x, y) = (d.Section, d.X, d.Y);
                into.Items.Remove(d);
                if (into.AddTo("main", d)) put.Add((d, into));
                else if (from.AddTo("main", d)) put.Add((d, from));
                else
                {
                    (d.Section, d.X, d.Y) = (section, x, y);
                    into.Items.Add(d);
                    room = false;
                    break;
                }
            }
            if (!room)
            {
                foreach (var (d, where) in put) { where.Items.Remove(d); d.Section = pick.Section; into.Items.Add(d); }
                continue;
            }
            owner.Items.Remove(pick.Item);
            pick.Item.Death = false;
            pick.Item.Rotated = false;
            (pick.Item.Section, pick.Item.X, pick.Item.Y) = (pick.Section, 0, 0);
            into.Items.Add(pick.Item);
            names.Add(pick.Item.Name);
            wear = true;
            worn |= gear;
        }
        if (worn) Assembly?.RefreshEquipment(victim);
        if (wear) Assembly?.RefreshEquipment(cold);
        bool seen = Looted(world, table, i, t, names.Count, 0);
        if (names.Count > 0) world.Post($"{cold.Name} took {string.Join(", ", names.Distinct())} from {victim.Name}");
        if (seen) world.Post($"{cold.Name} was seen looting {victim.Name}");
    }

    /// <summary>
    /// What a looter would take from a body (engine choices, <see cref="CharacterAssembly"/> worths): per armour section, the body's best piece the looter's race may wear when it is worth more than
    /// <see cref="LootAllMargin"/> × the one it wears (and more than nothing); for the hip, the best melee weapon that fits it, against the hip weapon (a crossbow there is kept); for the
    /// crossbow, the best one against its own (into its section, else the back); then the ammunition of the crossbow it will have.
    /// </summary>
    List<LootPick> Picks(CharacterCold looter, CharacterCold body)
    {
        var picks = new List<LootPick>();
        if (Assembly is not { } worth || looter.Inventory is not { } mine || body.Inventory is not { } theirs) return picks;
        string? race = looter.Race?.StringId;
        var offered = theirs.Items.Concat(theirs.Backpack?.Contents?.Items ?? []).Where(x => !x.LockedOn && x.Quantity >= 1 && InventoryCommands.IsMovableSection(x.Section)).ToList();
        bool Better(float? have, float? found) => found is { } f && f > 0 && f > (have is { } h && h > 0 ? h * LootAllMargin : 0);

        foreach (var section in offered.Where(x => x.Type == FcsRecordType.ARMOUR && x.Quantity == 1 && x.WearableBy(race) && InventoryCommands.IsSingleSlot(x.EquipmentSection)
            && x.EquipmentSection != "backpack_attach").Select(x => x.EquipmentSection).Distinct())
        {
            var current = mine.Items.FirstOrDefault(x => x.Section == section);
            if (current is { LockedOn: true }) continue;
            var best = offered.Where(x => x.Type == FcsRecordType.ARMOUR && x.EquipmentSection == section && x.Quantity == 1 && x.WearableBy(race)).MaxBy(x => worth.ArmourWorth(x) ?? float.MinValue);
            if (best is not null && Better(current is null ? 0 : worth.ArmourWorth(current), worth.ArmourWorth(best))) picks.Add(new(best, section));
        }

        // Worn upright (the take clears a turn).
        bool Fits(ItemInstance x, string section) => InventoryCommands.SectionSize(section) is var (w, h) && (x.Rotated ? x.Height : x.Width) <= w && (x.Rotated ? x.Width : x.Height) <= h;
        var hip = mine.Items.FirstOrDefault(x => x.Section == "hip");
        if (hip is null or { Type: FcsRecordType.WEAPON })
        {
            var blade = offered.Where(x => x.Type == FcsRecordType.WEAPON && x.Quantity == 1 && x.WearableBy(race) && Fits(x, "hip")).MaxBy(x => worth.MeleeWorth(x, looter) ?? float.MinValue);
            if (blade is not null && Better(hip is null ? 0 : worth.MeleeWorth(hip, looter), worth.MeleeWorth(blade, looter))) picks.Add(new(blade, "hip"));
        }

        var bow = mine.Items.FirstOrDefault(x => x.Type == FcsRecordType.CROSSBOW && x.Section is "back" or "hip");
        var found = offered.Where(x => x.Type == FcsRecordType.CROSSBOW && x.Quantity == 1 && x.WearableBy(race)).MaxBy(x => worth.CrossbowWorth(x) ?? float.MinValue);
        string bowSection = bow?.Section ?? "back";
        if (found is not null && Fits(found, bowSection) && Better(bow is null ? 0 : worth.CrossbowWorth(bow), worth.CrossbowWorth(found)))
        {
            picks.Add(new(found, bowSection));
            bow = found;
        }
        if (bow is not null && worth.AmmoOf(bow) is { Length: > 0 } ammo)
            foreach (var bolts in offered.Where(x => x.Record == ammo)) picks.Add(new(bolts, "main"));
        return picks;
    }
}
