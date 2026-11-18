using System.Diagnostics;
using System.Reflection;

namespace CATIA_CFD_Clean;

/// <summary>
/// Watertightness test. The bodies under test (including any visible test seals) are copied as
/// isolated solids (no link to the originals), the copies are unioned in a temporary body, and the
/// shell of the union's boundary that holds the flow point is extracted into a temporary geoset.
/// A watertight flow volume is a shell of its own; a leak merges it with the outer skin. The original bodies are never modified. <see cref="CleanUp"/> removes the
/// geoset, the union and the copies.
/// </summary>
internal static class CatiaWatertight
{
    public const string ShellsGeosetName = "Watertight test shells";
    private const int NoPropagation = 3;     // CATGSMExtractPropagationType

    // True for a body with no features (such as an unused PartBody): it has no solid to copy.
    public static bool IsEmpty(object body) => (int)((dynamic)body).Shapes.Count == 0;

    /// <summary>
    /// Copies the bodies into the part as isolated solids (paste "as result without link") and
    /// gives each copy a CFD_ key. CATIA pastes nothing at all when any one body cannot be copied,
    /// so on a shortfall the bodies are pasted one at a time. Returns the copies and the names of
    /// the bodies CATIA would not copy.
    /// </summary>
    public static (List<object> Copies, List<string> Failed) CopyAsResults(dynamic doc, IReadOnlyList<object> bodies)
    {
        List<object> copies = PasteAsResults(doc, bodies);
        var failed = new List<string>();
        if (copies.Count != bodies.Count)
        {
            foreach (object partial in copies)
                CatiaSeals.TryDelete(doc, partial);
            copies.Clear();
            foreach (object body in bodies)
            {
                List<object> one = PasteAsResults(doc, new[] { body });
                if (one.Count == 1)
                {
                    copies.Add(one[0]);
                    continue;
                }
                failed.Add(CatiaBodies.NameOf(body));
                foreach (object extra in one)
                    CatiaSeals.TryDelete(doc, extra);
            }
        }

        var keys = CatiaSeals.NextKeys(doc, copies.Count);
        for (int i = 0; i < copies.Count; i++)
            ((dynamic)copies[i]).Name = keys[i];
        return (copies, failed);
    }

    // Pastes copies of the bodies as isolated results. Returns the bodies that appeared in the part
    // (new bodies are appended at the end); empty if CATIA pasted nothing.
    private static List<object> PasteAsResults(dynamic doc, IReadOnlyList<object> bodies)
    {
        dynamic part = doc.Part;
        dynamic selection = doc.Selection;
        int before = part.Bodies.Count;
        try
        {
            selection.Clear();
            foreach (object body in bodies)
                selection.Add(body);
            selection.Copy();
            selection.Clear();
            selection.Add(part);
            selection.PasteSpecial("CATPrtResultWithOutLink");
        }
        finally
        {
            try { selection.Clear(); } catch { /* document closed */ }
        }

        int after = part.Bodies.Count;
        var pasted = new List<object>();
        for (int i = before + 1; i <= after; i++)
            pasted.Add(part.Bodies.Item(i));
        return pasted;
    }

    // Unions the copies into a new body. Returns that body.
    public static object Union(dynamic doc, IReadOnlyList<object> copies)
    {
        dynamic part = doc.Part;
        dynamic union = part.Bodies.Add();
        union.Name = CatiaSeals.NextKey(doc);
        part.InWorkObject = union;
        foreach (object copy in copies)
            part.ShapeFactory.AddNewAdd(copy);
        part.Update();
        return union;
    }

    /// <summary>
    /// Returns the coordinates (mm) of the one point selected in CATIA (a vertex or a point
    /// feature). Anything else is refused with the message <paramref name="ask"/>.
    /// </summary>
    public static double[] GetSelectedPoint(dynamic doc, string ask)
    {
        dynamic selection = doc.Selection;
        if ((int)selection.Count != 1)
            throw new InvalidOperationException(ask);

        dynamic element = selection.Item(1);
        object reference;
        try { reference = element.Reference; }
        catch { reference = doc.Part.CreateReferenceFromObject(element.Value); }

        object measurable = doc.GetWorkbench("SPAWorkbench").GetMeasurable(reference);
        try
        {
            return Measure(measurable, "GetPoint", 3);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"The selection is not a point. {ask}", ex);
        }
    }

    // Calls a CATIA measure method that fills an array (GetPoint, GetCOG, ...). The array must be
    // passed by reference, which plain dynamic calls do not do.
    private static double[] Measure(object measurable, string method, int size)
    {
        var args = new object[] { new object[size] };
        var byRef = new ParameterModifier(1);
        byRef[0] = true;
        measurable.GetType().InvokeMember(method, BindingFlags.InvokeMethod, null, measurable, args,
            new[] { byRef }, null, null);
        return ((Array)args[0]).Cast<object>().Select(Convert.ToDouble).ToArray();
    }

    /// <param name="FlowMm2">Area of the shell that holds the flow point.</param>
    /// <param name="SkinMm2">Area of the union's whole boundary (all shells).</param>
    /// <param name="FlowDistanceMm">Distance from the flow point to that shell.</param>
    public sealed record FlowShellResult(double FlowMm2, double SkinMm2, double FlowDistanceMm, long Ms);

    /// <summary>
    /// Extracts the shell of <paramref name="union"/> that holds <paramref name="flowPoint"/> (mm)
    /// into a new geoset named <see cref="ShellsGeosetName"/>: an extract of the whole union body
    /// gives its boundary (one domain per shell), and Near picks the domain closest to a marker at
    /// the flow point. If that domain is the whole boundary, the flow volume is not separated from
    /// the outer skin. A fixed number of CATIA operations, whatever the face count. Only the flow
    /// shell and the marker are left shown.
    /// </summary>
    public static FlowShellResult ExtractFlowShell(dynamic doc, object union, double[] flowPoint)
    {
        var watch = Stopwatch.StartNew();
        dynamic part = doc.Part;
        dynamic hsf = part.HybridShapeFactory;
        dynamic spa = doc.GetWorkbench("SPAWorkbench");
        dynamic selection = doc.Selection;

        dynamic geoset = part.HybridBodies.Add();
        geoset.Name = ShellsGeosetName;

        dynamic skin = hsf.AddNewExtract(part.CreateReferenceFromObject(union));
        skin.PropagationType = NoPropagation;
        skin.ComplementaryExtract = false;
        skin.IsFederated = false;
        geoset.AppendHybridShape(skin);

        dynamic marker = hsf.AddNewPointCoord(flowPoint[0], flowPoint[1], flowPoint[2]);
        geoset.AppendHybridShape(marker);
        dynamic flow = hsf.AddNewNear(part.CreateReferenceFromObject(skin), part.CreateReferenceFromObject(marker));
        geoset.AppendHybridShape(flow);
        part.InWorkObject = geoset;
        part.Update();

        double Area(object o) => 1e6 * (double)spa.GetMeasurable(part.CreateReferenceFromObject(o)).Area;
        double flowMm2 = Area(flow);
        double skinMm2 = Area(skin);
        double distanceMm = 1000 * (double)spa.GetMeasurable(part.CreateReferenceFromObject(marker))
            .GetMinimumDistance(part.CreateReferenceFromObject(flow));

        marker.Name = "Flow point";
        flow.Name = $"Flow volume ({flowMm2:0} mm2)";
        skin.Name = $"Whole skin ({skinMm2:0} mm2)";

        // Leave only the flow shell and the marker shown.
        try
        {
            selection.Clear();
            selection.Add(skin);
            selection.VisProperties.SetShow(1); // catVisPropertyNoShowAttr
        }
        finally
        {
            try { selection.Clear(); } catch { /* document closed */ }
        }

        return new FlowShellResult(flowMm2, skinMm2, distanceMm, watch.ElapsedMilliseconds);
    }

    // Hides every body and shows the test geoset. Other top-level geosets are left as they are.
    public static void ShowOnlyShells(dynamic doc)
    {
        CatiaBodies.ShowExactlyBodies(doc, Array.Empty<object>());
        dynamic selection = doc.Selection;
        try
        {
            selection.Clear();
            selection.Add(doc.Part.HybridBodies.Item(ShellsGeosetName));
            selection.VisProperties.SetShow(0); // catVisPropertyShowAttr
        }
        finally
        {
            try { selection.Clear(); } catch { /* document closed */ }
        }
    }

    /// <summary>
    /// Deletes the test's shells geoset(s) and the named bodies (union and copies) that still
    /// exist. Returns how many items were deleted.
    /// </summary>
    public static int CleanUp(dynamic doc, IEnumerable<string> bodyNames)
    {
        dynamic part = doc.Part;
        var items = new List<object>();

        int geosets = part.HybridBodies.Count;
        for (int i = 1; i <= geosets; i++)
        {
            dynamic geoset = part.HybridBodies.Item(i);
            if ((string)geoset.Name == ShellsGeosetName)
                items.Add(geoset);
        }
        foreach (string name in bodyNames)
        {
            if (CatiaBodies.FindBody(doc, name) is { } body)
                items.Add(body);
        }
        if (items.Count == 0)
            return 0;

        dynamic selection = doc.Selection;
        try
        {
            selection.Clear();
            foreach (object item in items)
                selection.Add(item);
            selection.Delete();
        }
        catch
        {
            // One delete failed: fall back to deleting item by item, best effort.
            foreach (object item in items)
                CatiaSeals.TryDelete(doc, item);
        }
        finally
        {
            try { selection.Clear(); } catch { /* document closed */ }
        }
        part.Update();
        return items.Count;
    }
}
