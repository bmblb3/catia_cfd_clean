namespace CATIA_CFD_Clean;

// Body selection and visibility operations on a CATIA part document.
internal static class CatiaBodies
{
    // CATIA CatVisPropertyShowAttribute values
    private const int ShowAttr = 0;
    private const int NoShowAttr = 1;

    // Returns the body currently selected in CATIA (spec tree or viewer),
    // or null if the selection is empty or is not a body.
    public static object? GetSelectedBody(dynamic doc)
    {
        dynamic selection = doc.Selection;
        if (selection.Count == 0)
            return null;

        object item = selection.Item(1).Value;
        return IsBody(item) ? item : null;
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

    // Bodies expose Shapes; features, sketches and other elements do not.
    private static bool IsBody(object item)
    {
        try
        {
            dynamic candidate = item;
            object shapes = candidate.Shapes;
            return shapes is not null;
        }
        catch
        {
            return false;
        }
    }
}
