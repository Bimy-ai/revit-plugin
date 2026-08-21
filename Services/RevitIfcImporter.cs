using System.Diagnostics;
using System.IO;
using System.Net;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.IFC;
using Autodesk.Revit.UI;
using RevitWallsPlugin.Models;
using RevitWallsPlugin.UI;

namespace RevitWallsPlugin.Services;

/// <summary>
/// Pulls a project's published IFC from BIMy and turns it into a native Revit
/// model. This is the whole import: BIMy's own generator
/// (frontend/src/lib/ifc/ifcGenerate.js) writes the faithful building — walls,
/// floors, ceilings, doors, windows, openings, spaces, materials and property
/// sets — and Revit's native IFC importer turns every one of those into a
/// first-class Revit element. There is no per-element creation code on this
/// side, and deliberately so: it keeps a single authority for what a building IS
/// (the app), instead of a second, ever-drifting one here.
///
/// What this file DOES own is everything around that conversion, which is where
/// a pull actually goes wrong in practice: not re-downloading a model nobody
/// republished, never silently overwriting a .rvt the user has since edited,
/// putting the result somewhere they can find it again, and saying plainly what
/// came across.
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

        // ── 1. Pull the published IFC (conditionally) ────────────────────────
        var ifcPath = Path.Combine(BimyPaths.ModelDir(projectId), "model.ifc");
        JsonFetcher.DownloadResult download;
        try
        {
            Log.Info($"Pulling published IFC for project {projectId} from {url}"
                     + (cached?.ETag is null ? "" : $" (If-None-Match: {cached.ETag})"));

            download = ProgressWindow.Run(hwnd, "Load from BIMy", "Contacting BIMy…", async progress =>
                await JsonFetcher.DownloadToFileAsync(
                    url, token, ifcPath,
                    ifNoneMatch: cached?.ETag,
                    bytesRead: new Progress<long>(bytes =>
                        progress.Report($"Downloading the published model… {Megabytes(bytes)}")),
                    ct: default));
        }
        catch (BimyFetchException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            ShowNotPublished(env, projectId, ex);
            return Result.Cancelled;
        }
        catch (BimyFetchException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized
                                         || ex.StatusCode == HttpStatusCode.Forbidden)
        {
            new TaskDialog("Load from BIMy")
            {
                MainIcon = TaskDialogIcon.TaskDialogIconWarning,
                MainInstruction = "Your BIMy session was rejected",
                MainContent = "Re-connect with BIMy → Set API token…, then try again.",
            }.Show();
            return Result.Cancelled;
        }
        catch (Exception ex)
        {
            Log.Error("Failed to download published IFC.", ex);
            message = $"Could not download the model from BIMy: {ex.Message}";
            return Result.Failed;
        }

        var projectName = ResolveProjectName(pick, download, cached, projectId);

        // ── 2. Nothing new? Offer the copy already on this machine ───────────
        // Re-converting an unchanged model costs a minute of Revit's time and
        // produces a second file the user then has to reconcile with the one
        // they've been working in. Ask instead of assuming.
        if (download.NotModified)
        {
            Log.Info($"Server reports no change since the last pull of {projectId}.");
            if (cached?.HasLocalCopy == true)
            {
                var choice = AskUpToDate(projectName, cached.RvtPath!, pick.Mode);
                if (choice == UpToDateChoice.Cancel) return Result.Cancelled;
                if (choice == UpToDateChoice.UseLocal)
                    return Deliver(uiApp, cached.RvtPath!, projectName, pick.Mode, counts: null, republished: false, ref message);
                // Re-import: fall through, but the conditional request wrote
                // nothing — re-fetch unconditionally so there IS an IFC on disk.
                try
                {
                    download = ProgressWindow.Run(hwnd, "Load from BIMy", "Re-downloading the published model…", async progress =>
                        await JsonFetcher.DownloadToFileAsync(
                            url, token, ifcPath,
                            ifNoneMatch: null,
                            bytesRead: new Progress<long>(bytes =>
                                progress.Report($"Re-downloading the published model… {Megabytes(bytes)}")),
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
                    download = ProgressWindow.Run(hwnd, "Load from BIMy", "Downloading the published model…", async progress =>
                        await JsonFetcher.DownloadToFileAsync(
                            url, token, ifcPath, ifNoneMatch: null,
                            bytesRead: new Progress<long>(bytes =>
                                progress.Report($"Downloading the published model… {Megabytes(bytes)}")),
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

        return Deliver(uiApp, target, projectName, pick.Mode, counts, republished: true, ref message);
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

    /// <summary>Opens or links the finished .rvt and reports what happened.</summary>
    private static Result Deliver(
        UIApplication uiApp,
        string rvtPath,
        string projectName,
        PullMode mode,
        IReadOnlyDictionary<string, int>? counts,
        bool republished,
        ref string message)
    {
        if (mode == PullMode.LinkIntoCurrent)
        {
            var doc = uiApp.ActiveUIDocument?.Document;
            if (doc is null)
            {
                // The picker only offers Link with a document open, but the user
                // can close it while the dialog is up.
                Log.Warn("Link was requested but no document is open — opening instead.");
                mode = PullMode.OpenNew;
            }
            else
            {
                try
                {
                    LinkInto(doc, rvtPath);
                    ShowSummary(projectName, rvtPath, counts, linked: true, republished);
                    return Result.Succeeded;
                }
                catch (Exception ex)
                {
                    Log.Error("Could not link the pulled model.", ex);
                    message = $"The model was saved to {rvtPath}, but linking it failed: {ex.Message}";
                    return Result.Failed;
                }
            }
        }

        try
        {
            uiApp.OpenAndActivateDocument(rvtPath);
            Log.Info($"Opened pulled model as {rvtPath}");
        }
        catch (Exception ex)
        {
            Log.Error("Could not open the converted model.", ex);
            message = $"The model was saved to {rvtPath}, but Revit could not open it: {ex.Message}";
            return Result.Failed;
        }

        ShowSummary(projectName, rvtPath, counts, linked: false, republished);
        return Result.Succeeded;
    }

    private static void LinkInto(Document doc, string rvtPath)
    {
        using var tx = new Transaction(doc, "Link BIMy model");
        tx.Start();

        var path = ModelPathUtils.ConvertUserVisiblePathToModelPath(rvtPath);
        // Relative path type (the ctor's bool): the link keeps resolving when
        // the host project and the pulled model move together, which is what
        // happens the moment someone copies both onto a shared drive.
        var link = RevitLinkType.Create(doc, path, new RevitLinkOptions(true));
        RevitLinkInstance.Create(doc, link.ElementId);

        tx.Commit();
        Log.Info($"Linked {rvtPath} into {doc.Title}.");
    }

    // ── Dialogs ─────────────────────────────────────────────────────────────

    private enum UpToDateChoice { UseLocal, Reimport, Cancel }

    private static UpToDateChoice AskUpToDate(string projectName, string rvtPath, PullMode mode)
    {
        var dialog = new TaskDialog("Load from BIMy")
        {
            MainIcon = TaskDialogIcon.TaskDialogIconInformation,
            MainInstruction = $"“{projectName}” hasn't changed since you last pulled it",
            MainContent = "Nothing has been re-exported in BIMy since this machine downloaded the model.",
            CommonButtons = TaskDialogCommonButtons.Cancel,
            DefaultButton = TaskDialogResult.CommandLink1,
        };
        dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1,
            mode == PullMode.LinkIntoCurrent ? "Link the copy I already have" : "Open the copy I already have",
            rvtPath);
        dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink2,
            "Import it again anyway",
            "Downloads and converts the model from scratch. Use this if your local copy was damaged or edited.");

        return dialog.Show() switch
        {
            TaskDialogResult.CommandLink1 => UpToDateChoice.UseLocal,
            TaskDialogResult.CommandLink2 => UpToDateChoice.Reimport,
            _ => UpToDateChoice.Cancel,
        };
    }

    private static void ShowNotPublished(BimyEnvironment env, string projectId, BimyFetchException ex)
    {
        var dialog = new TaskDialog("Load from BIMy")
        {
            MainIcon = TaskDialogIcon.TaskDialogIconInformation,
            MainInstruction = "This project hasn't been exported to Revit yet",
            MainContent =
                "Open the project in BIMy and choose \"Export to Revit\" to publish the model, "
                + "then run Load from BIMy again."
                + (string.IsNullOrWhiteSpace(ex.ServerMessage) ? "" : "\n\n" + ex.ServerMessage),
            CommonButtons = TaskDialogCommonButtons.Close,
        };
        dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1,
            "Open this project in BIMy",
            BimyEnvironments.ProjectWebUrl(env, projectId));

        if (dialog.Show() == TaskDialogResult.CommandLink1)
            OpenInShell(BimyEnvironments.ProjectWebUrl(env, projectId));
    }

    private static void ShowSummary(
        string projectName,
        string rvtPath,
        IReadOnlyDictionary<string, int>? counts,
        bool linked,
        bool republished)
    {
        var what = counts is { Count: > 0 }
            ? string.Join("\n", counts.Select(kv => $"    {kv.Value,6:N0}   {kv.Key}"))
            : "The model opened with walls, floors, ceilings, doors, windows, openings,\nspaces, materials and properties as native Revit elements.";

        var lead = linked
            ? $"Linked “{projectName}” from BIMy"
            : $"Loaded “{projectName}” from BIMy";
        if (!republished) lead += " (your existing copy)";

        var dialog = new TaskDialog("Load from BIMy")
        {
            MainIcon = TaskDialogIcon.TaskDialogIconInformation,
            MainInstruction = lead,
            MainContent = what + "\n\nSaved to:\n" + rvtPath,
            CommonButtons = TaskDialogCommonButtons.Close,
        };
        dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Show the file in Explorer");

        if (dialog.Show() == TaskDialogResult.CommandLink1)
            OpenInShell("/select,\"" + rvtPath + "\"", "explorer.exe");
    }

    private static void OpenInShell(string argument, string? executable = null)
    {
        try
        {
            var info = executable is null
                ? new ProcessStartInfo(argument) { UseShellExecute = true }
                : new ProcessStartInfo(executable, argument);
            Process.Start(info);
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not launch shell for '{argument}': {ex.Message}");
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static string ResolveProjectName(
        PickResult pick,
        JsonFetcher.DownloadResult download,
        PullCache.PullRecord? cached,
        string projectId)
    {
        // The picker's name is the one the user just read off the list, so it
        // wins. The server's x-ifc-name is next (it carries the project name at
        // publish time), then whatever this machine recorded on the last pull.
        if (!string.IsNullOrWhiteSpace(pick.ProjectName)) return pick.ProjectName!.Trim();
        if (!string.IsNullOrWhiteSpace(download.SuggestedName))
            return Path.GetFileNameWithoutExtension(download.SuggestedName!);
        if (!string.IsNullOrWhiteSpace(cached?.Name)) return cached!.Name!;
        return "BIMy model " + projectId[..Math.Min(8, projectId.Length)];
    }

    private static string Megabytes(long bytes) => $"{bytes / 1024d / 1024d:0.0} MB";
}
