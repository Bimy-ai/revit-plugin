using Autodesk.Revit.DB;

namespace RevitWallsPlugin.Services;

/// <summary>
/// Resolves a single default material that every imported wall, floor, and
/// ceiling shares. Previously we created one Revit <see cref="Material"/> per
/// source color (e.g. "BIMy #7ec60f") and painted it with that color, but PM
/// feedback is to stop coloring imports — BIMy type colors are an authoring
/// concern, not a BIM concern. Eventually the import will map BIMy types to
/// real Revit materials (concrete, drywall, etc.); until then we route every
/// layer through a neutral default.
/// Must be used inside an open transaction.
/// </summary>
internal sealed class ImportMaterialProvider
{
    private readonly ElementId _defaultMaterialId;

    public ImportMaterialProvider(Document doc)
    {
        _defaultMaterialId = ResolveDefaultMaterialId(doc);
    }

    /// <summary>
    /// Single material id used for every imported element's layer. May be
    /// <see cref="ElementId.InvalidElementId"/> — Revit treats that as
    /// "&lt;By Category&gt;", which still renders as a sensible neutral.
    /// </summary>
    public ElementId DefaultMaterialId => _defaultMaterialId;

    // Preference order: the project's own "Default Wall" / "Default" material
    // if it ships one (most OOTB Revit templates do), then any material named
    // "Generic". Falling back to InvalidElementId lets Revit use category
    // defaults rather than surprising the user with an unrelated material.
    private static ElementId ResolveDefaultMaterialId(Document doc)
    {
        var materials = new FilteredElementCollector(doc)
            .OfClass(typeof(Material))
            .Cast<Material>()
            .ToList();

        foreach (var name in new[] { "Default Wall", "Default", "Generic" })
        {
            var hit = materials.FirstOrDefault(m =>
                string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));
            if (hit is not null) return hit.Id;
        }

        return ElementId.InvalidElementId;
    }
}
