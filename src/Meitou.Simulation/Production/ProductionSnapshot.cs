using Meitou.Simulation.Items;
using Meitou.Data.Gameplay.Construction;

namespace Meitou.Simulation.Production;

/// <summary>A recipe slot as the host shows it: the item, how many are held of how many, and the level (input) or progress (output) 0..1.</summary>
public readonly record struct MachineSlotView(string Item, string Name, int Count, int Capacity, float Fill, int Width = 1, int Height = 1);

/// <summary>A farm as the host shows it (docs/simulation.md "Production").</summary>
public readonly record struct FarmView(float Growth, float Death, int Harvested, int Plants, FarmStatus Status, float Fertility, float CropMatch, float Ripeness)
{
    /// <summary>Each plant's growth (a copy; -10 harvested): what its crop mesh is drawn at.</summary>
    public IReadOnlyList<float> PlantGrowth { get; init; } = [];
}

/// <summary>
/// The resource richness of the building the HUD shows (docs/game/ui-screens.md "Selected building"): a mine's or rock's <c>world resource mining</c> and its yield, for the stats
/// panel's first line. Display only.
/// </summary>
public readonly record struct RichnessView(string Instance, int Resource, float Yield);

/// <summary>A crafting bench as the host shows it: the queue (the first is being made), the repeat toggle and the finished items waiting in its output.</summary>
public sealed record CraftView(IReadOnlyList<(string Key, string Name, float Progress, float Hours)> Queue, bool Repeat, IReadOnlyList<InventoryItemView> Done);

/// <summary>Something a bench can craft, with its hours and inputs per item, and whether research and the bench level allow it now.</summary>
public sealed record CraftOptionView(string Key, string Name, CraftKind Kind, int Level, float Hours, IReadOnlyList<(string Name, float PerItem)> Inputs, bool Unlocked);

/// <summary>A machine's power as the host shows it: a consumer's demand and grant, a generator's output and load, a battery's flow and store (docs/game/buildings-production.md "Power").</summary>
public enum PowerKind : byte { Consumer, Generator, Battery }

public readonly record struct PowerView(PowerKind Kind, float Demand, float Granted, float Usage, float Charge, float Store, bool Fueled);

/// <summary>What the host tells a machine's sound emitter (docs/audio.md "Point emitters"): the efficiency and whether it is powered, as the exe's <c>FUN_140258c20</c> takes them.</summary>
public readonly record struct MachineSound(float Efficiency, bool Powered);

/// <summary>
/// A wind generator as its panel shows it (docs/game/buildings-production.md "Wind generators"): its blades' wind share, the wind speed at the site (units/s), the blades' operating range
/// (null without a ROTATION_WIND_SPEED part), its rated output (data + research) and whether it is switched on.
/// </summary>
public readonly record struct WindView(float Share, float Speed, (float Min, float Max)? Range, float Rated, bool PowerOn);

/// <summary>A machine of a complete player building after a tick (docs/simulation.md "Production"); <see cref="Site"/> is the building's instance id.</summary>
public sealed record MachineSnapshot(string Site, string Record, IReadOnlyList<MachineSlotView> Inputs, MachineSlotView? Output, InventoryView? Storage, FarmView? Farm, byte Status, int Operators, int MaxOperators)
{
    /// <summary>A container of a building's interior, open (<see cref="PlayerState.OpenContainer"/>): <see cref="Site"/> is its fixture id; no research, recipe or power.</summary>
    public bool IsContainer { get; init; }
    /// <summary>The research bench's tech level (0 for other machines).</summary>
    public int TechLevel { get; init; }
    /// <summary>Characters can work it (a well, farm or research bench).</summary>
    public bool Operable { get; init; }
    /// <summary>An automatic machine: works unattended; its menu order is Auto-Haul (152) instead of Use.</summary>
    public bool IsAutomatic { get; init; }
    /// <summary>A storage (a "Hauling to" job can name it).</summary>
    public bool IsStorage { get; init; }
    /// <summary>Its BUILDING_FUNCTIONALITY <c>function</c> (-1: none; 12 a turret, 14 a furnace).</summary>
    public int Function { get; init; } = -1;
    /// <summary>The one item a storage is limited to (its heading in the window), else null.</summary>
    public string? StorageLimitName { get; init; }
    /// <summary>A limited storage's <c>Max Capacity</c>: the building's <c>stackable bonus mult</c> (0 when it has none).</summary>
    public int StorageCapacity { get; init; }
    /// <summary>The crafting bench state (null for other machines).</summary>
    public CraftView? Craft { get; init; }
    /// <summary>Its power (null for a building outside the grid).</summary>
    public PowerView? Power { get; init; }
    /// <summary>What its sound emitter is told each frame (docs/audio.md "Point emitters").</summary>
    public MachineSound Sound { get; init; }
    /// <summary>What its ROTATION_OUTPUT parts turn by (<see cref="Machine.Rotation"/>).</summary>
    public float Rotation { get; init; }
    /// <summary>A wind generator's wind (null for other machines).</summary>
    public WindView? Wind { get; init; }

    internal static MachineSnapshot Of(Machine m, ItemFactory items)
    {
        string Name(string record) => items.Info(record)?.Name ?? record;
        var inputs = m.Inputs.Select(i => new MachineSlotView(i.Item, Name(i.Item), i.Count, i.Recipe.Capacity, Math.Clamp(i.Level, 0, 1), items.Info(i.Item)?.Width ?? 1, items.Info(i.Item)?.Height ?? 1)).ToArray();
        MachineSlotView? output = m.Output is { } o ? new MachineSlotView(o.Recipe.Item, Name(o.Recipe.Item), o.Count, o.Recipe.Capacity, Math.Clamp(o.Progress, 0, 1), items.Info(o.Recipe.Item)?.Width ?? 1, items.Info(o.Recipe.Item)?.Height ?? 1) : null;
        InventoryView? storage = m.Storage is { } s ? new InventoryView(s.Width, s.Height, 0, 0,
            Array.AsReadOnly(s.Items.Select((item, index) => new InventoryItemView(index, item.Record, item.Name, "main", item.X, item.Y, item.Width, item.Height, item.Quantity, item.Weight, item.Quality)
                { Material = item.MaterialId, Rotated = item.Rotated, Charge = item.ChargeFraction }).ToArray())) : null;
        FarmView? farm = m.Farm is { } f && m.Data.Farm is { } fd
            ? new FarmView(f.Growth, f.Death, f.Harvested, f.Plants.Length, f.Status, m.Yield, m.CropMatch, ProductionSystem.Ripeness(f)) { PlantGrowth = [.. f.Plants] } : null;
        // The classes that update their emitter every frame tell it the efficiency and the power; the others powered it once, when built, with no operators (efficiency 0).
        var sound = m.Data.PowersEmitterEachFrame ? new MachineSound(m.Efficiency, m.HasPower && m.Efficiency > 0) : new MachineSound(0, true);
        return new MachineSnapshot(m.Site, m.Data.Id, inputs, output, storage, farm, m.Status, m.Operators, m.Data.MaxOperators) { Sound = sound, Rotation = m.Rotation, Wind = m.Data.IsWind ? new WindView(m.Wind, m.WindSpeed, m.Data.WindRotor, m.WindRated, m.PowerOn) : null, TechLevel = m.Data.IsResearch ? m.Data.TechLevel : 0, Operable = m.Data.Operable, IsAutomatic = m.Data.IsAutomatic, IsStorage = m.Data.IsStorage, Function = m.Data.Function, StorageLimitName = m.Data.StorageLimit is [var only] ? Name(only) : null, StorageCapacity = m.Data.StorageLimit is [_] ? (int)m.Data.StackBonusMult : 0, Craft = Crafting(m, items),
            Power = m.Data.IsConsumer || m.Data.IsGenerator || m.Data.IsBattery ? new PowerView(m.Data.IsBattery ? PowerKind.Battery : m.Data.IsGenerator ? PowerKind.Generator : PowerKind.Consumer, m.Demand, m.Granted, m.Usage, m.Charge, m.Store, m.Inputs.Length == 0 || Array.Exists(m.Inputs, i => i.Level > 0 || i.Count > 0)) : null };
    }

    static CraftView? Crafting(Machine m, ItemFactory items) => m.Craft is { } c
        ? new CraftView([.. c.Queue.Select(j => (j.Option.Key, j.Recipe.Name, Math.Clamp(j.Progress, 0, 1), j.Recipe.Hours))], c.Repeat,
            [.. c.Done.Select((item, index) => new InventoryItemView(index, item.Record, item.Name, "out", 0, 0, item.Width, item.Height, item.Quantity, item.Weight, item.Quality) { Material = item.MaterialId })])
        : null;
}

/// <summary>A research the player may queue, with its cost and whether the benches can pay it and the bench level allows it.</summary>
public readonly record struct ResearchOption(string Id, string Name, string Category, int Level, float Hours, bool Affordable, bool BenchTooLow, float Progress)
{
    public string Description { get; init; } = "";
    /// <summary>The items paid at queue time (empty once paid).</summary>
    public IReadOnlyList<(string Name, int Count)> Cost { get; init; } = [];
}

/// <summary>The research state as the host shows it (docs/simulation.md "Production", Research).</summary>
public sealed record ResearchSnapshot(int TechLevel, int BenchLevel, IReadOnlyList<(string Id, string Name, float Progress)> Queue, float Rate, IReadOnlyList<ResearchOption> Available)
{
    /// <summary>How many researches have been finished (counting repeats): the host plays the notification when it grows.</summary>
    public int Finished { get; init; }
    public static ResearchSnapshot Empty { get; } = new(1, 0, [], 0, []);
}

/// <summary>
/// A town grid's totals after a tick (docs/game/buildings-production.md "Power"): the generators' output and the consumers' demand (the original's Town +0x474, +0x47c), what charged the
/// batteries and what was drawn from them (+0x488, +0x484), and the batteries' charge and capacity (+0x48c, +0x490).
/// </summary>
public readonly record struct TownPower(float Generated, float Demand, float Charging, float Draining, float Charge, float Capacity);

/// <summary>A player outpost as the town panel shows it (docs/game/ui-screens.md "Town panel").</summary>
public sealed record OutpostSnapshot(int Number, string Name, System.Numerics.Vector3 Position, float Radius, int Owner, bool Public, TownPower Power);
