using System.IO;
using System.Net;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.IFC;
using Autodesk.Revit.UI;
using BimyRevit.Models;
using BimyRevit.UI;

namespace BimyRevit.Services;

/// <summary>
/// The IFC pull — now the FALLBACK path. The primary pull is
/// <see cref="RevitNativeImporter"/> (parametric payload → native Revit API
/// creation); this pipeline serves the projects native creation cannot: models
/// IMPORTED into BIMy as IFC (the stored bytes are the faithful artifact, and
/// Revit's IFC importer is the honest converter), published snapshots with
/// baked edits, and older API deployments without the native route.
///
/// What this file owns is everything around that conversion: conditional
/// download, never clobbering an edited .rvt, and saying what came across —
/// all shared with the native path through <see cref="PullUx"/>.
/// </summary>
internal static class RevitIfcImporter
{
    /// <summary>
    /// Categories worth naming in the summary. Not exhaustive by design: a list
    /// of forty categories with 0 next to thirty of them tells the user less
    /// than a list of the eight things they drew.
    /// </summary>
    private static readonly (BuiltInCategory Category, string Label)[] ReportedCategories =
    {
        (BuiltInCategory.OST_Walls, "Walls"),
        (BuiltInCategory.OST_Floors, "Floors"),
        (BuiltInCategory.OST_Ceilings, "Ceilings"),
        (BuiltInCategory.OST_Roofs, "Roofs"),
        (BuiltInCategory.OST_Doors, "Doors"),
        (BuiltInCategory.OST_Windows, "Windows"),
        (BuiltInCategory.OST_Columns, "Columns"),
        (BuiltInCategory.OST_StructuralColumns, "Structural columns"),
        (BuiltInCategory.OST_Stairs, "Stairs"),
        (BuiltInCategory.OST_Rooms, "Rooms"),
        (BuiltInCategory.OST_MEPSpaces, "Spaces"),
        (BuiltInCategory.OST_GenericModel, "Generic models"),
    };

    public static Result Run(
        UIApplication uiApp,
        BimyEnvironment env,
        PickResult pick,
        string token,
        ref string message)
    {
        var projectId = pick.ProjectId;
        var url = BimyEnvironments.RevitIfcUrl(env, projectId);
        var cached = PullCache.Get(env, projectId);
        var hwnd = uiApp.MainWindowHandle;

        // ── 1. Pull the project's IFC (conditionally) ────────────────────────
        // The cached ETag may belong to the native payload (the primary path
        // writes it); the IFC route then simply answers 200 with fresh bytes —
        // a harmless full download, never a wrong 304.
        var ifcPath = Path.Combine(BimyPaths.ModelDir(projectId), "model.ifc");
        JsonFetcher.DownloadResult download;
        try
        {
            Log.Info($"Pulling IFC for project {projectId} from {url}"
                     + (cached?.ETag is null ? "" : $" (If-None-Match: {cached.ETag})"));

            download = ProgressWindow.Run(hwnd, "Load from BIMy", "Contacting BIMy…", async progress =>
                await JsonFetcher.DownloadToFileAsync(
                    url, token, ifcPath,
                    ifNoneMatch: cached?.ETag,
                    bytesRead: new Progress<long>(bytes =>
                        progress.Report($"Downloading the model… {PullUx.Megabytes(bytes)}")),
                    ct: default));
        }
        catch (BimyFetchException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            PullUx.ShowNothingToLoad(env, projectId, ex.ServerMessage);
            return Result.Cancelled;
        }
        catch (BimyFetchException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized
                                         || ex.StatusCode == HttpStatusCode.Forbidden)
        {
            PullUx.ShowSessionRejected();
            return Result.Cancelled;
        }
        catch (Exception ex)
        {
            Log.Error("Failed to download published IFC.", ex);
            message = $"Could not download the model from BIMy: {ex.Message}";
            return Result.Failed;
        }

        var projectName = PullUx.ResolveProjectName(pick, download, cached, projectId);

        // ── 2. Nothing new? Offer the copy already on this machine ───────────
        // Re-converting an unchanged model costs a minute of Revit's time and
        // produces a second file the user then has to reconcile with the one
        // they've been working in. Ask instead of assuming.
        if (download.NotModified)
        {
            Log.Info($"Server reports no change since the last pull of {projectId}.");
            if (cached?.HasLocalCopy == true)
            {
                var choice = PullUx.AskUpToDate(projectName, cached.RvtPath!, pick.Mode);
                if (choice == PullUx.UpToDateChoice.Cancel) return Result.Cancelled;
                if (choice == PullUx.UpToDateChoice.UseLocal)
                    return PullUx.Deliver(uiApp, cached.RvtPath!, projectName, pick.Mode, counts: null, republished: false, ref message);
                // Re-import: fall through, but the conditional request wrote
                // nothing — re-fetch unconditionally so there IS an IFC on disk.
                try
                {
                    download = ProgressWindow.Run(hwnd, "Load from BIMy", "Re-downloading the model…", async progress =>
                        await JsonFetcher.DownloadToFileAsync(
                            url, token, ifcPath,
                            ifNoneMatch: null,
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
            else if (!File.Exists(ifcPath))
            {
                // Cache claims we have it, disk disagrees. Drop the ETag and
                // start clean rather than importing a file that isn't there.
                Log.Warn("Cache had an ETag but neither the .rvt nor the .ifc is on disk — pulling again unconditionally.");
                try
                {
                    download = ProgressWindow.Run(hwnd, "Load from BIMy", "Downloading the model…", async progress =>
                        await JsonFetcher.DownloadToFileAsync(
                            url, token, ifcPath, ifNoneMatch: null,
                            bytesRead: new Progress<long>(bytes =>
                                progress.Report($"Downloading the model… {PullUx.Megabytes(bytes)}")),
                            ct: default));
                }
                catch (Exception ex)
                {
                    Log.Error("Unconditional re-download failed.", ex);
                    message = $"Could not download the model from BIMy: {ex.Message}";
                    return Result.Failed;
                }
            }
        }

        Log.Info(download.NotModified
            ? $"Converting the IFC already on disk: {ifcPath}."
            : $"Downloaded {download.Bytes:N0} bytes to {ifcPath}.");

        // ── 3. Choose where the Revit file lands, BEFORE converting ──────────
        // Asking after a minute of import would mean throwing that minute away
        // if the user cancels — and silently reusing the previous path would
        // overwrite work they did in Revit since the last pull.
        var target = TargetPath.Resolve(projectName, cached?.RvtPath);
        if (target is null)
        {
            Log.Info("User cancelled at the save-location prompt.");
            return Result.Cancelled;
        }

        // ── 4. Convert with Revit's native IFC importer ──────────────────────
        Dictionary<string, int>? counts;
        try
        {
            counts = ConvertToRevit(uiApp, ifcPath, target);
        }
        catch (Exception ex)
        {
            Log.Error("IFC import failed.", ex);
            message = $"Revit could not open the BIMy model: {ex.Message}";
            return Result.Failed;
        }

        PullCache.Save(env, new PullCache.PullRecord
        {
            ProjectId = projectId,
            Name = projectName,
            ETag = download.ETag ?? cached?.ETag,
            PublishedAt = download.PublishedAt ?? cached?.PublishedAt,
            RvtPath = target,
        });

        return PullUx.Deliver(uiApp, target, projectName, pick.Mode, counts, republished: true, ref message);
    }

    /// <summary>
    /// Runs the IFC through Revit's importer and saves the result as a native
    /// .rvt at <paramref name="rvtPath"/>. Returns per-category element counts,
    /// read from the converted document before it is closed.
    /// </summary>
    private static Dictionary<string, int> ConvertToRevit(UIApplication uiApp, string ifcPath, string rvtPath)
    {
        // Parametric intent creates real Revit categories (Walls, Doors, Floors,
        // …) rather than a reference mesh, so the pulled model behaves like one
        // authored in Revit.
        var options = new IFCImportOptions
        {
            Action = IFCImportAction.Open,
            Intent = IFCImportIntent.Parametric,
            AutoJoin = true,
            AutocorrectOffAxisLines = true,
        };

        Log.Info($"Opening IFC document: {ifcPath}");
        Document? ifcDoc = null;
        try
        {
            ifcDoc = uiApp.Application.OpenIFCDocument(ifcPath, options)
                ?? throw new InvalidOperationException("Revit's IFC importer returned no document.");

            var counts = CountElements(ifcDoc);

            // OpenIFCDocument yields a DB document that is NOT the active one;
            // saving it and re-opening the .rvt through the UI is the reliable
            // way to surface it, and leaves the user a real project file rather
            // than an unsaved in-memory conversion.
            Directory.CreateDirectory(Path.GetDirectoryName(rvtPath)!);
            ifcDoc.SaveAs(rvtPath, new SaveAsOptions { OverwriteExistingFile = true });
            Log.Info($"Saved converted model to {rvtPath}.");
            return counts;
        }
        finally
        {
            // Close the importer's copy whether or not SaveAs threw, so a failed
            // save doesn't leave an orphan document open in the session.
            try { ifcDoc?.Close(false); } catch { /* already closing */ }
        }
    }

    private static Dictionary<string, int> CountElements(Document doc)
    {
        var counts = new Dictionary<string, int>();
        foreach (var (category, label) in ReportedCategories)
        {
            try
            {
                var n = new FilteredElementCollector(doc)
                    .OfCategory(category)
                    .WhereElementIsNotElementType()
                    .GetElementCount();
                if (n > 0) counts[label] = n;
            }
            catch (Exception ex)
            {
                // A category absent from this document's discipline throws
                // rather than returning zero; that is not worth failing a pull.
                Log.Warn($"Could not count {label}: {ex.Message}");
            }
        }

        try
        {
            var materials = new FilteredElementCollector(doc).OfClass(typeof(Material)).GetElementCount();
            if (materials > 0) counts["Materials"] = materials;
        }
        catch { /* same */ }

        Log.Info("Imported: " + (counts.Count == 0
            ? "(no elements in the reported categories)"
            : string.Join(", ", counts.Select(kv => $"{kv.Value} {kv.Key.ToLowerInvariant()}"))));
        return counts;
    }
}
