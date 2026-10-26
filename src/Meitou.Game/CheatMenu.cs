using System.Globalization;
using System.Numerics;
using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Data.Ui;
using Meitou.Engine.Input;
using Meitou.Rendering;
using Meitou.Simulation;

namespace Meitou.Game;

/// <summary>
/// The cheat menu (the Esc menu's CHEAT MENU; Meitou only): character, health, skills, items, factions and world cheats in the Meitou UI kit's window, an overhaul of the
/// RE_Kenshi "Cheat Menu" plugin (Workshop 3706407417). Every change is a <see cref="DebugCommand"/> (Meitou.Simulation/CheatCommands.cs), applied between ticks,
/// also while paused. The game keeps running while it is open; it takes the mouse over its window and the keyboard only while a text field is focused.
/// </summary>
sealed partial class CheatMenu(PlayerInterface player, Func<World> world, Func<GameDatabase?> database, Func<PopulationData?> population,
    Func<Vector3> cameraTarget)
{
    enum Page { Character, Health, Skills, Items, Factions, World }
    enum Target { Selected, Inspected, Squad }

    static readonly string[] PageNames = ["Character", "Health", "Skills", "Items", "Factions", "World"];

    readonly List<UiControl> controls = [];
    readonly List<Hit> hits = [];
    readonly SettingsMenuArt art = new();
    bool visible;
    Page page;
    Target target;
    float scale = 1, scroll, scrollTarget, maxScroll;
    Vector2 pointer;
    bool pointerDown;
    UiRect bounds, viewport;
    Hit? dragging;
    float dragFraction;
    Field? focused;
    Dropdown? openDropdown;
    int dropdownScroll;
    string notice = "";
    long noticeAt;

    /// <summary>A clickable area. <see cref="Drag"/>: a slider whose value follows the pointer while held and is applied once on release (<see cref="Commit"/>).</summary>
    sealed record Hit(UiRect Bounds, Action Activate, UiRect? Track = null, Action<float>? Commit = null);

    /// <summary>A one-line text box: its text, what Enter does, and whether only a number may be typed.</summary>
    sealed class Field(string id, Action<string>? enter = null, bool numeric = false)
    {
        public string Id { get; } = id;
        public string Text = "";
        public Action<string>? Enter { get; } = enter;
        public bool Numeric { get; } = numeric;
    }

    sealed record Dropdown(string Id, UiRect Anchor, IReadOnlyList<string> Options, int Current, Action<int> Choose);

    public bool Visible
    {
        get => visible;
        set
        {
            if (visible == value) return;
            visible = value;
            dragging = null; focused = null; openDropdown = null;
            if (!value) { townsMode = false; autoMode = false; picking = null; }
            if (value) { scroll = scrollTarget = 0; RefreshFields(); }
        }
    }

    /// <summary>True while a text box has the keyboard (the host then routes every key and character here).</summary>
    public bool TakesKeyboard => visible && focused is not null;

    public bool Contains(Vector2 at) => visible && (bounds.Contains(at.X, at.Y) || openDropdown is not null) || !visible && autButton.Width > 0 && autButton.Contains(at.X, at.Y);

    void Notice(string text) { notice = text; noticeAt = Environment.TickCount64; }

    long Now => world().Tick;

    void Send(DebugCommand command) => world().Commands.Enqueue(command);
    void Send(Meitou.Simulation.Towns.TownCommand command) => world().Commands.Enqueue(command);

    // ------------------------------------------------------------------ targets

    IReadOnlyList<CharacterId> Targets()
    {
        var w = world();
        var p = w.Player;
        switch (target)
        {
            case Target.Inspected:
                return p.Inspected.IsNone ? [] : [p.Inspected];
            case Target.Squad:
                var lead = p.Selection.Count > 0 ? p.Selection[0] : CharacterId.None;
                int squadId = Cold(lead)?.SquadId ?? p.Squad;
                return w.Squads.Find(squadId) is { } squad ? [.. squad.Members] : [];
            default:
                if (p.Selection.Count > 0) return [.. p.Selection];
                return p.Inspected.IsNone ? [] : [p.Inspected];
        }
    }

    CharacterId Primary => Targets() is { Count: > 0 } t ? t[0] : CharacterId.None;

    CharacterCold? Cold(CharacterId id)
    {
        var table = world().Characters;
        return !id.IsNone && table.TryResolveNext(id, out int slot) ? table.Cold(slot) : null;
    }

    string FactionName(int faction) => population() is { } d && faction >= 0 && faction < d.Factions.Count ? d.Factions[faction].Name : "";

    // ------------------------------------------------------------------ input

    public bool MouseDown(Vector2 at, MouseButton button = MouseButton.Left)
    {
        if (!visible) return button == MouseButton.Left && AutButtonDown(at);
        // Pick in world: only the banner is the window's; a click elsewhere selects the building.
        if (picking is not null && !bounds.Contains(at.X, at.Y)) return false;
        pointer = at;
        if (button != MouseButton.Left) return Contains(at);
        pointerDown = true;
        var hit = hits.LastOrDefault(h => h.Bounds.Contains(at.X, at.Y));
        if (hit is null)
        {
            bool inside = Contains(at);
            openDropdown = null;
            if (inside) focused = null;
            return inside;
        }
        if (hit.Track is { } track) { dragging = hit; dragFraction = Math.Clamp((at.X - track.X) / track.Width, 0, 1); }
        else hit.Activate();
        return true;
    }

    public bool MouseMove(Vector2 at)
    {
        pointer = at;
        if (!visible) return false;
        if (dragging is { Track: { } track }) { dragFraction = Math.Clamp((at.X - track.X) / track.Width, 0, 1); return true; }
        return Contains(at);
    }

    public bool MouseUp()
    {
        pointerDown = false;
        if (dragging is not { Commit: { } commit }) { bool had = dragging is not null; dragging = null; return had; }
        dragging = null;
        commit(dragFraction);
        return true;
    }

    public bool Scroll(float steps)
    {
        if (!visible || !bounds.Contains(pointer.X, pointer.Y) && openDropdown is null) return false;
        if (openDropdown is { } d) dropdownScroll = Math.Clamp(dropdownScroll - (int)steps, 0, Math.Max(0, d.Options.Count - DropdownRows));
        else scrollTarget = Math.Clamp(scrollTarget - steps * 60 * scale, 0, maxScroll);
        return true;
    }

    /// <summary>A key while the menu is open: Esc closes (a dropdown, the text box, then the menu); with a text box focused every key edits it. False: not the menu's.</summary>
    public bool KeyDown(Key key)
    {
        if (!visible) return false;
        if (key == Key.Escape)
        {
            if (picking is not null) picking = null;
            else if (openDropdown is not null) openDropdown = null;
            else if (focused is not null) focused = null;
            else Visible = false;
            return true;
        }
        if (focused is not { } field) return false;
        switch (key)
        {
            case Key.Backspace when field.Text.Length > 0: field.Text = field.Text[..^1]; break;
            case Key.Enter: field.Enter?.Invoke(field.Text.Trim()); break;
            case Key.Tab: focused = null; break;
        }
        return true;
    }

    public void TextInput(char c)
    {
        if (!visible || focused is not { } field || char.IsControl(c) || field.Text.Length >= 64) return;
        if (field.Numeric && !(char.IsDigit(c) || c is '-' or '+' or ',' or '.')) return;
        field.Text += c;
        if (field.Id == "item-search") { itemCache = null; scrollTarget = scroll = 0; }
        if (field.Id == "spawn-search") spawnCache = null;
        if (field.Id == "faction-filter") scrollTarget = scroll = 0;
    }

    static float Parse(string text, float fallback)
    {
        text = text.Replace(",", "").Replace("c.", "").Trim();
        return float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float v) && float.IsFinite(v) ? v : fallback;
    }
}
