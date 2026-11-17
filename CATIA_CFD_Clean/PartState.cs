using System.Text.Json;

namespace CATIA_CFD_Clean;

/// <summary>
/// What the app holds for one part, by body name. Loaded from the part's .sqlite3 file before
/// each operation and written back after it (and part-way through long operations), so nothing
/// is lost when the app closes.
/// Names can point at bodies that no longer exist (deleted or undone in CATIA, or a CATPart
/// closed without saving); operations skip and report those, and Re-catalogue removes them.
/// </summary>
internal sealed class PartState
{
    public sealed record Plug(string Name, double Mm, bool Flipped);

    private readonly string _dbPath;

    public readonly string?[] Bodies = new string?[3]; // O-I Body 1 and 2 (index 0 unused)
    public readonly string?[] Seals = new string?[3];  // their seal-faces bodies
    public string? Result;                             // the green intersected seal
    public readonly List<string> Locked = new();
    public readonly List<Plug> Plugs = new();

    // Watertight test. The flow point (mm, on the inside surface of the flow volume) and the test
    // seals are kept between tests; the rest belongs to one test and is cleared by Clean up.
    public double[]? WtFlowPoint;
    public readonly List<string> WtPlugs = new();      // test seals (orange plugs), included in every test
    public readonly List<string> WtBodies = new();     // bodies under test (visible when it started)
    public readonly List<string> WtCopies = new();     // isolated copies of bodies and test seals
    public string? WtUnion;                            // body holding the union of the copies

    public bool WtActive => WtBodies.Count > 0 || WtCopies.Count > 0 || WtUnion is not null;

    private PartState(string dbPath) => _dbPath = dbPath;

    public static PartState Load(string dbPath)
    {
        var state = new PartState(dbPath);
        foreach (var row in PartDb.LoadState(dbPath))
        {
            using var meta = JsonDocument.Parse(row.Meta);
            JsonElement m = meta.RootElement;
            string? name = m.TryGetProperty("name", out var n) ? n.GetString() : null;
            switch (row.Kind)
            {
                case "oi":
                    switch (row.Key)
                    {
                        case "body1": state.Bodies[1] = name; break;
                        case "body2": state.Bodies[2] = name; break;
                        case "seal1": state.Seals[1] = name; break;
                        case "seal2": state.Seals[2] = name; break;
                        case "result": state.Result = name; break;
                    }
                    break;
                case "lock":
                    state.Locked.Add(row.Key);
                    break;
                case "plug":
                    state.Plugs.Add(new Plug(row.Key,
                        m.TryGetProperty("mm", out var mm) ? mm.GetDouble() : 0,
                        m.TryGetProperty("flipped", out var f) && f.GetBoolean()));
                    break;
                case "wt_body": state.WtBodies.Add(row.Key); break;
                case "wt_plug": state.WtPlugs.Add(row.Key); break;
                case "wt_copy": state.WtCopies.Add(row.Key); break;
                case "wt" when row.Key == "union": state.WtUnion = name; break;
                case "wt" when row.Key == "flow_point":
                    state.WtFlowPoint = new[] { m.GetProperty("x").GetDouble(), m.GetProperty("y").GetDouble(), m.GetProperty("z").GetDouble() };
                    break;
            }
        }
        return state;
    }

    public void Save() => PartDb.SaveState(_dbPath, ToRows());

    // Forgets every body name not in the part. Returns how many references were dropped.
    public int Prune(IReadOnlySet<string> existing)
    {
        int dropped = 0;
        string? Keep(string? name)
        {
            if (name is null || existing.Contains(name))
                return name;
            dropped++;
            return null;
        }

        for (int s = 1; s <= 2; s++)
        {
            Bodies[s] = Keep(Bodies[s]);
            Seals[s] = Keep(Seals[s]);
        }
        Result = Keep(Result);
        WtUnion = Keep(WtUnion);
        dropped += Locked.RemoveAll(n => !existing.Contains(n));
        dropped += Plugs.RemoveAll(p => !existing.Contains(p.Name));
        dropped += WtBodies.RemoveAll(n => !existing.Contains(n));
        dropped += WtPlugs.RemoveAll(n => !existing.Contains(n));
        dropped += WtCopies.RemoveAll(n => !existing.Contains(n));
        return dropped;
    }

    // Forgets the current test. Test seals and the flow point are kept.
    public void ClearWatertight()
    {
        WtBodies.Clear();
        WtCopies.Clear();
        WtUnion = null;
    }

    private IEnumerable<PartDb.StateRow> ToRows()
    {
        static PartDb.StateRow Named(string kind, string key, string name) =>
            new(kind, key, JsonSerializer.Serialize(new { name }));

        var slots = new (string Key, string? Name)[]
        {
            ("body1", Bodies[1]), ("body2", Bodies[2]),
            ("seal1", Seals[1]), ("seal2", Seals[2]),
            ("result", Result),
        };
        foreach (var (key, name) in slots)
        {
            if (name is not null)
                yield return Named("oi", key, name);
        }
        foreach (string name in Locked)
            yield return new("lock", name, "{}");
        foreach (var p in Plugs)
            yield return new("plug", p.Name, JsonSerializer.Serialize(new { mm = p.Mm, flipped = p.Flipped }));

        foreach (string name in WtBodies)
            yield return new("wt_body", name, "{}");
        foreach (string name in WtPlugs)
            yield return new("wt_plug", name, "{}");
        foreach (string name in WtCopies)
            yield return new("wt_copy", name, "{}");
        if (WtUnion is not null)
            yield return Named("wt", "union", WtUnion);
        if (WtFlowPoint is { } fp)
            yield return new("wt", "flow_point", JsonSerializer.Serialize(new { x = fp[0], y = fp[1], z = fp[2] }));
    }
}
