namespace CATIA_CFD_Clean;

// Body selection and visibility operations on a CATIA part document.
internal static class CatiaBodies
{
    // CATIA CatVisPropertyShowAttribute values
    private const int ShowAttr = 0;
    private const int NoShowAttr = 1;

    private const int MaxParentDepth = 20;

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

    // Upper bound on items inspected per poll, so a huge selection (e.g. Ctrl+A in the
    // spec tree) cannot turn each poll into thousands of cross-process COM calls.
    private const int MaxItemsInspectedPerPoll = 50;

    /// <summary>
    /// Returns true if at least one of the first few items in the current CATIA selection is
    /// a face or a body. Reads item types only; intended for the periodic selection poll.
    /// </summary>
    public static bool HasToggleableSelection(dynamic doc)
    {
        dynamic selection = doc.Selection;
        int count = Math.Min((int)selection.Count, MaxItemsInspectedPerPoll);
        for (int i = 1; i <= count; i++)
        {
            if (IsToggleable(selection.Item(i)))
                return true;
        }
        return false;
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
        var names = new HashSet<string>();
        for (int i = 1; i <= count; i++)
        {
            dynamic element = selection.Item(i);
            object? body = null;
            try
            {
                object value = element.Value;
                if (IsFace(element))
                    body = FindOwningBody(value, new List<string>());
                else if (IsBodyObject(value))
                    body = value;
                else
                    body = ResolveSolidFeatureBody(value);
            }
            catch
            {
                continue; // item vanished or is not resolvable; skip it
            }

            if (body is not null && names.Add(NameOf(body)))
                bodies.Add(body);
        }
        return bodies;
    }

    /// <summary>
    /// Hides the given bodies but keeps their solid features (Pad, Pocket, Sketch, ...)
    /// shown, along with the non-helper geosets inside them. This keeps the viewport and the
    /// spec tree in agreement: the body node is hidden while its solids stay visible.
    /// </summary>
    /// <param name="doc">The active CATIA part document.</param>
    /// <param name="bodiesToHide">Bodies whose own visibility is switched off.</param>
    public static void HideBodiesKeepSolids(dynamic doc, IReadOnlyList<object> bodiesToHide)
    {
        if (bodiesToHide.Count == 0)
            return;

        dynamic selection = doc.Selection;
        try
        {
            // 1. Hide the bodies themselves.
            selection.Clear();
            foreach (object body in bodiesToHide)
                selection.Add(body);
            if (selection.Count > 0)
                selection.VisProperties.SetShow(NoShowAttr);
            selection.Clear();

            // 2. Explicitly show the solid features and non-helper geosets of each body.
            foreach (object body in bodiesToHide)
            {
                AddSolidsToSelection(selection, body);
                AddNonHelperGeosetsToSelection(selection, body);
            }
            if (selection.Count > 0)
                selection.VisProperties.SetShow(ShowAttr);
        }
        finally
        {
            TryClear(selection);
        }
    }

    /// <summary>
    /// Toggles the given bodies: if any is shown, all are hidden (solids kept visible);
    /// if all are already hidden, all are shown again with their solids and geosets.
    /// Returns true if the bodies ended up hidden, false if shown.
    /// </summary>
    public static bool ToggleBodiesKeepSolids(dynamic doc, IReadOnlyList<object> bodies)
    {
        dynamic selection = doc.Selection;
        bool anyShown = false;
        try
        {
            foreach (object body in bodies)
            {
                selection.Clear();
                selection.Add(body);
                int show = ShowAttr;
                selection.VisProperties.GetShow(ref show);
                if (show == ShowAttr)
                {
                    anyShown = true;
                    break;
                }
            }
        }
        finally
        {
            TryClear(selection);
        }

        if (anyShown)
        {
            HideBodiesKeepSolids(doc, bodies);
            return true;
        }

        try
        {
            selection.Clear();
            foreach (object body in bodies)
            {
                selection.Add(body);
                AddSolidsToSelection(selection, body);
                AddNonHelperGeosetsToSelection(selection, body);
            }
            if (selection.Count > 0)
                selection.VisProperties.SetShow(ShowAttr);
        }
        finally
        {
            TryClear(selection);
        }
        return false;
    }

    /// <summary>
    /// Shows every solid feature (Pad, Pocket, Sketch, ...) of one body. Companion to
    /// <see cref="HideBodiesKeepSolids"/>; does not change the body's own visibility.
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

    // A selection item the F2 toggle acts on: a face, a body itself, or a solid feature
    // (Pad, Pocket, ...) that belongs to a body.
    private static bool IsToggleable(dynamic selectedElement)
    {
        try
        {
            object value = selectedElement.Value;
            return IsFace(selectedElement) || IsBodyObject(value) || ResolveSolidFeatureBody(value) is not null;
        }
        catch
        {
            return false;
        }
    }

    // Returns the body owning a solid feature, or null if the item is not one. Sketches and
    // geometrical sets (HybridBody) inside a body are not solid features, so anything reached
    // through them is rejected.
    private static object? ResolveSolidFeatureBody(object value)
    {
        var trail = new List<string>();
        object? body = FindOwningBody(value, trail);
        if (body is null)
            return null;
        foreach (string typeName in trail)
        {
            if (typeName is "Sketch" or "HybridBody")
                return null;
        }
        return body;
    }

    // True if the object is a CATIA body (TypeName "Body"), the same test FindOwningBody uses.
    private static bool IsBodyObject(object value)
    {
        try
        {
            return (string)((dynamic)value).TypeName == "Body";
        }
        catch
        {
            return false;
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
    // Bodies are recognised by TypeName "Body" or by exposing Shapes.
    private static string Describe(object item, out bool isBody)
    {
        isBody = false;
        dynamic node = item;

        string typeName;
        try
        {
            typeName = (string)node.TypeName;
            isBody = typeName == "Body";
        }
        catch (Exception ex)
        {
            typeName = $"[no TypeName: {ex.GetType().Name}]";
        }

        if (!isBody)
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
