namespace CATIA_CFD_Clean;

// Body selection and visibility operations on a CATIA part document.
internal static class CatiaBodies
{
    // CATIA CatVisPropertyShowAttribute values
    private const int ShowAttr = 0;
    private const int NoShowAttr = 1;

    private const int MaxParentDepth = 20;

    /// <summary>
    /// While alive, stops CATIA from highlighting every item added to the selection
    /// (HSOSynchronized) and from redrawing the screen after each change (RefreshDisplay).
    /// Both are the main cost when a macro selects many items. The previous values are restored
    /// on dispose. Only for short visibility operations; never around a dialog, or CATIA would
    /// not redraw while the user works.
    /// </summary>
    private sealed class QuietDisplay : IDisposable
    {
        private readonly dynamic? _app;
        private readonly bool _hso;
        private readonly bool _refresh;

        public QuietDisplay(object doc)
        {
            try
            {
                dynamic app = ((dynamic)doc).Application;
                _hso = (bool)app.HSOSynchronized;
                _refresh = (bool)app.RefreshDisplay;
                _app = app;
            }
            catch
            {
                return; // settings not readable: leave CATIA as it is
            }
            try { _app.HSOSynchronized = false; } catch { /* keep going without it */ }
            try { _app.RefreshDisplay = false; } catch { /* keep going without it */ }
        }

        public void Dispose()
        {
            if (_app is null)
                return;
            try { _app.HSOSynchronized = _hso; } catch { /* CATIA gone */ }
            try { _app.RefreshDisplay = _refresh; } catch { /* CATIA gone */ }
        }
    }

    // Returns the body that owns the current selection (spec tree or viewer).
    // A selected face, edge, feature, etc. resolves to its parent body.
    // Returns null if nothing is selected; throws if the selection has no body owner.
    public static object? GetSelectedBody(dynamic doc)
    {
        dynamic selection = doc.Selection;
        if (selection.Count == 0)
            return null;

        object item = selection.Item(1).Value;
        var trail = new List<string>();
        object? body = FindOwningBody(item, trail);
        if (body is null)
            throw new InvalidOperationException(
                "Selected item does not belong to a body. Chain: " + string.Join(" -> ", trail));

        return body;
    }

    // Returns the distinct bodies owning every item in the current selection.
    public static List<object> GetSelectedBodies(dynamic doc)
    {
        dynamic selection = doc.Selection;
        int count = selection.Count;
        var bodies = new List<object>();
        var names = new HashSet<string>();
        for (int i = 1; i <= count; i++)
        {
            object item = selection.Item(i).Value;
            var trail = new List<string>();
            object? body = FindOwningBody(item, trail);
            if (body is null)
                throw new InvalidOperationException(
                    $"Selected item {i} does not belong to a body. Chain: " + string.Join(" -> ", trail));
            if (names.Add(NameOf(body)))
                bodies.Add(body);
        }
        return bodies;
    }

    // Returns the bodies of the part that are currently shown.
    public static List<object> GetVisibleBodies(dynamic doc)
    {
        using var quiet = new QuietDisplay((object)doc);
        dynamic part = doc.Part;
        dynamic selection = doc.Selection;
        int count = part.Bodies.Count;
        var visible = new List<object>();
        for (int i = 1; i <= count; i++)
        {
            object body = part.Bodies.Item(i);
            selection.Clear();
            selection.Add(body);
            int show = ShowAttr;
            selection.VisProperties.GetShow(ref show);
            selection.Clear();
            if (show == ShowAttr)
                visible.Add(body);
        }
        return visible;
    }

    // Hides every body in the part, then shows only the given body.
    public static void ShowOnly(dynamic doc, object body)
    {
        using var quiet = new QuietDisplay((object)doc);
        dynamic part = doc.Part;
        dynamic selection = doc.Selection;
        int count = part.Bodies.Count;

        selection.Clear();
        for (int i = 1; i <= count; i++)
            selection.Add(part.Bodies.Item(i));
        if (count > 0)
            selection.VisProperties.SetShow(NoShowAttr);
        selection.Clear();

        selection.Add(body);
        selection.VisProperties.SetShow(ShowAttr);
        selection.Clear();

        // Geometrical sets inside the body are shown with it, except the seal's helper surface.
        try
        {
            dynamic body_ = body;
            int geosets = body_.HybridBodies.Count;
            var sealGeosets = new List<object>();
            for (int i = 1; i <= geosets; i++)
            {
                dynamic geoset = body_.HybridBodies.Item(i);
                if (CatiaSeals.IsHelperGeoset((string)geoset.Name))
                    sealGeosets.Add(geoset);
                else
                    selection.Add(geoset);
            }
            if (selection.Count > 0)
                selection.VisProperties.SetShow(ShowAttr);
            selection.Clear();

            foreach (object geoset in sealGeosets)
                selection.Add(geoset);
            if (sealGeosets.Count > 0)
                selection.VisProperties.SetShow(NoShowAttr);
        }
        catch
        {
            // Bodies without geosets: nothing more to show.
        }
        selection.Clear();
    }

    // Shows exactly the given bodies (and the geosets inside them) and hides
    // everything else: all other bodies, including PartBody, and top-level geosets.
    public static void ShowOnlyBodies(dynamic doc, IReadOnlyList<object> bodiesToShow)
    {
        using var quiet = new QuietDisplay((object)doc);
        dynamic part = doc.Part;
        dynamic selection = doc.Selection;

        // Hide everything first.
        selection.Clear();
        int bodyCount = part.Bodies.Count;
        for (int i = 1; i <= bodyCount; i++)
        {
            dynamic body = part.Bodies.Item(i);
            selection.Add(body);

            int geosets = body.HybridBodies.Count;
            for (int j = 1; j <= geosets; j++)
                selection.Add(body.HybridBodies.Item(j));
        }
        int topLevelGeosets = part.HybridBodies.Count;
        for (int i = 1; i <= topLevelGeosets; i++)
            selection.Add(part.HybridBodies.Item(i));
        if (selection.Count > 0)
            selection.VisProperties.SetShow(NoShowAttr);
        selection.Clear();

        // Then show only the wanted bodies and their geosets.
        foreach (object body in bodiesToShow)
        {
            dynamic node = body;
            selection.Add(node);
            int geosets = node.HybridBodies.Count;
            for (int j = 1; j <= geosets; j++)
            {
                dynamic geoset = node.HybridBodies.Item(j);
                if (!CatiaSeals.IsHelperGeoset((string)geoset.Name))
                    selection.Add(geoset);
            }
        }
        if (selection.Count > 0)
            selection.VisProperties.SetShow(ShowAttr);
        selection.Clear();
    }

    /// <summary>
    /// Toggles the given bodies as a group. By default: if all are shown they are hidden,
    /// otherwise all are shown. With <paramref name="hideIfAnyShown"/> (the face-toggle rule): if any is
    /// shown they are all hidden, otherwise all are shown. Only these bodies (and their
    /// non-helper geosets) are touched; nothing else in the part changes, and their solid
    /// features are left alone. Returns true if they ended up shown.
    /// </summary>
    public static bool ToggleBodies(dynamic doc, IReadOnlyList<object> bodies, bool hideIfAnyShown = false)
    {
        using var quiet = new QuietDisplay((object)doc);
        bool show = hideIfAnyShown ? !AnyShown(doc, bodies) : !AllShown(doc, bodies);
        SetBodiesShown(doc, bodies, show);
        return show;
    }

    // True if at least one given body is shown. Stops at the first shown one.
    private static bool AnyShown(dynamic doc, IReadOnlyList<object> bodies)
    {
        dynamic selection = doc.Selection;
        try
        {
            foreach (object body in bodies)
            {
                selection.Clear();
                selection.Add(body);
                int show = ShowAttr;
                selection.VisProperties.GetShow(ref show);
                if (show == ShowAttr)
                    return true;
            }
            return false;
        }
        finally
        {
            TryClear(selection);
        }
    }

    /// <summary>
    /// Toggles every body in the part: if all are shown they are all hidden, otherwise all are
    /// shown. Returns true if they ended up shown.
    /// </summary>
    public static bool ToggleAllBodies(dynamic doc)
    {
        dynamic part = doc.Part;
        int count = part.Bodies.Count;
        var all = new List<object>(count);
        for (int i = 1; i <= count; i++)
            all.Add(part.Bodies.Item(i));
        return ToggleBodies(doc, all);
    }

    // True if every given body is shown. Stops at the first hidden one.
    private static bool AllShown(dynamic doc, IReadOnlyList<object> bodies)
    {
        dynamic selection = doc.Selection;
        try
        {
            foreach (object body in bodies)
            {
                selection.Clear();
                selection.Add(body);
                int show = ShowAttr;
                selection.VisProperties.GetShow(ref show);
                if (show != ShowAttr)
                    return false;
            }
            return true;
        }
        finally
        {
            TryClear(selection);
        }
    }

    // Shows or hides the given bodies with their non-helper geosets in a single call.
    // Helper geosets (the seal's construction surface) are never touched, so they stay hidden.
    private static void SetBodiesShown(dynamic doc, IReadOnlyList<object> bodies, bool show)
    {
        if (bodies.Count == 0)
            return;

        dynamic selection = doc.Selection;
        try
        {
            selection.Clear();
            foreach (object body in bodies)
            {
                selection.Add(body);
                AddNonHelperGeosetsToSelection(selection, body);
            }
            selection.VisProperties.SetShow(show ? ShowAttr : NoShowAttr);
        }
        finally
        {
            TryClear(selection);
        }
    }

    /// <summary>
    /// Returns the distinct bodies to toggle: the bodies owning selected faces or solid
    /// features, plus any selected bodies themselves (e.g. from a rectangle selection).
    /// Other selections are ignored; items whose owning body cannot be resolved are skipped.
    /// </summary>
    public static List<object> GetBodiesOfSelection(dynamic doc)
    {
        dynamic selection = doc.Selection;
        int count = selection.Count;
        var bodies = new List<object>();
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var names = new HashSet<string>();
        var resolver = new BodyResolver();
        for (int i = 1; i <= count; i++)
        {
            object? body;
            try
            {
                dynamic element = selection.Item(i);
                (body, bool viaNonSolid) = resolver.Resolve((object)element.Value);
                // Anything reached through a sketch or geoset only counts if it is a face.
                if (body is not null && viaNonSolid && !IsFace(element))
                    body = null;
            }
            catch
            {
                continue; // item vanished or is not resolvable; skip it
            }

            // Cheap reference check first; the name check catches distinct proxies of one body.
            if (body is not null && seen.Add(body) && names.Add(NameOf(body)))
                bodies.Add(body);
        }
        return bodies;
    }

    /// <summary>
    /// Shows every solid feature (Pad, Pocket, Sketch, ...) of one body, e.g. to recover
    /// features hidden individually. Does not change the body's own visibility.
    /// </summary>
    /// <param name="doc">The active CATIA part document.</param>
    /// <param name="body">The body whose solids are shown.</param>
    public static void ShowAllSolidsInBody(dynamic doc, object body)
    {
        dynamic selection = doc.Selection;
        try
        {
            selection.Clear();
            AddSolidsToSelection(selection, body);
            if (selection.Count > 0)
                selection.VisProperties.SetShow(ShowAttr);
        }
        finally
        {
            TryClear(selection);
        }
    }

    // Adds the solid features of a body (Shapes, plus Sketches) to the selection.
    // Items CATIA refuses to select are skipped individually.
    private static void AddSolidsToSelection(dynamic selection, object body)
    {
        dynamic node = body;
        foreach (string collectionName in new[] { "Shapes", "Sketches" })
        {
            try
            {
                dynamic items = collectionName == "Shapes" ? node.Shapes : node.Sketches;
                int count = items.Count;
                for (int i = 1; i <= count; i++)
                {
                    try { selection.Add(items.Item(i)); }
                    catch { /* not selectable; skip */ }
                }
            }
            catch
            {
                // Collection not available on this body.
            }
        }
    }

    private static void AddNonHelperGeosetsToSelection(dynamic selection, object body)
    {
        try
        {
            dynamic node = body;
            int count = node.HybridBodies.Count;
            for (int i = 1; i <= count; i++)
            {
                dynamic geoset = node.HybridBodies.Item(i);
                if (!CatiaSeals.IsHelperGeoset((string)geoset.Name))
                {
                    try { selection.Add(geoset); }
                    catch { /* skip */ }
                }
            }
        }
        catch
        {
            // Body without geosets.
        }
    }

    private static bool IsFace(dynamic selectedElement)
    {
        try
        {
            // CATIA reports faces as "Face", "PlanarFace", "CylindricalFace", etc.
            return ((string)selectedElement.Type).EndsWith("Face", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Walks items up to their owning body like <see cref="FindOwningBody"/>, but remembers every
    /// node it passes. Items that share a parent (many faces of one Pad, many Pads of one body)
    /// then stop at the first remembered node instead of repeating the whole walk in CATIA.
    /// Nodes are matched by .NET proxy identity; if CATIA hands out a fresh proxy for the same
    /// node, that lookup simply misses and the walk continues as before.
    /// </summary>
    private sealed class BodyResolver
    {
        private readonly Dictionary<object, (object? Body, bool ViaNonSolid)> _known =
            new(ReferenceEqualityComparer.Instance);

        /// <summary>
        /// Returns the owning body (null if none) and whether the path to it passed through a
        /// sketch or geometrical set, i.e. the item is not part of a solid feature.
        /// </summary>
        public (object? Body, bool ViaNonSolid) Resolve(object item)
        {
            var path = new List<(object Node, bool NonSolid)>();
            (object? Body, bool ViaNonSolid) top = (null, false);
            object? current = item;
            for (int depth = 0; depth < MaxParentDepth && current is not null; depth++)
            {
                if (_known.TryGetValue(current, out top))
                    break;

                string typeName = Describe(current, out bool isBody);
                if (isBody)
                {
                    top = (current, false);
                    _known[current] = top;
                    break;
                }
                path.Add((current, typeName is "Sketch" or "HybridBody"));

                try { current = ((dynamic)current).Parent; }
                catch { current = null; }
            }

            // Remember each node on the path, from the top down.
            bool viaNonSolid = top.ViaNonSolid;
            for (int i = path.Count - 1; i >= 0; i--)
            {
                viaNonSolid |= path[i].NonSolid;
                _known[path[i].Node] = (top.Body, viaNonSolid);
            }
            return (top.Body, viaNonSolid);
        }
    }

    private static void TryClear(dynamic selection)
    {
        try { selection.Clear(); }
        catch { /* document may have closed */ }
    }

    public static string NameOf(object body)
    {
        dynamic node = body;
        return (string)node.Name;
    }

    // Walks up the Parent chain (face -> feature -> body, edge -> ..., etc.)
    // until it reaches a body. Each step is recorded in trail for diagnostics.
    internal static object? FindOwningBody(object item, List<string> trail)
    {
        object? current = item;
        for (int depth = 0; depth < MaxParentDepth && current is not null; depth++)
        {
            string name = Describe(current, out bool isBody);
            trail.Add(name);
            if (isBody)
                return current;

            try
            {
                dynamic node = current;
                current = node.Parent;
            }
            catch (Exception ex)
            {
                trail.Add($"[no Parent: {ex.GetType().Name}]");
                return null;
            }
        }
        return null;
    }

    // Describes a CATIA object and says whether it is a body.
    // Bodies are recognised by TypeName "Body". Only when TypeName cannot be read is the
    // object probed for Shapes: that probe throws on every non-body, and exceptions across
    // COM are slow, so it must not run on each step of a parent walk.
    private static string Describe(object item, out bool isBody)
    {
        isBody = false;
        dynamic node = item;

        string typeName;
        bool hasTypeName;
        try
        {
            typeName = (string)node.TypeName;
            isBody = typeName == "Body";
            hasTypeName = true;
        }
        catch (Exception ex)
        {
            typeName = $"[no TypeName: {ex.GetType().Name}]";
            hasTypeName = false;
        }

        if (!hasTypeName)
        {
            try
            {
                object shapes = node.Shapes;
                isBody = shapes is not null;
            }
            catch
            {
                // Not a body; the caller keeps walking up.
            }
        }

        return typeName;
    }
}
