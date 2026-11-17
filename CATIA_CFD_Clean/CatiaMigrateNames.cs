using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace CATIA_CFD_Clean;

// One-time migration: gives the app's generated bodies unique serial keys (CFD_0001, CFD_0002, ...).
// Only bodies whose names exactly match what this app creates are renamed: "Seal body N", "Seal", "Plug body".
// Keys are assigned in tree order. Original bodies (PartBody, Body.N) are left alone.
// Dry run by default. Run with --migrate-names --apply to rename. Nothing is saved to disk.
// The report next to the exe lists every old and new name, so the change can be reversed by hand.
internal static class CatiaMigrateNames
{
    private static readonly Regex GeneratedName = new(@"^(Seal body \d+|Seal|Plug body)$", RegexOptions.CultureInvariant);

    public static void Run(bool apply)
    {
        var sb = new StringBuilder();
        string outPath = Path.Combine(AppContext.BaseDirectory, "catia_name_migration.txt");
        try
        {
            dynamic doc = CatiaConnection.GetActiveDocument();
            dynamic part = doc.Part;
            sb.AppendLine($"Document: {(string)doc.FullName}");
            sb.AppendLine(apply ? "Mode: APPLY" : "Mode: DRY RUN (no names changed)");
            sb.AppendLine();

            int count = part.Bodies.Count;
            var existing = new HashSet<string>();
            var targets = new List<(int Index, dynamic Body, string Old)>();

            for (int i = 1; i <= count; i++)
            {
                dynamic body = part.Bodies.Item(i);
                string name = (string)body.Name;
                existing.Add(name);
                if (GeneratedName.IsMatch(name))
                    targets.Add((i, body, name));
            }

            // Serial keys must not collide with any name already in the part.
            var plan = new List<(int Index, dynamic Body, string Old, string New)>();
            int serial = 1;
            foreach (var t in targets)
            {
                string key;
                do key = $"CFD_{serial++:0000}";
                while (existing.Contains(key));
                plan.Add((t.Index, t.Body, t.Old, key));
            }

            sb.AppendLine($"Bodies in part: {count}. Generated bodies to rename: {plan.Count}.");
            sb.AppendLine();
            foreach (var p in plan)
                sb.AppendLine($"[{p.Index}] \"{p.Old}\" -> \"{p.New}\"");

            if (apply)
            {
                int done = 0;
                foreach (var p in plan)
                {
                    p.Body.Name = p.New;
                    string after = (string)p.Body.Name;
                    if (after != p.New)
                        throw new InvalidOperationException(
                            $"Body [{p.Index}] renamed to \"{after}\", expected \"{p.New}\". Stopped after {done} rename(s).");
                    done++;
                }
                sb.AppendLine();
                sb.AppendLine($"Renamed {done} body(ies). The document is NOT saved. Save it in CATIA when you have checked it.");
            }
            else
            {
                sb.AppendLine();
                sb.AppendLine("Dry run only. Run with --migrate-names --apply to rename.");
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine("FAILED: " + ex);
        }
        finally
        {
            File.WriteAllText(outPath, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            MessageBox.Show($"Name migration report written to:\n{outPath}", "CATIA name migration");
        }
    }
}
