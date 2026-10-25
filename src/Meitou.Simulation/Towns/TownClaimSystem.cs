using Meitou.Data.Gameplay;

namespace Meitou.Simulation.Towns;

/// <summary>How a town is won (Town Ownership's <c>method</c>): by defeating its ruler, or by downing most of its residents.</summary>
public enum TownClaimMethod : byte { Ruler = 0, Clear = 1 }

/// <summary>A command of the Town Ownership window or prompt (<see cref="TownClaimSystem"/>).</summary>
public abstract record TownCommand : SimCommand;

/// <summary>Settings: the way towns are won from now on.</summary>
public sealed record SetTownClaimMethod(TownClaimMethod Method) : TownCommand;

/// <summary>The answer to the claim prompt <see cref="Offer"/> (<see cref="TownClaimOffer.Id"/>): YES claims the town, NO leaves it as it is.</summary>
public sealed record AnswerTownClaim(int Offer, bool Accept) : TownCommand;

/// <summary>CLAIM RUINED TOWN: the town <see cref="Character"/> (one of the player's) stands in is claimed when nobody lives there any more.</summary>
public sealed record ClaimRuinedTown(CharacterId Character) : TownCommand;

/// <summary>DISMISS CURRENT TOWN (after the interface's confirmation): the claimed town <see cref="Character"/> stands in goes back to the faction it was taken from.</summary>
public sealed record DismissTown(CharacterId Character) : TownCommand;

/// <summary>The claim prompt waiting for the player: the town and, when won by defeating its ruler, the ruler's name (null: the settlement was cleared).</summary>
public sealed record TownClaimOffer(int Id, int Site, string Town, string? Ruler);

/// <summary>
/// Meitou's version of the RE_Kenshi plugin Town Ownership ("Conquest", Workshop 3750141111; docs/game/factions-squads-towns.md "Claiming towns"): the player takes a loaded town
/// by downing its ruler (the resident with the highest bounty) or, in clear mode, at least 70 % of its residents; or claims one nobody lives in; and can give a claimed town back.
/// Claiming only changes the town's owner (<see cref="PopulationData.SiteFactions"/>): its buildings and residents stay as they are. The claims are saved with the game
/// (CAMERA <c>meitou claim*</c>; the plugin kept them in a file of its own, shared by every save).
/// </summary>
public sealed class TownClaimSystem : ITickSystem, IStateHashed
{
    /// <summary>The share of a town's residents that must be down or dead for clear mode (the plugin's fixed 70 %).</summary>
    public const float ClearShare = 0.7f;
    /// <summary>How often the loaded towns are looked at, in seconds (the plugin looked every fourth frame of each character).</summary>
    const float CheckSeconds = 1;

    readonly PopulationData data;
    /// <summary>The claimed towns and the faction each was taken from (-1: none).</summary>
    readonly SortedDictionary<int, int> claims = [];
    /// <summary>Towns whose prompt was answered NO: not offered again until the condition is gone (the ruler back up, the residents back on their feet).</summary>
    readonly SortedSet<int> declined = [];
    int nextOffer = 1;

    public TownClaimSystem(PopulationData data)
    {
        this.data = data;
        data.ResetSiteFactions();   // the data outlives worlds: a new world starts with every town its record's
    }

    public TownClaimMethod Method { get; private set; } = TownClaimMethod.Ruler;
    public TownClaimOffer? Offer { get; private set; }
    /// <summary>The claimed towns (site) and the faction each was taken from.</summary>
    public IReadOnlyDictionary<int, int> Claims => claims;

    /// <summary>A save's claims and method (<see cref="Saving.SaveLoader"/>): each town goes to the player's faction again.</summary>
    public void Restore(World world, TownClaimMethod method, IEnumerable<(int Site, int From)> saved)
    {
        Method = method;
        foreach (var (site, from) in saved)
        {
            if (site < 0 || site >= data.Sites.Count || world.Player.Faction < 0) continue;
            claims[site] = from;
            data.SetSiteFaction(site, world.Player.Faction);
        }
    }

    public void Inputs(World world, IReadOnlyList<SimCommand> commands)
    {
        foreach (var command in commands)
            switch (command)
            {
                case SetTownClaimMethod set:
                    if (set.Method != Method) { Method = set.Method; Offer = null; declined.Clear(); }
                    break;
                case AnswerTownClaim answer when Offer is { } offer && offer.Id == answer.Offer:
                    Offer = null;
                    if (answer.Accept) Claim(world, offer.Site);
                    else declined.Add(offer.Site);
                    break;
                case ClaimRuinedTown ruined: ClaimRuined(world, ruined.Character); break;
                case DismissTown dismiss: Dismiss(world, dismiss.Character); break;
            }
    }

    public void SlowWorld(World world)
    {
        long every = Math.Max(1, (long)MathF.Round(CheckSeconds / world.TickSeconds));
        if (world.Tick % every != 0 || Offer is not null || world.Player.Faction < 0 || world.SystemOf<PopulationSystem>() is not { } population) return;
        for (int site = 0; site < data.Sites.Count; site++)
        {
            if (!Claimable(world, site) || population.StatusOf(site) != SiteStatus.Loaded) { declined.Remove(site); continue; }
            var residents = Residents(world, site);
            bool won;
            string? ruler = null;
            if (Method == TownClaimMethod.Ruler)
            {
                var r = Ruler(world, site, residents);
                won = r is { } id && Down(world, id) && DefeatedByPlayer(world, id);
                if (won) ruler = Cold(world, r!.Value)?.Name ?? "";
            }
            else
            {
                int down = residents.Count(id => Down(world, id));
                won = residents.Count > 0 && down >= ClearShare * residents.Count;
            }
            if (!won) { declined.Remove(site); continue; }
            if (declined.Contains(site)) continue;
            Offer = new TownClaimOffer(nextOffer++, site, data.Sites[site].Town.Name, ruler);
            return;
        }
    }

    /// <summary>A placed town owned by another faction than the player's (a nest, a marker or a town with no owner is never won).</summary>
    bool Claimable(World world, int site) =>
        site < data.SiteFactions.Count && data.SiteFactions[site] is >= 0 and var owner && owner != world.Player.Faction
        && data.Sites[site].Town.Type is not (TownType.Nest or TownType.NestMarker or TownType.Null);

    /// <summary>The members of the town's own squads (residents and bar squads; not roaming squads passing through).</summary>
    static List<CharacterId> Residents(World world, int site)
    {
        var list = new List<CharacterId>();
        foreach (var squad in world.Squads.All)
            if (squad.Town == site && squad.TownOwn && !squad.Wild && !squad.Dead) list.AddRange(squad.Members);
        return list;
    }

    /// <summary>
    /// The town's ruler (the plugin's <c>FindTownRuler</c>): the resident of the town's faction with the highest bounty, a unique character before others at the same bounty;
    /// none when nobody of the faction has a bounty.
    /// </summary>
    CharacterId? Ruler(World world, int site, List<CharacterId> residents)
    {
        int owner = data.SiteFactions[site];
        CharacterId? best = null;
        (int Bounty, bool Unique) top = (0, false);
        foreach (var id in residents)
        {
            if (Cold(world, id) is not { } c || c.Faction != owner) continue;
            var key = (c.Bounties?.Total ?? 0, world.Uniques.Find(c.RecordId) is not null);
            if (key.Item1 <= 0 || key.Item1 < top.Bounty || key.Item1 == top.Bounty && (!key.Item2 || top.Unique)) continue;
            (best, top) = (id, key);
        }
        return best;
    }

    static CharacterCold? Cold(World world, CharacterId id) => world.Characters.TryResolveNext(id, out int slot) ? world.Characters.Cold(slot) : null;

    static bool Down(World world, CharacterId id) => Cold(world, id) is not { } c || c.Medical is { Incapacitated: true };

    /// <summary>The last blow the character took came from one of the player's characters (the plugin: a player character's opponent went down).</summary>
    static bool DefeatedByPlayer(World world, CharacterId id)
    {
        if (world.SystemOf<Combat.CombatSystem>() is not { } combat) return false;
        int from = combat.StateOf(id).HitFrom;
        return from >= 0 && from < world.Characters.HighWater && world.Characters.Cold(from) is { } hitter && (hitter.IsPlayer || hitter.Faction == world.Player.Faction);
    }

    void Claim(World world, int site)
    {
        if (world.Player.Faction < 0 || site < 0 || site >= data.Sites.Count) return;
        if (!claims.ContainsKey(site)) claims[site] = data.SiteFactions[site];
        data.SetSiteFaction(site, world.Player.Faction);
        declined.Remove(site);
        world.Post($"The town {data.Sites[site].Town.Name} is now under your control.");
    }

    /// <summary>The town a player character stands in, or -1 with the plugin's message posted.</summary>
    int TownOf(World world, CharacterId character)
    {
        if (Cold(world, character) is not { } c || !(c.IsPlayer || c.Faction == world.Player.Faction) || !world.Characters.TryResolveNext(character, out int slot))
        {
            world.Post("Select one of your characters first.");
            return -1;
        }
        int site = data.TownAt(world.Characters.Next[slot].Position);
        if (site < 0) world.Post("Your selected character is not standing in a town.");
        return site;
    }

    void ClaimRuined(World world, CharacterId character)
    {
        if (world.Player.Faction < 0 || world.SystemOf<PopulationSystem>() is not { } population) { world.Post("Cannot claim right now."); return; }
        if (TownOf(world, character) is not (>= 0 and var site)) return;
        string name = data.Sites[site].Town.Name;
        if (data.SiteFactions[site] == world.Player.Faction) { world.Post($"You already own {name}."); return; }
        if (population.StatusOf(site) != SiteStatus.Loaded) { world.Post("Cannot claim right now."); return; }
        if (Residents(world, site).Any(id => Cold(world, id) is { Medical: not { Dead: true } })) { world.Post("This town still has residents - it is not ruined."); return; }
        world.Post($"{name} is ruined - claiming.");
        Claim(world, site);
    }

    void Dismiss(World world, CharacterId character)
    {
        if (world.Player.Faction < 0) { world.Post("Cannot dismiss right now."); return; }
        if (TownOf(world, character) is not (>= 0 and var site)) return;
        if (data.SiteFactions[site] != world.Player.Faction) { world.Post("You do not own this town."); return; }
        int to = claims.TryGetValue(site, out int from) ? from : data.TownFactions[site];
        claims.Remove(site);
        data.SetSiteFaction(site, to == world.Player.Faction ? -1 : to);
        world.Post($"{data.Sites[site].Town.Name} returned to its original owners.");
    }

    public void Hash(ref StateHasher hasher)
    {
        if (claims.Count == 0 && Method == TownClaimMethod.Ruler && Offer is null && declined.Count == 0) return;   // nothing, so worlds that never claim hash as before
        hasher.Add(0x544F574E);
        hasher.Add((int)Method);
        hasher.Add(claims.Count);
        foreach (var (site, from) in claims) { hasher.Add(site); hasher.Add(from); }
        hasher.Add(declined.Count);
        foreach (int site in declined) hasher.Add(site);
        hasher.Add(Offer?.Id ?? 0);
        hasher.Add(Offer?.Site ?? -1);
    }
}
