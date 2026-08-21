using System.Diagnostics;
using System.IO;
using System.Reflection;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BimyRevit.Models;
using BimyRevit.Services;

namespace BimyRevit.Commands;

/// <summary>
/// Everything a support conversation needs, in one dialog: who is connected,
/// to which environment, which build of the add-in is loaded, and where the log
/// is. Without it, "it doesn't work" costs a round trip to establish that the
/// user is on Sandbox with last month's installer.
/// </summary>
// Manual, not ReadOnly: ReadOnly implies an active document to read, and this
// command must work from Revit's start page with nothing open — which is
// exactly where someone checks why "Load from BIMy" is greyed out.
[Transaction(TransactionMode.Manual)]
[Regeneration(RegenerationOption.Manual)]
public sealed class BimyStatusCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        // Re-verify rather than report the cached flag: the interesting failure
        // is a token that USED to work, and a status screen that says
        // "Connected" while every pull 401s is worse than none.
        try { SessionState.RefreshAsync().GetAwaiter().GetResult(); }
        catch (Exception ex) { Log.Warn($"Status refresh failed: {ex.Message}"); }

        var connected = SessionState.IsConnected;
        var env = SessionState.Environment;
        var user = SessionState.Current;

        var lines = new List<string>
        {
            connected
                ? $"Account          {user?.DisplayLabel ?? "(unknown)"}"
                : "Account          not connected",
            env is null
                ? "Environment      —"
                : $"Environment      {BimyEnvironments.DisplayName(env.Value)}  ({BimyEnvironments.BaseUrl(env.Value)})",
            $"Add-in version   {Version()}",
            $"Revit            {commandData.Application.Application.VersionNumber}"
                + $" ({commandData.Application.Application.VersionBuild})",
            $"Log              {Log.Path}",
        };

        var dialog = new TaskDialog("BIMy")
        {
            MainIcon = connected ? TaskDialogIcon.TaskDialogIconInformation : TaskDialogIcon.TaskDialogIconWarning,
            MainInstruction = connected ? "Connected to BIMy" : "Not connected to BIMy",
            MainContent = string.Join("\n", lines),
            CommonButtons = TaskDialogCommonButtons.Close,
        };
        dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Open the log file");
        dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Open the BIMy data folder", BimyPaths.Root);

        switch (dialog.Show())
        {
            case TaskDialogResult.CommandLink1:
                Open(File.Exists(Log.Path) ? Log.Path : BimyPaths.Root);
                break;
            case TaskDialogResult.CommandLink2:
                Open(BimyPaths.Root);
                break;
        }

        return Result.Succeeded;
    }

    private static string Version()
        => Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";

    private static void Open(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Warn($"Could not open {path}: {ex.Message}"); }
    }
}
