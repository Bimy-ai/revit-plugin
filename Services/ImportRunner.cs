using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitWallsPlugin.Models;

namespace RevitWallsPlugin.Services;

internal static class ImportRunner
{
    public static Result Run(UIApplication uiApp, string url, ref string message)
    {
        Log.Info("---- Import run invoked ----");

        var uiDoc = uiApp.ActiveUIDocument;
        if (uiDoc is null)
        {
            Log.Warn("No active document.");
            TaskDialog.Show("Import Walls", "No active Revit document.");
            return Result.Cancelled;
        }

        var doc = uiDoc.Document;

        Log.Info($"Fetching JSON from: {url}");
        UserObjectsPayload payload;
        ProjectBuilder.Result project;
        try
        {
            payload = JsonFetcher.FetchAsync<UserObjectsPayload>(url).GetAwaiter().GetResult();
            var objectCount = payload.UserObjects?.Count ?? 0;
            Log.Info($"Fetched {objectCount} userObject(s).");
            project = ProjectBuilder.BuildWalls(payload);
            Log.Info($"Built {project.Walls.Count} wall definition(s) across {project.LevelElevationsMm.Count} level(s) from {objectCount} userObject(s).");
        }
        catch (Exception ex)
        {
            Log.Error($"Fetch failed for {url}", ex);
            ShowError("Could not fetch wall data",
                $"URL: {url}\n\n{ex.GetType().Name}: {ex.Message}\n\nSee log: {Log.Path}");
            message = ex.Message;
            return Result.Failed;
        }

        WallBuilder.Result buildResult;
        using (var tx = new Transaction(doc, "Import Walls From URL"))
        {
            try
            {
                tx.Start();

                var opts = tx.GetFailureHandlingOptions();
                opts.SetFailuresPreprocessor(new SuppressWarningsPreprocessor());
                opts.SetClearAfterRollback(true);
                opts.SetForcedModalHandling(true);
                tx.SetFailureHandlingOptions(opts);

                Log.Info("Creating walls…");
                buildResult = WallBuilder.CreateWalls(doc, project.Walls, project.LevelElevationsMm);
                Log.Info($"Build pass finished. Created(in-memory)={buildResult.Created}, Skipped={buildResult.Errors.Count}. Committing…");

                DisableActiveCrop(doc);

                tx.Commit();

                Log.Info($"Transaction committed. Deleted={buildResult.Deleted}, Created={buildResult.Created}, Skipped={buildResult.Errors.Count}.");
                if (buildResult.CreatedLevels.Count > 0)
                    Log.Info($"Auto-created levels: {string.Join(", ", buildResult.CreatedLevels)}");
                if (buildResult.CreatedWallTypes.Count > 0)
                    Log.Info($"Auto-created wall types: {string.Join(", ", buildResult.CreatedWallTypes)}");
                foreach (var err in buildResult.Errors)
                    Log.Warn(err);
            }
            catch (Exception ex)
            {
                if (tx.HasStarted() && !tx.HasEnded())
                    tx.RollBack();

                Log.Error("Transaction failed, rolled back.", ex);
                ShowError("Wall creation failed",
                    $"{ex.GetType().Name}: {ex.Message}\n\nSee log: {Log.Path}");
                message = ex.Message;
                return Result.Failed;
            }
        }

        ZoomOpenViewsToFit(uiDoc);

        ShowSummary(buildResult, project.Walls.Count, url);
        return Result.Succeeded;
    }

    private static void DisableActiveCrop(Document doc)
    {
        if (doc.ActiveView is ViewPlan plan && plan.CropBoxActive)
        {
            try
            {
                plan.CropBoxActive = false;
                Log.Info($"Disabled crop box on active view '{plan.Name}'.");
            }
            catch (Exception ex)
            {
                Log.Warn($"Could not disable crop box: {ex.Message}");
            }
        }
    }

    private static void ZoomOpenViewsToFit(UIDocument uiDoc)
    {
        try
        {
            foreach (var v in uiDoc.GetOpenUIViews())
                v.ZoomToFit();
            Log.Info("Zoomed open views to fit.");
        }
        catch (Exception ex)
        {
            Log.Warn($"ZoomToFit failed: {ex.Message}");
        }
    }

    private static void ShowSummary(WallBuilder.Result result, int requested, string url)
    {
        var title = result.Errors.Count == 0 ? "Walls imported" : "Walls imported with warnings";

        var content = $"Source: {url}\nLog: {Log.Path}";
        if (result.Deleted > 0)
            content += $"\n\nReplaced {result.Deleted} existing wall(s) before import.";

        var expanded = new List<string>();
        if (result.CreatedLevels.Count > 0)
            expanded.Add("Auto-created levels: " + string.Join(", ", result.CreatedLevels));
        if (result.CreatedWallTypes.Count > 0)
            expanded.Add("Auto-created wall types: " + string.Join(", ", result.CreatedWallTypes));
        if (result.Errors.Count > 0)
            expanded.Add("Skipped walls:\n - " + string.Join("\n - ", result.Errors));

        var dialog = new TaskDialog(title)
        {
            MainInstruction = $"{result.Created} of {requested} wall(s) created.",
            MainContent = content,
        };
        if (expanded.Count > 0)
            dialog.ExpandedContent = string.Join("\n\n", expanded);

        dialog.Show();
    }

    private static void ShowError(string heading, string details)
    {
        new TaskDialog("Import Walls From URL")
        {
            MainIcon = TaskDialogIcon.TaskDialogIconError,
            MainInstruction = heading,
            MainContent = details,
        }.Show();
    }
}
