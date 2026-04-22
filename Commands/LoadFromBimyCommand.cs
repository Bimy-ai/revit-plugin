using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitWallsPlugin.Models;
using RevitWallsPlugin.Services;
using RevitWallsPlugin.UI;

namespace RevitWallsPlugin.Commands;

[Transaction(TransactionMode.Manual)]
[Regeneration(RegenerationOption.Manual)]
public sealed class LoadFromBimyCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        var uiApp = commandData.Application;
        var uiDoc = uiApp.ActiveUIDocument;
        if (uiDoc is null)
        {
            TaskDialog.Show("Load from BIMy", "No active Revit document.");
            return Result.Cancelled;
        }

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
                MainContent = "Use Connect BIMy → Set API token… to set up a session, then try again.",
            }.Show();
            return Result.Cancelled;
        }

        var token = SessionState.Token!;
        var env = SessionState.Environment ?? BimyEnvironments.Default;
        var envLabel = BimyEnvironments.DisplayName(env);

        var lastId = ProjectIdState.Load(env);
        var projectId = ProjectIdDialog.Show(uiApp.MainWindowHandle, "Load from BIMy", envLabel, lastId);
        if (string.IsNullOrWhiteSpace(projectId))
        {
            Log.Info("User cancelled Load from BIMy dialog.");
            return Result.Cancelled;
        }

        ProjectIdState.Save(env, projectId);

        var url = BimyEnvironments.ProjectDataUrl(env, projectId);
        return ImportRunner.Run(uiApp, env, projectId, url, token, ref message);
    }
}
