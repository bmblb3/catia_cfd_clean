namespace CATIA_CFD_Clean;

// Seal creation: selected faces -> geoset (join) -> thick surface in a new body,
// and the final boolean combination of two seals with the original bodies.
internal static class CatiaSeals
{
    public const string SealFacesGeosetName = "Seal faces";

    // Reads the faces currently selected in CATIA and returns a Reference for each.
    // Every face must belong to the given body.
    public static List<object> GetSelectedFaceReferences(dynamic doc, object body)
    {
        dynamic part = doc.Part;
        dynamic selection = doc.Selection;
        int count = selection.Count;
        if (count == 0)
            throw new InvalidOperationException("No faces are selected in CATIA.");

        string bodyName = CatiaBodies.NameOf(body);
        var references = new List<object>();
        for (int i = 1; i <= count; i++)
        {
            dynamic element = selection.Item(i);

            string? type = TryGetType(element);
            // CATIA reports faces as "Face", "PlanarFace", "CylindricalFace", etc.
            if (type is not null && !type.EndsWith("Face", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"Selected item {i} is a {type}; select faces only.");

            object value = element.Value;
            var trail = new List<string>();
            object? owner = CatiaBodies.FindOwningBody(value, trail);
            if (owner is null || CatiaBodies.NameOf(owner) != bodyName)
                throw new InvalidOperationException(
                    $"Selected item {i} is not a face of {bodyName}.");

            references.Add(GetReference(part, element, value));
        }
        return references;
    }

    // Creates a new body containing a geoset (joined faces) and a thick surface.
    public static object CreateSealBody(dynamic doc, string name, IReadOnlyList<object> faces,
        double distanceMm, bool reverse)
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

            dynamic surfaceRef = part.CreateReferenceFromObject(surface);
            part.InWorkObject = newBody;
            dynamic thick = part.ShapeFactory.AddNewThickSurface(surfaceRef, 0, distanceMm, 0.0);
            if (reverse)
                thick.swap_OffsetSide();
            part.Update();

            // Hide the geoset (the thick surface still depends on it, so it cannot be deleted).
            dynamic selection = doc.Selection;
            selection.Clear();
            selection.Add(geoset);
            selection.VisProperties.SetShow(1); // catVisPropertyNoShowAttr
            selection.Clear();

            // Pink marks a candidate seal; it turns red once the user confirms it.
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
        result.Name = "Seal";
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
        SetBodyColor(doc, result, ResultColor);

        return result;
    }

    public static readonly (int R, int G, int B) CandidateColor = (255, 150, 200); // pink
    public static readonly (int R, int G, int B) ConfirmedColor = (255, 0, 0);     // red
    public static readonly (int R, int G, int B) ResultColor = (0, 255, 0);        // green

    // Colours a body and each of its features (features can otherwise keep an old colour).
    public static void SetBodyColor(dynamic doc, object body, (int R, int G, int B) color)
    {
        dynamic selection = doc.Selection;
        selection.Clear();
        selection.Add(body);
        dynamic shapes = ((dynamic)body).Shapes;
        int shapeCount = shapes.Count;
        for (int i = 1; i <= shapeCount; i++)
            selection.Add(shapes.Item(i));
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
