using Meitou.Simulation;
using Meitou.Simulation.Towns;

namespace Meitou.Game;

/// <summary>
/// Town Ownership's prompt (<see cref="TownClaimSystem"/>): when the world offers a town, the message box asks "Would you like to claim this town?" (YES / NO, as the plugin's
/// window); closed any other way (Esc) it is a NO.
/// </summary>
sealed partial class PlayerInterface
{
    /// <summary>The offer the prompt was shown for, and whether that prompt is still up.</summary>
    int townOfferShown;
    bool townPromptOpen;

    /// <summary>The world as the interface last saw it (the Town Ownership window reads the method and the claimed towns).</summary>
    internal WorldSnapshot Snapshot => session.CurrentSnapshot;

    /// <summary>A YES / NO question in the game's message box; NO does nothing.</summary>
    internal void AskYesNo(string title, string text, Action yes) => Confirm(title, text, yes, "YES", null, "NO");

    void SyncTownClaimPrompt()
    {
        var offer = session.CurrentSnapshot.TownOffer;
        if (townPromptOpen && !faithfulScreens.IsOpen("message"))
        {
            townPromptOpen = false;
            if (offer?.Id == townOfferShown) Say(new AnswerTownClaim(townOfferShown, false) { Tick = session.World.Tick });
            return;
        }
        if (offer is null || offer.Id == townOfferShown || faithfulScreens.HasModal || TitleOpen || EditorOpen) return;
        int id = townOfferShown = offer.Id;
        townPromptOpen = true;
        string text = offer.Ruler is { } ruler
            ? $"You have defeated {ruler}, the ruler of {offer.Town}. Would you like to claim this town?"
            : $"You have cleared the settlement of {offer.Town}. Would you like to claim this town?";
        Confirm("Town Ownership", text, () => { townPromptOpen = false; Say(new AnswerTownClaim(id, true) { Tick = session.World.Tick }); }, "YES", null, "NO");
    }
}

sealed partial class PlayerInterface
{
    /// <summary>The selected building's instance (a player site's id, a town building's key), or null: what Squad Autonomy's Pick in world takes.</summary>
    internal string? SelectedBuildingInstance => BuildingFocus()?.Instance;

    /// <summary>The name of a building by instance (a player site's catalogue name, else a town building's), or null when the world has no such building.</summary>
    internal string? PlaceName(string instance, PopulationData? data)
    {
        if (session.CurrentSnapshot.Buildings.FirstOrDefault(b => b.Id == instance) is { } site) return SiteName(site);
        if (data is not null)
            foreach (var s in data.Sites)
                foreach (var b in s.Buildings)
                    if (b.Key == instance) return b.Name.Length > 0 ? $"{b.Name}, {s.Town.Name}" : s.Town.Name;
        return null;
    }
}
