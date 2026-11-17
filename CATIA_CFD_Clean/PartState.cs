using System.Text.Json;

namespace CATIA_CFD_Clean;

/// <summary>
/// What the app holds for one part, by body name. Loaded from the part's .sqlite3 file before
/// each operation and written back after it, so nothing is lost when the app closes.
/// Names can point at bodies that no longer exist (deleted or undone in CATIA, or a CATPart
/// closed without saving); operations skip and report those, and Re-catalogue removes them.
/// </summary>
internal sealed class PartState
{
    public sealed record Plug(string Name, double Mm, bool Flipped);

    public readonly string?[] Bodies = new string?[3]; // O-I Body 1 and 2 (index 0 unused)
    public readonly string?[] Seals = new string?[3];  // their seal-faces bodies
    public string? Result;                             // the green intersected seal
    public readonly List<string> Locked = new();
    public readonly List<Plug> Plugs = new();

    public static PartState Load(string dbPath)
    {
        var state = new PartState();
        foreach (var row in PartDb.LoadState(dbPath))
        {
            using var meta = JsonDocument.Parse(row.Meta);
            JsonElement m = meta.RootElement;
            switch (row.Kind)
            {
                case "oi":
                    string? name = m.TryGetProperty("name", out var n) ? n.GetString() : null;
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
            }
        }
        return state;
    }

    public void Save(string dbPath) => PartDb.SaveState(dbPath, ToRows());

    // Forgets every name not in the part. Returns how many references were dropped.
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
        dropped += Locked.RemoveAll(n => !existing.Contains(n));
        dropped += Plugs.RemoveAll(p => !existing.Contains(p.Name));
        return dropped;
    }

    private IEnumerable<PartDb.StateRow> ToRows()
    {
        PartDb.StateRow Slot(string key, string? name) =>
            new("oi", key, JsonSerializer.Serialize(new { name }));

        var slots = new (string Key, string? Name)[]
        {
            ("body1", Bodies[1]), ("body2", Bodies[2]),
            ("seal1", Seals[1]), ("seal2", Seals[2]),
            ("result", Result),
        };
        foreach (var (key, name) in slots)
        {
            if (name is not null)
                yield return Slot(key, name);
        }
        foreach (string name in Locked)
            yield return new("lock", name, "{}");
        foreach (var p in Plugs)
            yield return new("plug", p.Name, JsonSerializer.Serialize(new { mm = p.Mm, flipped = p.Flipped }));
    }
}
