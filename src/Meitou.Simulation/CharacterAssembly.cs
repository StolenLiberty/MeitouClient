using Meitou.Data;
using Meitou.Data.Characters;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Data.Gameplay.Combat;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Combat;
using Meitou.Simulation.Items;

namespace Meitou.Simulation;

/// <summary>
/// The spawn-time assembly of a character: from its CHARACTER record and race it gets <c>Race</c>, <c>Stats</c>, <c>Medical</c>, <c>Inventory</c>,
/// <c>Fighter</c>, <c>WaterFactor</c> and the top speed of the speed chain (docs/simulation.md "Bodies", "Combat"). The population
/// system uses the instance; the race lookups are static so the save code reads the same record the same way.
/// </summary>
sealed class CharacterAssembly(PopulationData data)
{
    /// <summary>The id of the race a CHARACTER record names: its first <c>race</c> reference with a positive count; null when it has none.</summary>
    public static string? RaceId(GameRecord record)
    {
        foreach (var r in record.GetReferences("race"))
            if (r.Values.Value0 > 0) return r.TargetStringId;
        return null;
    }

    /// <summary>The RACE record of a character: the one its generated look was made with, else the one its record names.</summary>
    public static GameRecord? RaceRecord(GameDatabase db, string recordId, CharacterAppearance? look)
    {
        var race = look?.Race;
        if (race is null && db.Find(recordId) is { } rec && RaceId(rec) is { } id) race = db.Find(id);
        return race;
    }

    /// <summary>The <see cref="RaceData"/> of a RACE record id, read once per id into <paramref name="cache"/> (null for an id that is not a race).</summary>
    public static RaceData? RaceDataOf(GameDatabase db, string raceId, Dictionary<string, RaceData?> cache)
    {
        if (cache.TryGetValue(raceId, out var cached)) return cached;
        var race = db.Find(raceId) is { Type: FcsRecordType.RACE } rec ? RaceData.From(rec, db) : null;
        cache[raceId] = race;
        return race;
    }

    /// <summary>The race record of a spawned character (see <see cref="RaceRecord"/>).</summary>
    public GameRecord? RaceOf(string recordId, CharacterAppearance? look) => RaceRecord(data.Db, recordId, look);

    /// <summary>
    /// The top speed a character starts with: the 45 of a creature without a race. A character with a race gets its real top speed from <see cref="Attach"/>
    /// (docs/game/pathfinding.md "Speed"), so the lerp over the race's speed skills that stood in for it before stats existed is gone.
    /// </summary>
    public const float StandInMaxSpeed = 45;

    /// <summary>The walk speed of a character: the race's <c>walk speed</c>, 15 without a race.</summary>
    public static float WalkSpeed(GameRecord? race) => race?.GetFloat("walk speed", 15) ?? 15;

    /// <summary>
    /// The weapons (the first of the hip and of the back section; quality the level x 0.01; which is in hand goes by the arms, <see cref="Fighter.Weapon"/>) and the worn armour
    /// (quality / 100), docs/simulation.md "Combat".
    /// </summary>
    Fighter MakeFighter(Inventory inventory, MedicalState? medical)
    {
        var combat = data.Combat;
        WeaponInstance? First(string section)
        {
            foreach (var item in inventory.Items.Where(i => i.Section == section && i.Type == FcsRecordType.WEAPON))
                if (combat.Weapon(item.Record) is { } w)
                    return WeaponInstance.Create(item.Level * 0.01f, w, combat.Manufacturer(item.CompanyId), combat.WeaponMaterial(item.MaterialId), combat.Constants);
            return null;
        }
        var armour = inventory.Items.Where(i => i.Type == FcsRecordType.ARMOUR && Items.InventoryCommands.IsEquipmentSection(i.Section) && combat.Armour(i.Record) is not null)
            .Select(i => ArmourPiece.Create(combat.Armour(i.Record)!, i.Quality * 0.01f));
        // The crossbow: the first CROSSBOW in the weapon sections (back, then hip), its quality the level x 0.01 like a weapon's.
        CrossbowInstance? crossbow = null;
        foreach (var section in new[] { "back", "hip" })
            foreach (var item in inventory.Items.Where(i => i.Section == section && i.Type == FcsRecordType.CROSSBOW))
                if (crossbow is null && combat.Crossbow(item.Record) is { } x)
                    crossbow = CrossbowInstance.Create(item.Level * 0.01f, x, combat.Constants, data.BodyOptions.GlobalDamageMultiplier);
        float? backpackStealth = inventory.Backpack is { } pack ? data.Db.Find(pack.Record)?.GetFloat("stealth mult", 1) ?? 1 : null;
        var fighter = new Fighter(First("hip"), armour, backpackStealth) { BackWeapon = First("back"), Crossbow = crossbow };
        if (medical is not null) fighter.SetArms(medical.ArmWorks(left: false), medical.ArmWorks(left: true));
        return fighter;
    }

    /// <summary>
    /// An animal's fighter (docs/game/animals.md): its natural weapon, the ANIMAL_CHARACTER's <c>weapon</c> (a WEAPON of a creature category) made by its <c>weapon level</c>
    /// manufacturer; no armour. The weapon's level is the manufacturer's first model's level, with that model (an <b>engine choice</b>: the original makes the item without a
    /// level argument, and its default was not traced, <b>Unknown</b>). No weapon (or one not in the data) gives a fighter without an attack.
    /// </summary>
    Fighter MakeAnimalFighter(AnimalData animal)
    {
        var combat = data.Combat;
        WeaponInstance? weapon = null;
        if (animal.WeaponId is { } wid && combat.Weapon(wid) is { } w)
        {
            var maker = animal.WeaponLevelId is { } mid ? combat.Manufacturer(mid) : null;
            var model = maker is { Models.Count: > 0 } ? maker.Models[0] : (WeaponModelEntry?)null;
            weapon = WeaponInstance.Create((model?.Level ?? 0) * 0.01f, w, maker, model is { } m ? combat.WeaponMaterial(m.ModelStringId) : null, combat.Constants);
        }
        return new Fighter(weapon) { IsAnimal = true };
    }

    Fighter FighterOf(GameRecord? record, Inventory inventory, MedicalState? medical) => AnimalData.From(record) is { } animal ? MakeAnimalFighter(animal) : MakeFighter(inventory, medical);

    // ---- Loot All's worth of gear (docs/simulation.md "Carrying, rescue and looting"; engine choices: the original has no such order) ----

    /// <summary>A melee weapon's worth to <paramref name="who"/>: (cut + blunt multiplier) × (0.5 + its skill in the weapon's class / 100), plus 0.02 per attack and defence mod; null: not a known weapon.</summary>
    public float? MeleeWorth(ItemInstance item, CharacterCold who)
    {
        if (item.Type != FcsRecordType.WEAPON || data.Combat.Weapon(item.Record) is not { } w) return null;
        var weapon = WeaponInstance.Create(item.Level * 0.01f, w, data.Combat.Manufacturer(item.CompanyId), data.Combat.WeaponMaterial(item.MaterialId), data.Combat.Constants);
        float skill = who.Stats?[weapon.Skill] ?? 0;
        return (weapon.Stats.CutMultiplier + weapon.Stats.BluntMultiplier) * (0.5f + skill / 100f) + (weapon.Stats.AttackMod + weapon.Stats.DefenceMod) * 0.02f;
    }

    /// <summary>A crossbow's worth: mean pierce damage × shots ÷ (mean reload seconds + 1), plus range / 500; null: not a known crossbow.</summary>
    public float? CrossbowWorth(ItemInstance item)
    {
        if (item.Type != FcsRecordType.CROSSBOW || data.Combat.Crossbow(item.Record) is not { } x) return null;
        var bow = CrossbowInstance.Create(item.Level * 0.01f, x, data.Combat.Constants, data.BodyOptions.GlobalDamageMultiplier);
        var s = bow.Stats;
        return (s.PierceMin + s.PierceMax) * 0.5f * bow.NumShots / ((s.ReloadMin + s.ReloadMax) * 0.5f + 1) + s.Range / 500;
    }

    /// <summary>The ammunition a crossbow item shoots (empty: none needed); null: not a known crossbow.</summary>
    public string? AmmoOf(ItemInstance item) => item.Type == FcsRecordType.CROSSBOW ? data.Combat.Crossbow(item.Record)?.AmmoId : null;

    /// <summary>
    /// An armour piece's worth: (cut + blunt defence at its quality) × its coverage (the parts' percents / 100), plus 0.02 per combat attack and defence bonus, plus the damage output and
    /// combat speed multipliers less 1, plus half the dodge multiplier less 1; null: not a known ARMOUR.
    /// </summary>
    public float? ArmourWorth(ItemInstance item)
    {
        if (item.Type != FcsRecordType.ARMOUR || data.Combat.Armour(item.Record) is not { } a) return null;
        var piece = ArmourPiece.Create(a, item.Quality * 0.01f);
        float cover = a.Coverage.Sum(c => c.Percent) / 100f;
        return (piece.CutDefence + piece.BluntDefence) * cover + (a.CombatAttackBonus + a.CombatDefenceBonus) * 0.02f + (a.DamageOutputMult - 1) + (a.CombatSpeedMult - 1) + 0.5f * (a.DodgeMult - 1);
    }

    public void RefreshEquipment(CharacterCold cold)
    {
        if (cold.Inventory is not { } inventory) return;
        var fighter = FighterOf(data.Db.Find(cold.RecordId), inventory, cold.Medical);
        fighter.Guarding = cold.Fighter?.Guarding ?? false;
        fighter.EncumbranceFactor = cold.Fighter?.EncumbranceFactor ?? 1;
        cold.Fighter = fighter;
        if (cold.Appearance is not { } look || look.Animal is not null) return;
        var body = look.Loadout?.Appearance.Record ?? look.Body?.Record;
        if (Saving.SavedKit.LoadoutOf(body, Saving.SavedKit.FromInventory(inventory), data.Db, look.Loadout?.Seed ?? 0) is { } kit)
        {
            Saving.SavedKit.SetLimbs(kit, cold.Medical, data.Db);
            if (data.Appearances?.Create(cold.RecordId, kit) is { } updated) cold.Appearance = updated;
        }
    }

    /// <summary>
    /// <see cref="Attach"/> for a character loaded from a save: the race, stats and medical state are the saved ones (not rolled), the inventory and the fighter are
    /// the saved <paramref name="inventory"/> when there is one, else built from the character's record and look like a new character's; the fighter from the inventory. The top
    /// speed is the loader's. An animal takes its saved GAMESTATE_CHARACTER <c>age</c> (<paramref name="age"/>; 1 when the save has none) as its stats multiplier and size
    /// (docs/game/animals.md "Ageing").
    /// </summary>
    public void AttachSaved(CharacterCold cold, RaceData race, CharacterStats stats, MedicalState medical, GameRecord? record, Inventory? inventory = null, float age = 1)
    {
        cold.NpcClass = NpcClassFor(cold, record);
        cold.FactionImportance = FactionImportanceOf(record);
        cold.Race = race;
        cold.Stats = stats;
        cold.Medical = medical;
        if (AnimalData.From(record) is { } animal)
        {
            (cold.Animal, cold.ShownAge, stats.Scale) = (animal, age, age);
            medical.SetAnimalBody(animal.Scale(age), animal.HpMult);
        }
        cold.Inventory = inventory ?? data.Items.Build(record, cold.Appearance?.Loadout);
        // Fitted prosthetics show in the limbs window: their items sit in the inventory's limb sections (saved apart, SavedLimbs).
        for (int k = 0; k < 4 && cold.Inventory is { } carried; k++)
            if (medical.LimbPart(k)?.ReplacementItem is { } fitted && !carried.Items.Contains(fitted))
            {
                (fitted.Section, fitted.X, fitted.Y) = (Meitou.Data.Gameplay.Bodies.LimbReplacement.SlotOf(k), 0, 0);
                carried.Items.Add(fitted);
            }
        cold.Fighter = FighterOf(record, cold.Inventory, cold.Medical);
        cold.WaterFactor = race.WaterAvoidance * (cold.Faction == data.PlayerFaction && data.PlayerFaction >= 0 ? 0.5f : 1);
        cold.RoadPreference = data.RoadPreferenceOf(cold.Faction);
    }

    /// <summary>
    /// CHARACTER <c>NPC class</c> (<b>Verified</b>, carry-loot.md 4.3: read by <c>FUN_14062b210</c>, the editor's CharacterTypeEnum, <c>fcs.def</c> default MILITARY); None without a
    /// CHARACTER record (an animal's ANIMAL_CHARACTER, or nothing).
    /// </summary>
    /// <summary>The record's <c>NPC class</c>, except that a player-faction character is an Adventurer (9), as the original sets it when the character is made (<c>FUN_14062b210</c>).</summary>
    FundamentalType NpcClassFor(CharacterCold cold, GameRecord? record) =>
        cold.IsPlayer || (data.PlayerFaction >= 0 && cold.Faction == data.PlayerFaction) ? FundamentalType.Adventurer : NpcClassOf(record);

    /// <summary>
    /// CHARACTER <c>faction importance</c> (<b>Verified</b>: every one of the 639 merged base CHARACTER records has it, 0 to 100, most 1); 0 for any other record (the game's float map
    /// lookup of a missing key), so an animal's faction does not mind.
    /// </summary>
    public static float FactionImportanceOf(GameRecord? record) =>
        record is { Type: Meitou.Data.Fcs.FcsRecordType.CHARACTER } ? record.GetFloat("faction importance", 0) : 0;

    public static FundamentalType NpcClassOf(GameRecord? record) =>
        record is { Type: Meitou.Data.Fcs.FcsRecordType.CHARACTER } ? (FundamentalType)record.GetInt("NPC class", (int)FundamentalType.Military) : FundamentalType.None;

    /// <summary>
    /// Gives a character with a race its stats and medical state, its inventory and fighter, and the top speed of the speed chain (stage 7). An animal (ANIMAL_CHARACTER, whose
    /// record names its race) gets its body at <paramref name="age"/> and its natural weapon (docs/game/animals.md). Anything without a race is left as it is.
    /// </summary>
    public void Attach(CharacterId id, CharacterCold cold, ref CharacterHot hot, GameRecord? record, GameRecord? race, ulong seed, float age = 1)
    {
        cold.NpcClass = NpcClassFor(cold, record);
        cold.FactionImportance = FactionImportanceOf(record);
        if (race is null) return;
        var (raceData, stats, medical) = data.Bodies.Create(race, record, seed, Rng.Key(id), age);
        cold.Race = raceData;
        cold.Stats = stats;
        cold.Medical = medical;
        if (AnimalData.From(record) is { } animal) (cold.Animal, cold.ShownAge) = (animal, age);
        cold.Inventory = data.Items.Build(record, cold.Appearance?.Loadout);
        cold.Fighter = FighterOf(record, cold.Inventory, cold.Medical);
        cold.WaterFactor = raceData.WaterAvoidance * (cold.Faction == data.PlayerFaction && data.PlayerFaction >= 0 ? 0.5f : 1);
        cold.RoadPreference = data.RoadPreferenceOf(cold.Faction);
        hot.MaxSpeed = Speed.Run(raceData, stats, medical, 1);
        // A start without a limb (CHARACTER `starting health`) shows its stump.
        if (Enumerable.Range(0, 4).Any(k => medical.LimbPart(k) is { Limb: not LimbState.Original })) RefreshEquipment(cold);
    }
}
