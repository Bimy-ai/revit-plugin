using Autodesk.Revit.DB;

namespace BimyRevit.Services.Import;

/// <summary>The building's named grid axes, as real Revit Grids.</summary>
internal static class GridFactory
{
    public static void Build(BuildContext ctx)
    {
        foreach (var axis in ctx.Model.Grids)
        {
            try
            {
                var a = ImportGeometry.Pt(axis.A);
                var b = ImportGeometry.Pt(axis.B);
                if (a.DistanceTo(b) < ImportGeometry.MinEdgeFt) continue;
                var grid = Grid.Create(ctx.Doc, Line.CreateBound(a, b));
                if (!string.IsNullOrWhiteSpace(axis.Name))
                {
                    try { grid.Name = axis.Name!; }
                    catch { /* duplicate gridline name — keep Revit's */ }
                }
                ctx.Count("Grids");
            }
            catch (Exception ex)
            {
                ctx.Warn($"Grid '{axis.Name}': {ex.Message}");
            }
        }
    }
}
