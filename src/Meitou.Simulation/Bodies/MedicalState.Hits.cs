using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Bodies;

namespace Meitou.Simulation.Bodies;

public sealed partial class MedicalState
{
    // ------------------------------------------------------------------ hits

    /// <summary>
    /// The game's F10 debug heal (Meitou only, not the original's): every part whole (no wear, stun, bandage or splint), full blood, no wounds and no KO time left,
    /// so the next body tick wakes the character. A dead character stays dead.
    /// </summary>
    public void Heal(in MedicalContext ctx)
    {
        if (Dead) return;
        foreach (var p in parts)
        {
            p.Wear = p.Stun = p.Bandage = p.Rig = 0;
            p.Flesh = p.MaxHp;
            p.HitMult = 1;
            p.WasDown = false;
        }
        wounds.Clear();
        Blood = Capacity(ctx);
        BloodKo = false;
        KoTimer = 0;
    }

    float NextRoll(in MedicalContext ctx) => BodyRolls.Float(ctx.Seed, ctx.CharacterKey, BodyRolls.MedicalDomain, rollCounter++);

    /// <summary>
    /// Chooses the part a blow lands on (<c>FUN_1406508d0</c>, <b>Observed</b>): among the parts not yet destroyed, weighted by hit weight times <see cref="HealthPart.HitMult"/>
    /// (the weight alone for hit direction 6); a low strike excludes arms and head. Draws one roll from the character's stream. Returns -1 when no part qualifies.
    /// </summary>
    public int ChoosePart(in MedicalContext ctx, bool lowStrike = false, bool directionSix = false)
    {
        float total = 0;
        for (int i = 0; i < Parts.Count; i++) total += Weight(Parts[i], lowStrike, directionSix);
        if (total <= 0) return -1;
        float target = NextRoll(ctx) * total;
        int last = -1;
        for (int i = 0; i < Parts.Count; i++)
        {
            float w = Weight(Parts[i], lowStrike, directionSix);
            if (w <= 0) continue;
            last = i;
            if (target < w) return i;
            target -= w;
        }
        return last;
    }

    static float Weight(HealthPart p, bool lowStrike, bool directionSix)
    {
        if (p.IsDestroyed) return 0;
        if (lowStrike && p.Template.Type is BodyPartType.Arm or BodyPartType.Head) return 0;
        return p.HitWeight * (directionSix ? 1 : p.HitMult);
    }

    /// <summary>
    /// Applies a blow to a part (<c>FUN_14064f300</c>): h falls by cut + pierce + blunt x <c>blunt permanent organ damage</c>; s rises by the rest of the blunt plus the extra
    /// stun; blood falls by (cut + pierce) x <c>immediate blood loss</c> x scale x race bleed rate; robots and prosthetics gain wear; a cut or pierce opens a wound. Then the
    /// part is checked: down (a vital one knocks the character out), destroyed (vital: death; other: severed, subject to the Dismemberment setting and to the owner having a stats object).
    /// The part's <see cref="HealthPart.HitMult"/> is raised by 1 per hit (to 4; held to 2 when its fraction is negative, 0 when destroyed).
    /// </summary>
    public void ApplyHit(int part, in HitDamage hit, in MedicalContext ctx, List<MedicalEvent>? events = null)
    {
        if (Dead) return;
        var c = ctx.Constants;
        var race = ctx.Race;
        var p = Parts[part];
        float perm = c.BluntPermanentOrganDamage;
        p.Flesh -= hit.Cut + hit.Pierce + hit.Blunt * perm;
        p.Stun += (1 - perm) * hit.Blunt + hit.ExtraStun;
        Blood -= (hit.Cut + hit.Pierce) * c.ImmediateBloodLoss * hit.Scale * race.BleedRate;
        if (p.IsRobotic(race) && !ctx.ExemptFromWear)
            p.Wear += (hit.Cut + hit.Pierce + hit.Blunt) * 0.04f * c.RobotWearRate;
        // The wound (FUN_1406508d0): strength max(cut, pierce) x bleed rate x scale, losing `bleeding clot rate` a second (TickBlood).
        // Opened whatever the race's bleed rate; a bloodless race's wounds drain nothing but still need first aid.
        if (MathF.Max(hit.Cut, hit.Pierce) > 0) wounds.Add(MathF.Max(hit.Cut, hit.Pierce) * c.BleedRate * hit.Scale);

        p.HitMult = p.IsDestroyed ? 0 : p.Fraction < 0 ? MathF.Min(p.HitMult + 1, 2) : MathF.Min(p.HitMult + 1, 4);

        CheckPart(part, ctx, events, inHitPath: true);
        CheckBlood(ctx, events);
    }

    void CheckPart(int index, in MedicalContext ctx, List<MedicalEvent>? events, bool inHitPath)
    {
        if (Dead) return;
        var p = Parts[index];
        bool down = p.IsDown;
        if (down && !p.WasDown) events?.Add(new MedicalEvent(MedicalEventKind.PartDown, index));
        // Every blow that leaves a vital part under 0 restarts the KO timer (FUN_14064f300); a part going under 0 in the tick does not (the KO point rule does, VitalPartsKeepDown).
        if (down && inHitPath && p.IsVital) RestartKo(KoTime(ctx), events);
        p.WasDown = down;
        bool destroyed = inHitPath ? p.IsDestroyed : p.Flesh - p.Stun < -p.MaxHp;
        if (!destroyed) return;
        if (p.IsVital) { Die(DeathCause.VitalPart, events); return; }
        var setting = ctx.Options.Dismemberment;
        bool allowed = inHitPath ? setting >= Dismemberment.Rare && ctx.HasStats && HasStatsObject : setting >= Dismemberment.Frequent;
        // A prosthetic is never cut off (FUN_14064edc0 returns for a replaced slot); a crushed limb can be.
        if (allowed && p.Limb is LimbState.Original or LimbState.Crushed && p.Template.Severance) Sever(index, ctx, events);
    }

    /// <summary>
    /// Cuts a limb off (<c>FUN_14064edc0</c>): the part becomes a stump (base HP 5), the character loses rand(5, 30) blood and gets a wound of strength <c>bleed rate</c> x 200;
    /// a lost leg also knocks the character out a second time. The caller awards the toughness XP (<see cref="XpService.ToughnessFromLimbLoss"/>) and spawns the
    /// severed limb item from the race's <c>severed limbs</c>.
    /// </summary>
    public void Sever(int part, in MedicalContext ctx, List<MedicalEvent>? events = null)
    {
        var p = Parts[part];
        p.Limb = LimbState.Stump;
        p.Replacement = null;
        Blood -= 5 + 25 * NextRoll(ctx);
        wounds.Add(ctx.Constants.BleedRate * 200);
        events?.Add(new MedicalEvent(MedicalEventKind.PartSevered, part));
        if (p.Template.LimbSlot is 2 or 3)
        {
            // The second KO after losing a leg (FUN_140644980): (2 x base + terms) x (1 - toughness / 100) x 4, at least 3, times a random number in [0, 1].
            float t = (2 * ctx.Constants.KnockoutTimeBase + KoTerms(ctx)) * (1 - Math.Clamp(ctx.Toughness / 100, 0, 1)) * 4;
            ApplyKo(MathF.Max(t, 3) * NextRoll(ctx), events);
        }
    }

    /// <summary>
    /// Fits a prosthetic on the part of its slot (<c>FUN_1406445b0</c>): the part's base HP becomes the limb's HP at the item's level, its wear and stun the item's, and its flesh
    /// the maximum less the item's damage (<paramref name="damage"/>, <paramref name="wear"/>, <paramref name="stun"/>: the INVENTORY_ITEM_STATE <c>dam</c>, <c>wear</c>,
    /// <c>stun</c> a removed limb carries). Bandage and splint are cleared (an engine choice).
    /// </summary>
    public bool FitReplacement(LimbReplacement limb, Items.ItemInstance? item, float damage = 0, float wear = 0, float stun = 0)
    {
        if (LimbPart(limb.LimbSlot) is not { } p) return false;
        p.Limb = LimbState.Replaced;
        p.Replacement = limb;
        p.ReplacementItem = item;
        p.ReplacementQuality = Math.Clamp((item?.Level ?? 0) * 0.01f, 0, 1);
        p.Wear = wear;
        p.Stun = stun;
        p.Bandage = 0;
        p.Rig = 0;
        float top = p.MaxHp;
        p.Flesh = MathF.Min(top - damage, top);
        p.WasDown = p.IsDown;
        return true;
    }

    /// <summary>
    /// Takes the prosthetic off limb slot <paramref name="slot"/> (the reverse of <see cref="FitReplacement"/>): its item keeps <c>dam</c> = maximum - flesh, the part's wear and stun;
    /// the part becomes a stump with flesh 5, wear and stun 0. Null when the slot has no prosthetic.
    /// </summary>
    public Items.ItemInstance? RemoveReplacement(int slot)
    {
        if (LimbPart(slot) is not { Limb: LimbState.Replaced } p) return null;
        var item = p.ReplacementItem;
        if (item is not null) (item.LimbDamage, item.LimbWear, item.LimbStun) = (MathF.Max(0, p.MaxHp - p.Flesh), p.Wear, p.Stun);
        p.Limb = LimbState.Stump;
        p.Replacement = null;
        p.ReplacementItem = null;
        p.ReplacementQuality = 0;
        p.Wear = 0;
        p.Stun = 0;
        p.Flesh = 5;
        p.WasDown = p.IsDown;
        return item;
    }

    /// <summary>
    /// One CHARACTER <c>starting health</c> entry on a new body (<c>FUN_140645ef0</c>): below -M a limb becomes a stump (flesh 0, the treated pool 5); otherwise the flesh is
    /// <paramref name="value"/> clamped to [-M, M] and the rest, M - flesh, is already treated. A part the anatomy lacks is ignored.
    /// </summary>
    public void ApplyStartingHealth(string partId, float value)
    {
        if (Parts.FirstOrDefault(p => p.Template.StringId == partId) is not { } p) return;
        float max = p.MaxHp;
        if (value < -max && p.Template.LimbSlot is >= 0 and < 4)
        {
            p.Limb = LimbState.Stump;
            p.Flesh = 0;
            p.Bandage = 5;
        }
        else
        {
            p.Flesh = Math.Clamp(value, -max, max);
            p.Bandage = max - p.Flesh;
        }
        p.WasDown = p.IsDown;
    }

    // ------------------------------------------------------------------ KO and death

    void StartKo(in MedicalContext ctx, List<MedicalEvent>? events) => ApplyKo(KoTime(ctx), events);

    /// <summary>The cheat menu's Knock out: a knockout of the usual length (<see cref="KoTime"/>) without a hit.</summary>
    public void CheatKnockOut(in MedicalContext ctx) => StartKo(ctx, null);

    /// <summary>
    /// The factor of the KO given to a non-friendly character that is put down (docs/game/carry-loot.md "Putting down"): lerp(0.1, 0.4, the carrier's assassination / 100)
    /// (<b>Observed</b>, <c>Task_PutDownObject</c>; the help text says assassination decides how long they stay out).
    /// </summary>
    public static float PutDownKoFactor(float assassination) => 0.1f + 0.3f * Math.Clamp(assassination / 100, 0, 1);

    /// <summary>
    /// The KO time of a character put down by a non-friendly carrier, in seconds of character time: max(3, (2 x <c>knockout time base</c> + <see cref="KoTerms"/>) x <paramref name="factor"/> x 4),
    /// the shape of the leg-loss KO (<c>FUN_140644980</c>) with the put-down factor in place of (1 - toughness) and no random roll (the notes give that function only the blood term and
    /// no roll; which reading is right is <b>Unknown</b>, the doc's leg-loss reading is used).
    /// </summary>
    public float PutDownKoSeconds(in MedicalContext ctx, float factor) => MathF.Max(3, (2 * ctx.Constants.KnockoutTimeBase + KoTerms(ctx)) * factor * 4);

    /// <summary>Knocks the character out for <paramref name="seconds"/> seconds of character time from outside the medical rules (a drug, a scripted event); a longer running KO is kept.</summary>
    public void KnockOut(float seconds, List<MedicalEvent>? events = null) => ApplyKo(seconds, events);

    void ApplyKo(float seconds, List<MedicalEvent>? events)
    {
        if (Dead) return;
        if (!Unconscious)
        {
            Unconscious = true;
            KoTimer = seconds;
            events?.Add(new MedicalEvent(MedicalEventKind.KnockedOut));
        }
        else KoTimer = MathF.Max(KoTimer, seconds);
    }

    /// <summary>Starts the KO timer over at <paramref name="seconds"/>, also when a longer KO is running (<c>startKnockoutTimer</c> overwrites it).</summary>
    void RestartKo(float seconds, List<MedicalEvent>? events)
    {
        if (Dead) return;
        ApplyKo(seconds, events);
        KoTimer = seconds;
    }

    /// <summary>Kills the character because one of its death items was taken while it was down (docs/game/animals.md "Death items").</summary>
    public void Butcher(List<MedicalEvent>? events = null) => Die(DeathCause.Butchered, events);

    void Die(DeathCause cause, List<MedicalEvent>? events)
    {
        if (Dead) return;
        Dead = true;
        Cause = cause;
        events?.Add(new MedicalEvent(MedicalEventKind.Died, Cause: cause));
    }

    /// <summary>Blood rules shared by a hit and the tick: blood at 0 (or -25 once flagged) starts a KO, at -capacity the character dies, above 25 the flag clears.</summary>
    void CheckBlood(in MedicalContext ctx, List<MedicalEvent>? events)
    {
        if (Dead) return;
        if (Blood > 25) BloodKo = false;
        float cap = Capacity(ctx);
        if (Blood <= -cap) { Die(DeathCause.BloodLoss, events); return; }
        if (Blood <= 0 && !BloodKo) { BloodKo = true; StartKo(ctx, events); }
        else if (BloodKo && Blood <= -25 && !Unconscious) StartKo(ctx, events);
    }

}
