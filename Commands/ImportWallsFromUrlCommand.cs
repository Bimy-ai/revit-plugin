using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitWallsPlugin.Models;
using RevitWallsPlugin.Services;
using RevitWallsPlugin.UI;

namespace RevitWallsPlugin.Commands;

[Transaction(TransactionMode.Manual)]
[Regeneration(RegenerationOption.Manual)]
public sealed class ImportWallsFromUrlCommand : IExternalCommand
{
    private const string DefaultUrl = "https://example.com/walls.json";

    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        var uiApp = commandData.Application;
        var uiDoc = uiApp.ActiveUIDocument;
        if (uiDoc is null)
        {
            TaskDialog.Show("Import Walls", "No active Revit document.");
            return Result.Cancelled;
        }

        var initialUrl = UrlState.Load() ?? DefaultUrl;
        var url = UrlInputDialog.Show(uiApp.MainWindowHandle, "Import Walls From URL", initialUrl);
        if (string.IsNullOrWhiteSpace(url))
        {
            Log.Info("User cancelled URL dialog.");
            return Result.Cancelled;
        }

        UrlState.Save(url);
        return ImportRunner.Run(uiApp, url, ref message);
    }
}
