using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;
using Meitou.Data;
using Meitou.Data.Save;
using Meitou.Data.World;
using Meitou.Simulation;
using Meitou.Simulation.Saving;

namespace Meitou.Game;

/// <summary>
/// Quicksave (F5) and quickload (F9), docs/simulation.md "Saves". The key only sets a request (a control tick runs inside <c>WorldSession.Advance</c>); the frame loop takes
/// the request right after <c>Advance</c> returns, when no world tick is in flight. A save captures the world there, on the thread that ticks it (a few milliseconds), and writes
/// the files on a worker; a load reads the files on a worker and swaps in a new world there. The slot is Meitou's own <c>%LOCALAPPDATA%\Meitou\save\quicksave</c>
/// (<see cref="QuickSave"/>), never a folder of the original game.
/// </summary>
sealed partial class GameHost
{
    bool quickSaveRequested, quickLoadRequested;
    string? saveRequestedFolder, loadRequestedFolder, readingFolder;
    (string Folder, ImportOptions Options)? importRequested;
    Task? saveWrite;

    // What the world was loaded from: the next capture carries the save's records the world does not model through (null for a world that started new).
    LoadedSave? loadedSave;
    // Counts the worlds the host has had: part of a character's key in the draw list, so a new world's characters never take over the renderer's per-character state of the old one's.
    int worldEpoch;
    // Results of the write worker, shown on the next frame (the HUD is drawn by the main thread only).
    readonly ConcurrentQueue<(string Text, bool Error)> saveNotices = [];
    IReadOnlyList<string>? saveDataFiles;
    // The save a capture starts from (a copy of the loaded one, or a new one from the data) costs tens of milliseconds and holds nothing of the world: it is made ahead on a worker,
    // so the capture at the tick boundary only reads the squad (a few ms). Stale (for another loaded save) after a load.
    Task<SaveBuilder>? preparedBase;
    LoadedSave? preparedFor;
    int captures;
    NewGameSettings difficulty = new();

    SaveCaptureOptions CaptureOptions()
    {
        saveDataFiles ??= [.. LoadOrder.FromInstall(install).Entries.Select(e => e.Name)];
        // The strategy camera's view (in free flight: where it was left), as the original saves its camera rig.
        var view = session.Camera.Strategy;
        return new SaveCaptureOptions
        {
            DataFiles = saveDataFiles, Placements = townPlacements, Levels = saveLevels, Seed = session.World.Seed ^ (ulong)++captures << 20,
            View = new SavedView(view.Pivot, view.Yaw, view.Pitch, view.Distance),
        };
    }

    // The footprints a worker's reach of a building is measured to (docs/game/buildings-production.md "Work distance").
    Meitou.Navigation.BuildingFootprints? siteFootprints;

    /// <summary>
    /// The X/Z gap from a point to a site's footprint hull (its collision, else for a building without it the hull of its parts' bounds); null while the record is being read, or
    /// without a database.
    /// </summary>
    float? SiteEdgeGap(Meitou.Simulation.Construction.BuildSite site, Vector2 point)
    {
        if (scene.Database is not { } db) return null;
        var footprints = siteFootprints ??= new Meitou.Navigation.BuildingFootprints(install, db, null);
        if (!footprints.Ready(site.Record)) return null;
        return (footprints.Of(site.Record) ?? footprints.Plot(site.Record))?.Gap(site.Position, site.Rotation, point);
    }

    /// <summary>The system list and its order are <see cref="StandardSystems"/>' (docs/simulation.md "Threading"); built again for each world.</summary>
    Meitou.Simulation.StandardSystemSet BuildSystems(bool interactive, PopulationData? data = null, NewGameSettings? settings = null)
    {
        var set = BuildStandardSystems(interactive, data, settings);
        if (set.Euphoria is { } euphoria) euphoria.Enabled = config.Euphoria;
        return set;
    }

    /// <summary>The euphoria characters (docs/euphoria.md): loaded once, on a worker thread, with the ragdolls; whether they loaded goes to the log.</summary>
    Task<Meitou.Simulation.Euphoria.EuphoriaData?>? Euphoria()
    {
        if (characterRigs is null) return null;
        var task = Meitou.Simulation.Euphoria.EuphoriaData.Shared(Meitou.Simulation.Euphoria.EuphoriaData.DefaultFolder,
            Meitou.Simulation.Euphoria.EuphoriaData.FindGame(install.Root, config.Gta4Folder));
        if (!euphoriaLogged)
        {
            euphoriaLogged = true;
            task.ContinueWith(t => Console.Error.WriteLine(t.Result is null ? $"euphoria  off: {Meitou.Simulation.Euphoria.EuphoriaData.Problem}" : "euphoria  bodies ready"), TaskScheduler.Default);
        }
        return task;
    }

    bool euphoriaLogged;

    Meitou.Simulation.StandardSystemSet BuildStandardSystems(bool interactive, PopulationData? data, NewGameSettings? settings) =>
        Meitou.Simulation.StandardSystems.Build(data ?? popData!, simWalkability!, new Meitou.Simulation.StandardSystemOptions
        {
            // The sandbox compares motion with the original on characters standing still: a hostile foe there must not start a fight by itself. The duel and the battle script their fights.
            // --battle-no-fear: no flee system, so nobody runs away.
            Parts = (IsSandbox || IsDuel || IsBattle ? Meitou.Simulation.StandardParts.All & ~Meitou.Simulation.StandardParts.Aggro : Meitou.Simulation.StandardParts.All)
                & ~(IsBattle && g.BattleNoFear ? Meitou.Simulation.StandardParts.Flee : Meitou.Simulation.StandardParts.None),
            Aggro = new Meitou.Simulation.AggroSettings { RecordLog = true },
            // NPCs that see a player character say a line as a speech bubble (docs/game/dialogue.md "Ambient conversations"); the sandbox, the duel and the battle stay silent.
            Dialogue = new Meitou.Simulation.Dialogue.DialogueSettings { AmbientEvents = !IsSandbox && !IsDuel && !IsBattle },
            Population = new Meitou.Simulation.PopulationSettings
            {
                Background = interactive && !IsDuel && !IsBattle, KeepRoamers = config.KeepRoamers, OffscreenFights = config.OffscreenFights, OffscreenHealing = config.OffscreenHealing,
                CampaignAncestorsOnce = config.CampaignAncestorsOnce, KeepWild = config.KeepWild, ResidentSchedule = config.ResidentSchedule,
                NestsMultiplier = (settings ?? difficulty)[6],
            },
            Construction = new Meitou.Simulation.Construction.ConstructionSettings { BuildingSpeed = (settings ?? difficulty)[5], EdgeGap = SiteEdgeGap },
            Production = new Meitou.Simulation.Production.ProductionSettings { ProductionOption = (settings ?? difficulty)[3], ResearchOption = (settings ?? difficulty)[4], EdgeGap = SiteEdgeGap },
            // The duel replays exactly: its paths are answered inside the tick, never on a thread whose timing differs from lap to lap.
            SynchronousPaths = !interactive || IsDuel,
            // The duel prints what its measured lap holds (MeasureLap).
            Combat = new Meitou.Simulation.Combat.CombatOptions { SelfApproach = false, TickMedical = false, RecordLog = IsDuel, AttackSlots = Math.Clamp(config.AttackSlots, 1, 5), LateDefences = config.LateDefences, GunnerFacesBarrel = config.FixGunnerFacing },
            DoorsCloseBehind = config.DoorsCloseBehind,
            AnimationLengths = animationLengths,
            AnimationLibrary = animationLibrary,
            AnimationBlendRate = popData!.Bodies.Constants.AnimationBlendRate,
            Ragdolls = characterRigs,
            RagdollEnvironment = ragdollGeometry,
            Euphoria = Euphoria(),
        });

    /// <summary>Once per frame, between ticks: the notices of the write worker, the requests of the keys, a finished read.</summary>
    void HandleSaves()
    {
        while (saveNotices.TryDequeue(out var notice)) player.Notice(notice.Text, notice.Error);
        PrintSavedLog();
        if (quickSaveRequested) { quickSaveRequested = false; QuickSaveNow(); }
        if (quickLoadRequested) { quickLoadRequested = false; BeginQuickLoad(); }
        if (saveRequestedFolder is { } to) { saveRequestedFolder = null; QuickSaveNow(to); }
        if (loadRequestedFolder is { } sourceFolder) { loadRequestedFolder = null; BeginQuickLoad(sourceFolder); }
        if (importRequested is { } import) { importRequested = null; BeginImport(import.Folder, import.Options); }

        if (preparedBase is null && SavesAvailable(out _) && session.World.Player.Exists)
        {
            var (data, from, options) = (popData!, loadedSave, CaptureOptions());
            (preparedBase, preparedFor) = (Task.Run(() => SaveCapture.PrepareBase(data, from, options)), from);
        }
    }

    /// <summary>Whether this run has a game to save and load: the population (so not <c>--no-population</c> or the sandbox).</summary>
    bool SavesAvailable(out string reason)
    {
        reason = popData is null || population is null || IsFlat ? "No game to save or load here (needs the town population)" : "";
        return reason.Length == 0;
    }

    bool Busy() => saveWrite is { IsCompleted: false } || Covered;

    void QuickSaveNow(string? destination = null)
    {
        if (!SavesAvailable(out string reason)) { player.Notice(reason, true); return; }
        session.SettleAhead();   // a tick run ahead counts as run: the saved world and its clock agree
        var world = session.World;
        if (!world.Player.Exists) { player.Notice("Nothing to save: no player squad (start a new game)", true); return; }
        if (Busy()) { player.Notice("Save or load still in progress", true); return; }
        var watch = Stopwatch.StartNew();
        SaveGame game;
        bool ahead = preparedBase is { IsCompletedSuccessfully: true } && ReferenceEquals(preparedFor, loadedSave);
        try
        {
            var clock = session.Clock;
            var prepared = ahead ? preparedBase!.Result : null;
            preparedBase = null;   // used, or stale
            game = SaveCapture.Capture(world, popData!, new SaveClock((int)clock.Day, clock.Hour, clock.Minute), loadedSave, CaptureOptions(), prepared);
            difficulty.Write(game.Camera);
            WriteWeather(game, (int)clock.Day, clock.HourOfDay);
            WriteDecals(game);
            player.WriteBiome(game.Camera);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"save      capture failed: {e}");
            player.Notice($"Save failed: {e.Message}", true);
            return;
        }
        double captureMs = watch.Elapsed.TotalMilliseconds;
        string folder = destination ?? QuickSave.Folder();
        saveWrite = Task.Run(() =>
        {
            try
            {
                var written = Stopwatch.StartNew();
                game.Write(folder);
                Console.WriteLine($"save      {folder}: captured in {captureMs:0.0} ms{(ahead ? "" : " (the base made on the spot)")}, written in {written.ElapsedMilliseconds} ms");
                saveNotices.Enqueue(("Saved", false));
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"save      write failed: {e}");
                saveNotices.Enqueue(($"Save failed: {e.Message}", true));
            }
        });
    }

    /// <summary>The save's NPC platoons made since the last call (one line each), to the log.</summary>
    void PrintSavedLog()
    {
        if (session.World.Saved is { } saved)
            foreach (var line in saved.TakeLog()) Console.WriteLine($"saved     {line}");
    }

    /// <summary>
    /// The regions' weather into the save's BIOMES record, and the CAMERA's sky transition as finished at the current sky (docs/formats/save.md "BIOMES", "CAMERA"):
    /// the loader snaps the sky to the camera region's weather, so a transition in progress is not carried.
    /// </summary>
    void WriteWeather(SaveGame game, int day, double hour)
    {
        if (ActiveWeather is not { } weather || game.Biomes is not { } biomes || weather.World.ForcedWeather is not null) return;
        SaveWeather.Write(biomes, weather.World.Snapshot(), new Meitou.Data.World.WeatherTime(day, hour), popData?.Db);
        var camera = game.Camera;
        var state = weather.State;
        camera.SkyUpdating = false;
        camera.SkyUpdateTotalTime = Meitou.Data.World.WeatherWorld.SkyTransitionSeconds;
        camera.SkyUpdateCurrentTime = Meitou.Data.World.WeatherWorld.SkyTransitionSeconds;
        camera.SkyAmbientColorMult = state.SkyColourMultiplier;
        camera.SkyCloudsDensity = state.CloudDensity;
    }

    /// <summary>Starts loading a save under the loading screen (GameHost.Transition.cs): the files are read on a worker, the new world is made, the navmesh round the player waits.</summary>
    void BeginQuickLoad(string? source = null)
    {
        if (!SavesAvailable(out string reason)) { player.Notice(reason, true); return; }
        if (Busy()) { player.Notice("Save or load still in progress", true); return; }
        string folder = source ?? QuickSave.Folder();
        if (!File.Exists(Path.Combine(folder, SaveFolder.QuickSaveName))) { player.Notice(source is null ? "No quicksave yet (F5 saves)" : "Save is no longer available", true); return; }
        readingFolder = folder;
        Task<SaveGame>? read = null;
        string summary = "";
        AudioGameStarts();
        StartSceneLoad(player.TitleOpen,
            Stage("read", () => read = Task.Run(() => SaveGame.Load(folder))),
            Stage("build", () => SwapInSave(read!, interactive: true, out summary)),
            Stage("finishing", () => { SettleNewWorld(); player.FollowSelected(); FinishLoadNotice(summary); return null; }));
    }

    /// <summary>
    /// Imports a save into a new world (docs/formats/save.md "Importing a game"): the save is read on a worker and cut down to what the ticks keep (<see cref="SaveImport"/>), then
    /// loaded as any save is. The difficulty is what the advanced options hold, not the save's.
    /// </summary>
    void BeginImport(string folder, ImportOptions options)
    {
        if (!SavesAvailable(out string reason)) { player.Notice(reason, true); return; }
        if (Busy()) { player.Notice("Save or load still in progress", true); return; }
        if (!File.Exists(Path.Combine(folder, SaveFolder.QuickSaveName))) { player.Notice("Failed to import game: the save is no longer available", true); return; }
        readingFolder = folder;
        var settings = player.NewGameOptions;
        var data = popData!;
        Task<SaveGame>? read = null;
        string summary = "";
        AudioGameStarts();
        StartSceneLoad(player.TitleOpen,
            Stage("read", () => read = Task.Run(() =>
            {
                var game = SaveImport.Filter(SaveGame.Load(folder), options, data);
                settings.Write(game.Camera);
                return game;
            })),
            // The squads moved to the reset place stand on its ground, whose navmesh is waited for.
            Stage("navmesh", () => (options & ImportOptions.ResetPositions) != 0 && read!.IsCompletedSuccessfully ? NavmeshAround(SaveImport.ResetPosition) : null),
            Stage("build", () =>
            {
                if ((options & ImportOptions.ResetPositions) != 0 && nav is not null && read!.IsCompletedSuccessfully) SaveImport.PlaceOnGround(read.Result, nav.Walkability.GroundHeight);
                return SwapInSave(read!, interactive: true, out summary, new SaveLoadOptions { Appearances = data.Appearances, ResetRelations = (options & ImportOptions.Relations) == 0 });
            }),
            Stage("finishing", () => { SettleNewWorld(); player.FollowSelected(); FinishLoadNotice(summary, imported: true); return null; }));
    }

    /// <summary>
    /// Swaps in a world made from the save (at a tick boundary, the frame loop's) and hooks the host to it: the game clock, the camera on the player's squad. Returns the
    /// navmesh of the zones round the player to wait for before the first tick (<see cref="SettleNewWorld"/>); throws <see cref="SceneLoadFailed"/> when the save does
    /// not load (the old world stays).
    /// </summary>
    Task SwapInSave(Task<SaveGame> read, bool interactive, out string summary, SaveLoadOptions? loadOptions = null)
    {
        if (read.IsFaulted || read.IsCanceled)
        {
            Console.Error.WriteLine($"load      read failed: {read.Exception?.GetBaseException()}");
            throw new SceneLoadFailed($"Load failed: {read.Exception?.GetBaseException().Message ?? "cancelled"}");
        }
        var watch = Stopwatch.StartNew();
        LoadedSave? loaded = null;
        StandardSystemSet built;
        var nextDifficulty = NewGameSettings.Read(read.Result.Camera);
        var nextData = nextDifficulty.Apply(popData!);
        try
        {
            built = BuildSystems(interactive, nextData, nextDifficulty);
            var data = nextData;
            // A world that fails to load leaves the old one running (ReplaceWorld drops the half-made one).
            session.ReplaceWorld(built.Systems, world =>
            {
                loaded = SaveLoader.Load(world, read.Result, data, loadOptions ?? new SaveLoadOptions { Appearances = data.Appearances });
                if (world.Squads.Find(world.Player.Squad) is { Members.Count: > 0 } squad)
                {
                    world.Player.Selection.Clear();
                    world.Player.Selection.Add(squad.Members[0]);
                }
            });
        }
        catch (Exception e) when (e is not SceneLoadFailed)
        {
            Console.Error.WriteLine($"load      failed: {e}");
            throw new SceneLoadFailed($"Load failed: {e.Message}");
        }
        popData = nextData;
        difficulty = nextDifficulty;
        population = built.Population;
        aggro = built.Aggro;
        if (nav is not null) population!.ZoneGate = zone => nav.Walkability.World.Contains(zone);
        loadedSave = loaded;
        preparedBase = null;
        worldEpoch++;
        var world = session.World;
        BloodForNewWorld(read.Result.Decals);
        session.Clock.SetTime(loaded!.Clock.Day, loaded.Clock.HourOfDay);
        // The regions' weather the save holds (docs/formats/save.md "BIOMES"); a save without (an old Meitou one) keeps the schedule rolled from day 0.
        if (ActiveWeather is { } weather && read.Result.Biomes is { } biomes && SaveWeather.Read(biomes) is { Count: > 0 } regions)
        {
            weather.Restore(regions, loaded.Clock.Day, loaded.Clock.HourOfDay);
            Console.WriteLine($"load      weather of {regions.Count} region(s) from the save");
        }
        playerSquad = world.Squads.Find(world.Player.Squad);
        foreach (var note in loaded.Notes) Console.WriteLine($"load      {note}");

        // The saved camera (FUN_1407ade60, docs/formats/camera.md "Saved camera"): pivot, orientation and boom, a pivot out of bounds moved to the loader's fallback. A save
        // without a view (an older Meitou one) puts the camera on the leader, keeping the view's heading, pitch and boom. The zones round the leader get their navmesh
        // before the first tick (like a new game's start).
        Task navmesh = Task.CompletedTask;
        var saved = read.Result.Camera;
        player.ReadBiome(saved);
        if (saved.HasView)
        {
            var (yaw, pitch, distance) = KenshiCamera.FromSaved(saved.Rotation, saved.Zoom);
            var pivot = KenshiCamera.SavedPivotInBounds(saved.Position) ? new Vector2(saved.Position.X, saved.Position.Z) : KenshiCamera.SavedPivotFallback;
            session.Camera.Place(pivot, yaw, pitch, distance);
        }
        if (playerSquad is { } squad && !squad.Leader.IsNone)
        {
            var at = world.Characters.Previous[squad.Leader.Slot].Position;
            if (!saved.HasView)
            {
                var view = session.Camera.Current;
                session.Camera.Place(new Vector2(at.X, at.Z), view.Yaw, view.Pitch, view.Distance);
            }
            navmesh = NavmeshAround(at);
        }
        summary = $"{readingFolder}: {loaded.PlayerPlatoons.Sum(p => p.Squad.Members.Count)} player character(s), {loaded.RoamingPlatoons.Count} roaming, {loaded.TownBoundPlatoons} town and {loaded.WildPlatoons} wild platoon(s) kept as saved, day {loaded.Clock.Day} {loaded.Clock.Hour:00}:{loaded.Clock.Minute:00}, made in {watch.ElapsedMilliseconds} ms";
        return navmesh;
    }

    void FinishLoadNotice(string summary, bool imported = false)
    {
        Console.WriteLine($"load      {summary}");
        var missing = loadedSave?.MissingMods ?? [];
        player.Notice(missing.Count == 0 ? (imported ? "Imported" : "Loaded") : $"Loaded; the save was made with mods not loaded now: {string.Join(", ", missing)}", missing.Count > 0);
    }

    /// <summary>
    /// The navmesh of the zones round <paramref name="at"/> (the ones already loaded cost nothing): done when they are, or after 15 s (loading goes on without them).
    /// </summary>
    Task NavmeshAround(Vector3 at)
    {
        if (nav is null) return Task.CompletedTask;
        long now = Stopwatch.GetTimestamp();
        var pending = new List<Task>();
        foreach (var z in ZoneActivation.ZonesAround(at, 0))
        {
            if (!navZones.ContainsKey(z)) pending.Add(nav.LoadZone(z));
            navZones[z] = now;
        }
        if (pending.Count == 0) return Task.CompletedTask;
        var all = Task.WhenAll(pending);
        return Task.WhenAny(all, Task.Delay(TimeSpan.FromSeconds(15))).ContinueWith(t =>
        {
            if (t.Result != all) Console.Error.WriteLine("load      the navmesh round the player was not ready after 15 s; loading goes on");
        }, TaskScheduler.Default);
    }
}
