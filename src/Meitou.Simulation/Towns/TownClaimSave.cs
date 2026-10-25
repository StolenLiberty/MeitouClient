using System.Globalization;
using Meitou.Data.Fcs;

namespace Meitou.Simulation.Towns;

/// <summary>
/// The claimed towns in a save (docs/formats/save.md "CAMERA"): Meitou-only CAMERA keys, ignored by the original. int <c>meitou claim method</c> (<see cref="TownClaimMethod"/>),
/// and per claim <c>i</c> from 0 the string <c>meitou claim&lt;i&gt;</c> (the town's placement instance id) and <c>meitou claim from&lt;i&gt;</c> (the FACTION it was taken from; "" none).
/// </summary>
public static class TownClaimSave
{
    const string MethodKey = "meitou claim method", ClaimKey = "meitou claim", FromKey = "meitou claim from";

    /// <summary>A town's key: its placement instance id, else its position in the placements (worlds without level data).</summary>
    static string KeyOf(TownSite site) => site.InstanceId.Length > 0 ? site.InstanceId : "site:" + site.Index.ToString(CultureInfo.InvariantCulture);

    public static void Write(FcsRecord camera, TownClaimSystem towns, PopulationData data)
    {
        for (int k = 0; camera.Strings.ContainsKey(ClaimKey + k.ToString(CultureInfo.InvariantCulture)); k++)
        {
            camera.Strings.Remove(ClaimKey + k.ToString(CultureInfo.InvariantCulture));
            camera.Strings.Remove(FromKey + k.ToString(CultureInfo.InvariantCulture));
        }
        camera.Ints[MethodKey] = (int)towns.Method;
        int i = 0;
        foreach (var (site, from) in towns.Claims)
        {
            string n = i++.ToString(CultureInfo.InvariantCulture);
            camera.Strings[ClaimKey + n] = KeyOf(data.Sites[site]);
            camera.Strings[FromKey + n] = from >= 0 && from < data.Factions.Count ? data.Factions[from].Id : "";
        }
    }

    public static void Read(FcsRecord camera, TownClaimSystem towns, World world, PopulationData data)
    {
        var byKey = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var site in data.Sites) byKey.TryAdd(KeyOf(site), site.Index);
        var claims = new List<(int, int)>();
        for (int k = 0; camera.Strings.TryGetValue(ClaimKey + k.ToString(CultureInfo.InvariantCulture), out var key); k++)
        {
            if (!byKey.TryGetValue(key, out int site)) continue;   // a town the running data does not place
            string from = camera.Strings.GetValueOrDefault(FromKey + k.ToString(CultureInfo.InvariantCulture)) ?? "";
            claims.Add((site, from.Length > 0 ? data.FactionIndex(from) : -1));
        }
        var method = camera.Ints.TryGetValue(MethodKey, out int m) && Enum.IsDefined((TownClaimMethod)m) ? (TownClaimMethod)m : TownClaimMethod.Ruler;
        towns.Restore(world, method, claims);
    }
}
