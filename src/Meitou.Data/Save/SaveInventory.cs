using System.Numerics;
using Meitou.Data.Fcs;

namespace Meitou.Data.Save;

/// <summary>
/// One item of a saved inventory: an INVENTORY_ITEM_STATE (docs/formats/save.md), with what the engine models. <see cref="Source"/> is the record it was read from, so
/// the keys the engine does not model (<c>color sid</c>, <c>uniform</c>, <c>unique</c>, <c>death</c>, the <c>ownedby</c> and <c>insideBuilding</c> handles) survive a round trip.
/// </summary>
public sealed class SaveItem
{
    public required string BaseId { get; init; }
    public string MaterialId { get; init; } = "";
    public string CompanyId { get; init; } = "";
    /// <summary>The <c>crafter</c> key: the name of the smith who made the item at a crafting bench (empty: none).</summary>
    public string Crafter { get; init; } = "";
    public string Section { get; init; } = "main";
    public int X { get; init; }
    public int Y { get; init; }
    public int Quantity { get; init; } = 1;
    public int Level { get; init; }
    public int ItemFunction { get; init; }
    public float Quality { get; init; }
    public float Charges { get; init; } = 1;
    /// <summary>A removed prosthetic's condition, the <c>dam</c>, <c>wear</c> and <c>stun</c> floats (docs/game/character-stats.md "Limb replacements and robots"); 0 for other items.</summary>
    public float Damage { get; init; }
    public float Wear { get; init; }
    public float Stun { get; init; }
    /// <summary>The <c>death</c> bool: a death item still on its owner (docs/game/animals.md "Death items").</summary>
    public bool Death { get; init; }
    /// <summary>Meitou's <c>meitou rotated</c> bool: the item lies turned a quarter in its grid (written only when true).</summary>
    public bool Rotated { get; init; }
    /// <summary>Shackles' lock (LockedArmour, docs/game/locks.md "Save keys"): <c>lock lvl</c>, <c>locked</c>, <c>broken</c>, <c>hardness</c>; null for other items.</summary>
    public SaveItemLock? Lock { get; init; }
    /// <summary>A container's contents (the INVENTORY_STATE its one instance names); null for other items.</summary>
    public List<SaveItem>? Contents { get; init; }
    public FcsRecord? Source { get; init; }
}

/// <summary>An item's lock as saved; <see cref="Locked"/> is written true only while the lock is effectively locked (as the original's writer, <c>FUN_14029ffd0</c>).</summary>
public readonly record struct SaveItemLock(int Level, bool Locked, bool Broken, float Hardness);

/// <summary>Reads and writes the items of an INVENTORY_STATE inside one save file (a platoon's).</summary>
public static class SaveInventory
{
    /// <summary>
    /// The items an INVENTORY_STATE holds: each instance names an INVENTORY_ITEM_STATE of the same file; a container item's single instance names the INVENTORY_STATE of its
    /// contents (<b>Verified</b> in the samples, save.md). Instances whose target is missing are skipped.
    /// </summary>
    public static List<SaveItem> Read(SaveFile file, FcsRecord inventory)
    {
        var byId = new Dictionary<string, FcsRecord>(StringComparer.Ordinal);
        foreach (var r in file.Records) byId.TryAdd(r.StringId, r);
        return Read(byId, inventory, 0);
    }

    static List<SaveItem> Read(Dictionary<string, FcsRecord> byId, FcsRecord inventory, int depth)
    {
        var items = new List<SaveItem>();
        if (depth > 4) return items;
        foreach (var instance in inventory.Instances)
        {
            if (!byId.TryGetValue(instance.Target, out var r) || r.RecordType != FcsRecordType.INVENTORY_ITEM_STATE) continue;
            List<SaveItem>? contents = null;
            if (r.Instances.Count > 0 && byId.TryGetValue(r.Instances[0].Target, out var inner) && inner.RecordType == FcsRecordType.INVENTORY_STATE)
                contents = Read(byId, inner, depth + 1);
            items.Add(ItemOf(r, contents));
        }
        return items;
    }

    /// <summary>The modelled keys of an INVENTORY_ITEM_STATE, with <paramref name="contents"/> for a container.</summary>
    internal static SaveItem ItemOf(FcsRecord r, List<SaveItem>? contents = null) => new()
    {
        BaseId = r.Strings.GetValueOrDefault("base data sid", ""),
        MaterialId = r.Strings.GetValueOrDefault("material sid", ""),
        CompanyId = r.Strings.GetValueOrDefault("company sid", ""),
        Crafter = r.Strings.GetValueOrDefault("crafter", ""),
        Section = r.Strings.GetValueOrDefault("section", "main"),
        X = r.Ints.GetValueOrDefault("inventory x"),
        Y = r.Ints.GetValueOrDefault("inventory y"),
        Quantity = r.Ints.GetValueOrDefault("quantity", 1),
        Level = r.Ints.GetValueOrDefault("level"),
        ItemFunction = r.Ints.GetValueOrDefault("item function"),
        Quality = r.Floats.GetValueOrDefault("quality"),
        Charges = r.Floats.GetValueOrDefault("charges", 1),
        Damage = r.Floats.GetValueOrDefault("dam"),
        Wear = r.Floats.GetValueOrDefault("wear"),
        Stun = r.Floats.GetValueOrDefault("stun"),
        Death = r.Bools.GetValueOrDefault("death"),
        Rotated = r.Bools.GetValueOrDefault("meitou rotated"),
        Lock = r.Ints.TryGetValue("lock lvl", out int lockLevel) ? new SaveItemLock(lockLevel, r.Bools.GetValueOrDefault("locked"), r.Bools.GetValueOrDefault("broken"), r.Floats.GetValueOrDefault("hardness")) : null,
        Contents = contents,
        Source = r,
    };

    /// <summary>The records an INVENTORY_STATE reaches: its items and, through containers, their contents' states and items (not the state itself).</summary>
    internal static HashSet<string> Reachable(SaveFile file, FcsRecord inventory)
    {
        var byId = new Dictionary<string, FcsRecord>(StringComparer.Ordinal);
        foreach (var r in file.Records) byId.TryAdd(r.StringId, r);
        var found = new HashSet<string>(StringComparer.Ordinal);
        void Walk(FcsRecord r)
        {
            foreach (var i in r.Instances)
                if (found.Add(i.Target) && byId.TryGetValue(i.Target, out var next)) Walk(next);
        }
        Walk(inventory);
        return found;
    }

    /// <summary>
    /// The INVENTORY_ITEM_STATE of an item: a copy of its <see cref="SaveItem.Source"/> when it has one (the keys the engine does not model kept), else the key set every
    /// real item record has (null handles, no colour or uniform), with the modelled keys written over.
    /// </summary>
    internal static FcsRecord ItemRecord(SaveItem item)
    {
        var r = new FcsRecord { Type = (int)FcsRecordType.INVENTORY_ITEM_STATE, Name = "0" };
        if (item.Source is { } s)
        {
            foreach (var (k, v) in s.Strings) r.Strings[k] = v;
            foreach (var (k, v) in s.Ints) r.Ints[k] = v;
            foreach (var (k, v) in s.Floats) r.Floats[k] = v;
            foreach (var (k, v) in s.Bools) r.Bools[k] = v;
            foreach (var (k, v) in s.Vector3s) r.Vector3s[k] = v;
        }
        else
        {
            r.Strings["uniform"] = "";
            r.Strings["color sid"] = "";
            Hand.Null.Write(r, "insideBuilding");
            Hand.Null.Write(r, "ownedby");
        }
        r.Bools["death"] = item.Death;
        if (item.Rotated) r.Bools["meitou rotated"] = true; else r.Bools.Remove("meitou rotated");
        r.Strings["material sid"] = item.MaterialId;
        r.Strings["company sid"] = item.CompanyId;
        if (item.Crafter.Length > 0) r.Strings["crafter"] = item.Crafter;   // written only when not empty, as the original
        r.Strings["section"] = item.Section;
        r.Strings["base data sid"] = item.BaseId;
        r.Ints["item function"] = item.ItemFunction;
        r.Ints["inventory x"] = item.X;
        r.Ints["inventory y"] = item.Y;
        r.Ints["level"] = item.Level;
        r.Ints["quantity"] = item.Quantity;
        r.Floats["charges"] = item.Charges;
        r.Floats["quality"] = item.Quality;
        // A prosthetic's condition: written where the record had it or it is not zero.
        foreach (var (k, v) in new[] { ("dam", item.Damage), ("wear", item.Wear), ("stun", item.Stun) })
            if (v != 0 || r.Floats.ContainsKey(k)) r.Floats[k] = v;
        if (item.Lock is { } l)
        {
            r.Ints["lock lvl"] = l.Level;
            r.Bools["locked"] = l.Locked;
            r.Bools["broken"] = l.Broken;
            r.Floats["hardness"] = l.Hardness;
        }
        r.Bools["in inventory"] = true;
        return r;
    }

    internal static FcsInstance Link(int index, string target) => new() { Id = index.ToString(System.Globalization.CultureInfo.InvariantCulture), Target = target, Position = Vector3.Zero, Rotation = Quaternion.Identity };

    /// <summary>
    /// Replaces what an INVENTORY_STATE of <paramref name="file"/> holds with <paramref name="items"/>: the old item records (and the contents of containers among them) leave the file, and
    /// each item gets an INVENTORY_ITEM_STATE linked by an instance numbered from 1; a container's contents get an INVENTORY_STATE of their own, linked by the item's one instance, as the
    /// game writes them. An item read from this inventory keeps its record's id and place in the file, so an inventory that did not change is written back the same. New records take the
    /// next numbers of the file, named by <paramref name="idOf"/>.
    /// </summary>
    public static void Replace(SaveFile file, FcsRecord inventory, IReadOnlyList<SaveItem> items, Func<int, string> idOf)
    {
        var old = Reachable(file, inventory);
        var oldRecords = file.Records.Where(r => old.Contains(r.StringId)).ToDictionary(r => r.StringId, StringComparer.Ordinal);
        var reused = new Dictionary<string, FcsRecord>(StringComparer.Ordinal);
        var added = new List<FcsRecord>();
        int next = SaveBuilder.NextNumber(file);

        FcsRecord Take(FcsRecord fresh, string? oldId)
        {
            if (oldId is not null && oldRecords.TryGetValue(oldId, out var was) && !reused.ContainsKey(oldId))
            {
                (fresh.Id, fresh.StringId, fresh.Name, fresh.Flags) = (was.Id, was.StringId, was.Name, was.Flags);
                reused[oldId] = fresh;
            }
            else
            {
                fresh.Id = next;
                fresh.StringId = idOf(next);
                file.Data.NextId = Math.Max(file.Data.NextId, next++);
                added.Add(fresh);
            }
            return fresh;
        }

        void Fill(FcsRecord state, IReadOnlyList<SaveItem> list, int depth)
        {
            state.Instances.Clear();
            int index = 0;
            foreach (var item in list)
            {
                var r = Take(ItemRecord(item), item.Source?.StringId);
                state.Instances.Add(Link(++index, r.StringId));
                if (item.Contents is { } contents && depth < 4)
                {
                    string? oldInner = item.Source is { Instances.Count: > 0 } s ? s.Instances[0].Target : null;
                    var inner = Take(new FcsRecord { Type = (int)FcsRecordType.INVENTORY_STATE, Name = "0" }, oldInner);
                    r.Instances.Add(Link(1, inner.StringId));
                    Fill(inner, contents, depth + 1);
                }
            }
        }

        Fill(inventory, items, 0);
        // Reused records take the place of the ones they replace; the rest of the old ones leave; new ones go at the end.
        for (int i = file.Records.Count - 1; i >= 0; i--)
        {
            string id = file.Records[i].StringId;
            if (!old.Contains(id)) continue;
            if (reused.TryGetValue(id, out var replacement)) file.Records[i] = replacement;
            else file.Records.RemoveAt(i);
        }
        file.Records.AddRange(added);
    }
}
