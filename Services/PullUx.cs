using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BimyRevit.Models;
using BimyRevit.UI;

namespace BimyRevit.Services;

/// <summary>
/// Everything a pull says to the user and does with the finished .rvt — shared
/// by the native importer and the IFC fallback so the two paths cannot drift
/// apart in behaviour: the up-to-date offer, the nothing-to-load explanation,
/// the session-rejected warning, open-vs-link delivery and the final summary.
/// </summary>
internal static class PullUx
{
    // ── Delivery ────────────────────────────────────────────────────────────

    /// <summary>Opens or links the finished .rvt and reports what happened.</summary>
    public static Result Deliver(
        UIApplication uiApp,
        string rvtPath,
        string projectName,
        PullMode mode,
        IReadOnlyDictionary<string, int>? counts,
        bool republished,
        ref string message,
        string? detail = null)
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
                    ShowSummary(projectName, rvtPath, counts, linked: true, republished, detail);
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

        ShowSummary(projectName, rvtPath, counts, linked: false, republished, detail);
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

    public enum UpToDateChoice { UseLocal, Reimport, Cancel }

    public static UpToDateChoice AskUpToDate(string projectName, string rvtPath, PullMode mode)
    {
        var dialog = new TaskDialog("Load from BIMy")
        {
            MainIcon = TaskDialogIcon.TaskDialogIconInformation,
            MainInstruction = $"“{projectName}” hasn't changed since you last pulled it",
            MainContent = "The model hasn't changed in BIMy since this machine downloaded it.",
            CommonButtons = TaskDialogCommonButtons.Cancel,
            DefaultButton = TaskDialogResult.CommandLink1,
        };
        dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1,
            mode == PullMode.LinkIntoCurrent ? "Link the copy I already have" : "Open the copy I already have",
            rvtPath);
        dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink2,
            "Import it again anyway",
            "Downloads and rebuilds the model from scratch. Use this if your local copy was damaged or edited.");

        return dialog.Show() switch
        {
            TaskDialogResult.CommandLink1 => UpToDateChoice.UseLocal,
            TaskDialogResult.CommandLink2 => UpToDateChoice.Reimport,
            _ => UpToDateChoice.Cancel,
        };
    }

    // The API serves every project's model on demand, so a 404 means the
    // project has nothing to load at all (nothing drawn or imported), or the
    // id doesn't resolve for this token — a normal state, not an error.
    public static void ShowNothingToLoad(BimyEnvironment env, string projectId, string? serverMessage)
    {
        var dialog = new TaskDialog("Load from BIMy")
        {
            MainIcon = TaskDialogIcon.TaskDialogIconInformation,
            MainInstruction = "This project has nothing to load yet",
            MainContent =
                "Draw or import a building in BIMy first, then run Load from BIMy again. "
                + "If you pasted an id, check it belongs to a project this account can open."
                + (string.IsNullOrWhiteSpace(serverMessage) ? "" : "\n\n" + serverMessage),
            CommonButtons = TaskDialogCommonButtons.Close,
        };
        dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1,
            "Open this project in BIMy",
            BimyEnvironments.ProjectWebUrl(env, projectId));

        if (dialog.Show() == TaskDialogResult.CommandLink1)
            OpenInShell(BimyEnvironments.ProjectWebUrl(env, projectId));
    }

    public static void ShowSessionRejected()
    {
        new TaskDialog("Load from BIMy")
        {
            MainIcon = TaskDialogIcon.TaskDialogIconWarning,
            MainInstruction = "Your BIMy session was rejected",
            MainContent = "Re-connect with BIMy → Set API token…, then try again.",
        }.Show();
    }

    public static void ShowSummary(
        string projectName,
        string rvtPath,
        IReadOnlyDictionary<string, int>? counts,
        bool linked,
        bool republished,
        string? detail = null)
    {
        var what = counts is { Count: > 0 }
            ? string.Join("\n", counts.Select(kv => $"    {kv.Value,6:N0}   {kv.Key}"))
            : "The model opened with walls, floors, ceilings, doors, windows, openings,\nrooms, materials and properties as native Revit elements.";

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
        if (!string.IsNullOrWhiteSpace(detail)) dialog.ExpandedContent = detail;
        dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Show the file in Explorer");

        if (dialog.Show() == TaskDialogResult.CommandLink1)
            OpenInShell("/select,\"" + rvtPath + "\"", "explorer.exe");
    }

    public static void OpenInShell(string argument, string? executable = null)
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

    public static string ResolveProjectName(
        PickResult pick,
        JsonFetcher.DownloadResult? download,
        PullCache.PullRecord? cached,
        string projectId,
        string? payloadName = null)
    {
        // The picker's name is the one the user just read off the list, so it
        // wins. The server's x-ifc-name is next (it carries the project name),
        // then the payload's own claim, then the last pull's record.
        if (!string.IsNullOrWhiteSpace(pick.ProjectName)) return pick.ProjectName!.Trim();
        if (!string.IsNullOrWhiteSpace(download?.SuggestedName))
            return System.IO.Path.GetFileNameWithoutExtension(download!.SuggestedName!);
        if (!string.IsNullOrWhiteSpace(payloadName)) return payloadName!.Trim();
        if (!string.IsNullOrWhiteSpace(cached?.Name)) return cached!.Name!;
        return "BIMy model " + projectId[..Math.Min(8, projectId.Length)];
    }

    public static string Megabytes(long bytes) => $"{bytes / 1024d / 1024d:0.0} MB";
}
