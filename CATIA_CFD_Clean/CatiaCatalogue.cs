using System.Diagnostics;

namespace CATIA_CFD_Clean;

// Reads the current state of the active CATPart and saves it as the catalogue next to the file.
internal static class CatiaCatalogue
{
    public sealed record Result(string DbPath, int Bodies, IReadOnlySet<string> Names, int DuplicateNames);

    /// <summary>
    /// Catalogues every body of the active part: tree position, visibility, and feature and
    /// geoset names. Bodies are keyed by name; names shared by several bodies are flagged, not
    /// guessed. Replaces the previous catalogue.
    /// </summary>
    public static Result Run(object document)
    {
        dynamic doc = document;
        string dbPath = PartDb.PathFor(document);

        var watch = Stopwatch.StartNew();
        List<(object Body, bool Shown)> bodies = CatiaBodies.GetBodiesWithVisibility(document);
        var names = bodies.Select(b => CatiaBodies.NameOf(b.Body)).ToList();
        var nameCounts = names.GroupBy(n => n).ToDictionary(g => g.Key, g => g.Count());

        var entities = new List<PartDb.Entity>(bodies.Count);
        for (int i = 0; i < bodies.Count; i++)
        {
            dynamic body = bodies[i].Body;
            string name = names[i];
            entities.Add(new PartDb.Entity("body", name, null, new
            {
                index = i + 1,
                shown = bodies[i].Shown,
                keyed = CatiaSeals.IsKey(name),
                duplicate_name = nameCounts[name] > 1,
                shapes = ChildNames(() => body.Shapes),
                geosets = ChildNames(() => body.HybridBodies),
            }));
        }
        int duplicates = nameCounts.Count(kv => kv.Value > 1);

        PartDb.SaveCatalogue(dbPath, new
        {
            document = (string)doc.FullName,
            bodies = bodies.Count,
            duplicate_names = duplicates,
            top_level_geosets = ChildNames(() => doc.Part.HybridBodies),
            scan_ms = watch.ElapsedMilliseconds,
        }, entities);

        return new Result(dbPath, bodies.Count, nameCounts.Keys.ToHashSet(), duplicates);
    }

    // Names of the items in a CATIA collection; empty if the collection is not available.
    private static List<string> ChildNames(Func<object> getCollection)
    {
        var names = new List<string>();
        try
        {
            dynamic items = getCollection();
            int count = items.Count;
            for (int i = 1; i <= count; i++)
                names.Add((string)items.Item(i).Name);
        }
        catch
        {
            // Not available on this node.
        }
        return names;
    }
}
