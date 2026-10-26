using System.Numerics;
using Meitou.Data.Gameplay;
using Meitou.Simulation;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Combat;
using Meitou.Tests.Combat;
using static Meitou.Tests.Bodies.BodyFixtures;
using static Meitou.Tests.Combat.CombatFixtures;
using SimWorld = Meitou.Simulation.World;

namespace Meitou.Tests.Simulation;

/// <summary>Faction relations driving fights: the relation table's rules, the auto-aggro, the chase give-up, the player's self-defence and deeds.</summary>
public class AggroTests
{
    // Factions: 0 raiders, 1 villagers (raiders hate them), 2 nomads (raiders hate them but coexist), 3 friends (allied to the raiders),
    // 4 hermits (the raiders' own list says hostile, the hermits list the raiders as coexisting), 5 the player (Nameless, no table of its own),
    // 6 wildlife (not real), 7 townsfolk (neutral to everyone, the player's victims).
    const int Raiders = 0, Villagers = 1, Nomads = 2, Friends = 3, Hermits = 4, Player = 5, Wildlife = 6, Townsfolk = 7;

    static FactionRelations Relations()
    {
        FactionData F(string id, int def = 0, (string, int)[]? rel = null, string[]? coexist = null, bool notReal = false) => new()
        {
            Id = id, Name = id, DefaultRelation = def, NotReal = notReal,
            Relations = (rel ?? []).ToDictionary(r => r.Item1, r => r.Item2),
            Coexistence = coexist ?? [],
        };
        var table = FactionRelations.Build([
            F("raiders", 0, [("villagers", -80), ("nomads", -80), ("friends", 70), ("hermits", -80), ("Nameless", -60)], ["nomads"]),
            F("villagers"),
            F("nomads"),
            F("friends", 0, [("raiders", 70)]),
            F("hermits", 0, null, ["raiders"]),
            F("Nameless", 10, [("raiders", 40)]),
            F("wildlife", -100, notReal: true),
            F("townsfolk", 10),
        ]);
        table.PlayerFaction = Player;
        return table;
    }

    // ------------------------------------------------------------------ worlds

    sealed class Arena(SimWorld world, CombatSystem combat, AggroSystem aggro, FactionRelations relations) : IDisposable
    {
        public SimWorld World { get; } = world;
        public CombatSystem Combat { get; } = combat;
        public AggroSystem Aggro { get; } = aggro;
        public FactionRelations Relations { get; } = relations;
        public void Dispose() => World.Dispose();

        public Squad Squad(int faction, Vector2 home, params Vector2[] at) => FacingSquad(faction, home, null, at);

        /// <summary>A squad whose members face <paramref name="face"/> (null: yaw 0, along +Z); the original's NPCs see only ahead (the view cone) and hear only movers.</summary>
        public Squad FacingSquad(int faction, Vector2 home, Vector2? face, params Vector2[] at) =>
            SquadOf(faction, home, false, [.. at.Select(p => (p, face is { } f ? MathF.Atan2(f.X - p.X, f.Y - p.Y) : 0f))]);

        /// <summary>A squad with each member's position and yaw; <paramref name="idle"/> makes them stand (no wandering, so they make no noise).</summary>
        public Squad SquadOf(int faction, Vector2 home, bool idle, params (Vector2 At, float Yaw)[] members)
        {
            var squad = new Squad { Id = World.Squads.NextId(), TemplateId = "t", Faction = faction, HomeCentre = home, HomeRadius = 30 };
            World.Squads.Add(squad);
            foreach (var (p, yaw) in members)
            {
                var cold = new Combatant { Name = $"f{faction}-{squad.Members.Count}" }.MakeCold();
                cold.Faction = faction;
                cold.SquadId = squad.Id;
                cold.FactionImportance = 1;   // the most common CHARACTER value: blows on them move their faction's relations
                bool leader = squad.Members.Count == 0;
                var id = World.Characters.Spawn(new CharacterHot
                {
                    Position = new Vector3(p.X, 150, p.Y), Health = 100, MaxSpeed = 60, WalkSpeed = 15, Yaw = yaw,
                    Task = (byte)(idle ? CharacterTask.Idle : leader ? CharacterTask.Wander : CharacterTask.Follow), TaskTime = 1,
                }, cold, 0);
                squad.Members.Add(id);
                if (leader) squad.Leader = id;
            }
            return squad;
        }

        public CharacterId PlayerCharacter(Vector2 at)
        {
            var cold = new Combatant { Name = "player" }.MakeCold();
            cold.Faction = Player;
            cold.IsPlayer = true;
            return World.Characters.Spawn(new CharacterHot { Position = new Vector3(at.X, 150, at.Y), Health = 100, MaxSpeed = 60, WalkSpeed = 15 }, cold, 0);
        }

        public bool Fighting(CharacterId id) => World.Characters.Cold(id.Slot)!.InCombat;
    }

    /// <summary>Catches the dialogue events the systems fire (there is no dialogue system here).</summary>
    sealed class EventLog : ITickSystem
    {
        public readonly List<Meitou.Simulation.Dialogue.DialogueEvent> Events = [];
        public void Inputs(SimWorld world, IReadOnlyList<SimCommand> commands) => Events.AddRange(commands.OfType<Meitou.Simulation.Dialogue.DialogueEvent>());
        public void Hash(ref StateHasher hasher) { }
    }

    static Arena Make(ulong seed, int threads, FactionRelations? relations = null, AggroSettings? settings = null, ITickSystem? extra = null)
    {
        relations ??= Relations();
        var combat = new CombatSystem(KatanaTechniques(), Constants, BaseGame, BodyOptions.Default, DuelFixture.Lengths, new CombatOptions { SelfApproach = false, TickMedical = false, RecordLog = true })
        {
            Relations = relations,
            Deeds = new FactionDeeds(relations),
        };
        var aggro = new AggroSystem(relations, settings ?? new AggroSettings { RecordLog = true });
        var walk = new OpenGroundWalkability((_, _) => 150);
        var world = new SimWorld(new WorldSettings { Seed = seed, Threads = threads, PublishSnapshots = false, MinPartitionSize = 1 }, walk,
            [new PursuitSystem(combat), new MovementSystem(new PathService(walk, true)), new BodySystem(BaseGame), combat, new RetaliationSystem(combat), aggro, .. extra is null ? Array.Empty<ITickSystem>() : [extra]]);
        return new Arena(world, combat, aggro, relations);
    }

    /// <summary>
    /// Raiders between villagers (hostile), nomads (coexisting), hermits (coexisting from their side) and friends (allied), each a squad 150 units off.
    /// Returns the squads in that order.
    /// </summary>
    static (Arena Arena, Squad Raiders, Squad Villagers, Squad Nomads, Squad Hermits, Squad Friends) Crossroads(ulong seed, int threads)
    {
        var a = Make(seed, threads);
        var o = new Vector2(2000, 2000);
        var raiders = a.Squad(Raiders, o, o, o + new Vector2(0, 12));
        var villagers = a.Squad(Villagers, o + new Vector2(150, 0), o + new Vector2(150, 0), o + new Vector2(160, 10));
        var nomads = a.Squad(Nomads, o + new Vector2(-150, 0), o + new Vector2(-150, 0), o + new Vector2(-160, 10));
        var hermits = a.Squad(Hermits, o + new Vector2(0, 150), o + new Vector2(0, 150));
        var friends = a.Squad(Friends, o + new Vector2(0, -150), o + new Vector2(0, -150));
        return (a, raiders, villagers, nomads, hermits, friends);
    }

    [Fact]
    public void Hostile_squads_in_sight_fight_and_nobody_else_does()
    {
        var (a, raiders, villagers, nomads, hermits, friends) = Crossroads(3, 1);
        using var _ = a;
        a.World.RunTicks(60);
        // The raiders started it: every logged fight is a raider attacking a villager, and both raiders took part (the second one joined).
        Assert.NotEmpty(a.Aggro.Log);
        Assert.All(a.Aggro.Log, e => { Assert.Equal(Raiders, e.AttackerFaction); Assert.Equal(Villagers, e.TargetFaction); });
        Assert.All(raiders.Members, m => Assert.True(a.Fighting(m), "both raiders fight"));
        // The villagers answer (retaliation), and nobody touches the nomads or hermits; the raiders' allies (friends) may come to the raiders' side (Meitou's ally help, AllyHelpChance).
        a.World.RunTicks(60);
        Assert.Contains(villagers.Members, a.Fighting);
        foreach (var m in nomads.Members.Concat(hermits.Members)) Assert.False(a.Fighting(m));
        foreach (var e in a.Combat.Log)
        {
            int fa = a.World.Characters.Cold(e.Attacker)!.Faction, fd = a.World.Characters.Cold(e.Defender)!.Faction;
            Assert.True((fa, fd) is (Raiders, Villagers) or (Villagers, Raiders) or (Friends, Villagers) or (Villagers, Friends), $"a blow between factions {fa} and {fd}");
        }
    }

    [Fact]
    public void A_war_on_the_player_turns_a_faction_hostile_with_the_games_messages_and_its_people_then_attack()
    {
        using var a = Make(7, 1);
        var o = new Vector2(2000, 2000);
        var town = a.Squad(Townsfolk, o, o, o + new Vector2(30, 0));
        // Beyond hearing the first fight (RetaliationSystem.HearFight) and not looking at it, so it does not come to help before the war.
        var watch = a.Squad(Townsfolk, o + new Vector2(350, 0), o + new Vector2(350, 0));
        var me = a.PlayerCharacter(o + new Vector2(60, 60));
        a.World.RunTicks(20);
        Assert.Empty(a.Aggro.Log);   // neutral: 10
        // The order itself changes no relation (the game's relation events come from the blows that land).
        a.World.Commands.Enqueue(new AttackOrder([me], town.Members[0]) { Tick = a.World.Tick });
        a.World.RunTick();
        Assert.Equal(10, a.Relations.Get(Townsfolk, Player));
        Assert.Empty(a.World.TakeMessages());
        // The townsfolk declare war on the player: -35, hostile, and the game's messages.
        PlayerRelations.DeclareWar(a.World, a.Relations, Townsfolk, Player);
        Assert.Equal(-35, a.Relations.Get(Townsfolk, Player));
        Assert.Equal(["War breaks out between townsfolk and Nameless", "townsfolk are now hostile towards you"], a.World.TakeMessages());
        // Now another squad of theirs, which nobody attacked (the victims' mates join them anyway), picks the fight with the player by itself.
        a.World.RunTicks(30);
        Assert.Contains(a.Aggro.Log, e => e.Attacker == watch.Members[0] && e.Target == me);
    }

    /// <summary>
    /// The original's chase give-up (<c>ChaseMode</c>, docs/game/combat.md "Leaving a fight"): a raider chasing a runner that gets out of its sight and hearing stops once the
    /// gate and 24 s without a sense of it have passed, saying event 60 GIVE_UP_CHASE to it; one whose runner stays in view keeps on.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_chaser_gives_up_a_target_it_no_longer_senses_and_keeps_one_it_sees(bool escapes)
    {
        var log = new EventLog();
        using var a = Make(5, 1, extra: log);
        a.Aggro.Combat = a.Combat;   // as the standard systems wire it: the chase reads the fight target
        var o = new Vector2(2000, 2000);
        var raider = a.SquadOf(Raiders, o, true, (o, 0f)).Members[0];
        var runner = a.PlayerCharacter(o + new Vector2(0, 200));
        a.World.Characters.Cold(runner.Slot)!.Orders |= CharacterOrders.Passive;   // it never turns to fight back
        a.World.Commands.Enqueue(new AttackOrder([raider], runner) { Tick = a.World.Tick });
        a.World.Commands.Enqueue(new MoveOrder([runner], new Vector3(o.X, 150, o.Y + 20000)) { Tick = a.World.Tick });
        a.World.RunTicks(60);
        Assert.True(a.World.Characters.Cold(raider.Slot)!.CombatRunning, "the raider runs after it");
        // Escaping: the runner is put far ahead, beyond any sight or noise range, and stands there (so it makes no noise either).
        if (escapes) a.World.Characters.Place(runner.Slot, new Vector3(o.X, 150, o.Y + 9000), 0);
        long from = a.World.Tick;
        a.World.RunTicks((int)(60 / a.World.TickSeconds));
        var gaveUp = log.Events.Where(e => e.Event == 60).ToList();
        if (escapes)
        {
            var e = Assert.Single(gaveUp);
            Assert.Equal((raider, runner), (e.Npc, e.Target));
            // 24 s after the last look that sensed it, which came up to one look interval before it vanished.
            float after = (e.Tick - from) * a.World.TickSeconds, look = new AggroSettings().ScanEveryTicks * a.World.TickSeconds;
            Assert.InRange(after, AggroSystem.GiveUpSenseSeconds - look, AggroSystem.GiveUpSenseSeconds + look);
            Assert.NotEqual((byte)CharacterTask.Attack, a.World.Characters.Next[raider.Slot].Task);
        }
        else
        {
            Assert.Empty(gaveUp);
            Assert.Equal((byte)CharacterTask.Attack, a.World.Characters.Next[raider.Slot].Task);
        }
    }

    /// <summary>
    /// The chase lead with solid bodies (docs/game/combat.md "Combat stance", Meitou): a raider faster than its runner passes it beside its track and lands a blow
    /// within a few seconds of catching up, instead of pushing it along or stopping beside it out of reach of its led aim point.
    /// </summary>
    [Fact]
    public void A_faster_chaser_passes_its_runner_and_lands_a_blow()
    {
        using var a = Make(5, 1);
        a.Aggro.Combat = a.Combat;
        var o = new Vector2(2000, 2000);
        var raider = a.SquadOf(Raiders, o, true, (o, 0f)).Members[0];
        var runner = a.PlayerCharacter(o + new Vector2(0, 100));   // dead ahead: the pass must not need a sideways start
        var table = a.World.Characters;
        table.Cold(runner.Slot)!.Orders |= CharacterOrders.Passive;
        table.Cold(runner.Slot)!.SpeedOverride = 55;   // slower than the raider's run
        a.World.Commands.Enqueue(new AttackOrder([raider], runner) { Tick = a.World.Tick });
        a.World.Commands.Enqueue(new MoveOrder([runner], new Vector3(o.X, 150, o.Y + 20000)) { Tick = a.World.Tick });
        long caught = -1;
        for (int k = 0; k < (int)(60 / a.World.TickSeconds); k++)
        {
            a.World.RunTicks(1);
            var d = table.Next[runner.Slot].Position - table.Next[raider.Slot].Position;
            if (caught < 0 && new Vector2(d.X, d.Z).Length() < 20) caught = a.World.Tick;
            if (a.Combat.StateOf(raider) is { AttackSeq: > 0, LastOutcome: BlowOutcome.Hit }) break;
        }
        Assert.True(caught >= 0, "the raider catches up");
        Assert.True(a.Combat.StateOf(raider) is { AttackSeq: > 0, LastOutcome: BlowOutcome.Hit }, "a blow lands");
        Assert.InRange((a.World.Tick - caught) * a.World.TickSeconds, 0, 5);
    }

    /// <summary>Raiders (0) and the player's Nameless (1), enemies (the player's relations are in the other side's table).</summary>
    static FactionRelations OneSided()
    {
        var table = FactionRelations.Build([
            new FactionData { Id = "raiders", Name = "raiders", Relations = new Dictionary<string, int> { ["Nameless"] = -80 } },
            new FactionData { Id = "Nameless", Name = "Nameless", Relations = new Dictionary<string, int>() },
        ]);
        table.PlayerFaction = 1;
        return table;
    }

    /// <summary>Makes a raider a raiders' slave: it looks for no fight, yet stays a target for the raiders' enemies.</summary>
    static void Docile(Arena a, CharacterId raider) => (a.World.Characters.Cold(raider.Slot)!.SlaveState, a.World.Characters.Cold(raider.Slot)!.SlaveOwner) = (1, 0);

    /// <summary>
    /// Hold (docs/game/combat.md "Fight lifecycle and orders", perception.md "The attack-on-sight tests"): an idle holder attacks on sight only targets its post test takes, so a
    /// raider standing 50 away is left alone while one walking past within 80 of the hold point is attacked.
    /// </summary>
    [Fact]
    public void A_holder_takes_on_a_raider_passing_its_post_but_not_one_standing_off()
    {
        using var a = Make(4, 1, OneSided());
        a.Aggro.Combat = a.Combat;
        var o = new Vector2(2000, 2000);
        var me = a.World.Characters.Spawn(new CharacterHot { Position = new Vector3(o.X, 150, o.Y), Health = 100, MaxSpeed = 60, WalkSpeed = 15 },
            new Combatant { Name = "holder" }.MakeCold(), 0);
        var cold = a.World.Characters.Cold(me.Slot)!;
        (cold.Faction, cold.IsPlayer, cold.Orders, cold.HoldPoint) = (1, true, CharacterOrders.Hold, o);
        var stander = a.SquadOf(0, o, true, (o + new Vector2(0, 50), MathF.PI)).Members[0];
        Docile(a, stander);
        a.World.RunTicks((int)(3 / a.World.TickSeconds));
        Assert.False(a.Fighting(me), "a raider standing 50 away fails the post test (standing targets within 40 only)");
        // A raider walking across in front, 60 from the hold point at its nearest.
        var walker = a.SquadOf(0, o, true, (o + new Vector2(-300, 60), 0f)).Members[0];
        Docile(a, walker);
        a.World.Commands.Enqueue(new MoveOrder([walker], new Vector3(o.X + 300, 150, o.Y + 60)) { Tick = a.World.Tick });
        for (int k = 0; k < (int)(10 / a.World.TickSeconds) && !a.Fighting(me); k++) a.World.RunTicks(1);
        Assert.True(a.Fighting(me), "the walker passing within 80 of the hold point is attacked");
        Assert.Equal(walker.Slot, a.Combat.StateOf(me).TargetSlot);
        Assert.NotEqual(stander.Slot, a.Combat.StateOf(me).TargetSlot);
    }

    /// <summary>
    /// The fighters' re-pick (docs/game/combat.md "Target selection"): a villager whose raider goes down turns at once on the raider attacking its squad mate, and once
    /// that one is down too and nobody is left, its fight ends.
    /// </summary>
    [Fact]
    public void A_fighter_whose_target_drops_turns_on_a_mates_attacker_and_stops_when_none_is_left()
    {
        var table = FactionRelations.Build([
            new FactionData { Id = "raiders", Name = "raiders", Relations = new Dictionary<string, int>() },
            new FactionData { Id = "villagers", Name = "villagers", Relations = new Dictionary<string, int>() },
        ]);
        using var a = Make(6, 1, table);
        var o = new Vector2(2000, 2000);
        var villagers = a.SquadOf(1, o, true, (o, 0f), (o + new Vector2(40, 0), 0f));
        var raiders = a.SquadOf(0, o, true, (o + new Vector2(0, 20), MathF.PI), (o + new Vector2(40, 20), MathF.PI));
        CharacterId fighter = villagers.Members[0], mate = villagers.Members[1], first = raiders.Members[0], second = raiders.Members[1];
        a.World.Commands.Enqueue(new AttackOrder([fighter], first) { Tick = a.World.Tick });
        a.World.Commands.Enqueue(new AttackOrder([first], fighter) { Tick = a.World.Tick });
        a.World.Commands.Enqueue(new AttackOrder([second], mate) { Tick = a.World.Tick });
        a.World.RunTicks(30);
        Assert.Equal(first.Slot, a.Combat.StateOf(fighter).TargetSlot);
        a.World.Characters.Cold(first.Slot)!.Medical!.KnockOut(600);
        a.World.RunTicks(5);
        Assert.Equal(second.Slot, a.Combat.StateOf(fighter).TargetSlot);
        a.World.Characters.Cold(second.Slot)!.Medical!.KnockOut(600);
        a.World.RunTicks(5);
        Assert.False(a.Fighting(fighter), "nobody left: the fight ends");
        Assert.True(a.Combat.StateOf(fighter).TargetSlot < 0);
    }

    /// <summary>The crossroads with a player character among the raiders who then assaults a villager; pinned in <see cref="GoldenHashTests"/>.</summary>
    internal static List<ulong> Hashes(int threads)
    {
        var (a, _, _, _, _, _) = Crossroads(11, threads);
        using var _a = a;
        // A player character close to the raiders, who hate the player: it is attacked and defends itself; at tick 120 it is ordered onto a villager, an assault on the villagers.
        var me = a.PlayerCharacter(new Vector2(2000, 2080));
        var hashes = new List<ulong>();
        foreach (int target in new[] { 1, 30, 120, 400, 900 })
        {
            a.World.RunTicks(target - (int)a.World.Tick);
            if (target == 120) a.World.Commands.Enqueue(new AttackOrder([me], a.World.Squads.All.First(s => s.Faction == Villagers).Members[0]) { Tick = a.World.Tick });
            hashes.Add(a.World.StateHash());
        }
        var seen = (RelationChanges: a.Relations.Changes, a.Aggro.Started, Blows: a.Combat.Log.Count);
        // The scenario covers what it is for: fights started by hostility, blows, and relations changed by the blows (so the table is hashed).
        Assert.True(seen.RelationChanges > 0 && seen.Started > 0 && seen.Blows > 0, $"{seen}");
        Assert.NotEqual(hashes[^1], hashes[^2]);
        return hashes;
    }
}
