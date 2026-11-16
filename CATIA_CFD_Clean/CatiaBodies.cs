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
    }

    // Walks up the Parent chain (face -> feature -> body, edge -> ..., etc.)
    // until it reaches a body. Each step is recorded in trail for diagnostics.
    private static object? FindOwningBody(object item, List<string> trail)
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
