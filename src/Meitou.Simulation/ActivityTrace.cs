using System.Reflection;

namespace Meitou.Simulation;

/// <summary>
/// The activity recorder (Meitou, a debugging aid; docs/simulation.md "Activity trace"): what each of the player's characters is doing, written down every time it changes, with the system
/// (and phase) whose step changed it; and every command a system gives one of them (deferred) or the player gives them, with who gave it. The host writes the lines to activity.txt.
/// Not hashed, not saved. Reads only, apart from its own lists.
/// </summary>
public sealed class ActivityTrace
{
    public bool Enabled { get; set; } = true;

    readonly List<string> lines = [];
    readonly Dictionary<int, (Key Key, string Text)> was = [];

    /// <summary>What is compared each step (cheap); the text is made only when it changes.</summary>
    readonly record struct Key(byte Task, Locks.HeldState Held, string? Build, string? Operate, string? Haul, bool Combat);
    readonly List<int> players = [];
    static readonly Dictionary<Type, PropertyInfo[]> idProperties = [];

    /// <summary>The tick of the player's last order to each character (kept whether or not the recorder writes).</summary>
    readonly Dictionary<CharacterId, long> playerOrdered = [];

    /// <summary>The tick the player last gave this character an order (long.MinValue: never).</summary>
    public long LastPlayerOrder(CharacterId id) => playerOrdered.TryGetValue(id, out long t) ? t : long.MinValue;

    /// <summary>The player's command: its characters are remembered as ordered now (and the command written when recording).</summary>
    internal void PlayerCommand(World world, SimCommand command)
    {
        foreach (var p in PropertiesOf(command.GetType()))
            foreach (var id in p.GetValue(command) switch { CharacterId one => [one], IEnumerable<CharacterId> many => many, _ => Array.Empty<CharacterId>() })
                playerOrdered[id] = world.Tick;
        if (Enabled) Command(world, command, "you");
    }

    /// <summary>The system running now (set by the world's loop), named on the commands it defers.</summary>
    internal string Current { get; set; } = "";

    /// <summary>The lines written since the last call (the host, between ticks).</summary>
    public IReadOnlyList<string> Take()
    {
        lock (lines)
        {
            if (lines.Count == 0) return [];
            var taken = lines.ToArray();
            lines.Clear();
            return taken;
        }
    }

    void Write(World world, string text)
    {
        double h = ((world.GameHours % 24) + 24) % 24;
        int day = (int)(world.GameHours / 24) + 1;
        lock (lines) lines.Add($"day {day} {(int)h:00}:{(int)(h % 1 * 60):00} t{world.Tick}  {text}");
    }

    /// <summary>At the start of a tick: who is the player's.</summary>
    internal void BeginTick(World world)
    {
        players.Clear();
        var table = world.Characters;
        for (int s = 0; s < table.HighWater; s++)
            if (table.Next[s].Alive && table.Cold(s) is { IsPlayer: true }) players.Add(s);
    }

    /// <summary>After a system's step: each of the player's characters whose activity changed, and the step that changed it.</summary>
    internal void Check(World world, string step)
    {
        var table = world.Characters;
        foreach (int s in players)
        {
            if (!table.Next[s].Alive || table.Cold(s) is not { } c) continue;
            var key = new Key(table.Next[s].Task, c.InSomething, c.BuildSite, c.OperateSite, c.HaulTo, c.InCombat);
            bool seen = was.TryGetValue(s, out var before);
            if (seen && before.Key == key) continue;
            string now = Describe(world, s, c);
            if (seen && before.Text != now) Write(world, $"{c.Name}: {before.Text}  ->  {now}   [{step}]");
            was[s] = (key, now);
        }
    }

    string Describe(World world, int s, CharacterCold c)
    {
        var task = (CharacterTask)world.Characters.Next[s].Task;
        string text = task.ToString();
        if (c.BuildSite is { } b && task is CharacterTask.Build or CharacterTask.DeliverMaterials or CharacterTask.FetchMaterials) text += " " + SiteName(world, b);
        if (c.OperateSite is { } o && task is CharacterTask.Operate or CharacterTask.Sleep) text += " " + SiteName(world, o);
        if (c.HaulTo is { } h && task == CharacterTask.Haul) text += " to " + SiteName(world, h);
        if (c.InSomething != Locks.HeldState.None) text += " (in " + c.InSomething + ")";
        if (c.InCombat) text += " (in combat)";
        return text;
    }

    static string SiteName(World world, string id) =>
        world.Buildings.Find(id) is { } b ? (b.Name.Length > 0 ? b.Name : b.Record) : world.Machines.Find(id)?.Data.Name ?? (world.Fixtures.Find(id) is { } f ? f.Kind + " " + id : id);

    /// <summary>A command reaching the player's characters: <paramref name="from"/> the system that deferred it, or the player.</summary>
    internal void Command(World world, SimCommand command, string from)
    {
        var table = world.Characters;
        var names = new List<string>();
        foreach (var p in PropertiesOf(command.GetType()))
        {
            var value = p.GetValue(command);
            IEnumerable<CharacterId> ids = value switch { CharacterId one => [one], IEnumerable<CharacterId> many => many, _ => [] };
            foreach (var id in ids)
                if (table.TryResolveNext(id, out int s) && table.Cold(s) is { IsPlayer: true } c) names.Add(c.Name);
        }
        if (names.Count == 0) return;
        Write(world, $"{from} ordered {command.GetType().Name} for {string.Join(", ", names.Distinct())}");
    }

    static PropertyInfo[] PropertiesOf(Type type)
    {
        lock (idProperties)
        {
            if (!idProperties.TryGetValue(type, out var props))
                idProperties[type] = props = [.. type.GetProperties().Where(p => (p.PropertyType == typeof(CharacterId) || typeof(IEnumerable<CharacterId>).IsAssignableFrom(p.PropertyType))
                    && p.Name is not ("Target" or "Wearer" or "Captive" or "Prisoner" or "Victim" or "Followed"))];
            return props;
        }
    }
}
