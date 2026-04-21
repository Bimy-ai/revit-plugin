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
        WallsPayload payload;
        try
        {
            payload = JsonFetcher.FetchAsync(url).GetAwaiter().GetResult();
            Log.Info($"Fetched {payload.Walls.Count} wall(s), units='{payload.Units}'.");
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
                buildResult = WallBuilder.CreateWalls(doc, payload);
                Log.Info($"Build pass finished. Created(in-memory)={buildResult.Created}, Skipped={buildResult.Errors.Count}. Committing…");

                DisableActiveCrop(doc);

                tx.Commit();

                Log.Info($"Transaction committed. Deleted={buildResult.Deleted}, Created={buildResult.Created}, Fallback={buildResult.FallbackCount}, Skipped={buildResult.Errors.Count}.");
                if (buildResult.MissingTypes.Count > 0)
                    Log.Warn($"Wall types not found, substituted project default: {string.Join(", ", buildResult.MissingTypes)}");
                if (buildResult.CreatedLevels.Count > 0)
                    Log.Info($"Auto-created levels: {string.Join(", ", buildResult.CreatedLevels)}");
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

        ShowSummary(buildResult, payload.Walls.Count, url);
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
        if (result.FallbackCount > 0)
            content += $"\n{result.FallbackCount} wall(s) used a fallback type/level.";

        var expanded = new List<string>();
        if (result.MissingTypes.Count > 0)
            expanded.Add("Wall types not found → substituted project default: " + string.Join(", ", result.MissingTypes));
        if (result.CreatedLevels.Count > 0)
            expanded.Add("Auto-created levels: " + string.Join(", ", result.CreatedLevels));
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
