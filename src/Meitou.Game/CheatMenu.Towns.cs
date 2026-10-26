using System.Numerics;
using Meitou.Data.Ui;
using Meitou.Rendering.Ui;
using Meitou.Simulation;
using Meitou.Simulation.Towns;

namespace Meitou.Game;

// The Town Ownership window (' by default; Meitou's version of the RE_Kenshi plugin Town Ownership, Workshop 3750141111): how towns are won, CLAIM RUINED TOWN and
// DISMISS CURRENT TOWN for the town the selected character stands in. Drawn with the cheat menu's kit in a window of its own; the claiming itself is TownClaimSystem's.
sealed partial class CheatMenu
{
    /// <summary>The window shows the Town Ownership settings instead of the cheats.</summary>
    bool townsMode;

    /// <summary>Opens the cheat menu (<paramref name="towns"/> false) or the Town Ownership window; a second press of the window's key closes it.</summary>
    public void Open(bool towns)
    {
        if (visible && townsMode == towns) { Visible = false; return; }
        Visible = false;
        townsMode = towns;
        Visible = true;
    }

    /// <summary>The town the selected character stands in (site, or -1) and the character.</summary>
    (int Site, CharacterId Who) StandingIn()
    {
        var w = world();
        var who = w.Player.Selection.Count > 0 ? w.Player.Selection[0] : CharacterId.None;
        if (who.IsNone || !w.Characters.IsAlive(who) || population() is not { } data) return (-1, who);
        return (data.TownAt(w.Characters.Previous[who.Slot].Position), who);
    }

    void DrawTownWindow(int width, int height, UiPainter painter)
    {
        const float W = 560, H = 392;
        scale = Math.Max(0.35f, Math.Min(1.25f, Math.Min(height / 900f, width / 1280f))) * player.HudScale;
        bounds = new(MathF.Round((width - W * scale) / 2), MathF.Round((height - H * scale) / 2), MathF.Round(W * scale), MathF.Round(H * scale));
        controls.Clear(); hits.Clear();
        var snapshot = player.Snapshot;
        var data = population();

        Surface(bounds, "window");
        Text("Town Ownership", R(20, 6, 400, 32), 22, LightInk);
        var close = R(W - 51, 6, 31, 32);
        controls.Add(new("towns-close", "Button", close, "", "towns:close", "Kenshi_CloseButtonSkin", true, new Dictionary<string, string>(), Vector4.Zero));
        hits.Add(new(close, () => Visible = false));
        Rect(R(20, 44, W - 40, 1), MainInk with { W = 0.12f });

        Text("Choose how to claim towns:", R(20, 56, W - 40, 24), 14, MainInk);
        var method = snapshot.TownClaimMethod;
        void Option(TownClaimMethod m, string name, string help, float y)
        {
            Button("", R(20, y, W - 40, 50), () => { if (snapshot.TownClaimMethod != m) Send(new SetTownClaimMethod(m) { Tick = Now }); }, method == m);
            if (method == m) Rect(R(20, y, 3, 50), Brass);
            Text(name + (method == m ? "   <-- current" : ""), R(36, y + 4, W - 72, 22), 15, method == m ? LightInk : MainInk);
            Text(help, R(36, y + 26, W - 72, 18), 11, MutedInk);
        }
        Option(TownClaimMethod.Ruler, "Defeat the ruler", "Knock out or kill the town's ruler (its resident with the highest bounty).", 84);
        Option(TownClaimMethod.Clear, "Clear entire settlement", $"Knock out or kill {TownClaimSystem.ClearShare:P0} of the town's residents.", 140);

        Rect(R(20, 204, W - 40, 1), MainInk with { W = 0.12f });
        var (site, who) = StandingIn();
        string where;
        bool owned = false;
        if (who.IsNone) where = "Select one of your characters to claim or dismiss the town they stand in.";
        else if (site < 0 || data is null) where = "Your selected character is not standing in a town.";
        else
        {
            int owner = site < data.SiteFactions.Count ? data.SiteFactions[site] : -1;
            owned = owner >= 0 && owner == world().Player.Faction;
            where = $"Standing in {data.Sites[site].Town.Name}" + (owned ? " (yours)" : owner >= 0 ? $" (owned by {FactionName(owner)})" : " (no owner)");
        }
        Text(where, R(20, 212, W - 40, 22), 13, site >= 0 ? LightInk : MutedInk);
        Button("Claim ruined town", R(20, 242, (W - 52) / 2, 34), () => Send(new ClaimRuinedTown(who) { Tick = Now }), enabled: site >= 0 && !owned);
        Button("Dismiss current town", R(32 + (W - 52) / 2, 242, (W - 52) / 2, 34), () =>
        {
            if (data is null || site < 0) return;
            string town = data.Sites[site].Town.Name;
            Visible = false;
            player.AskYesNo("Dismiss Town", $"Are you sure you want to dismiss {town}? Ownership will be released.", () => Send(new DismissTown(who) { Tick = Now }));
        }, enabled: owned);

        var claimed = snapshot.ClaimedTowns;
        string list = data is null || claimed.Count == 0 ? "None yet." : string.Join(", ", claimed.Where(s => s < data.Sites.Count).Select(s => data.Sites[s].Town.Name));
        Text($"Your towns: {list}", R(20, 286, W - 40, 36), 12, MutedInk, "Left Top");

        Rect(R(20, H - 52, W - 40, 1), MainInk with { W = 0.12f });
        if (notice.Length > 0 && Environment.TickCount64 - noticeAt < 8000) Text(notice, R(20, H - 44, W - 180, 28), 12, Brass);
        Button("OK", R(W - 140, H - 44, 120, 28), () => Visible = false, primary: true);
        painter.Draw(controls, width, height, pointer, null);
    }
}
