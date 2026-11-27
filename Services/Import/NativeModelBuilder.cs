using System.IO;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;
using BimyRevit.Models;

namespace BimyRevit.Services.Import;

/// <summary>
/// Turns one `bimy-revit-model/1` payload into one fresh Revit document of
/// native elements. The document starts from the user's own default project
/// template (their door/window families, their type seeds, their units), and
/// the build runs as transaction phases — levels → materials/types → walls →
/// openings → slabs → roofs → solids/objects → rooms → finish — each with the
/// warning swallower, each failing PER ELEMENT rather than per pull: one bad
/// wall is a line in the summary, not a dead import.
/// </summary>
internal static class NativeModelBuilder
{
    public sealed record Result(
        Document Document,
        IReadOnlyDictionary<string, int> Counts,
        IReadOnlyList<string> Warnings);

    public static Result Build(Application app, RevitModel model, string projectId)
    {
        if (model.Storeys.Count == 0)
            throw new InvalidOperationException("The payload has no storeys — nothing to build.");

        var doc = NewDocument(app);
        try
        {
            var ctx = new BuildContext(doc, model, $"BIMy import {projectId}");

            Phase(ctx, "Levels and grids", c => { LevelFactory.Build(c); GridFactory.Build(c); });
            Phase(ctx, "Materials and types", c => { MaterialFactory.Build(c); TypeFactory.Build(c); });
            Phase(ctx, "Walls", WallFactory.Build);
            Phase(ctx, "Doors and windows", OpeningFactory.Build);
            Phase(ctx, "Floors and ceilings", SlabFactory.Build);
            Phase(ctx, "Roofs", RoofFactory.Build);
            Phase(ctx, "Structure and objects", c => { SolidFactory.Build(c); FurnitureFactory.Build(c); });
            Phase(ctx, "Rooms", RoomFactory.Build);
            Phase(ctx, "Finish", Finish);

            foreach (var u in model.Unhandled)
                ctx.Warn($"{u.Count} × {u.Kind ?? u.Shape ?? "element"} had no native mapping upstream.");

            Log.Info("Native build done: " + (ctx.Counts.Count == 0
                ? "(nothing built)"
                : string.Join(", ", ctx.Counts.Select(kv => $"{kv.Value} {kv.Key.ToLowerInvariant()}"))));
            return new Result(doc, ctx.Counts, ctx.Warnings);
        }
        catch
        {
            // A failed build must not leave an orphan document open in the
            // session — the caller never saw it.
            try { doc.Close(false); } catch { /* already closing */ }
            throw;
        }
    }

    /// <summary>
    /// A fresh project from the user's configured default template — that is
    /// where THEIR door families and type seeds live — else any stock template
    /// beside the Revit install, else the bare metric document.
    /// </summary>
    private static Document NewDocument(Application app)
    {
        string? template = null;
        try
        {
            var configured = app.DefaultProjectTemplate;
            if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
                template = configured;
        }
        catch { /* unset is normal */ }

        if (template is null)
        {
            try
            {
                foreach (string candidate in app.GetLibraryPaths().Values)
                {
                    if (!Directory.Exists(candidate)) continue;
                    template = Directory.EnumerateFiles(candidate, "*.rte", SearchOption.AllDirectories)
                        .FirstOrDefault();
                    if (template is not null) break;
                }
            }
            catch { /* library paths are optional */ }
        }

        if (template is not null)
        {
            try
            {
                Log.Info($"New project from template: {template}");
                return app.NewProjectDocument(template);
            }
            catch (Exception ex)
            {
                Log.Warn($"Template '{template}' refused ({ex.Message}) — using the default metric document.");
            }
        }
        return app.NewProjectDocument(UnitSystem.Metric);
    }

    private static void Phase(BuildContext ctx, string name, Action<BuildContext> work)
    {
        using var tx = new Transaction(ctx.Doc, $"BIMy · {name}");
        tx.Start();
        var opts = tx.GetFailureHandlingOptions();
        opts.SetFailuresPreprocessor(new SuppressWarningsPreprocessor());
        opts.SetClearAfterRollback(true);
        opts.SetForcedModalHandling(true);
        tx.SetFailureHandlingOptions(opts);
        try
        {
            work(ctx);
            tx.Commit();
        }
        catch (Exception ex)
        {
            if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
            // One failed phase degrades that phase, not the pull: the model
            // without its rooms (say) is still the model.
            ctx.Warn($"Phase '{name}' failed and was rolled back: {ex.Message}");
            Log.Error($"Native import phase '{name}' failed.", ex);
        }
    }

    /// <summary>Project identity, site coordinates, a 3D view to land in.</summary>
    private static void Finish(BuildContext ctx)
    {
        var doc = ctx.Doc;
        var model = ctx.Model;

        try
        {
            if (!string.IsNullOrWhiteSpace(model.Project.Name))
                doc.ProjectInformation.Name = model.Project.Name!;
            if (!string.IsNullOrWhiteSpace(model.Project.Id))
                doc.ProjectInformation.Number = model.Project.Id!;
        }
        catch { /* non-fatal */ }

        if (model.Location is { } loc)
        {
            try
            {
                doc.SiteLocation.Latitude = loc.Latitude * Math.PI / 180;
                doc.SiteLocation.Longitude = loc.Longitude * Math.PI / 180;
                if (!string.IsNullOrWhiteSpace(loc.Address))
                    ParamSet.TrySet(doc.ProjectInformation, BuiltInParameter.PROJECT_ADDRESS, loc.Address!);
            }
            catch (Exception ex)
            {
                ctx.Warn($"Site location: {ex.Message}");
            }
        }

        // Somewhere to land: templates usually ship a {3D} view; make one when
        // this one doesn't.
        try
        {
            var has3d = new FilteredElementCollector(doc)
                .OfClass(typeof(View3D))
                .Cast<View3D>()
                .Any(v => !v.IsTemplate);
            if (!has3d)
            {
                var vft = new FilteredElementCollector(doc)
                    .OfClass(typeof(ViewFamilyType))
                    .Cast<ViewFamilyType>()
                    .FirstOrDefault(v => v.ViewFamily == ViewFamily.ThreeDimensional);
                if (vft is not null) View3D.CreateIsometric(doc, vft.Id);
            }
        }
        catch { /* non-fatal */ }
    }
}
