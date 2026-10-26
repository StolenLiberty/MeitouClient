using System.Text;

namespace Meitou.Game;

/// <summary>activity.txt next to MeitouClient.exe: the world's activity trace (<see cref="Meitou.Simulation.ActivityTrace"/>), started afresh each run, cut back past <see cref="Cap"/>.</summary>
static class ActivityFile
{
    const long Cap = 20 << 20;
    static StreamWriter? writer;
    static bool failed;

    public static void Write(IReadOnlyList<string> lines)
    {
        if (lines.Count == 0 || failed) return;
        try
        {
            writer ??= new StreamWriter(new FileStream(Path.Combine(AppContext.BaseDirectory, "activity.txt"), FileMode.Create, FileAccess.Write, FileShare.Read), new UTF8Encoding(false));
            if (writer.BaseStream.Length > Cap) { writer.Flush(); writer.BaseStream.SetLength(0); }
            foreach (var line in lines) writer.WriteLine(line);
            writer.Flush();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { failed = true; }
    }
}
