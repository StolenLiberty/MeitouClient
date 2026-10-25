using System.Numerics;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Simulation.Bodies;

namespace Meitou.Simulation;

// The cheat menu's commands (Meitou only; the menu itself is Meitou.Game/CheatMenu*.cs). They are debug commands, so they go through
// DebugCheats.Apply between ticks, also while paused (World.ApplyPausedInterfaceCommands), like the debug menu's.

/// <summary>Sets one body part's flesh, stun and/or bandage (null: unchanged), each clamped to the part's range.</summary>
public sealed record CheatSetPart(CharacterId Character, int Part, float? Flesh = null, float? Stun = null, float? Bandage = null) : DebugCommand;

/// <summary>Heals one body part: flesh to its maximum, stun, bandage, splint and wear cleared. A lost limb is not regrown (<see cref="CheatRegrowLimbs"/>).</summary>
public sealed record CheatHealPart(CharacterId Character, int Part) : DebugCommand;

/// <summary>What <see cref="CheatBody"/> does to every part of the characters.</summary>
public enum CheatBodyAction { ClearStun, BandageAll, KnockOut }

/// <summary>One action on every body part of the characters.</summary>
public sealed record CheatBody(IReadOnlyList<CharacterId> Characters, CheatBodyAction Action) : DebugCommand;

/// <summary>Regrows lost limbs: stumps and crushed limbs become whole again at full health; with <see cref="Prosthetics"/> fitted prosthetics are taken off into the inventory and the limb regrown too.</summary>
public sealed record CheatRegrowLimbs(IReadOnlyList<CharacterId> Characters, bool Prosthetics = false, int Part = -1) : DebugCommand;

/// <summary>Sets the blood (clamped to 0 .. the character's capacity) and/or the hunger (0 .. <see cref="MedicalState.MaxHunger"/>); null: unchanged.</summary>
public sealed record CheatSetVitals(IReadOnlyList<CharacterId> Characters, float? Blood = null, float? Hunger = null) : DebugCommand;

/// <summary>Sets one stat (<see cref="Stat"/>) or, with <see cref="StatsEnumerated.None"/>, every stat the menu lists, to <see cref="Value"/> (0 to 100).</summary>
public sealed record CheatSetStat(IReadOnlyList<CharacterId> Characters, StatsEnumerated Stat, float Value) : DebugCommand;

/// <summary>Renames a character (trimmed; at most 64 characters; empty is refused).</summary>
public sealed record CheatRename(CharacterId Character, string Name) : DebugCommand;

/// <summary>Sets the player's money to <see cref="Amount"/> (at least 0).</summary>
public sealed record CheatSetMoney(long Amount) : DebugCommand;

/// <summary>Sets faction <see cref="A"/>'s relation towards <see cref="B"/> (and, <see cref="BothWays"/>, B's towards A) to <see cref="Value"/>, -100 to 100; a value above hostile ends a war.</summary>
public sealed record CheatSetRelation(int A, int B, float Value, bool BothWays = true) : DebugCommand;

/// <summary>Marks every town on the map discovered and explored.</summary>
public sealed record CheatRevealMap : DebugCommand;

/// <summary>Has a character of another faction join the player's squad, as recruiting dialogue does (<see cref="Dialogue.DialogueSystem.JoinPlayer"/>).</summary>
public sealed record CheatRecruit(CharacterId Character) : DebugCommand;

/// <summary>
/// Gives <see cref="Quantity"/> of item <see cref="Record"/>. A weapon or crossbow takes the manufacturer <see cref="Company"/> and its model <see cref="Model"/> (empty: the player's own
/// weapon grade for <see cref="Quality"/>, as <see cref="DebugGiveItem"/>); other graded items take <see cref="Quality"/> (0 to 100). <see cref="AtFeet"/> drops it on the ground instead.
/// </summary>
public sealed record CheatGiveItem(CharacterId Character, string Record, int Quantity, int Quality = 0, string Company = "", string Model = "", bool AtFeet = false) : DebugCommand;

static class CheatApply
{
    public static bool Apply(World world, PopulationData? data, CharacterAssembly? assembly, DebugCommand command)
    {
        var table = world.Characters;
        switch (command)
        {
            case CheatSetPart set when data is not null:
                if (Part(world, data, set.Character, set.Part) is not var (medical, part, _) || medical.Dead) break;
                float max = MathF.Max(part.MaxHp, 0);
                if (set.Flesh is { } flesh) part.Flesh = Math.Clamp(flesh, -max, max);
                if (set.Stun is { } stun) part.Stun = Math.Clamp(stun, 0, max);
                if (set.Bandage is { } bandage) part.Bandage = Math.Clamp(bandage, 0, MathF.Max(0, max - part.Flesh));
                part.WasDown = part.IsDown;
                break;
            case CheatHealPart heal when data is not null:
                if (Part(world, data, heal.Character, heal.Part) is not var (hm, hp, _) || hm.Dead) break;
                hp.Wear = hp.Stun = hp.Bandage = hp.Rig = 0;
                hp.Flesh = hp.MaxHp;
                hp.HitMult = 1;
                hp.WasDown = false;
                break;
            case CheatBody body when data is not null:
                foreach (var id in body.Characters)
                {
                    if (Body(world, data, id) is not var (bm, bctx) || bm.Dead) continue;
                    if (body.Action == CheatBodyAction.KnockOut)
                    {
                        if (!table.TryResolveNext(id, out int slot)) continue;
                        bm.CheatKnockOut(bctx);
                        MovementSystem.Stop(table, slot);
                        table.Next[slot].Velocity = Vector3.Zero;
                        continue;
                    }
                    foreach (var bp in bm.Parts)
                    {
                        if (body.Action == CheatBodyAction.ClearStun) bp.Stun = 0;
                        else bp.Bandage = MathF.Max(bp.Bandage, MathF.Max(0, bp.MaxHp - bp.Flesh));
                        bp.WasDown = bp.IsDown;
                    }
                }
                break;
            case CheatRegrowLimbs regrow:
                foreach (var id in regrow.Characters)
                {
                    if (!table.TryResolveNext(id, out int slot) || table.Cold(slot) is not { Medical: { } rm } cold || rm.Dead) continue;
                    bool changed = false;
                    for (int i = 0; i < rm.Parts.Count; i++)
                    {
                        var rp = rm.Parts[i];
                        if (regrow.Part >= 0 && i != regrow.Part) continue;
                        if (rp.Limb == LimbState.Replaced)
                        {
                            if (!regrow.Prosthetics) continue;
                            if (rm.RemoveReplacement(rp.Template.LimbSlot) is { } item && (cold.Inventory is not { } inventory || !inventory.Add(item)))
                                world.GroundItems.Drop(item, table.Next[slot].Position, 0);
                        }
                        if (rp.Limb == LimbState.Original) continue;
                        rp.Limb = LimbState.Original;
                        rp.Replacement = null;
                        rp.ReplacementItem = null;
                        rp.ReplacementQuality = 0;
                        rp.Wear = rp.Stun = rp.Bandage = rp.Rig = 0;
                        rp.Flesh = rp.MaxHp;
                        rp.WasDown = false;
                        changed = true;
                    }
                    if (changed) assembly?.RefreshEquipment(cold);
                }
                break;
            case CheatSetVitals vitals when data is not null:
                foreach (var id in vitals.Characters)
                {
                    if (Body(world, data, id) is not var (vm, vctx) || vm.Dead) continue;
                    if (vitals.Blood is { } blood) vm.Blood = Math.Clamp(blood, 0, vm.Capacity(vctx));
                    if (vitals.Hunger is { } hunger) vm.Hunger = Math.Clamp(hunger, 0, MedicalState.MaxHunger);
                }
                break;
            case CheatSetStat stat:
                foreach (var id in stat.Characters)
                {
                    if (!table.TryResolveNext(id, out int slot) || table.Cold(slot)?.Stats is not { } stats) continue;
                    float v = Math.Clamp(stat.Value, 0, 100);
                    if (stat.Stat != StatsEnumerated.None) stats[stat.Stat] = v;
                    else foreach (var s in MenuStats) stats[s] = v;
                }
                break;
            case CheatRename rename:
                string name = rename.Name.Trim();
                if (name.Length == 0 || !table.TryResolveNext(rename.Character, out int named) || table.Cold(named) is not { } cn) break;
                cn.Name = name.Length > 64 ? name[..64] : name;
                break;
            case CheatSetMoney money:
                world.Player.Money = Math.Max(0, money.Amount);
                break;
            case CheatSetRelation relation when data is not null:
                SetRelation(world, data.Relations, relation.A, relation.B, relation.Value);
                if (relation.BothWays) SetRelation(world, data.Relations, relation.B, relation.A, relation.Value);
                break;
            case CheatRevealMap when data is not null:
                int known = 0;
                foreach (var site in data.Sites)
                    if (world.Player.Know(site.Index, TownKnowledge.Discovered | TownKnowledge.Explored)) known++;
                world.Post(known == 0 ? "The whole map is already known." : $"{known} location(s) added to the map.");
                break;
            case CheatRecruit recruit when data is not null:
                if (!table.TryResolveNext(recruit.Character, out int joining) || table.Cold(joining) is not { IsPlayer: false } jc || jc.DiedTick >= 0) break;
                if (Dialogue.DialogueSystem.PlayerHeads(world) >= data.Bodies.Constants.MaxFactionSize) { world.Post("It looks like your squad is a little full."); break; }
                var talker = world.Player.Selection.Select(s => table.TryResolveNext(s, out int t) ? table.Cold(t) : null).FirstOrDefault(c => c is { IsPlayer: true });
                if (Dialogue.DialogueSystem.JoinPlayer(world, data, joining, talker)) world.Post($"{jc.Name} joined your squad.");
                break;
            case CheatGiveItem give when data is not null:
                Give(world, data, assembly, give);
                break;
            default:
                return false;
        }
        return true;
    }

    /// <summary>The 32 stats the menu lists (the original's character screen without mass combat and friendly fire).</summary>
    public static readonly StatsEnumerated[] MenuStats =
    [
        StatsEnumerated.Strength, StatsEnumerated.Toughness, StatsEnumerated.Dexterity, StatsEnumerated.Perception,
        StatsEnumerated.MeleeAttack, StatsEnumerated.MeleeDefence, StatsEnumerated.Dodge, StatsEnumerated.MartialArts,
        StatsEnumerated.Katanas, StatsEnumerated.Sabres, StatsEnumerated.Hackers, StatsEnumerated.HeavyWeapons, StatsEnumerated.Blunt, StatsEnumerated.Polearms,
        StatsEnumerated.Turrets, StatsEnumerated.Crossbows,
        StatsEnumerated.Stealth, StatsEnumerated.Lockpicking, StatsEnumerated.Thieving, StatsEnumerated.Assassination,
        StatsEnumerated.Athletics, StatsEnumerated.Swimming,
        StatsEnumerated.Medic, StatsEnumerated.Engineering, StatsEnumerated.Robotics, StatsEnumerated.Science,
        StatsEnumerated.SmithingWeapon, StatsEnumerated.SmithingArmour, StatsEnumerated.SmithingBow,
        StatsEnumerated.Labouring, StatsEnumerated.Farming, StatsEnumerated.Cooking,
    ];

    static void SetRelation(World world, FactionRelations relations, int a, int b, float value)
    {
        if (a == b || !relations.Contains(a) || !relations.Contains(b)) return;
        value = Math.Clamp(value, -100, 100);
        if (value > FactionRelations.HostileThreshold) PlayerRelations.EndWar(world, relations, a, b);
        float now = relations.Get(a, b);
        if (now != value) PlayerRelations.Add(world, relations, a, b, value - now);
    }

    static void Give(World world, PopulationData data, CharacterAssembly? assembly, CheatGiveItem give)
    {
        var table = world.Characters;
        if (!table.TryResolveNext(give.Character, out int owner) || table.Cold(owner) is not { } cold) return;
        if (data.Items.Create(give.Record, Math.Clamp(give.Quantity, 1, 1000)) is not { } item) { world.Post($"No item record {give.Record}."); return; }
        int quality = Math.Clamp(give.Quality, 0, 100);
        if (item.Type is FcsRecordType.WEAPON or FcsRecordType.CROSSBOW && give.Company.Length > 0 && data.Db.Find(give.Company) is { } company)
        {
            var models = company.GetReferences("weapon models").Select(r => (Record: data.Db.Find(r.TargetStringId), Level: r.Values.Value0)).Where(m => m.Record is not null).ToList();
            var model = models.FirstOrDefault(m => m.Record!.StringId == give.Model);
            if (model.Record is null) model = models.Where(m => m.Level <= quality).OrderByDescending(m => m.Level).FirstOrDefault();
            if (model.Record is null && models.Count > 0) model = models.OrderBy(m => m.Level).First();
            item.CompanyId = company.StringId;
            if (model.Record is not null) (item.MaterialId, item.Level) = (model.Record.StringId, model.Level);
        }
        else if (item.Type == FcsRecordType.ARMOUR) (item.Quality, item.Level) = (quality, quality);
        else if (item.Type == FcsRecordType.WEAPON && DebugGiveItem.Grade(data.Db, quality) is { } grade)
            (item.CompanyId, item.MaterialId, item.Level) = (Data.Gameplay.Construction.CraftBench.PlayerWeapons, grade.Model, grade.Level);
        else if (DebugGiveItem.QualityTypes.Contains(item.Type)) item.Level = quality;
        if (give.AtFeet || cold.Inventory is not { } inventory)
        {
            world.GroundItems.Drop(item, table.Next[owner].Position, 0);
            return;
        }
        if (Items.InventoryCommands.TryEquip(inventory, item.WithQuantity(1)))
        {
            assembly?.RefreshEquipment(cold);
            if (item.Quantity == 1) return;
            item = item.WithQuantity(item.Quantity - 1);
        }
        if (inventory.Add(item)) return;
        world.GroundItems.Drop(item, table.Next[owner].Position, 0);
        world.Post($"{item.Quantity} x {item.Name} did not fit; dropped on the ground.");
    }

    static (MedicalState, MedicalContext)? Body(World world, PopulationData data, CharacterId id) => DebugCheats.BodyOf(world, data, id);

    static (MedicalState Medical, HealthPart Part, MedicalContext Context)? Part(World world, PopulationData data, CharacterId id, int part) =>
        Body(world, data, id) is var (medical, ctx) && part >= 0 && part < medical.Parts.Count ? (medical, medical.Parts[part], ctx) : null;
}
