using System.Numerics;
using Meitou.Engine;
using Meitou.Engine.Input;
using Meitou.Rendering;
using Meitou.Simulation;

namespace Meitou.Game;

/// <summary>
/// The player's hands and the minimal HUD (docs/game/ui-input.md): click or box select, right click move orders (shift queues; holding the
/// button steers: the order follows the cursor),
/// the squad keys, the clock and speed buttons. Everything the player does becomes a <see cref="SimCommand"/> at the world's
/// current tick; this class never touches the simulation's state, it reads the published snapshot.
/// Positions are framebuffer pixels, top-left origin. The host sets <see cref="ViewProjection"/>, <see cref="Eye"/>, <see cref="Width"/>
/// and <see cref="Height"/> each frame (events arrive on the same thread, between frames).
/// </summary>
sealed partial class PlayerInterface(WorldSession session, Func<float, float, float> ground, Func<CharacterSnapshot, Vector3> where)
{
    public Matrix4x4 ViewProjection = Matrix4x4.Identity;
    public Vector3 Eye;
    public int Width = 1, Height = 1;
    /// <summary>How far from the camera a box select still takes characters.</summary>
    public const float BoxSelectRange = 7500;
    /// <summary>A person's height (the renderer's sphere takes 20) for the box select's aim point and the pick capsule of a character the renderer does not draw.</summary>
    public const float CharacterHeight = 18;
    const float DragThreshold = 5;

    Vector2? dragFrom;
    Vector2 mouse;
    bool dragging;
    // The held right button's move order: where it goes now and the tick it was given.
    bool steering;
    Vector3 steerTarget;
    long steerTick;


    IReadOnlyList<CharacterId> SelectedIds() => [.. Selected().Select(c => c.Id)];

    IReadOnlyList<CharacterId> PlayerCharacters() =>
        [.. session.CurrentSnapshot.Characters.Where(c => c.IsPlayer).Select(c => c.Id)];

    /// <summary>
    /// The active squad's members in squad order, the first leading (the squad bar's; the number keys and select_all pick from them, the original's <c>UiSelectSquadMember</c> /
    /// <c>UiSelectAllInSquad</c>). Every player character when there is no squad list (a world without a player squad).
    /// </summary>
    IReadOnlyList<CharacterId> ActiveSquadMembers()
    {
        var snapshot = session.CurrentSnapshot;
        if (snapshot.PlayerSquads.FirstOrDefault(s => s.Id == snapshot.ActiveSquad) is not { } squad) return PlayerCharacters();
        var present = PlayerCharacters().ToHashSet();
        return [.. squad.Members.Where(present.Contains)];
    }

    // ---- commands ----

    void Say(SimCommand command)
    {
        SoundForCommand(command);
        session.World.Commands.Enqueue(command);
    }

    public void Select(IReadOnlyList<CharacterId> ids, bool additive)
    {
        if (ids.Count > 0) SetBuilding(null);   // selecting a character ends the building's selection
        Say(new SelectCommand(ids, additive) { Tick = session.World.Tick });
    }

    public void Move(Vector3 target, bool queued) =>
        Say(new MoveOrder([], target) { Tick = session.World.Tick, Queued = queued });

    // ---- events ----

    /// <summary>Returns true when the click belonged to the HUD or the interface (the camera input should not see it).</summary>
    public bool MouseDown(MouseButton button, Vector2 p, bool shift, bool ctrl = false, bool alt = false)
    {
        mouse = p;
        (pressShift, pressCtrl) = (shift, ctrl);
        HideTooltip();   // a press captures the mouse, which hides a tooltip (MyGUI)
        if (EditorPointerDown(button, p)) return true;
        if (button == MouseButton.Right && CancelInventoryDrag()) return true;
        if (button == MouseButton.Right && !OverHud(p) && WallCancel()) return true;
        if (button == MouseButton.Left)
        {
            if (UiMouseDown(p)) return true;
            if (CloseContextMenu()) return true;   // a click beside the open menu only closes it
            if (OverHud(p)) return true;
            if (BuildClick()) return true;
            dragFrom = p;
            dragging = false;
            return true;
        }
        if (button == MouseButton.Right && InventoryRightClick(p, shift)) return true;
        if (button == MouseButton.Right && CloseContextMenu()) return true;
        if (button == MouseButton.Right && MapRightDown(p)) return true;
        if (button == MouseButton.Middle && UiVisible && MapPointerDown(p)) return true;
        if (button == MouseButton.Right && !OverHud(p))
        {
            // Alt + right click on a downed or dead character: the nearest selected loots it (take all). Ctrl + right click: picks it up, or puts it down when a selected
            // character carries it. A plain right click on one of your own downed characters that needs no first aid picks it up too (the original's quick right click).
            var downed = alt || ctrl ? PickDowned(p) : null;
            if (alt && downed is { } loot && SelectedIds() is { Count: > 0 })
                Say(new Meitou.Simulation.Carrying.LootOrder([], loot) { Tick = session.World.Tick });
            else if (ctrl && downed is { } body && Selected().Where(c => c.Carrying == body).Select(c => c.Id).ToArray() is { Length: > 0 } carriers)
                Say(new Meitou.Simulation.Carrying.PutDownOrder(carriers) { Tick = session.World.Tick });
            else if (ctrl && downed is { } lift && SelectedIds() is { Count: > 0 })
                Say(new Meitou.Simulation.Carrying.PickUpOrder([], lift) { Tick = session.World.Tick });
            // Else the nearest target along the ray (PickAt). On a character: the quick order (trade with a trader, attack anyone else, treat a hurt ally, pick up a downed one of yours,
            // loot anyone else's) is given when the button comes up before the context menu's hold time; held longer, the menu opens instead (PlayerInterface.ContextMenu.cs).
            else if (PickAt(p).Kind is var kind && kind == Picked.Character) { var quick = QuickOrder(p)?.Do; if (!ContextPress(p, quick)) quick?.Invoke(); }
            else if (kind == Picked.GroundItem && GroundItemClick(p)) { }
            else if (kind == Picked.Container && ContainerClick(p, alt)) { }
            else if (kind == Picked.Door && DoorClick(p, alt)) { }
            else if (kind == Picked.Bed && BedPress(p)) { }
            else if (kind == Picked.Cage && CageClick(p)) { }
            else if (kind == Picked.Rock && RockPress(p)) { }
            else if (kind is Picked.Site or Picked.None && SitePress(p)) { }
            else if (alt && FormationPress(p, shift)) { }   // Alt + right drag: a formation line (PlayerInterface.FormationDrag.cs)
            else if (Ray(p) is { } ray && MoveHit(ray.Origin, ray.Direction) is { } hit)
            {
                Move(hit, shift);
                PlaceMarker(hit);
                (steering, steerTarget, steerTick) = (!shift, hit, session.World.Tick);
            }
            return true;
        }
        return OverHud(p);
    }

    /// <summary>
    /// The order a quick right click on a character gives and the mouse pointer that shows it (PlayerInterface.Pointer.cs), or null when there is none (the ground
    /// under it is then a move).
    /// </summary>
    (string Pointer, CharacterId Target, Action Do)? QuickOrder(Vector2 p)
    {
        if (SelectedIds() is not { Count: > 0 } selection) return null;
        if (PickTrader(p) is { } trader && !Hostile(trader)) return ("talk", trader, () => Say(new Meitou.Simulation.Trading.OpenTrade(selection[0], trader) { Tick = session.World.Tick }));
        // A non-hostile character with something to say is talked to (the same Talk as the context menu's).
        if (PickTarget(p) is { } talkee && !Hostile(talkee) && session.World.CanTalk(talkee, selection[0]))
            return ("talk", talkee, () => Say(new Meitou.Simulation.Dialogue.TalkOrder(selection[0], talkee) { Tick = session.World.Tick }));
        // While sneaking, a right click on an NPC is a stealth knock-out (the original's tutorial: "right click on an NPC while sneaking").
        // Only a hostile character is attacked this way; a neutral is left alone (the context menu still offers the attack).
        if (PickTarget(p) is { } mark && Hostile(mark) && session.CurrentSnapshot.Characters.FirstOrDefault(c => c.Id == mark) is { StealthKoChance: not null })
            return ("attk", mark, () => Say(new Meitou.Simulation.Carrying.StealthKoOrder(selection, mark) { Tick = session.World.Tick }));
        // Fighting back at someone attacking the squad is defence, not an assault on its faction.
        if (PickTarget(p) is { } victim && Hostile(victim))
            return ("attk", victim, () => OrderOrJob(Meitou.Data.Gameplay.Ai.TaskType.MELEE_ATTACK, victim, () => Say(new Meitou.Simulation.Combat.AttackOrder(selection, victim)
            {
                Tick = session.World.Tick, Picked = true, Defensive = session.CurrentSnapshot.Characters.FirstOrDefault(c => c.Id == victim) is { FightsPlayer: true },
            })));
        if (PickHurtAlly(p) is { } patient)
            return ("med", patient, () => OrderOrJob(Meitou.Data.Gameplay.Ai.TaskType.FIRST_AID_ORDER, patient, () => Say(new Meitou.Simulation.FirstAid.TreatOrder(selection, patient) { Tick = session.World.Tick })));
        if (PickDowned(p, ownOnly: true) is { } ally) return ("lift", ally, () => Say(new Meitou.Simulation.Carrying.PickUpOrder([], ally) { Tick = session.World.Tick }));
        // Anyone else's knocked-out or dead body: the nearest walks over and its inventory opens to take from (Alt + right click takes all at once).
        if (PickDowned(p) is { } body) return ("search", body, () => Say(new Meitou.Simulation.Carrying.OpenLootOrder([], body) { Tick = session.World.Tick }));
        return null;
    }

    public void MouseMove(Vector2 p)
    {
        mouse = p;
        if (EditorPointerMove(p)) return;
        if (ScrollThumbMove(p)) return;
        if (MapPointerMove(p)) return;
        if (UiVisible && faithfulScreens.PointerMove(p)) return;
        if (dragFrom is { } from && !dragging && Vector2.Distance(from, p) >= DragThreshold) dragging = true;
    }

    public void MouseUp(MouseButton button, Vector2 p, bool shift)
    {
        mouse = p;
        EditorPointerUp(button);
        if (button == MouseButton.Left) { InventoryPointerUp(p); JobDragEnd(p); SquadPointerUp(p, shift); faithfulScreens.PointerUp(); mapDrag = null; }
        if (button == MouseButton.Middle) mapDrag = null;
        if (button == MouseButton.Right && MapRightUp(p)) return;
        if (button == MouseButton.Right) { ContextRelease(); SiteRelease(); RockRelease(); BedRelease(); }
        if (button == MouseButton.Right && FormationRelease()) return;
        if (button == MouseButton.Right && steering)
        {
            Steer(final: true);
            steering = false;
        }
        if (button != MouseButton.Left || dragFrom is not { } from) return;
        dragFrom = null;
        if (dragging)
        {
            dragging = false;
            var boxed = InBox(from, p);
            // Meitou: a box over nobody (without Shift) drops the selection.
            if (boxed.Count == 0 && !shift) Say(new SelectCommand([]) { Tick = session.World.Tick, ClearWhenEmpty = true });
            else Select(boxed, shift);
            if (boxed.Count > 0) Inspect(CharacterId.None);
        }
        else
        {
            // A click on another faction's character shows it in the information and medical panels and drops the squad's selection (docs/game/ui-screens.md section 4). Shift keeps the selection, a click on your own or on the ground clears the inspection.
            var hit = Pick(p);
            var other = hit is null && !shift ? PickWhere(p, c => !c.IsPlayer) : null;
            // A building under the cursor (and no character) is selected and the characters keep their selection (docs/game/ui-input.md "Building picking and selection").
            if (hit is null && PickWhere(p, _ => true) is null && PickBuilding(p) is { } building)
            {
                SetBuilding(building);
                Inspect(CharacterId.None);
                return;
            }
            Select(hit is { } id ? new[] { id } : [], shift);
            if (other is { } stranger) Inspect(stranger);
            else if (!shift || hit is not null) Inspect(CharacterId.None);
        }
    }

    /// <summary>Shows <paramref name="target"/> (another faction's character; <see cref="CharacterId.None"/> for nobody) in the HUD panels.</summary>
    void Inspect(CharacterId target)
    {
        if (!target.IsNone) SetBuilding(null);
        Say(new InspectCommand(target) { Tick = session.World.Tick });
    }

    /// <summary>
    /// Once a frame: while the right button is held after a move order, the order is given again at the floor or terrain point under the cursor once every world
    /// tick (also when the camera moves and the cursor does not). <paramref name="final"/> (the button is let go) gives the last point unless this tick
    /// already has the same one.
    /// </summary>
    public void Steer(bool final = false)
    {
        UpdateContextMenu();
        UpdateFactionName();
        UpdateTownName();
        UpdateBed();
        UpdateMap();
        if (!final) UpdateBuild();
        UpdateFormationDrag();
        if (!steering || OverHud(mouse)) return;
        if (Ray(mouse) is not { } ray || MoveHit(ray.Origin, ray.Direction) is not { } hit) return;
        PlaceMarker(hit);   // the marker follows the cursor every frame; the order only once a tick
        if (!final && session.World.Tick == steerTick) return;
        if (final && session.World.Tick == steerTick && hit == steerTarget) return;
        Move(hit, queued: false);
        (steerTarget, steerTick) = (hit, session.World.Tick);
    }

    /// <summary>The character the strategy camera follows (a double tap of its number key, F, or a double click on its squad button); panning the camera lets go.</summary>
    public CharacterId? Follow { get; set; }
    /// <summary>The host's camera flight to a double-clicked squad member (Advanced settings "Character flights"); false: centre on it at once.</summary>
    public Func<CharacterId, bool>? FlyTo { get; set; }
    /// <summary>A third press (a single one while the flight to that member is under way): the host ends the flight on it at once.</summary>
    public Action<CharacterId>? SkipFlight { get; set; }
    // The last number key and when it was pressed: the same key again within DoubleTapSeconds centres on the character (docs/game/ui-input.md, events 100..120).
    Key lastSelectKey;
    long lastSelectAt;
    const double DoubleTapSeconds = 0.5;

    /// <summary>Selects the squad's <paramref name="index"/>th member (<paramref name="shift"/> adds); the second press within half a second centres the camera on it and follows.</summary>
    void SelectMember(int index, bool shift, bool doubleTap)
    {
        var members = ActiveSquadMembers();
        if (index < 0 || index >= members.Count) return;
        Select([members[index]], shift);
        if (!doubleTap) SkipFlight?.Invoke(members[index]);
        else if (FlyTo?.Invoke(members[index]) != true) CentreOn(members[index]);
    }

    /// <summary>Centres the camera on the lead selected character and follows it (after a load, which drops the follow).</summary>
    public void FollowSelected()
    {
        if (Selected().FirstOrDefault() is { } lead) CentreOn(lead.Id);
    }

    void CentreOn(CharacterId id, bool follow = true)
    {
        if (session.CurrentSnapshot.Characters.FirstOrDefault(c => c.Id == id) is not { } c) return;
        var view = session.Camera.Current;
        session.Camera.Place(new Vector2(c.Position.X, c.Position.Z), view.Yaw, view.Pitch, view.Distance);
        Follow = follow ? id : null;
    }
}
