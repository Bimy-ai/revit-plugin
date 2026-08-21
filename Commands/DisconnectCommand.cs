using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BimyRevit.Services;

namespace BimyRevit.Commands;

[Transaction(TransactionMode.ReadOnly)]
[Regeneration(RegenerationOption.Manual)]
public sealed class DisconnectCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        if (!SessionState.IsConnected && SessionStore.TryLoad() is null)
        {
            new TaskDialog("BIMy")
            {
                MainInstruction = "Not connected",
                MainContent = "There is no saved BIMy session to disconnect.",
            }.Show();
            return Result.Cancelled;
        }

        var confirm = new TaskDialog("Disconnect BIMy")
        {
            MainInstruction = "Disconnect from BIMy?",
            MainContent = "Your saved API token will be removed. You'll need to set it again to load projects.",
            CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No,
            DefaultButton = TaskDialogResult.No,
        };
        if (confirm.Show() != TaskDialogResult.Yes)
            return Result.Cancelled;

        SessionStore.Clear();
        SessionState.Clear();
        Log.Info("Session cleared by user.");

        new TaskDialog("BIMy")
        {
            MainInstruction = "Disconnected.",
        }.Show();

        return Result.Succeeded;
    }
}
