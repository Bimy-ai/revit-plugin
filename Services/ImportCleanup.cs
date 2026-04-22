using Autodesk.Revit.DB;

namespace RevitWallsPlugin.Services;

/// <summary>
/// Finds and deletes previously-imported Revit elements branded with the
/// current import's <c>importTag</c> (prefix-match, so multiple projects can
/// coexist). Only instances carrying the tag in their Comments parameter are
/// removed — user-drawn elements and those imported by other plug-ins are
/// left alone.
/// </summary>
internal static class ImportCleanup
{
    public static int DeleteImportedInstances(Document doc, Type elementType, string importTag)
    {
        if (string.IsNullOrEmpty(importTag)) return 0;

        var ids = new FilteredElementCollector(doc)
            .OfClass(elementType)
            .Where(e => IsImported(e, importTag))
            .Select(e => e.Id)
            .ToList();

        if (ids.Count == 0) return 0;
        var deleted = doc.Delete(ids);
        return deleted?.Count ?? 0;
    }

    private static bool IsImported(Element e, string importTag)
    {
        var p = e.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
        var comment = p?.AsString();
        return !string.IsNullOrEmpty(comment)
               && comment!.StartsWith(importTag, StringComparison.OrdinalIgnoreCase);
    }
}
