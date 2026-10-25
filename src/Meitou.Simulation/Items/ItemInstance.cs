using System.Collections.Concurrent;
using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay;

namespace Meitou.Simulation.Items;

/// <summary>
/// One item a character carries (an INVENTORY_ITEM_STATE of a save). The fields keep the save's names (docs/formats/save.md): <c>base data sid</c> = <see cref="Record"/>,
/// <c>material sid</c>, <c>company sid</c>, <c>section</c>, <c>inventory x</c> / <c>inventory y</c>, <c>quantity</c>, <c>level</c>, <c>item function</c>, <c>quality</c>, <c>charges</c>.
/// </summary>
public sealed class ItemInstance
{
    public required string Record { get; init; }
    public string Name { get; init; } = "";
    public FcsRecordType Type { get; init; }
    /// <summary>The armour section allowed by the record's slot; empty for other items.</summary>
    public string EquipmentSection { get; init; } = "";
    /// <summary>ARMOUR <c>is locked</c> (the original makes it a <c>LockedArmour</c>: shackles); worn, it shows the portrait's <c>Slave</c> overlay (docs/game/ui-screens.md "Portrait cells").</summary>
    public bool IsLocked { get; init; }
    /// <summary>The record's race limits (<see cref="ItemInfo.WornOnlyBy"/>, <see cref="ItemInfo.WornNeverBy"/>): who may put it in an equipment section.</summary>
    public string[] WornOnlyBy { get; init; } = [];
    public string[] WornNeverBy { get; init; } = [];
    /// <summary>Whether a character of the RACE <paramref name="race"/> may wear it (null: no wearer, no limit).</summary>
    public bool WearableBy(string? race) => Wearable(WornOnlyBy, WornNeverBy, race);
    internal static bool Wearable(string[] only, string[] never, string? race) => race is null || (only.Length == 0 ? !never.Contains(race) : only.Contains(race));
    /// <summary>Shackles' lock (level = ARMOUR <c>lock level</c> clamped 0..100, its <c>hardness</c>, locked when made: <c>FUN_14089a440</c>); null for other items.</summary>
    public Locks.LockState? Lock { get; set; }
    /// <summary>Worn and effectively locked: the item cannot be taken off, sold or moved (LockedArmour's tooltip: "locked in place and cannot be removed").</summary>
    public bool LockedOn => Lock is { EffectivelyLocked: true } && InventoryCommands.IsEquipmentSection(Section);
    public string MaterialId { get; set; } = "";
    public string CompanyId { get; set; } = "";
    /// <summary>Where it is: <c>main</c>, <c>shirt</c>, <c>head</c>, <c>boots</c>, <c>armour</c>, <c>legs</c>, <c>hip</c>, <c>back</c>, <c>backpack_attach</c> (a worn backpack), <c>backpack_content</c> (inside it).</summary>
    public string Section { get; set; } = "main";
    public int X { get; set; }
    public int Y { get; set; }
    int quantity = 1;
    public int Quantity
    {
        get => quantity;
        set
        {
            if (value < quantity && Buyback is { } receipt) Buyback = receipt.Skip(quantity - value);
            else if (value > quantity) Buyback = null;
            quantity = value;
        }
    }
    TradeReceipt? buyback;
    internal TradeReceipt? Buyback
    {
        get => buyback is { Session.Active: true } ? buyback : null;
        set => buyback = value;
    }
    public int Level { get; set; }
    /// <summary>The name of the smith who crafted the item at a bench (the save key <c>crafter</c>; empty: not crafted by the player's smith). The tooltip prints it with the player faction's name.</summary>
    public string Crafter { get; set; } = "";
    public int ItemFunction { get; set; }
    /// <summary>Quality 0 to 100 (clothing from the character's armour grade).</summary>
    public float Quality { get; set; }
    /// <summary>Charges left; for food the record's charges times CONSTANTS <c>food quality mult</c>, which is what is eaten (100 points are one hunger level).</summary>
    public float Charges { get; set; } = 1;
    /// <summary>The charges of a full unit (the record's, as <see cref="Charges"/> starts); a stack of ammunition refills <see cref="Charges"/> from it when one unit is used up. 0 when not known (an item made elsewhere).</summary>
    public float MaxCharges { get; set; }
    /// <summary>A prosthetic's condition while it is not fitted, the save's <c>dam</c>, <c>wear</c> and <c>stun</c> (<see cref="Bodies.MedicalState.FitReplacement"/>); 0 for other items.</summary>
    public float LimbDamage { get; set; }
    public float LimbWear { get; set; }
    public float LimbStun { get; set; }
    int width = 1, height = 1;
    /// <summary>The footprint in cells, turned a quarter when <see cref="Rotated"/> (the record's width and height swapped).</summary>
    public int Width { get => Rotated ? height : width; init => width = value; }
    public int Height { get => Rotated ? width : height; init => height = value; }
    /// <summary>
    /// Meitou: turned a quarter in its grid (R while held, as the RE_Kenshi plugin KenshiRotate; the original cannot rotate items). Only in grids: an equipment or prosthetic section
    /// takes it upright (<see cref="Upright"/>). Saved as <c>meitou rotated</c>.
    /// </summary>
    public bool Rotated { get; set; }
    /// <summary>Whether turning it changes its footprint (it is not square).</summary>
    public bool Rotatable => width != height;
    /// <summary>Weight of one unit in kg as it counts in the inventory total (weapons already scaled by <c>weapon inventory weight mult</c>).</summary>
    public float UnitWeight { get => DynamicWeight is null ? baseWeight : WorkedWeight(); init => baseWeight = value; }
    float baseWeight;
    /// <summary><see cref="DynamicWeight"/>'s answer for the inputs it was asked with (the inventory total reads it every tick); one object, so a reader on another thread sees a whole one.</summary>
    sealed record WeightMemo(int Level, string Material, string Company, float Weight);
    WeightMemo? weightMemo;
    float WorkedWeight()
    {
        if (weightMemo is { } m && m.Level == Level && m.Material == MaterialId && m.Company == CompanyId) return m.Weight;
        float weight = DynamicWeight!(this);
        weightMemo = new(Level, MaterialId, CompanyId, weight);
        return weight;
    }
    /// <summary>The record weight (<see cref="UnitWeight"/> when nothing replaces it).</summary>
    public float RecordWeight => baseWeight;
    /// <summary>
    /// What replaces <see cref="RecordWeight"/> for items whose weight is worked out from the item: an armour piece's by its level and part coverage
    /// (<see cref="Meitou.Data.Gameplay.Combat.ArmourData.WeightAt"/>), a melee weapon's by its level, manufacturer and material (<c>WeaponStats</c>); null for anything else.
    /// </summary>
    public Func<ItemInstance, float>? DynamicWeight { get; init; }
    /// <summary>How many units one cell holds (<see cref="ItemInfo.StackLimit"/>; 1: it does not stack). Inside a backpack the bag's bonus applies (<see cref="Inventory.LimitOf"/>).</summary>
    public int StackLimit { get; init; } = 1;
    public bool IsFood { get; init; }
    /// <summary>
    /// The faction the item was stolen from (-1: none), the item's "taken from" owner of docs/game/carry-loot.md 4.4 reduced to its faction: set when the player's looting is a theft
    /// (<see cref="Carrying.CarrySystem"/>). The item counts as stolen while its holder is of another faction (<see cref="IsStolenFor"/>).
    /// </summary>
    public int StolenFrom { get; set; } = -1;
    /// <summary>
    /// One of its owner's CHARACTER / ANIMAL_CHARACTER <c>death items</c> (the save's <c>death</c>, docs/game/animals.md "Death items"): a live owner's own food search skips it, taking
    /// it from a live owner that is down butchers the owner, and it stops being one when it leaves the owner's inventory. Its stack does not merge with ordinary units.
    /// </summary>
    public bool Death { get; set; }
    /// <summary>A worn backpack's contents (null for other items).</summary>
    public Inventory? Contents { get; init; }
    /// <summary>The factor of the contents' weight (a bag's <c>encumbrance effect</c>).</summary>
    public float ContentsFactor { get; init; } = 1;
    /// <summary>The INVENTORY_ITEM_STATE a loaded item was read from; saving copies it so the keys the engine does not model survive (null for an item made in play). Not hashed.</summary>
    public FcsRecord? Saved { get; set; }

    /// <summary>A copy of this item with another quantity (a stack split off; never used for a backpack, whose contents are not copied).</summary>
    public ItemInstance WithQuantity(int quantity) => new()
    {
        Record = Record, Name = Name, Type = Type, EquipmentSection = EquipmentSection, IsLocked = IsLocked, WornOnlyBy = WornOnlyBy, WornNeverBy = WornNeverBy, MaterialId = MaterialId, CompanyId = CompanyId, Section = Section, X = X, Y = Y, Quantity = quantity, Level = Level, Crafter = Crafter,
        ItemFunction = ItemFunction, Quality = Quality, Charges = Charges, MaxCharges = MaxCharges, Width = width, Height = height, Rotated = Rotated, UnitWeight = baseWeight, DynamicWeight = DynamicWeight, StackLimit = StackLimit,
        IsFood = IsFood, ContentsFactor = ContentsFactor, StolenFrom = StolenFrom, Death = Death, Buyback = Buyback?.Take(quantity),
    };

    /// <summary>Whether the item is stolen while held by a character of <paramref name="holderFaction"/> (carry-loot.md 4.4: the from-owner is set and of another faction).</summary>
    public bool IsStolenFor(int holderFaction) => StolenFrom >= 0 && StolenFrom != holderFaction;

    /// <summary>The weight this item and what is in it add to the load.</summary>
    public float Weight => UnitWeight * Quantity + (Contents?.ContentWeight() ?? 0) * ContentsFactor;

    /// <summary>
    /// How full the item (a stack's partly used unit) is, 0 to 1, for the inventory cell's charge bar, whose width is charges / maximum (<c>FUN_140711200</c>, <b>Observed</b>); null when the
    /// maximum is 1 or less (no bar: gear, plain items).
    /// </summary>
    public float? ChargeFraction => MaxCharges > 1 ? Math.Clamp(Charges / MaxCharges, 0f, 1f) : null;

    /// <summary>The hunger levels eating the item gives: <see cref="Charges"/> / 100.</summary>
    public float Nutrition => IsFood ? Charges / 100f : 0;
}
