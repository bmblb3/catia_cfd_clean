namespace CATIA_CFD_Clean;

// Seal creation: selected faces -> geoset (join) -> thick surface in a new body,
// and the final boolean combination of two seals with the original bodies.
internal static class CatiaSeals
{
    public const string SealFacesGeosetName = "Seal faces";

    // Seal thickness: a thin skin on the faces' normal side, the bulk on the opposite side.
    public const double SealMainSideMm = 0.5;
    public const double SealReverseSideMm = 3.0;

    /// <summary>
    /// Reads the faces in the current CATIA selection (edges and other items are ignored) and
    /// the one body that owns them all. Throws if no face is selected or if the faces belong to
    /// more than one body.
    /// </summary>
    public static (object Body, List<object> Faces) GetSelectedFaces(dynamic doc)
    {
        dynamic part = doc.Part;
        dynamic selection = doc.Selection;
        int count = selection.Count;

        object? body = null;
        string? bodyName = null;
        var references = new List<object>();
        for (int i = 1; i <= count; i++)
        {
            dynamic element = selection.Item(i);

            // CATIA reports faces as "Face", "PlanarFace", "CylindricalFace", etc.
            string? type = TryGetType(element);
            if (type is null || !type.EndsWith("Face", StringComparison.OrdinalIgnoreCase))
                continue;

            object value = element.Value;
            var trail = new List<string>();
            object owner = CatiaBodies.FindOwningBody(value, trail)
                ?? throw new InvalidOperationException(
                    "A selected face does not belong to a body. Chain: " + string.Join(" -> ", trail));

            string ownerName = CatiaBodies.NameOf(owner);
            if (bodyName is null)
            {
                body = owner;
                bodyName = ownerName;
            }
            else if (ownerName != bodyName)
            {
                throw new InvalidOperationException(
                    $"The selected faces belong to more than one body ({bodyName}, {ownerName}). " +
                    "Select faces of one body only.");
            }

            references.Add(GetReference(part, element, value));
        }

        if (body is null)
            throw new InvalidOperationException("No faces are selected in CATIA. Select the seal faces first.");
        return (body, references);
    }

    /// <summary>
    /// Creates a new body holding only a hidden geoset with the faces joined into one surface.
    /// The thick surface is added later by <see cref="ThickenSeal"/>.
    /// </summary>
    public static object CreateSealFacesBody(dynamic doc, string name, IReadOnlyList<object> faces)
    {
        dynamic part = doc.Part;
        dynamic newBody = part.Bodies.Add();
        newBody.Name = name;

        try
        {
            dynamic geoset = newBody.HybridBodies.Add();
            geoset.Name = SealFacesGeosetName;

            dynamic hsf = part.HybridShapeFactory;
            dynamic surface;
            if (faces.Count == 1)
            {
                surface = hsf.AddNewExtract(faces[0]);
            }
            else
            {
                surface = hsf.AddNewJoin(faces[0], faces[1]);
                for (int i = 2; i < faces.Count; i++)
                    surface.AddElement(faces[i]);
            }
            geoset.AppendHybridShape(surface);
            part.InWorkObject = geoset;
            part.Update();

            // Hide the geoset (the thick surface will depend on it, so it cannot be deleted).
            dynamic selection = doc.Selection;
            selection.Clear();
            selection.Add(geoset);
            selection.VisProperties.SetShow(1); // catVisPropertyNoShowAttr
            selection.Clear();
        }
        catch
        {
            TryDelete(doc, newBody);
            throw;
        }

        return newBody;
    }

    /// <summary>
    /// True once the seal body has its thick surface.
    /// </summary>
    public static bool IsThickened(object sealBody) => (int)((dynamic)sealBody).Shapes.Count > 0;

    /// <summary>
    /// Thickens the joined faces of a seal body (<see cref="SealMainSideMm"/> on the normal side,
    /// <see cref="SealReverseSideMm"/> on the other) and colours the body red.
    /// </summary>
    public static void ThickenSeal(dynamic doc, object sealBody)
    {
        dynamic part = doc.Part;
        dynamic body = sealBody;
        dynamic geoset = body.HybridBodies.Item(SealFacesGeosetName);
        dynamic surface = geoset.HybridShapes.Item(1);

        dynamic surfaceRef = part.CreateReferenceFromObject(surface);
        part.InWorkObject = body;
        dynamic thick = part.ShapeFactory.AddNewThickSurface(surfaceRef, 0, SealMainSideMm, SealReverseSideMm);
        try
        {
            part.Update();
        }
        catch
        {
            TryDelete(doc, thick); // leave the body unthickened so Make seal can be retried
            throw;
        }

        SetBodyColor(doc, sealBody, ConfirmedColor);
    }

    public const string PlugFillGeosetName = "Plug fill";

    public static bool IsHelperGeoset(string name) =>
        name == SealFacesGeosetName || name == PlugFillGeosetName;

    // Reads the curves/edges currently selected in CATIA and returns a Reference for each.
    public static List<object> GetSelectedCurveReferences(dynamic doc)
    {
        dynamic part = doc.Part;
        dynamic selection = doc.Selection;
        int count = selection.Count;
        if (count == 0)
            throw new InvalidOperationException("No curves are selected in CATIA.");

        var references = new List<object>();
        for (int i = 1; i <= count; i++)
        {
            dynamic element = selection.Item(i);
            object value = element.Value;
            references.Add(GetReference(part, element, value));
        }
        return references;
    }

    // Creates a new body with a hidden geoset holding a Fill of the closed curve chain,
    // and a thick surface of that fill in the body.
    public static object CreatePlugBody(dynamic doc, string name, IReadOnlyList<object> curves,
        double distanceMm, bool reverse)
    {
        dynamic part = doc.Part;
        dynamic newBody = part.Bodies.Add();
        newBody.Name = name;

        try
        {
            dynamic geoset = newBody.HybridBodies.Add();
            geoset.Name = PlugFillGeosetName;

            dynamic hsf = part.HybridShapeFactory;
            object boundary;
            if (curves.Count == 1)
            {
                boundary = curves[0];
            }
            else
            {
                dynamic join = hsf.AddNewJoin(curves[0], curves[1]);
                for (int i = 2; i < curves.Count; i++)
                    join.AddElement(curves[i]);
                geoset.AppendHybridShape(join);
                part.InWorkObject = geoset;
                part.Update();
                boundary = part.CreateReferenceFromObject(join);
            }

            dynamic fill = hsf.AddNewFill();
            fill.AddBound((dynamic)boundary);
            geoset.AppendHybridShape(fill);
            part.InWorkObject = geoset;
            part.Update();

            dynamic fillRef = part.CreateReferenceFromObject(fill);
            part.InWorkObject = newBody;
            dynamic thick = part.ShapeFactory.AddNewThickSurface(fillRef, 0, distanceMm, 0.0);
            if (reverse)
                thick.swap_OffsetSide();
            part.Update();

            dynamic selection = doc.Selection;
            selection.Clear();
            selection.Add(geoset);
            selection.VisProperties.SetShow(1);
            selection.Clear();

            SetBodyColor(doc, newBody, CandidateColor);
        }
        catch
        {
            TryDelete(doc, newBody);
            throw;
        }

        return newBody;
    }

    // Changes the thickness of an existing seal body in place (no rebuild).
    public static void SetSealThickness(dynamic doc, object sealBody, double distanceMm)
    {
        dynamic thick = GetThickSurface(sealBody);
        thick.TopOffset.Value = distanceMm;
        doc.Part.Update();
    }

    // Flips an existing seal body to the other side of its faces.
    public static void FlipSeal(dynamic doc, object sealBody)
    {
        dynamic thick = GetThickSurface(sealBody);
        thick.swap_OffsetSide();
        doc.Part.Update();
    }

    private static dynamic GetThickSurface(object sealBody)
    {
        dynamic shapes = ((dynamic)sealBody).Shapes;
        int count = shapes.Count;
        if (count < 1)
            throw new InvalidOperationException("The seal body has no thick surface.");
        return shapes.Item(count);
    }

    // Intersects the two seals into a new green body.
    public static object MakeSeal(dynamic doc, object seal1, object seal2)
    {
        dynamic part = doc.Part;
        dynamic shapes = part.ShapeFactory;
        dynamic selection = doc.Selection;

        dynamic result = part.Bodies.Add();
        result.Name = NextKey(doc);
        try
        {
            part.InWorkObject = result;
            shapes.AddNewAdd(seal1);
            shapes.AddNewIntersect(seal2);
            part.Update();
        }
        catch (Exception ex)
        {
            TryDelete(doc, result);
            throw new InvalidOperationException(
                "Could not intersect the two seals. Check that they overlap. " + ex.Message, ex);
        }

        // The boolean features keep the red inherited from the seal operands, so the
        // helper colours the result body and each of its child features explicitly.
        SetBodyColor(doc, result, ResultColor, includeFeatures: true);

        return result;
    }

    private static readonly System.Text.RegularExpressions.Regex KeyPattern =
        new(@"^CFD_(\d{4})$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    // Next unused body key ("CFD_0050"): one above the highest CFD_ key in the part.
    // Every body this app creates gets one, so names stay unique and can identify it.
    public static string NextKey(dynamic doc)
    {
        dynamic part = doc.Part;
        int count = part.Bodies.Count;
        int max = 0;
        for (int i = 1; i <= count; i++)
        {
            string name = (string)((dynamic)part.Bodies.Item(i)).Name;
            var match = KeyPattern.Match(name);
            if (match.Success)
                max = Math.Max(max, int.Parse(match.Groups[1].Value));
        }
        return $"CFD_{max + 1:0000}";
    }

    public static readonly (int R, int G, int B) CandidateColor = (255, 150, 200); // pink
    public static readonly (int R, int G, int B) ConfirmedColor = (255, 0, 0);     // red
    public static readonly (int R, int G, int B) ResultColor = (0, 255, 0);        // green

    // Colours a body. With includeFeatures, its features are coloured too (boolean
    // features otherwise keep their operands' colour). Never use includeFeatures on
    // seal bodies: a feature-level colour there overrides the green of the final Seal.
    public static void SetBodyColor(dynamic doc, object body, (int R, int G, int B) color,
        bool includeFeatures = false)
    {
        dynamic selection = doc.Selection;
        selection.Clear();
        selection.Add(body);
        if (includeFeatures)
        {
            dynamic shapes = ((dynamic)body).Shapes;
            int shapeCount = shapes.Count;
            for (int i = 1; i <= shapeCount; i++)
                selection.Add(shapes.Item(i));
        }
        selection.VisProperties.SetRealColor(color.R, color.G, color.B, 1);
        selection.Clear();
    }

    public static void TryDelete(dynamic doc, object body)
    {
        try
        {
            dynamic selection = doc.Selection;
            selection.Clear();
            selection.Add(body);
            selection.Delete();
            selection.Clear();
        }
        catch
        {
            // Best effort cleanup.
        }
    }

    private static string? TryGetType(dynamic element)
    {
        try { return (string)element.Type; }
        catch { return null; }
    }

    private static object GetReference(dynamic part, dynamic element, object value)
    {
        try
        {
            object reference = element.Reference;
            if (reference is not null)
                return reference;
        }
        catch
        {
            // Fall back to creating a reference from the object.
        }
        return part.CreateReferenceFromObject(value);
    }
}
