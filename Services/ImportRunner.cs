using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitWallsPlugin.Models;

namespace RevitWallsPlugin.Services;

internal static class ImportRunner
{
    public static Result Run(
        UIApplication uiApp,
        BimyEnvironment env,
        string projectId,
        string url,
        string bearerToken,
        ref string message)
    {
        Log.Info("---- Import run invoked ----");

        var uiDoc = uiApp.ActiveUIDocument;
        if (uiDoc is null)
        {
            Log.Warn("No active document.");
            TaskDialog.Show("Load from BIMy", "No active Revit document.");
            return Result.Cancelled;
        }

        var doc = uiDoc.Document;
        var envLabel = BimyEnvironments.DisplayName(env);
        var sourceLabel = $"{envLabel} · project {projectId}";

        Log.Info($"Fetching userObjects from {sourceLabel} ({url})");
        UserObjectsPayload payload;
        ProjectBuilder.Result project;
        try
        {
            payload = BimyApi.FetchUserObjectsAsync(url, bearerToken).GetAwaiter().GetResult();
            var objectCount = payload.UserObjects?.Count ?? 0;
            Log.Info($"Fetched {objectCount} userObject(s).");
            project = ProjectBuilder.Build(payload);
            var s = project.Stats;
            Log.Info($"Built {project.Walls.Count} wall(s), {project.Floors.Count} floor(s), "
                     + $"{project.Ceilings.Count} ceiling(s) across {project.LevelElevationsMm.Count} level(s).");
            Log.Info($"Wall DSL: {s.SegmentObjects} segment, {s.PolygonObjects} polygon. "
                     + $"Segments read={s.SegmentsRead}, dropped: short={s.SegmentsDroppedShort}, malformed={s.SegmentsDroppedMalformed}. "
                     + $"Edges deduped={s.EdgesDeduped}.");
        }
        catch (Exception ex)
        {
            Log.Error($"Fetch failed for {sourceLabel}", ex);
            ShowError("Could not fetch wall data",
                $"Source: {sourceLabel}\n\n{ex.GetType().Name}: {ex.Message}\n\nSee log: {Log.Path}");
            message = ex.Message;
            return Result.Failed;
        }

        var importTag = $"{WallTypeProvider.ImportedTag} {projectId}";

        // Before we start deleting plan views, move the active view off any
        // plan — otherwise Revit would refuse to delete the plan the user is
        // sitting on, leaving behind a stray "L1 - Architectural"-style view.
        ParkActiveViewOffPlans(uiDoc);

        BuildSummary summary;
        using (var tx = new Transaction(doc, "Load from BIMy"))
        {
            try
            {
                tx.Start();

                var opts = tx.GetFailureHandlingOptions();
                opts.SetFailuresPreprocessor(new SuppressWarningsPreprocessor());
                opts.SetClearAfterRollback(true);
                opts.SetForcedModalHandling(true);
                tx.SetFailureHandlingOptions(opts);

                summary = BuildAll(doc, project, importTag);

                var disabledBoxes = RevitLookup.DisableAll3DSectionBoxes(doc);
                if (disabledBoxes > 0)
                    Log.Info($"Disabled section box on {disabledBoxes} 3D view(s).");

                tx.Commit();

                Log.Info($"Transaction committed. "
                         + $"Walls: del={summary.DeletedWalls}, new={summary.CreatedWalls}. "
                         + $"Floors: del={summary.DeletedFloors}, new={summary.CreatedFloors}. "
                         + $"Ceilings: del={summary.DeletedCeilings}, new={summary.CreatedCeilings}.");

                if (summary.CreatedLevels.Count > 0)
                    Log.Info($"Auto-created levels: {string.Join(", ", summary.CreatedLevels)}");
                if (summary.CreatedTypes.Count > 0)
                    Log.Info($"Auto-created types: {string.Join(", ", summary.CreatedTypes)}");
                foreach (var err in summary.Errors)
                    Log.Warn(err);
            }
            catch (Exception ex)
            {
                if (tx.HasStarted() && !tx.HasEnded())
                    tx.RollBack();

                Log.Error("Transaction failed, rolled back.", ex);
                ShowError("Import failed",
                    $"{ex.GetType().Name}: {ex.Message}\n\nSee log: {Log.Path}");
                message = ex.Message;
                return Result.Failed;
            }
        }

        ZoomActiveViewToFit(uiDoc);
        ShowSummary(summary, project, sourceLabel);
        return Result.Succeeded;
    }

    private sealed record BuildSummary(
        int DeletedWalls, int CreatedWalls,
        int DeletedFloors, int CreatedFloors,
        int DeletedCeilings, int CreatedCeilings,
        HashSet<string> CreatedLevels,
        HashSet<string> CreatedTypes,
        List<string> Errors);

    private static BuildSummary BuildAll(Document doc, ProjectBuilder.Result project, string importTag)
    {
        // Delete previously-imported elements of this project first so freshly
        // created ones aren't mistaken for leftovers by a second import pass.
        var deletedWalls    = ImportCleanup.DeleteImportedInstances(doc, typeof(Wall),    WallTypeProvider.ImportedTag);
        var deletedFloors   = ImportCleanup.DeleteImportedInstances(doc, typeof(Floor),   WallTypeProvider.ImportedTag);
        var deletedCeilings = ImportCleanup.DeleteImportedInstances(doc, typeof(Ceiling), WallTypeProvider.ImportedTag);

        // Drop every existing floor-plan and ceiling-plan view so EnsureLevels
        // can recreate exactly one canonical "Plan - <level>" / "Ceiling Plan
        // - <level>" per level. Without this, the template's pre-existing
        // views (e.g. "L1 - Architectural", "Level 1") stay attached to the
        // levels we reuse and clutter the Project Browser.
        var deletedFloorPlans   = RevitLookup.DeleteAllFloorPlans(doc);
        var deletedCeilingPlans = RevitLookup.DeleteAllCeilingPlans(doc);
        if (deletedFloorPlans > 0 || deletedCeilingPlans > 0)
            Log.Info($"Deleted {deletedFloorPlans} floor-plan view(s), {deletedCeilingPlans} ceiling-plan view(s).");

        // One shared level map so walls, floors, and ceilings all land on the
        // same Revit Level instances (Revit won't let us create duplicates with
        // the same name anyway, but routing through the same dictionary avoids
        // a second collector pass).
        var referencedLevels = project.Walls
            .SelectMany(w => new[] { w.Level, w.TopLevel })
            .Concat(project.Floors.Select(f => f.Level))
            .Concat(project.Ceilings.Select(c => c.Level))
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n!.Trim())
            .ToList();
        var (levelByName, createdLevels) = RevitLookup.EnsureLevels(
            doc, referencedLevels, project.LevelElevationsMm);

        var materials = new ImportMaterialProvider(doc);

        Log.Info("Creating walls…");
        var wallResult = WallBuilder.CreateWalls(doc, project.Walls, levelByName, materials, importTag);
        Log.Info($"Walls: created={wallResult.Created}, skipped={wallResult.Errors.Count}.");

        Log.Info("Creating floors…");
        var floorResult = FloorBuilder.CreateFloors(doc, project.Floors, levelByName, materials, importTag);
        Log.Info($"Floors: created={floorResult.Created}, skipped={floorResult.Errors.Count}.");

        Log.Info("Creating ceilings…");
        var ceilingResult = CeilingBuilder.CreateCeilings(doc, project.Ceilings, levelByName, materials, importTag);
        Log.Info($"Ceilings: created={ceilingResult.Created}, skipped={ceilingResult.Errors.Count}.");

        var createdTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        createdTypes.UnionWith(wallResult.CreatedWallTypes);
        createdTypes.UnionWith(floorResult.CreatedFloorTypes);
        createdTypes.UnionWith(ceilingResult.CreatedCeilingTypes);

        var errors = new List<string>();
        errors.AddRange(wallResult.Errors);
        errors.AddRange(floorResult.Errors);
        errors.AddRange(ceilingResult.Errors);

        return new BuildSummary(
            deletedWalls, wallResult.Created,
            deletedFloors, floorResult.Created,
            deletedCeilings, ceilingResult.Created,
            createdLevels, createdTypes, errors);
    }

    // Revit refuses to delete the active view. If the user is currently in a
    // floor or ceiling plan, that plan would survive DeleteAllFloorPlans /
    // DeleteAllCeilingPlans as a stray — move the focus to any non-plan view
    // (preferring a 3D view) before we start cleaning up.
    private static void ParkActiveViewOffPlans(UIDocument uiDoc)
    {
        var active = uiDoc.ActiveView;
        if (active is not ViewPlan) return;

        var doc = uiDoc.Document;

        var threeD = new FilteredElementCollector(doc)
            .OfClass(typeof(View3D))
            .Cast<View3D>()
            .FirstOrDefault(v => !v.IsTemplate);

        View? target = threeD;
        if (target is null)
        {
            target = new FilteredElementCollector(doc)
                .OfClass(typeof(View))
                .Cast<View>()
                .FirstOrDefault(v => !v.IsTemplate && v is not ViewPlan);
        }

        if (target is null)
        {
            Log.Warn("No non-plan view to park on — the active plan may survive the import.");
            return;
        }

        try
        {
            uiDoc.ActiveView = target;
            Log.Info($"Parked active view on '{target.Name}' to free plan views for cleanup.");
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not park active view: {ex.Message}");
        }
    }

    private static void ZoomActiveViewToFit(UIDocument uiDoc)
    {
        try
        {
            var active = uiDoc.GetOpenUIViews()
                .FirstOrDefault(v => v.ViewId == uiDoc.ActiveView.Id);
            active?.ZoomToFit();
            Log.Info("Zoomed active view to fit.");
        }
        catch (Exception ex)
        {
            Log.Warn($"ZoomToFit failed: {ex.Message}");
        }
    }

    private static void ShowSummary(BuildSummary s, ProjectBuilder.Result project, string sourceLabel)
    {
        var title = s.Errors.Count == 0 ? "Import complete" : "Imported with warnings";

        var mainLines = new List<string>();
        if (project.Walls.Count > 0)
            mainLines.Add($"{s.CreatedWalls} of {project.Walls.Count} wall(s) created.");
        if (project.Floors.Count > 0)
            mainLines.Add($"{s.CreatedFloors} of {project.Floors.Count} floor(s) created.");
        if (project.Ceilings.Count > 0)
            mainLines.Add($"{s.CreatedCeilings} of {project.Ceilings.Count} ceiling(s) created.");
        if (mainLines.Count == 0)
            mainLines.Add("Nothing to import.");

        var content = $"Source: {sourceLabel}\nLog: {Log.Path}";
        var totalDeleted = s.DeletedWalls + s.DeletedFloors + s.DeletedCeilings;
        if (totalDeleted > 0)
        {
            content += $"\n\nReplaced {s.DeletedWalls} wall(s), {s.DeletedFloors} floor(s), "
                     + $"{s.DeletedCeilings} ceiling(s) from a previous import.";
        }

        var expanded = new List<string>();
        if (s.CreatedLevels.Count > 0)
            expanded.Add("Auto-created levels: " + string.Join(", ", s.CreatedLevels));
        if (s.CreatedTypes.Count > 0)
            expanded.Add("Auto-created types: " + string.Join(", ", s.CreatedTypes));
        if (s.Errors.Count > 0)
            expanded.Add("Skipped:\n - " + string.Join("\n - ", s.Errors));

        var dialog = new TaskDialog(title)
        {
            MainInstruction = string.Join(" ", mainLines),
            MainContent = content,
        };
        if (expanded.Count > 0)
            dialog.ExpandedContent = string.Join("\n\n", expanded);

        dialog.Show();
    }

    private static void ShowError(string heading, string details)
    {
        new TaskDialog("Load from BIMy")
        {
            MainIcon = TaskDialogIcon.TaskDialogIconError,
            MainInstruction = heading,
            MainContent = details,
        }.Show();
    }
}
