using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitWallsPlugin.Services;

namespace RevitWallsPlugin.Commands;

[Transaction(TransactionMode.Manual)]
[Regeneration(RegenerationOption.Manual)]
public sealed class RefreshWallsCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        var url = UrlState.Load();
        if (string.IsNullOrWhiteSpace(url))
        {
            TaskDialog.Show("Refresh Walls",
                "No URL remembered yet. Run 'Import Walls From URL' first.");
            return Result.Cancelled;
        }

        Log.Info($"Refresh invoked with remembered URL: {url}");
        return ImportRunner.Run(commandData.Application, url, ref message);
    }
}
