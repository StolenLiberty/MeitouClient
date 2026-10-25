using System.Globalization;
using System.Numerics;
using Meitou.Data.Ui;
using Meitou.Simulation;
using Meitou.Simulation.Items;
using Meitou.Simulation.Production;
using Meitou.Ui;

namespace Meitou.Game;

/// <summary>
/// Production in the UI (docs/simulation.md "Production", docs/game/ui-screens.md "Selected building"):
/// <list type="bullet">
/// <item>Selecting a complete machine of the player's opens its window beside the inventory: a well or farm the original's <c>Kenshi_GenericBuildingWindow</c> (inputs in <c>in1</c> / <c>in2</c>,
/// the output in <c>out</c>, their bars and texts), a research bench <c>Kenshi_InventoryResearchWindow</c> (its storage grid; the RESEARCH button opens the overview's research tab).
/// Items are held with the mouse between the windows and the selected character's inventory (PlayerInterface.Inventory.cs), or moved with a right click (<see cref="PutInMachine"/>,
/// <see cref="TakeFromMachine"/>).</item>
/// <item>The site menu has Operate for a machine characters work; a quick right click on one gives it.</item>
/// <item>The overview's research tab: TYPES lists the categories, BLUEPRINTS the research of the selected category that can be queued (a click queues it), QUEUE the queue (a click
/// takes one off), DESCRIPTION the hovered or last clicked research. The original's row widgets and colours are <b>Unknown</b>; Meitou draws plain text rows.</item>
/// </list>
/// </summary>
sealed partial class PlayerInterface
{
    /// <summary>The site whose machine window is shown (null: none), and a site whose window the player closed (not reopened until the selection changes).</summary>
    string? shownMachine, closedMachine;

    /// <summary>The machine window placed beside the inventory by an order (a right click) and where it stood before; it goes back there when the inventory closes, as the original's does.</summary>
    (string Screen, Vector2 Offset)? machineHome;
    string? researchCategory, researchFocus;

    MachineSnapshot? Machine(string? site) => site is null ? null : session.CurrentSnapshot.Machines.FirstOrDefault(m => m.Site == site);

    /// <summary>The storage window's layout pixels above its grid (caption and item heading) and in all around it, with the two-line Max Capacity text below (measured on the original's window).</summary>
    const float StorageAbove = 86, StorageChrome = 150;

    static string MachineScreen(MachineSnapshot m) => m.Storage is null || m.Inputs.Count > 0 || m.Output is not null ? "building" : m.IsStorage && !m.IsContainer && m.TechLevel == 0 ? "storage" : "bench";

    /// <summary>Opens the selected machine's window and closes it when the selection moves on.</summary>
    void SyncMachineScreens()
    {
        if (faithfulScreens is null) return;
        var focus = BuildingFocus();
        // A rock's natural site is no registry building: its machine, once mined, is enough.
        var m = focus?.SiteId is { } id && (ProductionSystem.IsNatural(id) || session.CurrentSnapshot.Buildings.FirstOrDefault(s => s.Id == id) is { Complete: true }) ? Machine(id) : null;
        if (m is not null && m.Inputs.Count == 0 && m.Output is null && m.Storage is null && m.Craft is null) m = null;
        // An open container takes the window.
        m = session.CurrentSnapshot.Machines.FirstOrDefault(c => c.IsContainer) ?? m;
        if (closedMachine is not null && closedMachine != m?.Site) closedMachine = null;
        if (shownMachine is not null && (m is null || m.Site != shownMachine))
        {
            faithfulScreens.Close("building");
            faithfulScreens.Close("bench");
            faithfulScreens.Close("storage");
            shownMachine = null;
            storageWithInventory = null;
            machineHome = null;
        }
        if (machineHome is { } home && !faithfulScreens.IsOpen("inventory"))
        {
            if (faithfulScreens.IsOpen(home.Screen)) faithfulScreens.SetOffset(home.Screen, home.Offset);
            machineHome = null;
        }
        if (shownMachine is not null && Machine(shownMachine) is { } shown && !faithfulScreens.IsOpen(MachineScreen(shown)))
        {
            if (shown.IsContainer) Say(new Meitou.Simulation.Items.CloseContainer { Tick = session.World.Tick });
            closedMachine = shownMachine;
            shownMachine = null;
            storageWithInventory = null;
            machineHome = null;
            return;
        }
        if (m is null || shownMachine == m.Site || closedMachine == m.Site) return;
        shownMachine = m.Site;
        string screen = MachineScreen(m);
        inventoryScroll.Remove(screen);
        // The character's inventory opens with an order-driven container window (the original's order 26 opens the looter's own window and the target's together) and with a machine's; selecting
        // a selected machine, storage, bench or mined rock shows its window alone (the original's windows of a selected ore storage and electrical workbench have no inventory beside them).
        bool alone = !m.IsContainer && storageWithInventory != m.Site;
        if (!alone && Selected().Count > 0 && !faithfulScreens.IsOpen("inventory")) faithfulScreens.Open("inventory");
        faithfulScreens.Open(screen);
        if (!alone) machineHome = (screen, faithfulScreens.GetOffset(screen));
        PlaceInventoryBeside(screen, left: false);
    }

    void BindMachineScreen(string id, UiBindings b)
    {
        var m = Machine(shownMachine);
        if (id == "storage")
        {
            // The original's generic container window: caption STORAGE, the limited item as heading, and Max Capacity (red "(Full)" once the stack is full).
            b.Text["Root"] = "STORAGE";
            b.Text["TypeText"] = m?.StorageLimitName ?? "";
            if (m is { StorageCapacity: > 0 } cap)
            {
                bool full = m.Storage?.Items.FirstOrDefault()?.Quantity >= cap.StorageCapacity;
                b.Text["CapacityText"] = $"Max Capacity: {cap.StorageCapacity.ToString("N0", CultureInfo.InvariantCulture)}" + (full ? " (Full)" : "");
                // The original paints the whole line red once the stack is full (the exact red is Unknown; read off its screenshot).
                if (full) b.Properties["CapacityText"] = new Dictionary<string, string> { ["TextColour"] = "0.91 0.25 0.25" };
            }
            else b.Hidden.Add("CapacityText");
            b.Disabled.Remove("ArrangeButton");
            b.Hidden.Add("ArrangeButton");
            if (m?.Storage is { } sg)
                b.Positions["Root"] = new(0, 0, sg.Width * InventoryCell + 40 * InventoryScale, Math.Min(Height * 0.8f, sg.Height * InventoryCell + StorageChrome * InventoryScale));
            b.TemplateScale = InventoryScale;
            return;
        }
        b.Text["Root"] = (m is { IsContainer: true } ? buildHost?.ItemName(m.Record) ?? "CONTAINER" : selectedBuilding?.Name ?? "BUILDING").ToUpperInvariant();
        if (id == "bench")
        {
            b.Disabled.Remove("ResearchButton");
            b.Actions["ResearchButton"] = "ResearchButton";
            if (m is not { TechLevel: > 0 }) b.Hidden.Add("ResearchButton");
            b.Disabled.Remove("ArrangeButton");
            b.Hidden.Add("ArrangeButton");
            if (m?.Storage is { } grid)
                b.Positions["Root"] = new(0, 0, grid.Width * InventoryCell + 40 * InventoryScale, Math.Min(Height * 0.8f, grid.Height * InventoryCell + 108 * InventoryScale));
            b.TemplateScale = InventoryScale;
            return;
        }
        if (m?.Craft is { } craft)
        {
            b.Text["CraftingName"] = craft.Queue is [var (_, current, progress, _), ..] ? $"{current}  {(int)(progress * 100)}%" : "Nothing queued";
            b.Disabled.Remove("QueueButton");
            b.Actions["QueueButton"] = "QueueButton";
        }
        else b.Hidden.Add("CraftingPanel");
        b.TemplateScale = InventoryScale;
        if (m is null) return;
        for (int k = 0; k < 2; k++)
        {
            string n = (k + 1).ToString(CultureInfo.InvariantCulture);
            if (k >= m.Inputs.Count) { b.Hidden.Add($"Input{n}Panel"); continue; }
            var input = m.Inputs[k];
            b.Text[$"Input{n}Text"] = input.Name;
            b.Text[$"Input{n}StatusText"] = $"{input.Count.ToString(CultureInfo.InvariantCulture)}/{input.Capacity.ToString(CultureInfo.InvariantCulture)}";
            b.Progress[$"Input{n}ProgressBar"] = input.Fill;
        }
        if (m.Output is { } output)
        {
            b.Text["OutputText"] = $"{output.Name}\n{output.Count.ToString(CultureInfo.InvariantCulture)}/{output.Capacity.ToString(CultureInfo.InvariantCulture)}";
            b.Progress["OutputProgressBar"] = m.Farm is { } farm ? farm.Ripeness : output.Fill;
        }
        else if (m.Craft is { } done)
        {
            b.Text["OutputText"] = done.Done is [var first, ..] ? $"{first.Name}\n{done.Done.Sum(d => d.Quantity).ToString(CultureInfo.InvariantCulture)}" : "";
            b.Progress["OutputProgressBar"] = done.Queue is [var (_, _, p, _), ..] ? p : 0;
        }
        else b.Hidden.Add("OutputColumn");
        if (m.Inputs.Count == 0) b.Hidden.Add("InputTitleText");
    }

    void AddMachineContent(string id, List<UiControl> controls)
    {
        if (Machine(shownMachine) is not { } m) return;
        if (id is "bench" or "storage")
        {
            if (m.Storage is not { } grid) return;
            if (id == "storage") ArrangeContainerGrid(id, controls, grid.Width, grid.Height, StorageAbove, StorageChrome - StorageAbove - 6);
            else ArrangeContainerGrid(id, controls, grid.Width, grid.Height);
            if (controls.FirstOrDefault(c => c.Name == "backpack_content") is not { } slot) return;
            var area = slot.Bounds;
            foreach (var item in grid.Items)
            {
                if (Lifted(ItemOwner.AtSite(m.Site), 1000 + item.Index)) continue;
                AddItemCell(controls, $"machine-item-{item.Index}",
                    new(area.X + item.X * area.Width / grid.Width, area.Y + item.Y * area.Height / grid.Height, item.Width * area.Width / grid.Width, item.Height * area.Height / grid.Height),
                    item.Record, item.Material, item.Name, item.Quantity, ItemTip.Of(item), "MachineItem", 1000 + item.Index,
                    slot.Bounds.Intersect(slot.Clip ?? slot.Bounds), charge: item.Charge, rotated: item.Rotated);
            }
            return;
        }
        void Slot(string name, string bar, MachineSlotView view, int code)
        {
            int index = controls.FindIndex(c => c.Name == name);
            if (index < 0) return;
            // The original makes the section the recipe item's footprint (a box of building material is 6 x 3 cells); the grid keeps the layout's top and takes the progress bar's horizontal centre (the layout slot is not centred in its column), so it stays under its label and bar.
            // A grid wider than the bar keeps the slot's left edge, so it overflows only to the right as before rather than into the column on its left.
            var slot = controls[index];
            var barRoot = controls.FirstOrDefault(c => c.Name == bar);
            float width = view.Width * InventoryCell;
            var column = barRoot?.Bounds ?? slot.Bounds;
            float x = width <= column.Width ? column.X + (column.Width - width) / 2 : slot.Bounds.X;
            var b = slot.Bounds with { X = x, Width = width, Height = view.Height * InventoryCell };
            // The grid and a moved bar may leave their content panel, so they clip to the window instead.
            var clip = controls.FirstOrDefault(c => c.Type == "Window")?.Bounds ?? slot.Clip;
            controls[index] = slot with
            {
                Bounds = b,
                Clip = clip,
                Properties = new Dictionary<string, string>(slot.Properties) { ["TileSize"] = FormattableString.Invariant($"{InventoryCell} {InventoryCell}"), ["State"] = "normal" },
            };
            // Engine choice: a grid taller than the room above the progress bar pushes the bar down under it.
            if (barRoot is { } root && b.Y + b.Height + 4 * InventoryScale - root.Bounds.Y is var dy and > 0)
                for (int i = 0; i < controls.Count; i++)
                    if (controls[i].Name == bar || controls[i].Name == bar + "@track" || controls[i].Properties.GetValueOrDefault("TemplateOwner") == bar)
                        controls[i] = controls[i] with { Bounds = controls[i].Bounds with { Y = controls[i].Bounds.Y + dy }, Clip = clip };
            if (view.Count <= 0 || Lifted(ItemOwner.AtSite(m.Site), code)) return;
            AddItemCell(controls, $"machine-{name}", b, view.Item, "", view.Name, view.Count, null, "MachineItem", code, clip);
        }
        for (int k = 0; k < Math.Min(2, m.Inputs.Count); k++) Slot($"in{k + 1}", $"Input{k + 1}ProgressBar", m.Inputs[k], 10 + k);
        if (m.Output is { } output) Slot("out", "OutputProgressBar", output, 8);
        else if (m.Craft is { Done: [var made, ..] } && controls.FirstOrDefault(c => c.Name == "out") is { } outSlot && !Lifted(ItemOwner.AtSite(m.Site), 8))
        {
            var ob = outSlot.Bounds;
            float side = MathF.Min(ob.Width, ob.Height);
            AddItemCell(controls, "machine-out", new(ob.X, ob.Y, side, side), made.Record, made.Material, made.Name, made.Quantity, null, "MachineItem", 8, outSlot.Clip);
        }
    }

    void MachineAction(MyGuiEvent e)
    {
        if (e.Screen == "bench" && e.Widget == "ResearchButton") { faithfulScreens.Open("overview"); faithfulScreens.SetTab("overview", "TabsMain", 2); }
        else if (e.Screen == "building" && e.Widget == "QueueButton" && shownMachine is not null) OpenCraftTab(shownMachine);
    }

    /// <summary>
    /// The verb for a complete machine characters work: the original's Use (order 87, <c>FUN_14079b2d0</c>); with Shift held on the click it becomes the Operating machine job, as the
    /// original's dispatch turns a menu order into a job (<c>FUN_1407fa650</c>).
    /// </summary>
    void AddMachineVerb(List<(string Caption, Action Give)> verbs, Meitou.Simulation.Construction.BuildingSnapshot site, IReadOnlyList<CharacterId> selection)
    {
        // An automatic machine: Auto-Haul (152) in place of Use; with Shift the 152 job (its companion 124 follows), else a one-shot haul round (the operator only hauls there).
        if (site.Complete && Machine(site.Id) is { Operable: true, IsAutomatic: true })
            verbs.Add(("Auto-Haul", () => Say(SlowModifier
                ? (Meitou.Simulation.SimCommand)new AddJob(selection, Meitou.Data.Gameplay.Ai.TaskType.OPERATE_AUTOMATIC_MACHINERY, site.Id) { Tick = session.World.Tick }
                : new OperateOrder(selection, site.Id) { Tick = session.World.Tick })));
        else if (site.Complete && Machine(site.Id) is { Operable: true })
            verbs.Add(("Use", () => Say(SlowModifier
                ? (Meitou.Simulation.SimCommand)new AddJob(selection, Meitou.Data.Gameplay.Ai.TaskType.OPERATE_MACHINERY, site.Id) { Tick = session.World.Tick }
                : new OperateOrder(selection, site.Id) { Tick = session.World.Tick })));
        // A training dummy: Training (97 USE_TRAINING_DUMMY, UseableStuff slot 131 for function 7), which Shift turns into the Training job.
        else if (site.Complete && Machine(site.Id) is { Function: Meitou.Data.Gameplay.Construction.MachineData.TrainingFunction })
            verbs.Add(("Training", () => Say(SlowModifier
                ? (Meitou.Simulation.SimCommand)new AddJob(selection, Meitou.Data.Gameplay.Ai.TaskType.USE_TRAINING_DUMMY, site.Id) { Tick = session.World.Tick }
                : new OperateOrder(selection, site.Id) { Tick = session.World.Tick })));
    }

    /// <summary>
    /// The first stats line of a mine, farm, well or rock (<c>ProductionBuilding::getGUIFertility</c>, docs/game/ui-screens.md "Selected building"): the resource's label, the yield as
    /// a whole percent without the sign, red to green (r = 0.8 - y, g = 0.8 y, b = 0.1), and the tooltip naming the resource in lower case.
    /// </summary>
    static void AddRichnessLine(Meitou.Simulation.Production.RichnessView rich, List<HudLine> stats)
    {
        (string Label, string Name) = rich.Resource switch
        {
            1 => ("Iron Quality:", "Iron"), 2 => ("Stone Richness:", "Stone"), 3 => ("Copper Quality:", "Copper"), 4 => ("Carbon Quality:", "Carbon"),
            5 => ("Water Available:", "Water"), _ => ("Ground Fertility:", "Fertility"),
        };
        static int Byte(float v) => (int)(Math.Clamp(v, 0, 1) * 255 + 0.5f);
        string colour = $"{Byte(0.8f - rich.Yield):x2}{Byte(0.8f * rich.Yield):x2}{Byte(0.1f):x2}";
        stats.Add(new(Label, ((int)(rich.Yield * 100)).ToString(CultureInfo.InvariantCulture), MainColour, colour,
            Loc.Tr("The richness of natural {1} present in this location. Anything over 50% is considered good.", Loc.Tr(Name).ToLower(CultureInfo.CurrentCulture))));
    }

    /// <summary>Miles per hour per world unit per second (the original's 0.22369362: a unit is a metre).</summary>
    const float MphPerUnit = 0.22369362f;

    /// <summary>The stats lines of a selected machine: the farm's crop and growth, the output's progress, workers and the bench's tech level.</summary>
    void AddMachineLines(MachineSnapshot m, List<HudLine> stats, ref string status, ref bool bad)
    {
        string N(int v) => v.ToString(CultureInfo.InvariantCulture);
        string P(float v) => ((int)(Math.Clamp(v, 0, 1) * 100)).ToString(CultureInfo.InvariantCulture) + "%";
        if (m.Farm is { } f)
        {
            status = f.Status switch { FarmStatus.NoWater => "No water", FarmStatus.OutputFull => "Output full", FarmStatus.Harvest => "Ready to harvest", _ => "Growing" };
            bad = f.Status is FarmStatus.NoWater or FarmStatus.OutputFull;
            stats.Add(new("Growth:", P(MathF.Min(f.Growth, 1)), MainColour));
            if (f.Growth >= 1) stats.Add(new("Ripeness:", P(f.Ripeness), MainColour, f.Growth >= 2 ? BadColour : GoodColour));
            if (f.Death > 0) stats.Add(new("Dying:", P(f.Death), MainColour, BadColour));
        }
        else if (m.Output is { } o)
        {
            status = m.Status switch { 2 => "Working", 1 => "Output full", _ => "Idle" };
            bad = m.Status == 1;
            stats.Add(new("Output:", $"{o.Name} {N(o.Count)}/{N(o.Capacity)}", MainColour));
        }
        // Power, in the original's words where it has them (GeneratorBuilding vfunc_114, vfunc_153); "No power" and "Low power" are Meitou's.
        if (m.Power is { } pw)
        {
            string F(float v) => v.ToString("0.#", CultureInfo.InvariantCulture);
            if (pw.Kind == PowerKind.Battery)
            {
                status = pw.Granted > 0 ? "Charging" : pw.Granted < 0 ? "Draining" : pw.Charge <= 0 ? "Empty" : "Stored";
                bad = pw.Charge <= 0;
                stats.Add(new("Charge:", P(pw.Store > 0 ? pw.Charge / pw.Store : 0), MainColour));
            }
            else if (m.Wind is { } wind)
            {
                // WindGeneratorBuilding::getGUIState: the production state, then "No Wind" while the share is 0; getGUIPower section 6: the wind speed at the building.
                status = wind.Share <= 0 ? "No Wind" : "Running";
                bad = wind.Share <= 0;
                stats.Add(new("Wind Speed", Loc.Tr("{1,num=fix,p=1} mph", wind.Speed * MphPerUnit), MainColour, Tooltip: "The current wind speed"));
            }
            else if (pw.Kind == PowerKind.Generator)
            {
                status = pw.Fueled ? "Running" : "Out of fuel";
                bad = !pw.Fueled;
                stats.Add(new("Power:", F(pw.Demand), MainColour));
                if (m.Inputs.Count > 0) stats.Add(new("Fuel Consumption", P(pw.Usage), MainColour));
            }
            else if (pw.Demand > 0)
            {
                if (pw.Granted < pw.Demand) { status = pw.Granted <= 0 ? "No power" : "Low power"; bad = true; }
                stats.Add(new("Power:", $"{F(pw.Granted)}/{F(pw.Demand)}", MainColour, pw.Granted < pw.Demand ? BadColour : null));
            }
        }
        if (m.TechLevel > 0) stats.Add(new("Tech level:", N(m.TechLevel), MainColour));
        // FUN_1403075b0: an automatic machine (raw max operators 0) reads "-".
        if (m.IsAutomatic) stats.Add(new("Workers:", "-", MainColour, Tooltip: "This machine is fully automatic."));
        else if (m.MaxOperators > 0) stats.Add(new("Workers:", $"{N(m.Operators)}/{N(m.MaxOperators)}", MainColour));
    }

    // ---- the overview's research tab ----

    void BindResearchTab(UiBindings b)
    {
        var research = session.CurrentSnapshot.Research;
        var categories = research.Available.Select(r => r.Category).Distinct(StringComparer.Ordinal).ToList();
        if (researchCategory is null || !categories.Contains(researchCategory)) researchCategory = categories.FirstOrDefault();
        b.Lists["ResearchTypeList"] = [.. categories.Select(c => new UiListItem(c, c))];
        b.SelectedItems["ResearchTypeList"] = researchCategory is null ? -1 : categories.IndexOf(researchCategory);
        string message = research.BenchLevel <= 0 ? "Research needs a research bench." : research.Queue.Count == 0 ? "Nothing being researched." : "";
        if (message.Length > 0) b.Text["ResearchBenchMessage"] = message;
        else b.Hidden.Add("ResearchBenchMessage");
    }

    void ResearchAction(MyGuiEvent e)
    {
        if (e.Widget == "ResearchTypeList" && e.Action == "select") researchCategory = e.Text;
        else if (e.Action.StartsWith("research-queue:", StringComparison.Ordinal))
        {
            researchFocus = e.Action[15..];
            Say(new QueueResearch(researchFocus) { Tick = session.World.Tick });
        }
        else if (e.Action.StartsWith("research-cancel:", StringComparison.Ordinal)) Say(new CancelResearch(e.Action[16..]) { Tick = session.World.Tick });
    }

    /// <summary>The blueprint rows, the queue rows and the description, drawn into the research tab's panels.</summary>
    void AddResearchContent(List<UiControl> controls)
    {
        if (faithfulScreens.GetTab("overview", "TabsMain") != 2) return;
        var research = session.CurrentSnapshot.Research;
        var face = new Dictionary<string, string> { ["FontName"] = "Kenshi_StandardFont_Medium", ["TextAlign"] = "Left VCenter" };
        float line = MathF.Max(18, 24 * InventoryScale);
        var ink = new Vector4(0.86f, 0.82f, 0.71f, 1);
        if (controls.FirstOrDefault(c => c.Name == "BlueprintsAvailablePanel") is { } blueprints)
        {
            var rows = research.Available.Where(r => r.Category == researchCategory).OrderBy(r => r.Level).ThenBy(r => r.Name, StringComparer.Ordinal).ToList();
            for (int i = 0; i < rows.Count; i++)
            {
                var r = rows[i];
                bool queued = research.Queue.Any(q => q.Id == r.Id);
                string caption = $"{r.Name}  ({Loc.Tr("lvl {1,num}", r.Level)})" + (r.Progress > 0 ? $"  {(int)(r.Progress * 100)}%" : "");
                var colour = queued ? new Vector4(0.6f, 0.6f, 0.6f, 1) : r.BenchTooLow || !r.Affordable ? new Vector4(0.75f, 0.35f, 0.3f, 1) : ink;
                controls.Add(new($"research-row-{i}", "Button", new(blueprints.Bounds.X + 6, blueprints.Bounds.Y + i * line, blueprints.Bounds.Width - 12, line), caption,
                    queued ? "" : "research-queue:" + r.Id, "", !queued, face, Vector4.Zero, TextColour: colour, HoverColour: new(0, 1, 0, 1), Pickable: true, Clip: blueprints.Bounds));
            }
        }
        if (controls.FirstOrDefault(c => c.Name == "ResearchQueueList") is { } queue)
            for (int i = 0; i < research.Queue.Count; i++)
            {
                var (qid, name, progress) = research.Queue[i];
                controls.Add(new($"research-queue-{i}", "Button", new(queue.Bounds.X + 6, queue.Bounds.Y + i * line, queue.Bounds.Width - 12, line),
                    $"{name}  {(int)(progress * 100)}%", "research-cancel:" + qid, "", true, face, Vector4.Zero, TextColour: i == 0 ? new(0.66f, 0.72f, 0.45f, 1) : ink,
                    HoverColour: new(1, 0.4f, 0.3f, 1), Pickable: true, Clip: queue.Bounds));
            }
        var hovered = controls.LastOrDefault(c => c.Name.StartsWith("research-row-", StringComparison.Ordinal) && c.Bounds.Contains(mouse.X, mouse.Y))?.Action;
        string? focus = hovered is { Length: > 15 } h ? h[15..] : researchFocus;
        if (focus is null || research.Available.FirstOrDefault(r => r.Id == focus) is not { Id: not null } option) return;
        var lines = new List<HudLine>
        {
            new(option.Name, ""),
            new("Level:", option.Level.ToString(CultureInfo.InvariantCulture), MainColour, option.BenchTooLow ? BadColour : null),
            new("Time:", $"{option.Hours.ToString("0.#", CultureInfo.InvariantCulture)} h", MainColour),
        };
        foreach (var (item, count) in option.Cost) lines.Add(new(item, count.ToString(CultureInfo.InvariantCulture), MainColour, option.Affordable ? null : BadColour));
        AddLines(controls, "ResearchDescriptionPanel1", lines, line);
        if (option.Description.Length > 0 && controls.FirstOrDefault(c => c.Name == "ResearchDescriptionPanel2") is { } text)
            controls.Add(new("research-description", "EditBox", text.Bounds, option.Description, "", "", true,
                new Dictionary<string, string> { ["FontName"] = "Kenshi_StandardFont_Medium", ["WordWrap"] = "true", ["MultiLine"] = "true", ["Static"] = "true", ["TextAlign"] = "Left Top" },
                Vector4.Zero, TextColour: ink, Pickable: false, Clip: text.Bounds));
    }
}
