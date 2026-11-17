using System.Reflection;
using System.Text;
using System.Windows.Forms;

namespace CATIA_CFD_Clean;

// Read-only probe: what identifying information does CATIA expose for bodies, and are names unique?
// Run: CATIA_CFD_Clean.exe --id-spike   (needs a CATIA part open and active).
// Writes catia_id_spike.txt next to the exe and shows where it went. Changes nothing in CATIA.
internal static class CatiaIdSpike
{
    // Members that might carry a stable identity. Missing ones are reported as such.
    private static readonly string[] BodyMembers =
    {
        "Name", "Identifier", "Id", "ID", "UID", "Guid", "GUID", "UserRefProperties",
        "Parameters", "Attributes", "ReferenceProperties", "Shapes", "Features", "Parent"
    };

    private static readonly string[] PartMembers =
    {
        "Name", "FullName", "UserRefProperties", "Parameters", "Bodies", "HybridBodies"
    };

    public static void Run()
    {
        var sb = new StringBuilder();
        string outPath = Path.Combine(AppContext.BaseDirectory, "catia_id_spike.txt");
        try
        {
            dynamic doc = CatiaConnection.GetActiveDocument();
            dynamic part;
            try
            {
                part = doc.Part;
            }
            catch (Exception ex)
            {
                sb.AppendLine("Active document is not a part: " + ex.Message);
                return;
            }

            sb.AppendLine($"Document: {Safe(() => (string)doc.FullName)}");
            sb.AppendLine();
            sb.AppendLine("== Part-level members ==");
            foreach (string m in PartMembers)
                ProbeMember(sb, "part", (object)part, m);

            int count = part.Bodies.Count;
            sb.AppendLine();
            sb.AppendLine($"== Bodies: {count} ==");

            var names = new Dictionary<string, List<int>>();
            for (int i = 1; i <= count; i++)
            {
                object body = part.Bodies.Item(i);
                string name = Safe(() => (string)((dynamic)body).Name) ?? "<no name>";
                if (!names.TryGetValue(name, out var list))
                    names[name] = list = new List<int>();
                list.Add(i);
            }

            // Probe the first few bodies in detail, the rest only by name.
            for (int i = 1; i <= count; i++)
            {
                object body = part.Bodies.Item(i);
                string name = Safe(() => (string)((dynamic)body).Name) ?? "<no name>";
                bool dup = names[name].Count > 1;
                sb.AppendLine($"[{i}] \"{name}\"{(dup ? "  <-- DUPLICATE NAME" : "")}");

                if (i <= 3)
                    foreach (string m in BodyMembers)
                        ProbeMember(sb, "  body", body, m);
            }

            sb.AppendLine();
            sb.AppendLine("== Name uniqueness ==");
            int dupGroups = names.Count(kv => kv.Value.Count > 1);
            sb.AppendLine(dupGroups == 0
                ? "All body names are unique in this part."
                : $"{dupGroups} name(s) are shared by more than one body. Names alone are NOT a safe key.");

            sb.AppendLine();
            sb.AppendLine("Done. Nothing in CATIA was changed.");
        }
        catch (Exception ex)
        {
            sb.AppendLine("FAILED: " + ex);
        }
        finally
        {
            File.WriteAllText(outPath, sb.ToString());
            MessageBox.Show($"CATIA id spike written to:\n{outPath}", "CATIA ID spike");
        }
    }

    // Reads a member through COM reflection and records its type and value, or why it failed.
    private static void ProbeMember(StringBuilder sb, string label, object target, string member)
    {
        try
        {
            object? value = target.GetType().InvokeMember(member,
                BindingFlags.GetProperty, null, target, null);
            sb.AppendLine($"{label}.{member} = {Describe(value)}");
        }
        catch (Exception ex)
        {
            string reason = ex.InnerException?.Message ?? ex.Message;
            sb.AppendLine($"{label}.{member} : not available ({reason})");
        }
    }

    // Shows strings as-is, and for COM collections also reports the Count when there is one.
    private static string Describe(object? value)
    {
        if (value is null)
            return "null";
        if (value is string s)
            return $"\"{s}\"";
        try
        {
            object? count = value.GetType().InvokeMember("Count",
                BindingFlags.GetProperty, null, value, null);
            if (count is not null)
                return $"collection(Count={count})";
        }
        catch { /* not a collection */ }
        return $"{value.GetType().FullName}";
    }

    private static T? Safe<T>(Func<T> read)
    {
        try { return read(); }
        catch { return default; }
    }
}
