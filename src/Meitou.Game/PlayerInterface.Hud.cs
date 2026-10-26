using System.Numerics;
using Meitou.Data.Ui;
using Meitou.Engine.Time;
using Meitou.Rendering;
using Meitou.Simulation;

namespace Meitou.Game;

/// <summary>The colour of a DIALOGUE log line (the original's <c>MessageLogColor</c>: ML_NORMAL, ML_PLAYER, ML_SYSTEM).</summary>
enum MessageTone { Normal, Player, System }

/// <summary>The minimal HUD: the clock, the speed buttons, the selection panel, rings, paths and the drag box.</summary>
sealed partial class PlayerInterface
{

    /// <summary>Pause, then the clock's speeds.</summary>
    static readonly double[] ButtonSpeeds = [0, .. SimulationClock.Speeds];
    const float Margin = 10, Pad = 6;

    public float HudBottom => 48;

    // ---- notices (the original's MessageRoller, docs/game/ui-screens.md "Messages") ----

    /// <summary>The seconds a message holds at full alpha, then the alpha it loses per second (<b>Verified</b>).</summary>
    /// <summary>Seconds a message line shows at full strength, then fades over <see cref="NoticeFadeSeconds"/>; at most <see cref="NoticeLines"/> show at once.</summary>
    const float NoticeHoldSeconds = 5, NoticeFadeSeconds = 1;
    const int NoticeLines = 5;

    /// <summary>A line of the message feed: its text, red when an error, how many times it came while shown, and its age.</summary>
    sealed class NoticeLine(string text, bool error)
    {
        public string Text { get; } = text;
        public bool Error { get; } = error;
        public int Count { get; set; } = 1;
        public float Age { get; set; }
    }

    readonly List<NoticeLine> notices = [];
    long noticeAt;

    /// <summary>
    /// A message for the feed ("Discovered {1}", "Saved", a crime, an error in red; Meitou's own: the game's big centre bar showed one at a time for 4 s each, and a burst queued up): a
    /// small line at the bottom left, the newest lowest; the same text again while it shows counts up ("x3") instead of adding a line.
    /// </summary>
    public void Notice(string text, bool error = false)
    {
        string line = Loc.Tr(text).ReplaceLineEndings(" ");
        LogMessage("", line, MessageTone.System);
        if (notices.FirstOrDefault(n => n.Text == line && n.Error == error) is { } same)
        {
            same.Count++;
            same.Age = 0;
            notices.Remove(same);
            notices.Add(same);
            return;
        }
        notices.Add(new NoticeLine(line, error));
        if (notices.Count > NoticeLines) notices.RemoveAt(0);
    }

    /// <summary>Adds a line to the Overview's DIALOGUE log only ("owner: text", docs/game/ui-screens.md "Dialogue log"); the newest 200 are kept.</summary>
    public void LogMessage(string owner, string text, MessageTone tone)
    {
        uiMessages.Add((owner.Length == 0 ? text : $"{owner}: {text}", tone));
        if (uiMessages.Count > 200) uiMessages.RemoveAt(0);
    }

    /// <summary>
    /// The DIALOGUE log as the text box's text: each line after the colour tag of its tone (the original's MessageLogColor: #000000, <c>DialogueTextOption</c>, #c6ac6a),
    /// its own <c>#</c> doubled. The Meitou mode's dark panel takes the kit's text colour for the black.
    /// </summary>
    string MessagesText()
    {
        string normal = UiMode == UiMode.Meitou ? Tag(MainColour) : "#000000", player = Tag(MyGuiColours.Named("DialogueTextOption", "b3b3b3"));
        return string.Join('\n', uiMessages.Select(m => (m.Tone switch { MessageTone.Player => player, MessageTone.System => "#c6ac6a", _ => normal })
            + m.Text.Replace("#", "##", StringComparison.Ordinal)));
    }

    /// <summary>A MyGUI "r g b" colour as a <c>#rrggbb</c> tag.</summary>
    static string Tag(string rgb)
    {
        var c = MyGuiColours.Parse(rgb, Vector4.One);
        return $"#{(int)MathF.Round(c.X * 255):x2}{(int)MathF.Round(c.Y * 255):x2}{(int)MathF.Round(c.Z * 255):x2}";
    }

    /// <summary>Once a frame: the lines age; the faded ones go.</summary>
    void PollNotice()
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        float dt = noticeAt == 0 ? 0 : MathF.Min((float)System.Diagnostics.Stopwatch.GetElapsedTime(noticeAt, now).TotalSeconds, 0.25f);
        noticeAt = now;
        foreach (var n in notices) n.Age += dt;
        notices.RemoveAll(n => n.Age >= NoticeHoldSeconds + NoticeFadeSeconds);
    }

    /// <summary>
    /// The feed: one line each on a faint dark strip, small type, left-aligned, stacked up from just above the HUD's information panel (bottom left); it never takes the mouse.
    /// </summary>
    void AddNotice(List<UiControl> controls)
    {
        if (notices.Count == 0) return;
        float s = Height / 1080f, line = MathF.Round(20 * s), pad = MathF.Round(6 * s), x = MathF.Round(8 * s);
        float width = MathF.Min(MathF.Round(Width * 0.2f), MathF.Round(420 * s));
        // Just above the top of the character panel as it is drawn this frame (its name bar, or the bounty bar over it; the panel slides with the extended stats).
        float top = controls.Where(c => c.Name is "NamePanel" or "BountyPanel").Select(c => c.Bounds.Y).DefaultIfEmpty(Height).Min();
        float bottom = MathF.Min(top, Height) - MathF.Round(6 * s);
        for (int k = notices.Count - 1, row = 0; k >= 0; k--, row++)
        {
            var n = notices[k];
            float alpha = n.Age <= NoticeHoldSeconds ? 1 : Math.Clamp(1 - (n.Age - NoticeHoldSeconds) / NoticeFadeSeconds, 0, 1);
            var strip = new UiRect(x, bottom - (row + 1) * (line + MathF.Round(2 * s)), width, line);
            string text = n.Count > 1 ? $"{n.Text}  x{n.Count}" : n.Text;
            controls.Add(new UiControl($"notice-strip-{row}", "Widget", strip, "", "", "", true, new Dictionary<string, string>(), new Vector4(0.05f, 0.04f, 0.03f, 0.55f * alpha), Pickable: false));
            var colour = n.Error ? new Vector4(1f, 0.45f, 0.38f, alpha) : new Vector4(0.92f, 0.88f, 0.78f, alpha);
            controls.Add(new UiControl($"notice-text-{row}", "TextBox", strip with { X = strip.X + pad, Width = strip.Width - 2 * pad }, text, "", "", true,
                new Dictionary<string, string> { ["FontName"] = "Kenshi_StandardFont_Small", ["TextAlign"] = "Left VCenter" }, Vector4.Zero, colour, Pickable: false, Clip: strip));
        }
    }

    /// <summary>Drops the transient state of the mouse (a drag box, a steering right click): the world it belonged to is gone.</summary>
    public void Reset()
    {
        floaters.Clear();
        dragFrom = null;
        dragging = false;
        steering = false;
        CancelFormationDrag();
        mapDrag = null;
        (held, liftPress, lifting, heldSerial) = (null, null, null, 0);
        selectedFor = null;
        selected = [];
        selectedBuilding = null;
        Follow = null;
        CloseContextMenu();
    }

    bool OverHud(Vector2 p) => ContainsUi(p);

    void PressButton(int index)
    {
        if (index == 0) session.Simulation.TogglePause();
        else session.Simulation.SetSpeed(ButtonSpeeds[index]);
    }

    /// <summary>The selected characters of the current snapshot, found once per snapshot (the HUD box is asked for on every hit test).</summary>
    IReadOnlyList<CharacterSnapshot> Selected()
    {
        var snapshot = session.CurrentSnapshot;
        if (!ReferenceEquals(snapshot, selectedFor))
        {
            selected = [.. snapshot.Characters.Where(c => c.Selected)];
            inspected = snapshot.Characters.FirstOrDefault(c => c.Inspected);
            selectedFor = snapshot;
        }
        return selected;
    }

    WorldSnapshot? selectedFor;
    IReadOnlyList<CharacterSnapshot> selected = [];
    CharacterSnapshot? inspected;

    /// <summary>The character the HUD panels describe: the other faction's character last clicked, else the first selected one (docs/game/ui-screens.md section 4). The selection stays whoever is inspected.</summary>
    CharacterSnapshot? Lead() => Selected().FirstOrDefault() is { } first ? inspected ?? first : inspected;

    /// <summary>Draw the green ring under each selected character (Advanced → Selection rings).</summary>
    public bool SelectionRings { get; set; } = true;

    /// <summary>The HUD, the selection rings and the drag box, over the finished picture.</summary>
    public void Draw(DebugOverlay o, int width, int height, DebugOverlay.OverlayPass? pass = null)
    {
        PollUiSounds();
        Width = width;
        Height = height;
        PollBiome();
        PollNotice();
        PollProspect();
        var white = DebugOverlay.TextColour;
        var green = new Vector4(0.35f, 1f, 0.45f, 1f);
        var red = new Vector4(1f, 0.4f, 0.35f, 1f);

        // Rings under the selected characters.
        foreach (var c in session.CurrentSnapshot.Characters)
        {
            if (!c.Selected || EditorOpen || !SelectionRings) continue;
            var centre = where(c);
            // A swimmer is drawn that far down (Speed.WadingHeight) so its body lies in the water: its ring stays on the surface.
            if (centre.Y <= Meitou.Data.World.WorldWater.Height - Meitou.Simulation.Bodies.Speed.DeepWaterDepth + 0.01f) centre.Y = Meitou.Data.World.WorldWater.Height;
            Vector2? previous = null, first = null;
            for (int i = 0; i <= 24; i++)
            {
                float a = i * MathF.Tau / 24;
                var point = centre + new Vector3(MathF.Sin(a) * 1.6f, 0.15f, MathF.Cos(a) * 1.6f);
                var s = Project(point);
                if (s is null) { previous = null; continue; }
                if (previous is { } prev) o.Line(prev.X, prev.Y, s.Value.X, s.Value.Y, 2, green);
                first ??= s;
                previous = s;
            }
        }

        UpdateStealthArrows();
        DrawFloaters(o);
        FadeMarker();

        // The box.
        if (dragging && dragFrom is { } from)
        {
            var edge = new Vector4(0.6f, 1f, 0.6f, 1);
            o.Rect(MathF.Min(from.X, mouse.X), MathF.Min(from.Y, mouse.Y), MathF.Max(from.X, mouse.X), MathF.Max(from.Y, mouse.Y), new Vector4(0.4f, 1f, 0.5f, 0.12f));
            o.Line(from.X, from.Y, mouse.X, from.Y, 1, edge);
            o.Line(mouse.X, from.Y, mouse.X, mouse.Y, 1, edge);
            o.Line(mouse.X, mouse.Y, from.X, mouse.Y, 1, edge);
            o.Line(from.X, mouse.Y, from.X, from.Y, 1, edge);
        }

        DrawGameUi(o, width, height, pass);
        o.Flush(width, height, null, pass);
    }
}
