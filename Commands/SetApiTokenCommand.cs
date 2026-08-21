using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BimyRevit.Models;
using BimyRevit.Services;
using BimyRevit.UI;

namespace BimyRevit.Commands;

[Transaction(TransactionMode.ReadOnly)]
[Regeneration(RegenerationOption.Manual)]
public sealed class SetApiTokenCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        var uiApp = commandData.Application;

        var existing = SessionStore.TryLoad();
        var defaultEnv = existing?.Environment
                         ?? SessionState.Environment
                         ?? BimyEnvironments.Default;
        var locked = existing is not null;

        var input = SetApiTokenDialog.Show(
            uiApp.MainWindowHandle,
            defaultEnv,
            environmentLocked: locked,
            lockedReasonHint: locked
                ? $"Locked on {BimyEnvironments.DisplayName(defaultEnv)} — disconnect to change environment."
                : null);

        if (input is null)
        {
            Log.Info("User cancelled Set API token dialog.");
            return Result.Cancelled;
        }

        BimyUser? user;
        try
        {
            user = BimyApi.VerifyAsync(input.Environment, input.Token).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Log.Error("Set API token failed during verify.", ex);
            ShowError("Could not verify API token",
                $"{ex.GetType().Name}: {ex.Message}\n\nSee log: {Log.Path}");
            message = ex.Message;
            return Result.Failed;
        }

        if (user is null)
        {
            ShowError("Token rejected",
                "The BIMy API did not accept that token. Please double-check the value and the selected environment, then try again.");
            return Result.Cancelled;
        }

        var session = new Session { Environment = input.Environment, Token = input.Token };
        SessionStore.Save(input.Environment, input.Token);
        SessionState.SetConnected(session, user);

        new TaskDialog("BIMy")
        {
            MainInstruction = $"Connected as {user.DisplayLabel}",
            MainContent =
                $"Environment: {BimyEnvironments.DisplayName(input.Environment)} ({BimyEnvironments.BaseUrl(input.Environment)})"
                + (string.IsNullOrWhiteSpace(user.WorkspaceName) ? "" : $"\nWorkspace: {user.WorkspaceName}"),
        }.Show();

        return Result.Succeeded;
    }

    private static void ShowError(string heading, string details)
    {
        new TaskDialog("Connect BIMy")
        {
            MainIcon = TaskDialogIcon.TaskDialogIconError,
            MainInstruction = heading,
            MainContent = details,
        }.Show();
    }
}
