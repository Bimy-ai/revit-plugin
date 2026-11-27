using System.IO;
using System.Net;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BimyRevit.Models;
using BimyRevit.Services.Import;
using BimyRevit.UI;

namespace BimyRevit.Services;

/// <summary>
/// The NATIVE pull: fetch the project's parametric `bimy-revit-model/1` JSON
/// and build a fresh Revit document of real elements — `Wall.Create` with the
/// drawn location line, hosted door/window instances, floor and ceiling loops,
/// footprint roofs, rooms, grids, layered types and materials. The app stays
/// the single authority on what the building IS (the payload is written by the
/// same generator as the IFC export); this side owns how it ARRIVES.
///
/// The IFC pipeline remains the automatic fallback: the server answers
/// `{ fallback: "revit-ifc" }` for imported-IFC projects, a 404 from an older
/// deployment means the route doesn't exist yet, and a native build failure
/// offers the IFC importer before giving up — no project that pulled before
/// can stop pulling.
/// </summary>
internal static class RevitNativeImporter
{
    public static Result Run(
        UIApplication uiApp,
        BimyEnvironment env,
        PickResult pick,
        string token,
        ref string message)
    {
        var projectId = pick.ProjectId;
        var url = BimyEnvironments.RevitModelUrl(env, projectId);
        var cached = PullCache.Get(env, projectId);
        var hwnd = uiApp.MainWindowHandle;
        var jsonPath = Path.Combine(BimyPaths.ModelDir(projectId), "model.json");

        // ── 1. Pull the payload (conditionally) ──────────────────────────────
        JsonFetcher.DownloadResult download;
        try
        {
            Log.Info($"Pulling native model for project {projectId} from {url}"
                     + (cached?.ETag is null ? "" : $" (If-None-Match: {cached.ETag})"));

            download = ProgressWindow.Run(hwnd, "Load from BIMy", "Contacting BIMy…", async progress =>
                await JsonFetcher.DownloadToFileAsync(
                    url, token, jsonPath,
                    ifNoneMatch: cached?.ETag,
                    bytesRead: new Progress<long>(bytes =>
                        progress.Report($"Downloading the model… {PullUx.Megabytes(bytes)}")),
                    ct: default));
        }
        catch (BimyFetchException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            // Two meanings share the status: a current API saying the project
            // is empty (its body names `nothing_to_load`), or an older
            // deployment without the route at all (a framework 404). The second
            // still serves IFC — keep that pull working.
            if (ex.RawBody.Contains("nothing_to_load", StringComparison.OrdinalIgnoreCase))
            {
                PullUx.ShowNothingToLoad(env, projectId, ex.ServerMessage);
                return Result.Cancelled;
            }
            Log.Info("revit-model answered 404 without the nothing_to_load marker — likely an older API. Falling back to IFC.");
            return RevitIfcImporter.Run(uiApp, env, pick, token, ref message);
        }
        catch (BimyFetchException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized
                                         || ex.StatusCode == HttpStatusCode.Forbidden)
        {
            PullUx.ShowSessionRejected();
            return Result.Cancelled;
        }
        catch (Exception ex)
        {
            Log.Error("Failed to download the native model payload.", ex);
            message = $"Could not download the model from BIMy: {ex.Message}";
            return Result.Failed;
        }

        // ── 2. Nothing new? Offer the copy already on this machine ───────────
        if (download.NotModified)
        {
            Log.Info($"Server reports no change since the last pull of {projectId}.");
            if (cached?.HasLocalCopy == true)
            {
                var projectNameCached = PullUx.ResolveProjectName(pick, download, cached, projectId);
                var choice = PullUx.AskUpToDate(projectNameCached, cached.RvtPath!, pick.Mode);
                if (choice == PullUx.UpToDateChoice.Cancel) return Result.Cancelled;
                if (choice == PullUx.UpToDateChoice.UseLocal)
                    return PullUx.Deliver(uiApp, cached.RvtPath!, projectNameCached, pick.Mode,
                        counts: null, republished: false, ref message);
            }
            // Re-import (or the cache lied about a local copy): the conditional
            // request wrote nothing — fetch unconditionally so there IS a
            // payload on disk.
            try
            {
                download = ProgressWindow.Run(hwnd, "Load from BIMy", "Re-downloading the model…", async progress =>
                    await JsonFetcher.DownloadToFileAsync(
                        url, token, jsonPath, ifNoneMatch: null,
                        bytesRead: new Progress<long>(bytes =>
                            progress.Report($"Re-downloading the model… {PullUx.Megabytes(bytes)}")),
                        ct: default));
            }
            catch (Exception ex)
            {
                Log.Error("Re-download after 304 failed.", ex);
                message = $"Could not re-download the model from BIMy: {ex.Message}";
                return Result.Failed;
            }
        }

        // ── 3. Parse; the server may say "this one is an IFC project" ────────
        RevitModel? model;
        try
        {
            model = RevitModel.Parse(File.ReadAllText(jsonPath));
        }
        catch (Exception ex)
        {
            Log.Error("Could not parse the native model payload — falling back to IFC.", ex);
            return RevitIfcImporter.Run(uiApp, env, pick, token, ref message);
        }
        if (model is null || !string.IsNullOrEmpty(model.Fallback))
        {
            Log.Info($"Server directed this project to the IFC path"
                     + (model?.Fallback is null ? "." : $" (fallback: {model.Fallback})."));
            return RevitIfcImporter.Run(uiApp, env, pick, token, ref message);
        }
        if (model.Schema != RevitModel.ExpectedSchema)
        {
            Log.Warn($"Unknown payload schema '{model.Schema}' — falling back to IFC.");
            return RevitIfcImporter.Run(uiApp, env, pick, token, ref message);
        }

        var projectName = PullUx.ResolveProjectName(pick, download, cached, projectId, model.Project.Name);

        // ── 4. Choose where the .rvt lands, BEFORE building ──────────────────
        var target = TargetPath.Resolve(projectName, cached?.RvtPath);
        if (target is null)
        {
            Log.Info("User cancelled at the save-location prompt.");
            return Result.Cancelled;
        }

        // ── 5. Build natively; a failed build offers the IFC importer ────────
        NativeModelBuilder.Result built;
        try
        {
            Log.Info($"Building native model: {model.Stats.GetValueOrDefault("walls")} walls, "
                     + $"{model.Stats.GetValueOrDefault("floors")} floors, "
                     + $"{model.Stats.GetValueOrDefault("rooms")} rooms declared.");
            built = NativeModelBuilder.Build(uiApp.Application, model, projectId);
        }
        catch (Exception ex)
        {
            Log.Error("Native build failed.", ex);
            if (AskIfcFallback(ex.Message))
                return RevitIfcImporter.Run(uiApp, env, pick, token, ref message);
            message = $"Could not build the BIMy model natively: {ex.Message}";
            return Result.Failed;
        }

        // ── 6. Save, remember, deliver ───────────────────────────────────────
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            built.Document.SaveAs(target, new SaveAsOptions { OverwriteExistingFile = true });
            Log.Info($"Saved native model to {target}.");
        }
        catch (Exception ex)
        {
            Log.Error("Could not save the built model.", ex);
            message = $"The model was built, but saving it failed: {ex.Message}";
            try { built.Document.Close(false); } catch { /* already closing */ }
            return Result.Failed;
        }
        finally
        {
            try { built.Document.Close(false); } catch { /* opened below from disk */ }
        }

        PullCache.Save(env, new PullCache.PullRecord
        {
            ProjectId = projectId,
            Name = projectName,
            ETag = download.ETag ?? cached?.ETag,
            PublishedAt = download.PublishedAt ?? cached?.PublishedAt,
            RvtPath = target,
        });

        var detail = BuildDetail(built, model);
        return PullUx.Deliver(uiApp, target, projectName, pick.Mode, built.Counts, republished: true, ref message, detail);
    }

    private static string? BuildDetail(NativeModelBuilder.Result built, RevitModel model)
    {
        var lines = new List<string>();
        var declared = model.Stats.GetValueOrDefault("walls")
                       + model.Stats.GetValueOrDefault("floors")
                       + model.Stats.GetValueOrDefault("ceilings")
                       + model.Stats.GetValueOrDefault("roofs");
        var builtCount = built.Counts.GetValueOrDefault("Walls")
                         + built.Counts.GetValueOrDefault("Floors")
                         + built.Counts.GetValueOrDefault("Ceilings")
                         + built.Counts.GetValueOrDefault("Roofs");
        if (declared > builtCount)
            lines.Add($"{declared - builtCount} element(s) could not be built — see the log.");
        if (built.Warnings.Count > 0)
        {
            lines.Add("Warnings:");
            lines.AddRange(built.Warnings.Take(12).Select(w => " · " + w));
            if (built.Warnings.Count > 12) lines.Add($" · …and {built.Warnings.Count - 12} more (see the log).");
        }
        return lines.Count == 0 ? null : string.Join("\n", lines);
    }

    private static bool AskIfcFallback(string error)
    {
        var dialog = new TaskDialog("Load from BIMy")
        {
            MainIcon = TaskDialogIcon.TaskDialogIconWarning,
            MainInstruction = "Native import failed",
            MainContent = error,
            CommonButtons = TaskDialogCommonButtons.Cancel,
            DefaultButton = TaskDialogResult.CommandLink1,
        };
        dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1,
            "Import via IFC instead",
            "Slower and less native, but robust — the pipeline this add-in used before.");
        return dialog.Show() == TaskDialogResult.CommandLink1;
    }
}
