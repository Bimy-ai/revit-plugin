using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BimyRevit.Models;
using BimyRevit.Services;
using BimyRevit.UI;

namespace BimyRevit.Commands;

[Transaction(TransactionMode.Manual)]
[Regeneration(RegenerationOption.Manual)]
public sealed class LoadFromBimyCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        var uiApp = commandData.Application;
        // No active-document check: pulling a BIMy model OPENS a new native
        // project (via Revit's IFC importer), so it works from the Revit start
        // page with nothing open, not only inside an existing document.

        if (!SessionState.IsConnected)
        {
            try { SessionState.RefreshAsync().GetAwaiter().GetResult(); }
            catch (Exception ex) { Log.Warn($"Pre-load session refresh failed: {ex.Message}"); }
        }

        if (!SessionState.IsConnected)
        {
            new TaskDialog("Load from BIMy")
            {
                MainIcon = TaskDialogIcon.TaskDialogIconWarning,
                MainInstruction = "Not connected to BIMy",
                MainContent = "Use BIMy → Set API token… to set up a session, then try again.",
            }.Show();
            return Result.Cancelled;
        }

        var token = SessionState.Token!;
        var env = SessionState.Environment ?? BimyEnvironments.Default;

        // Fetch the project list behind the progress window — one small call,
        // but on a slow connection it is still seconds during which a
        // dialog-less Revit looks hung. It cannot fail the command: it degrades
        // to an empty list, and the picker's paste-an-id field covers that.
        //
        // No publish index any more: the API serves every project's model on
        // demand (publishing in the app is just a freshness optimisation), so
        // "which projects are pullable" stopped being a question — all of them.
        var projects = ProgressWindow.Run(
            uiApp.MainWindowHandle, "Load from BIMy", "Loading your BIMy projects…",
            async _ => await BimyApi.ListProjectsAsync(env, token));

        var pick = ProjectPickerDialog.Show(
            uiApp.MainWindowHandle,
            env,
            SessionState.Current?.DisplayLabel,
            projects,
            preselectProjectId: PullCache.LastProjectId(env),
            canLink: uiApp.ActiveUIDocument?.Document is not null);

        if (pick is null)
        {
            Log.Info("User cancelled the Load from BIMy picker.");
            return Result.Cancelled;
        }

        PullCache.RememberProjectId(env, pick.ProjectId);
        return RevitIfcImporter.Run(uiApp, env, pick, token, ref message);
    }
}
